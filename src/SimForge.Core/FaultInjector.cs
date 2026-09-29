using System.Collections.Immutable;
using System.Globalization;

namespace SimForge;

/// <summary>Named phases at which providers evaluate fault rules.</summary>
public static class FaultPhases
{
    /// <summary>Before the operation changes any state. A fault here means the operation did not happen.</summary>
    public const string Before = "before";

    /// <summary>After the operation changed state but before the caller learns of success. A fault here is an ambiguous outcome.</summary>
    public const string After = "after";
}

/// <summary>A point at which a provider evaluates fault rules. Resources declare the points they support.</summary>
public readonly record struct FaultPoint(string Provider, string Operation, string Phase)
{
    public override string ToString() => $"{Provider} {Operation}:{Phase}";
}

/// <summary>
/// A one-shot fault. It matches a provider, optionally one resource, an operation, and a phase, and it fires on the
/// <see cref="Occurrence"/>-th matching evaluation counted from the moment it was added.
/// </summary>
public sealed record FaultRule
{
    public required string Provider { get; init; }

    /// <summary>Resource name to match, or null for any resource of <see cref="Provider"/>.</summary>
    public string? Resource { get; init; }

    public required string Operation { get; init; }

    public string Phase { get; init; } = FaultPhases.Before;

    /// <summary>1-based matching occurrence at which the rule fires.</summary>
    public int Occurrence { get; init; } = 1;

    /// <summary>Optional human-readable reason included in the fault report and exception message.</summary>
    public string? Reason { get; init; }

    public override string ToString() =>
        $"{Provider}/{Resource ?? "*"} {Operation}:{Phase} occurrence {Occurrence.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>What a fired fault did: which rule fired, where, and whether state had changed.</summary>
public sealed record FaultReport(
    string RuleId,
    string Provider,
    string Resource,
    string Operation,
    string Phase,
    int Occurrence,
    bool StateChanged,
    DateTimeOffset VirtualTime,
    string? CorrelationId,
    string? Reason);

/// <summary>Snapshot of a rule that has not fired, for diagnostics.</summary>
public sealed record UnfiredFault(string RuleId, FaultRule Rule, int Matches);

/// <summary>A fault rule registered with an environment.</summary>
public sealed class ActiveFault
{
    internal ActiveFault(string id, FaultRule rule)
    {
        Id = id;
        Rule = rule;
    }

    public string Id { get; }

    public FaultRule Rule { get; }

    /// <summary>Number of evaluations that matched the rule before and including the one at which it fired.</summary>
    public int Matches { get; internal set; }

    public FaultReport? Report { get; internal set; }

    public bool HasFired => Report is not null;
}

/// <summary>
/// Environment-owned fault registry. Providers call <see cref="Evaluate"/> at their declared fault points.
/// </summary>
/// <remarks>
/// At every evaluation, each unfired rule that matches increments its count. The earliest-registered rule whose count
/// has reached its occurrence fires; at most one rule fires per evaluation. A rule that became due at the same
/// evaluation fires at its next match.
/// </remarks>
public sealed class FaultInjector
{
    private readonly Lock _gate = new();
    private readonly List<ActiveFault> _rules = [];
    private readonly List<FaultReport> _fired = [];
    private readonly SimulationEnvironment _environment;

    internal FaultInjector(SimulationEnvironment environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Registers a one-shot fault. The target resource must already be added to the environment, and a registered
    /// resource must declare a matching <see cref="FaultPoint"/>; otherwise the rule is rejected instead of never firing.
    /// </summary>
    public ActiveFault Add(FaultRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Provider, nameof(rule));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Operation, nameof(rule));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.Phase, nameof(rule));
        ArgumentOutOfRangeException.ThrowIfLessThan(rule.Occurrence, 1, nameof(rule));
        _environment.ThrowIfDisposed();

        var point = new FaultPoint(rule.Provider, rule.Operation, rule.Phase);
        var declared = _environment.Resources.Any(resource =>
            resource.Provider == rule.Provider &&
            (rule.Resource is null || resource.Name == rule.Resource) &&
            resource.FaultPoints.Contains(point));
        if (!declared)
        {
            var target = rule.Resource is null ? $"any '{rule.Provider}' resource" : $"resource '{rule.Resource}'";
            throw new UnsupportedCapabilityException(
                $"{rule.Provider}.faults",
                $"No registered {target} declares fault point '{point}'. Add the resource before its fault rules and use a declared operation/phase");
        }

        lock (_gate)
        {
            if (_rules.Any(existing => !existing.HasFired && existing.Rule == rule))
            {
                throw new ArgumentException($"An identical unfired fault rule is already registered: {rule}.", nameof(rule));
            }

            var active = new ActiveFault($"fault-{(_rules.Count + 1).ToString(CultureInfo.InvariantCulture)}", rule);
            _rules.Add(active);
            return active;
        }
    }

    public ImmutableArray<ActiveFault> Rules
    {
        get
        {
            lock (_gate)
            {
                return [.. _rules];
            }
        }
    }

    public ImmutableArray<FaultReport> GetFiredFaults()
    {
        lock (_gate)
        {
            return [.. _fired];
        }
    }

    public ImmutableArray<UnfiredFault> GetUnfiredFaults()
    {
        lock (_gate)
        {
            return [.. _rules.Where(rule => !rule.HasFired).Select(rule => new UnfiredFault(rule.Id, rule.Rule, rule.Matches))];
        }
    }

    /// <summary>
    /// Evaluates the rules at a fault point and returns the report of the rule that fired, if any. The provider decides
    /// how to surface the fault, which is normally by throwing <see cref="SimulatedFaultException"/>.
    /// <paramref name="stateChanged"/> states whether the operation has already changed state at this point.
    /// </summary>
    public FaultReport? Evaluate(ISimulationResource resource, string operation, string phase, bool stateChanged)
    {
        ArgumentNullException.ThrowIfNull(resource);
        FaultReport? report = null;
        lock (_gate)
        {
            ActiveFault? firing = null;
            foreach (var rule in _rules)
            {
                if (rule.HasFired || !Matches(rule.Rule, resource, operation, phase))
                {
                    continue;
                }

                rule.Matches++;
                if (firing is null && rule.Matches >= rule.Rule.Occurrence)
                {
                    firing = rule;
                }
            }

            if (firing is not null)
            {
                report = new FaultReport(
                    firing.Id,
                    resource.Provider,
                    resource.Name,
                    operation,
                    phase,
                    firing.Matches,
                    stateChanged,
                    _environment.Scheduler.Now,
                    _environment.Journal.CurrentCorrelationId,
                    firing.Rule.Reason);
                firing.Report = report;
                _fired.Add(report);
            }
        }

        if (report is not null)
        {
            _environment.Journal.Record(
                report.Provider,
                report.Resource,
                operation,
                phase,
                OperationOutcome.Faulted,
                details: $"rule={report.RuleId}; stateChanged={(stateChanged ? "true" : "false")}",
                error: report.Reason ?? "injected fault");
        }

        return report;
    }

    /// <summary>Evaluates the rules and throws <see cref="SimulatedFaultException"/> when one fires.</summary>
    public void ThrowIfTriggered(ISimulationResource resource, string operation, string phase, bool stateChanged)
    {
        if (Evaluate(resource, operation, phase, stateChanged) is { } report)
        {
            throw new SimulatedFaultException(report);
        }
    }

    private static bool Matches(FaultRule rule, ISimulationResource resource, string operation, string phase) =>
        rule.Provider == resource.Provider &&
        (rule.Resource is null || rule.Resource == resource.Name) &&
        rule.Operation == operation &&
        rule.Phase == phase;
}
