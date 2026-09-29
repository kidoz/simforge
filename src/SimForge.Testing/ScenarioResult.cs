using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace SimForge.Testing;

public enum ScenarioOutcome
{
    Passed,
    Failed,
    Skipped,

    /// <summary>The caller's cancellation token was canceled before the scenario completed.</summary>
    Canceled,

    /// <summary>The real-time deadline elapsed before the scenario completed.</summary>
    TimedOut,
}

/// <summary>Why a scenario failed. It is <see cref="None"/> for every outcome other than <see cref="ScenarioOutcome.Failed"/>.</summary>
public enum ScenarioFailureKind
{
    None,

    /// <summary>A <see cref="SimForgeAssertionException"/> was thrown.</summary>
    Assertion,

    /// <summary>An operation outside a provider's capabilities was requested.</summary>
    UnsupportedCapability,

    /// <summary>A simulated service error (including injected faults) was not handled by the scenario.</summary>
    SimulatedServiceError,

    /// <summary>A scheduler step bound was exceeded.</summary>
    SimulationLimit,

    /// <summary>Scenario configuration threw.</summary>
    Configuration,

    /// <summary>Environment initialization failed.</summary>
    Initialization,

    /// <summary>The scenario body completed, but disposal of services or resources failed.</summary>
    Cleanup,

    /// <summary>Any other exception thrown by the scenario or the application code under test.</summary>
    Application,

    /// <summary>A SimForge invariant was violated (a SimForge defect).</summary>
    Internal,
}

/// <summary>Complete result of one scenario run. Runner adapters translate it into their own reporting model.</summary>
public sealed class ScenarioResult
{
    internal ScenarioResult(
        ScenarioDescriptor descriptor,
        ScenarioOutcome outcome,
        ScenarioFailureKind failureKind,
        int seed,
        TimeSpan elapsed,
        Exception? primaryError,
        ImmutableArray<Exception> cleanupErrors,
        string? skipReason,
        bool workAbandoned,
        DiagnosticSnapshot diagnostics,
        string? diagnosticsPath)
    {
        Descriptor = descriptor;
        Outcome = outcome;
        FailureKind = failureKind;
        Seed = seed;
        Elapsed = elapsed;
        PrimaryError = primaryError;
        CleanupErrors = cleanupErrors;
        SkipReason = skipReason;
        WorkAbandoned = workAbandoned;
        Diagnostics = diagnostics;
        DiagnosticsPath = diagnosticsPath;
    }

    public ScenarioDescriptor Descriptor { get; }

    public ScenarioOutcome Outcome { get; }

    public ScenarioFailureKind FailureKind { get; }

    /// <summary>Seed the run used; pass it back through <see cref="ScenarioRunOptions.Seed"/> to reproduce the run.</summary>
    public int Seed { get; }

    /// <summary>Real elapsed time of the run, including cleanup.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The error that decided the outcome, if any.</summary>
    public Exception? PrimaryError { get; }

    /// <summary>Errors from disposing services and the environment. They are retained alongside <see cref="PrimaryError"/>.</summary>
    public ImmutableArray<Exception> CleanupErrors { get; }

    public string? SkipReason { get; }

    /// <summary>True when the scenario body did not stop within the cleanup window after cancellation. The environment was disposed and must not be reused.</summary>
    public bool WorkAbandoned { get; }

    public DiagnosticSnapshot Diagnostics { get; }

    /// <summary>Path of the exported diagnostics file, when diagnostics were exported.</summary>
    public string? DiagnosticsPath { get; }

    public bool Passed => Outcome == ScenarioOutcome.Passed;

    /// <summary>Human-readable report containing the outcome, errors, reproduction seed, and diagnostics summary.</summary>
    public string FormatReport(int maxJournalEntries = 25)
    {
        var builder = new StringBuilder()
            .Append("Scenario ").Append(Descriptor).Append(": ").Append(Outcome);
        if (FailureKind != ScenarioFailureKind.None)
        {
            builder.Append(" (").Append(FailureKind).Append(')');
        }

        builder.Append(" after ").Append(Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms")
            .Append("; seed ").Append(Seed.ToString(CultureInfo.InvariantCulture)).AppendLine();
        if (Descriptor.Source is { } source)
        {
            builder.Append("Defined at ").Append(source.FilePath).Append(':').Append(source.LineNumber.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }

        if (SkipReason is not null)
        {
            builder.Append("Skip reason: ").AppendLine(SkipReason);
        }

        if (PrimaryError is not null)
        {
            builder.Append("Primary error: ").Append(PrimaryError.GetType().FullName).Append(": ").AppendLine(PrimaryError.Message);
        }

        if (WorkAbandoned)
        {
            builder.AppendLine("Work was abandoned after cancellation; the environment was disposed and must not be reused.");
        }

        if (!CleanupErrors.IsEmpty)
        {
            builder.Append("Cleanup errors (").Append(CleanupErrors.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("):");
            foreach (var error in CleanupErrors)
            {
                builder.Append("  ").Append(error.GetType().FullName).Append(": ").AppendLine(error.Message);
            }
        }

        if (DiagnosticsPath is not null)
        {
            builder.Append("Diagnostics exported to ").AppendLine(DiagnosticsPath);
        }

        builder.Append(Diagnostics.FormatSummary(maxJournalEntries));
        return builder.ToString();
    }

    public override string ToString() => $"{Descriptor.Id}: {Outcome}";
}
