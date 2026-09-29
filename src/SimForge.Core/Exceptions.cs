using System.Collections.Immutable;
using System.Text;

namespace SimForge;

/// <summary>Base type for every exception raised by SimForge itself.</summary>
public abstract class SimForgeException : Exception
{
    protected SimForgeException(string message)
        : base(message)
    {
    }

    protected SimForgeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The requested operation, option, or value lies outside what a provider implements.
/// Providers raise it before changing any state.
/// </summary>
public sealed class UnsupportedCapabilityException : SimForgeException
{
    public UnsupportedCapabilityException(string capabilityId, string message)
        : base($"{message} [capability: {capabilityId}]")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        CapabilityId = capabilityId;
    }

    /// <summary>Identifier of the capability in the provider's capability manifest.</summary>
    public string CapabilityId { get; }
}

/// <summary>A simulated service reported an error in the way the modeled service would, for example a constraint violation.</summary>
public class SimulatedServiceException : SimForgeException
{
    public SimulatedServiceException(string provider, string resource, string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Provider = provider;
        Resource = resource;
        ErrorCode = errorCode;
    }

    public string Provider { get; }

    public string Resource { get; }

    /// <summary>SimForge error code. It is not a claim of parity with the real service's error codes.</summary>
    public string ErrorCode { get; }
}

/// <summary>A fault rule fired. <see cref="StateChanged"/> tells whether the operation took effect before the caller saw the failure.</summary>
public sealed class SimulatedFaultException : SimulatedServiceException
{
    public const string InjectedFaultErrorCode = "injected_fault";

    public SimulatedFaultException(FaultReport report)
        : base(report.Provider, report.Resource, InjectedFaultErrorCode, Describe(report))
    {
        Report = report;
    }

    public FaultReport Report { get; }

    /// <summary>True when the operation was applied but the caller did not receive success (an ambiguous outcome).</summary>
    public bool StateChanged => Report.StateChanged;

    private static string Describe(FaultReport report)
    {
        var effect = report.StateChanged
            ? "the operation was applied, but its success response was lost"
            : "the operation was not applied";
        var reason = report.Reason is null ? string.Empty : $" Reason: {report.Reason}.";
        return $"Injected fault '{report.RuleId}' fired at {report.Provider}/{report.Resource} {report.Operation}:{report.Phase} " +
               $"(matching occurrence {report.Occurrence}); {effect}.{reason}";
    }
}

/// <summary>A SimForge assertion failed. It carries no dependency on any test framework.</summary>
public sealed class SimForgeAssertionException : SimForgeException
{
    public SimForgeAssertionException(string message)
        : base(message)
    {
    }

    public SimForgeAssertionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A scheduler drive exceeded its step bound, which usually indicates self-rescheduling work (a suspected infinite loop).</summary>
public sealed class SimulationLimitExceededException : SimForgeException
{
    public SimulationLimitExceededException(int stepLimit, DateTimeOffset virtualTime, ImmutableArray<PendingWork> pendingWork)
        : base(Describe(stepLimit, virtualTime, pendingWork))
    {
        StepLimit = stepLimit;
        VirtualTime = virtualTime;
        PendingWork = pendingWork;
    }

    public int StepLimit { get; }

    public DateTimeOffset VirtualTime { get; }

    public ImmutableArray<PendingWork> PendingWork { get; }

    private static string Describe(int stepLimit, DateTimeOffset virtualTime, ImmutableArray<PendingWork> pendingWork)
    {
        var builder = new StringBuilder()
            .Append("The scheduler executed ").Append(stepLimit)
            .Append(" work item(s) in one drive and runnable work remains at virtual time ")
            .Append(virtualTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append("; work that keeps rescheduling itself is suspected. Pending work (")
            .Append(pendingWork.Length).Append("):");
        foreach (var work in pendingWork.Take(10))
        {
            builder.Append(Environment.NewLine).Append("  ").Append(work);
        }

        if (pendingWork.Length > 10)
        {
            builder.Append(Environment.NewLine).Append("  ... ").Append(pendingWork.Length - 10).Append(" more");
        }

        return builder.ToString();
    }
}

/// <summary>SimForge detected a violation of its own invariants. This indicates a SimForge defect, not an application failure.</summary>
public sealed class SimForgeInternalException : SimForgeException
{
    public SimForgeInternalException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Environment initialization failed. Resources that had started initializing were disposed in reverse order.</summary>
public sealed class SimulationInitializationException : SimForgeException
{
    public SimulationInitializationException(string? resourceName, Exception primaryError, IReadOnlyList<Exception> cleanupErrors)
        : base(Describe(resourceName, primaryError, cleanupErrors), primaryError)
    {
        ResourceName = resourceName;
        CleanupErrors = [.. cleanupErrors];
    }

    /// <summary>Name of the resource whose initialization failed, or null when the failure is not attributable to one resource.</summary>
    public string? ResourceName { get; }

    public ImmutableArray<Exception> CleanupErrors { get; }

    private static string Describe(string? resourceName, Exception primaryError, IReadOnlyList<Exception> cleanupErrors)
    {
        var target = resourceName is null ? "Environment initialization failed" : $"Initialization of resource '{resourceName}' failed";
        var cleanup = cleanupErrors.Count == 0 ? string.Empty : $" {cleanupErrors.Count} cleanup error(s) also occurred.";
        return $"{target}: {primaryError.Message}{cleanup}";
    }
}

/// <summary>One or more errors occurred while disposing an environment. Every error is retained.</summary>
public sealed class SimulationCleanupException : SimForgeException
{
    public SimulationCleanupException(IReadOnlyList<Exception> errors)
        : base(Describe(errors), errors.Count > 0 ? errors[0] : null)
    {
        Errors = [.. errors];
    }

    public ImmutableArray<Exception> Errors { get; }

    private static string Describe(IReadOnlyList<Exception> errors)
    {
        var builder = new StringBuilder().Append(errors.Count).Append(" error(s) occurred during environment cleanup:");
        foreach (var error in errors)
        {
            builder.Append(Environment.NewLine).Append("  ").Append(error.GetType().Name).Append(": ").Append(error.Message);
        }

        return builder.ToString();
    }
}

/// <summary>A resource failed while being disposed.</summary>
public sealed class ResourceCleanupException : SimForgeException
{
    public ResourceCleanupException(string resourceName, Exception innerException)
        : base($"Disposing resource '{resourceName}' failed: {innerException.Message}", innerException)
    {
        ResourceName = resourceName;
    }

    public string ResourceName { get; }
}

/// <summary>
/// Work was still running after the cleanup timeout. The environment was disposed without waiting further
/// and must not be reused; the running work was not forcibly stopped.
/// </summary>
public sealed class SimulationWorkAbandonedException : SimForgeException
{
    public SimulationWorkAbandonedException(string workDescription, TimeSpan cleanupTimeout)
        : base(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{workDescription} was still running {cleanupTimeout.TotalMilliseconds:0} ms after cancellation was requested. It was abandoned (managed code cannot be stopped forcibly) and its environment must not be reused."))
    {
        WorkDescription = workDescription;
        CleanupTimeout = cleanupTimeout;
    }

    public string WorkDescription { get; }

    public TimeSpan CleanupTimeout { get; }
}
