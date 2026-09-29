using SimForge.Assertions;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Testing.Tests;

public sealed class ScenarioExecutorTests
{
    private static readonly ScenarioExecutor Executor = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Scenario Define(string id, Func<ScenarioContext, CancellationToken, ValueTask> body, Action<ScenarioSetup>? configure = null) =>
        new(new ScenarioDescriptor(id), configure, body);

    [Fact]
    public async Task Passing_scenario_passes_with_default_seed_and_diagnostics()
    {
        var scenario = Define("executor.pass", async (context, cancellationToken) =>
        {
            context.Journal.Record("test", "body", "work", FaultPhases.After, OperationOutcome.Succeeded);
            await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(1), cancellationToken);
        });

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Passed, ScenarioFailureKind.None), (result.Outcome, result.FailureKind));
        Assert.True(result.Passed);
        Assert.Null(result.PrimaryError);
        Assert.Empty(result.CleanupErrors);
        Assert.Equal(scenario.Descriptor.DefaultSeed, result.Seed);
        Assert.Equal(SimulationEnvironmentState.Disposed, result.Diagnostics.EnvironmentState);
        Assert.Contains(result.Diagnostics.Journal, entry => entry.Operation == "work");
        Assert.True(result.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public void Default_seed_is_a_stable_hash_of_the_scenario_id()
    {
        // FNV-1a 32-bit of "orders.place"; the value must not change between processes, machines, or releases.
        Assert.Equal(-1600725367, new ScenarioDescriptor("orders.place").DefaultSeed);
    }

    [Fact]
    public async Task Same_seed_reproduces_the_same_run_and_an_explicit_seed_overrides_the_default()
    {
        var observed = new List<Guid>();
        var scenario = Define("executor.determinism", (context, _) =>
        {
            lock (observed)
            {
                observed.Add(context.Ids.NewGuid());
            }

            return ValueTask.CompletedTask;
        });

        var first = await Executor.ExecuteAsync(scenario, cancellationToken: Token);
        var second = await Executor.ExecuteAsync(scenario, cancellationToken: Token);
        var reseeded = await Executor.ExecuteAsync(scenario, new ScenarioRunOptions { Seed = 99 }, Token);

        Assert.Equal(observed[0], observed[1]);
        Assert.NotEqual(observed[0], observed[2]);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(99, reseeded.Seed);
    }

    public static TheoryData<string, Exception, ScenarioFailureKind> ClassifiedFailures => new()
    {
        { "assertion", new SimForgeAssertionException("expected 1, got 2"), ScenarioFailureKind.Assertion },
        { "unsupported", new UnsupportedCapabilityException("test.feature", "not modeled"), ScenarioFailureKind.UnsupportedCapability },
        { "simulated", new SimulatedServiceException("test", "db", "unique_violation", "duplicate"), ScenarioFailureKind.SimulatedServiceError },
        { "limit", new SimulationLimitExceededException(10, DateTimeOffset.UnixEpoch, []), ScenarioFailureKind.SimulationLimit },
        { "internal", new SimForgeInternalException("invariant broken"), ScenarioFailureKind.Internal },
        { "application", new InvalidOperationException("application bug"), ScenarioFailureKind.Application },
        { "uncanceled-oce", new OperationCanceledException("canceled by the application itself"), ScenarioFailureKind.Application },
    };

    [Theory]
    [MemberData(nameof(ClassifiedFailures))]
    public async Task Body_exceptions_are_classified_and_preserved(string name, Exception error, ScenarioFailureKind expectedKind)
    {
        var scenario = Define($"executor.failure.{name}", (_, _) => throw error);

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal(ScenarioOutcome.Failed, result.Outcome);
        Assert.Equal(expectedKind, result.FailureKind);
        Assert.Same(error, result.PrimaryError);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task Skip_ends_the_scenario_as_skipped_with_its_reason()
    {
        var scenario = Define("executor.skip", (context, _) =>
        {
            context.Skip("requires a capability that is not modeled yet");
            return ValueTask.CompletedTask;
        });

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal(ScenarioOutcome.Skipped, result.Outcome);
        Assert.Equal("requires a capability that is not modeled yet", result.SkipReason);
    }

    [Fact]
    public async Task Configuration_failure_fails_before_the_body_runs_and_still_disposes_added_resources()
    {
        RecordingResource? resource = null;
        var bodyRan = false;
        var scenario = Define(
            "executor.configure-fails",
            (_, _) =>
            {
                bodyRan = true;
                return ValueTask.CompletedTask;
            },
            setup =>
            {
                resource = setup.Environment.AddResource(new RecordingResource("db"));
                throw new InvalidOperationException("bad configuration");
            });

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.Configuration), (result.Outcome, result.FailureKind));
        Assert.False(bodyRan);
        Assert.Equal(1, resource!.DisposeCount);
    }

    [Fact]
    public async Task Unsupported_capability_during_configuration_keeps_the_specific_kind()
    {
        var scenario = Define(
            "executor.configure-unsupported",
            (_, _) => ValueTask.CompletedTask,
            _ => throw new UnsupportedCapabilityException("test.schema", "composite keys"));

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.UnsupportedCapability), (result.Outcome, result.FailureKind));
    }

    [Fact]
    public async Task Initialization_failure_reports_the_failing_resource_and_its_cleanup_errors()
    {
        var bodyRan = false;
        var scenario = Define(
            "executor.init-fails",
            (_, _) =>
            {
                bodyRan = true;
                return ValueTask.CompletedTask;
            },
            setup =>
            {
                setup.Environment.AddResource(new RecordingResource("first") { DisposeError = new IOException("first cleanup") });
                setup.Environment.AddResource(new RecordingResource("second") { InitializeError = new InvalidOperationException("second init") });
            });

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.Initialization), (result.Outcome, result.FailureKind));
        var primary = Assert.IsType<SimulationInitializationException>(result.PrimaryError);
        Assert.Equal("second", primary.ResourceName);
        Assert.Equal("first", Assert.IsType<ResourceCleanupException>(Assert.Single(result.CleanupErrors)).ResourceName);
        Assert.False(bodyRan);
    }

    [Fact]
    public async Task Cleanup_failure_after_a_passing_body_fails_the_scenario()
    {
        var scenario = Define(
            "executor.cleanup-fails",
            (_, _) => ValueTask.CompletedTask,
            setup => setup.Environment.AddResource(new RecordingResource("db") { DisposeError = new IOException("disk gone") }));

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.Cleanup), (result.Outcome, result.FailureKind));
        var cleanup = Assert.IsType<ResourceCleanupException>(Assert.Single(result.CleanupErrors));
        Assert.Same(cleanup, result.PrimaryError);
    }

    [Fact]
    public async Task Assertion_failure_and_cleanup_failure_are_both_retained()
    {
        var scenario = Define(
            "executor.assertion-and-cleanup",
            (_, _) =>
            {
                SimAssert.Equal(1, 2);
                return ValueTask.CompletedTask;
            },
            setup => setup.Environment.AddResource(new RecordingResource("db") { DisposeError = new IOException("disk gone") }));

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.Assertion), (result.Outcome, result.FailureKind));
        Assert.IsType<SimForgeAssertionException>(result.PrimaryError);
        Assert.IsType<ResourceCleanupException>(Assert.Single(result.CleanupErrors));
        var report = result.FormatReport();
        Assert.Contains("SimAssert.Equal failed", report, StringComparison.Ordinal);
        Assert.Contains("disk gone", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_failure_after_a_skip_fails_the_scenario()
    {
        var scenario = Define(
            "executor.skip-with-cleanup-failure",
            (context, _) =>
            {
                context.Skip("not modeled");
                return ValueTask.CompletedTask;
            },
            setup => setup.Environment.AddResource(new RecordingResource("db") { DisposeError = new IOException("disk gone") }));

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal((ScenarioOutcome.Failed, ScenarioFailureKind.Cleanup), (result.Outcome, result.FailureKind));
        Assert.Equal("not modeled", result.SkipReason);
        Assert.IsType<ResourceCleanupException>(result.PrimaryError);
    }

    [Fact]
    public async Task Deadline_also_bounds_a_hanging_initialization()
    {
        var bodyRan = false;
        var scenario = Define(
            "executor.init-timeout",
            (_, _) =>
            {
                bodyRan = true;
                return ValueTask.CompletedTask;
            },
            setup => setup.Environment.AddResource(new RecordingResource("slow")
            {
                OnInitialize = async cancellationToken => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            }));

        var result = await Executor.ExecuteAsync(scenario, new ScenarioRunOptions { Timeout = TimeSpan.FromMilliseconds(100) }, Token);

        Assert.Equal(ScenarioOutcome.TimedOut, result.Outcome);
        Assert.False(bodyRan);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_body_is_reported_as_canceled()
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = Define("executor.caller-cancel", async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        var running = Executor.ExecuteAsync(scenario, cancellationToken: caller.Token);
        await started.Task;
        await caller.CancelAsync();
        var result = await running;

        Assert.Equal((ScenarioOutcome.Canceled, ScenarioFailureKind.None), (result.Outcome, result.FailureKind));
        Assert.IsAssignableFrom<OperationCanceledException>(result.PrimaryError);
        Assert.False(result.WorkAbandoned);
    }

    [Fact]
    public async Task Already_canceled_token_prevents_the_body_from_running()
    {
        var bodyRan = false;
        var scenario = Define("executor.pre-canceled", (_, _) =>
        {
            bodyRan = true;
            return ValueTask.CompletedTask;
        }, setup => setup.Environment.AddResource(new RecordingResource("db")));

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: new CancellationToken(canceled: true));

        Assert.Equal(ScenarioOutcome.Canceled, result.Outcome);
        Assert.False(bodyRan);
    }

    [Fact]
    public async Task Cooperative_body_exceeding_its_real_time_deadline_times_out()
    {
        var scenario = Define("executor.timeout", async (_, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        var result = await Executor.ExecuteAsync(scenario, new ScenarioRunOptions { Timeout = TimeSpan.FromMilliseconds(100) }, Token);

        Assert.Equal((ScenarioOutcome.TimedOut, ScenarioFailureKind.None), (result.Outcome, result.FailureKind));
        Assert.IsType<TimeoutException>(result.PrimaryError);
        Assert.False(result.WorkAbandoned);
    }

    [Fact]
    public async Task Virtual_time_does_not_count_against_the_real_time_deadline()
    {
        var scenario = Define("executor.virtual-time", async (context, cancellationToken) =>
            await context.Scheduler.AdvanceByAsync(TimeSpan.FromDays(365), cancellationToken));

        var result = await Executor.ExecuteAsync(scenario, new ScenarioRunOptions { Timeout = TimeSpan.FromSeconds(30) }, Token);

        Assert.Equal(ScenarioOutcome.Passed, result.Outcome);
        Assert.Equal(SimulationEnvironmentOptions.DefaultStartTime.AddDays(365), result.Diagnostics.VirtualTime);
    }

    [Fact]
    public async Task Body_that_ignores_cancellation_is_abandoned_and_its_environment_is_still_disposed()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingResource? resource = null;
        var scenario = Define(
            "executor.abandoned",
            async (_, _) => await never.Task,
            setup => resource = setup.Environment.AddResource(new RecordingResource("db")));

        var result = await Executor.ExecuteAsync(
            scenario,
            new ScenarioRunOptions { Timeout = TimeSpan.FromMilliseconds(100), CleanupTimeout = TimeSpan.FromMilliseconds(100) },
            Token);

        Assert.Equal(ScenarioOutcome.TimedOut, result.Outcome);
        Assert.True(result.WorkAbandoned);
        Assert.IsType<SimulationWorkAbandonedException>(result.PrimaryError!.InnerException);
        Assert.Equal(1, resource!.DisposeCount);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(10));
        never.SetResult();
    }

    [Fact]
    public async Task Body_that_finishes_after_its_deadline_is_still_timed_out()
    {
        var scenario = Define("executor.late-success", async (_, _) => await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System));

        var result = await Executor.ExecuteAsync(
            scenario,
            new ScenarioRunOptions { Timeout = TimeSpan.FromMilliseconds(50), CleanupTimeout = TimeSpan.FromSeconds(5) },
            Token);

        Assert.Equal(ScenarioOutcome.TimedOut, result.Outcome);
        Assert.False(result.WorkAbandoned);
    }

    [Fact]
    public async Task Services_are_created_lazily_once_and_disposed_in_reverse_order_before_resources()
    {
        var log = new EventLog();
        var created = 0;
        var scenario = Define(
            "executor.services",
            (context, _) =>
            {
                var first = context.GetRequiredService<FirstService>();
                Assert.Same(first, context.GetRequiredService<FirstService>());
                Assert.Same(context.Clock, context.Services.GetService(typeof(TimeProvider)));
                Assert.Null(context.Services.GetService(typeof(Uri)));
                return ValueTask.CompletedTask;
            },
            setup =>
            {
                setup.Environment.AddResource(new RecordingResource("db", log));
                setup.AddService(context =>
                {
                    Interlocked.Increment(ref created);
                    return new FirstService(log, context.GetRequiredService<SecondService>());
                });
                setup.AddService(_ => new SecondService(log));
            });

        var result = await Executor.ExecuteAsync(scenario, cancellationToken: Token);

        Assert.Equal(ScenarioOutcome.Passed, result.Outcome);
        Assert.Equal(1, created);
        Assert.Equal(["init:db", "dispose:first", "dispose:second", "dispose:db"], log.Snapshot());
    }

    [Fact]
    public async Task Circular_or_missing_services_fail_the_scenario()
    {
        var circular = Define(
            "executor.services-circular",
            (context, _) =>
            {
                context.GetRequiredService<FirstService>();
                return ValueTask.CompletedTask;
            },
            setup => setup.AddService(context => new FirstService(new EventLog(), (SecondService)context.GetRequiredService<FirstService>().Dependency)));
        var missing = Define("executor.services-missing", (context, _) =>
        {
            context.GetRequiredService<SecondService>();
            return ValueTask.CompletedTask;
        });

        var circularResult = await Executor.ExecuteAsync(circular, cancellationToken: Token);
        var missingResult = await Executor.ExecuteAsync(missing, cancellationToken: Token);

        Assert.Contains("Circular", circularResult.PrimaryError!.Message, StringComparison.Ordinal);
        Assert.Equal(ScenarioFailureKind.Application, missingResult.FailureKind);
    }

    [Fact]
    public async Task Parallel_runs_of_one_scenario_use_isolated_environments()
    {
        var scenario = Define(
            "executor.parallel",
            async (context, cancellationToken) =>
            {
                var resource = context.GetResource<RecordingResource>("db");
                SimAssert.Equal(1, context.Environment.Resources.Count);
                await context.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(1), cancellationToken);
                SimAssert.Equal(0, resource.DisposeCount);
            },
            setup => setup.Environment.AddResource(new RecordingResource("db")));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Executor.ExecuteAsync(scenario, cancellationToken: Token)));

        Assert.All(results, result => Assert.Equal(ScenarioOutcome.Passed, result.Outcome));
    }

    [Fact]
    public async Task Diagnostics_are_exported_on_failure_only_by_default()
    {
        var directory = Path.Combine(Path.GetTempPath(), "simforge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new ScenarioRunOptions { DiagnosticsDirectory = directory };
            var passing = await Executor.ExecuteAsync(Define("executor.export-pass", (_, _) => ValueTask.CompletedTask), options, Token);
            var failing = await Executor.ExecuteAsync(Define("executor.export-fail", (_, _) => throw new InvalidOperationException("x")), options, Token);
            var always = await Executor.ExecuteAsync(
                Define("executor.export-always", (_, _) => ValueTask.CompletedTask),
                options with { ExportDiagnostics = DiagnosticsExport.Always },
                Token);

            Assert.Null(passing.DiagnosticsPath);
            Assert.True(File.Exists(failing.DiagnosticsPath));
            Assert.True(File.Exists(always.DiagnosticsPath));
            Assert.Contains(failing.DiagnosticsPath!, failing.FormatReport(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Descriptors_validate_ids_and_normalize_tags()
    {
        var descriptor = new ScenarioDescriptor("orders/place:happy-path", "Place order", ["storage", "orders", "storage"]);

        Assert.Equal(["orders", "storage"], descriptor.Tags);
        Assert.Equal("Place order (orders/place:happy-path)", descriptor.ToString());
        Assert.Throws<ArgumentException>(() => new ScenarioDescriptor("has space"));
        Assert.Throws<ArgumentException>(() => new ScenarioDescriptor("-leading-dash"));
        Assert.Throws<ArgumentException>(() => new ScenarioDescriptor("ok", tags: ["bad tag"]));
    }

    [Fact]
    public void Descriptors_with_equal_content_are_equal()
    {
        var source = new ScenarioSourceLocation("file.cs", 10);
        var first = new ScenarioDescriptor("orders.place", "Place", ["b", "a"], source);
        var second = new ScenarioDescriptor("orders.place", "Place", ["a", "b"], source);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new ScenarioDescriptor("orders.place", "Place", ["a"], source));
    }

    [Fact]
    public async Task Invalid_run_options_are_rejected()
    {
        var scenario = Define("executor.options", (_, _) => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Executor.ExecuteAsync(scenario, new ScenarioRunOptions { Timeout = TimeSpan.Zero }, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Executor.ExecuteAsync(scenario, new ScenarioRunOptions { MaxSchedulerSteps = 0 }, Token));
    }

    private sealed class FirstService(EventLog log, object dependency) : IDisposable
    {
        public object Dependency { get; } = dependency;

        public void Dispose() => log.Add("dispose:first");
    }

    private sealed class SecondService(EventLog log) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Add("dispose:second");
            return ValueTask.CompletedTask;
        }
    }
}
