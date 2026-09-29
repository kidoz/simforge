using System.Globalization;

namespace SimForge.Testing;

/// <summary>When <see cref="ScenarioExecutor"/> writes diagnostics to <see cref="ScenarioRunOptions.DiagnosticsDirectory"/>.</summary>
public enum DiagnosticsExport
{
    Never,

    /// <summary>For every outcome other than <see cref="ScenarioOutcome.Passed"/> or <see cref="ScenarioOutcome.Skipped"/>.</summary>
    OnFailure,
    Always,
}

/// <summary>Options for one scenario run.</summary>
public sealed record ScenarioRunOptions
{
    public static ScenarioRunOptions Default { get; } = new();

    /// <summary>Seed for the run. Defaults to <see cref="ScenarioDescriptor.DefaultSeed"/>.</summary>
    public int? Seed { get; init; }

    public DateTimeOffset StartTime { get; init; } = SimulationEnvironmentOptions.DefaultStartTime;

    /// <summary>
    /// Real elapsed-time deadline covering configuration, initialization, and the scenario body (cleanup is bounded
    /// separately by <see cref="CleanupTimeout"/>). Virtual time never counts against it.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Real time granted to a canceled body, and to running scheduled work, before it is reported as abandoned.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxSchedulerSteps { get; init; } = 10_000;

    public bool CapturePayloads { get; init; }

    /// <summary>Caller-selected directory for diagnostics files. Nothing is written when it is null.</summary>
    public string? DiagnosticsDirectory { get; init; }

    public DiagnosticsExport ExportDiagnostics { get; init; } = DiagnosticsExport.OnFailure;

    internal void Validate()
    {
        if (Timeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(Timeout));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(CleanupTimeout, TimeSpan.Zero, nameof(CleanupTimeout));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSchedulerSteps, 1, nameof(MaxSchedulerSteps));
    }
}

/// <summary>
/// The single owner of a scenario's lifecycle and outcome classification. It creates a fresh environment, configures
/// it, initializes it, runs the body under cancellation and an optional real-time deadline, and then always disposes
/// services and the environment.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> reports failures through the returned <see cref="ScenarioResult"/> and does not throw
/// them. Runner adapters must turn every outcome other than <see cref="ScenarioOutcome.Passed"/> into a host failure
/// (or a skip). Cancellation is cooperative: after cancellation or timeout the body gets
/// <see cref="ScenarioRunOptions.CleanupTimeout"/> to stop. If it does not stop, the result is marked
/// <see cref="ScenarioResult.WorkAbandoned"/> and the environment is disposed anyway, so it cannot be reused.
/// </remarks>
public sealed class ScenarioExecutor
{
    public async Task<ScenarioResult> ExecuteAsync(IScenario scenario, ScenarioRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        options ??= ScenarioRunOptions.Default;
        options.Validate();
        var descriptor = scenario.Descriptor ?? throw new ArgumentException("The scenario has no descriptor.", nameof(scenario));
        var seed = options.Seed ?? descriptor.DefaultSeed;
        var startedAt = TimeProvider.System.GetTimestamp();

        var environment = new SimulationEnvironment(new SimulationEnvironmentOptions
        {
            ScenarioId = descriptor.Id,
            Seed = seed,
            StartTime = options.StartTime,
            MaxSchedulerSteps = options.MaxSchedulerSteps,
            CapturePayloads = options.CapturePayloads,
            CleanupTimeout = options.CleanupTimeout,
        });

        using var deadline = new CancellationTokenSource();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (options.Timeout is { } timeout)
        {
            deadline.CancelAfter(timeout);
        }

        var state = new RunState(descriptor, options, cancellationToken, deadline.Token, run.Token);
        ScenarioServices? services = null;

        try
        {
            var setup = new ScenarioSetup(descriptor, environment);
            scenario.Configure(setup);
            services = setup.Seal();
        }
        catch (Exception exception)
        {
            state.StageFailed(exception, exception, ScenarioFailureKind.Configuration);
        }

        if (!state.Decided)
        {
            try
            {
                await environment.InitializeAsync(run.Token).ConfigureAwait(false);
            }
            catch (SimulationInitializationException exception)
            {
                state.CleanupErrors.AddRange(exception.CleanupErrors);
                state.StageFailed(exception, exception.InnerException ?? exception, ScenarioFailureKind.Initialization);
            }
            catch (Exception exception)
            {
                state.StageFailed(exception, exception, ScenarioFailureKind.Initialization);
            }
        }

        if (!state.Decided && services is not null)
        {
            var context = new ScenarioContext(descriptor, environment, services);
            services.Attach(context);
            await RunBodyAsync(scenario, context, state).ConfigureAwait(false);
        }

        if (services is not null)
        {
            state.CleanupErrors.AddRange(await services.DisposeAsync().ConfigureAwait(false));
        }

        try
        {
            await environment.DisposeAsync().ConfigureAwait(false);
        }
        catch (SimulationCleanupException exception)
        {
            state.CleanupErrors.AddRange(exception.Errors);
        }
        catch (Exception exception)
        {
            state.CleanupErrors.Add(exception);
        }

        var diagnostics = environment.CaptureDiagnostics();
        var diagnosticsPath = await ExportDiagnosticsAsync(state, diagnostics).ConfigureAwait(false);
        // Cleanup failures turn a pass or a skip into a failure; other outcomes already fail and keep their classification.
        if (!state.Decided || state.Outcome == ScenarioOutcome.Skipped)
        {
            if (state.CleanupErrors.Count > 0)
            {
                state.Decide(ScenarioOutcome.Failed, ScenarioFailureKind.Cleanup, state.CleanupErrors[0]);
            }
            else if (!state.Decided)
            {
                state.Decide(ScenarioOutcome.Passed, ScenarioFailureKind.None, null);
            }
        }

        return new ScenarioResult(
            descriptor,
            state.Outcome,
            state.FailureKind,
            seed,
            TimeProvider.System.GetElapsedTime(startedAt),
            state.PrimaryError,
            [.. state.CleanupErrors],
            state.SkipReason,
            state.WorkAbandoned,
            diagnostics,
            diagnosticsPath);
    }

    private static async Task RunBodyAsync(IScenario scenario, ScenarioContext context, RunState state)
    {
        // Running the body on the thread pool lets the executor observe the deadline even if the body blocks synchronously.
        var body = Task.Run(async () => await scenario.ExecuteAsync(context, state.RunToken).ConfigureAwait(false), CancellationToken.None);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (state.RunToken.Register(() => interrupted.TrySetResult()))
        {
            if (await Task.WhenAny(body, interrupted.Task).ConfigureAwait(false) == body)
            {
                if (await ObserveAsync(body).ConfigureAwait(false) is { } error)
                {
                    state.BodyFailed(error);
                }

                return;
            }
        }

        var cleanupWindow = Task.Delay(state.Options.CleanupTimeout, TimeProvider.System);
        if (await Task.WhenAny(body, cleanupWindow).ConfigureAwait(false) != body)
        {
            state.WorkAbandoned = true;
            _ = body.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            state.Interrupted(new SimulationWorkAbandonedException($"The body of scenario '{state.Descriptor.Id}'", state.Options.CleanupTimeout));
            return;
        }

        state.Interrupted(await ObserveAsync(body).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns the exception a completed task ended with. Awaiting also covers canceled tasks, whose
    /// <see cref="Task.Exception"/> is null even though the body threw an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<string?> ExportDiagnosticsAsync(RunState state, DiagnosticSnapshot diagnostics)
    {
        if (state.Options.DiagnosticsDirectory is not { } directory || state.Options.ExportDiagnostics == DiagnosticsExport.Never)
        {
            return null;
        }

        var failing = state.CleanupErrors.Count > 0 ||
                      (state.Decided && state.Outcome is not (ScenarioOutcome.Passed or ScenarioOutcome.Skipped));
        if (state.Options.ExportDiagnostics == DiagnosticsExport.OnFailure && !failing)
        {
            return null;
        }

        try
        {
            return await diagnostics.ExportAsync(directory).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state.CleanupErrors.Add(new IOException($"Exporting diagnostics to '{directory}' failed: {exception.Message}", exception));
            return null;
        }
    }

    private static ScenarioFailureKind Classify(Exception exception, ScenarioFailureKind fallback) => exception switch
    {
        SimForgeAssertionException => ScenarioFailureKind.Assertion,
        UnsupportedCapabilityException => ScenarioFailureKind.UnsupportedCapability,
        SimulatedServiceException => ScenarioFailureKind.SimulatedServiceError,
        SimulationLimitExceededException => ScenarioFailureKind.SimulationLimit,
        SimForgeInternalException => ScenarioFailureKind.Internal,
        _ => fallback,
    };

    private sealed class RunState(
        ScenarioDescriptor descriptor,
        ScenarioRunOptions options,
        CancellationToken callerToken,
        CancellationToken deadlineToken,
        CancellationToken runToken)
    {
        public ScenarioDescriptor Descriptor { get; } = descriptor;

        public ScenarioRunOptions Options { get; } = options;

        public CancellationToken RunToken { get; } = runToken;

        public List<Exception> CleanupErrors { get; } = [];

        public bool Decided { get; private set; }

        public ScenarioOutcome Outcome { get; private set; }

        public ScenarioFailureKind FailureKind { get; private set; }

        public Exception? PrimaryError { get; private set; }

        public string? SkipReason { get; private set; }

        public bool WorkAbandoned { get; set; }

        private bool TimedOut => deadlineToken.IsCancellationRequested && !callerToken.IsCancellationRequested;

        public void Decide(ScenarioOutcome outcome, ScenarioFailureKind kind, Exception? primaryError)
        {
            Decided = true;
            Outcome = outcome;
            FailureKind = kind;
            PrimaryError = primaryError;
        }

        /// <summary>Configuration or initialization failed; <paramref name="cause"/> is the underlying error used for classification.</summary>
        public void StageFailed(Exception reported, Exception cause, ScenarioFailureKind stage)
        {
            if (cause is ScenarioSkippedException skipped)
            {
                SkipReason = skipped.Message;
                Decide(ScenarioOutcome.Skipped, ScenarioFailureKind.None, null);
            }
            else if (cause is OperationCanceledException && RunToken.IsCancellationRequested)
            {
                Interrupted(reported);
            }
            else
            {
                Decide(ScenarioOutcome.Failed, Classify(cause, stage), reported);
            }
        }

        public void BodyFailed(Exception exception)
        {
            if (exception is ScenarioSkippedException skipped)
            {
                SkipReason = skipped.Message;
                Decide(ScenarioOutcome.Skipped, ScenarioFailureKind.None, null);
            }
            else if (exception is OperationCanceledException && RunToken.IsCancellationRequested)
            {
                Interrupted(exception);
            }
            else
            {
                Decide(ScenarioOutcome.Failed, Classify(exception, ScenarioFailureKind.Application), exception);
            }
        }

        /// <summary>The caller canceled or the deadline elapsed before the scenario completed.</summary>
        public void Interrupted(Exception? observed)
        {
            if (TimedOut)
            {
                var error = new TimeoutException(
                    string.Create(CultureInfo.InvariantCulture, $"Scenario '{Descriptor.Id}' exceeded its real-time timeout of {Options.Timeout?.TotalMilliseconds:0} ms."),
                    observed is OperationCanceledException ? null : observed);
                Decide(ScenarioOutcome.TimedOut, ScenarioFailureKind.None, error);
            }
            else
            {
                Decide(
                    ScenarioOutcome.Canceled,
                    ScenarioFailureKind.None,
                    observed ?? new OperationCanceledException($"Scenario '{Descriptor.Id}' was canceled.", callerToken));
            }
        }
    }
}
