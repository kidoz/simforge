using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Core.Tests;

public sealed class FaultInjectorTests
{
    private static FaultRule Rule(int occurrence = 1, string phase = FaultPhases.Before, string? resource = null) => new()
    {
        Provider = RecordingResource.ProviderName,
        Resource = resource,
        Operation = RecordingResource.Operation,
        Phase = phase,
        Occurrence = occurrence,
    };

    [Fact]
    public async Task Rule_fires_exactly_once_at_the_configured_matching_occurrence()
    {
        await using var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("db"));
        var fault = environment.Faults.Add(Rule(occurrence: 3));

        var results = Enumerable.Range(0, 5)
            .Select(_ => environment.Faults.Evaluate(resource, RecordingResource.Operation, FaultPhases.Before, stateChanged: false))
            .ToList();

        Assert.Equal([false, false, true, false, false], results.Select(report => report is not null));
        Assert.True(fault.HasFired);
        Assert.Equal(3, fault.Report!.Occurrence);
        Assert.Equal(fault.Id, results[2]!.RuleId);
    }

    [Fact]
    public async Task Evaluations_at_other_operations_phases_or_resources_do_not_count()
    {
        await using var environment = new SimulationEnvironment();
        var target = environment.AddResource(new RecordingResource("target"));
        var other = environment.AddResource(new RecordingResource("other"));
        var fault = environment.Faults.Add(Rule(occurrence: 2, resource: "target"));

        Assert.Null(environment.Faults.Evaluate(other, RecordingResource.Operation, FaultPhases.Before, false));
        Assert.Null(environment.Faults.Evaluate(target, RecordingResource.Operation, FaultPhases.After, true));
        Assert.Null(environment.Faults.Evaluate(target, "another-op", FaultPhases.Before, false));
        Assert.Equal(0, fault.Matches);

        Assert.Null(environment.Faults.Evaluate(target, RecordingResource.Operation, FaultPhases.Before, false));
        Assert.NotNull(environment.Faults.Evaluate(target, RecordingResource.Operation, FaultPhases.Before, false));
    }

    [Fact]
    public async Task Report_distinguishes_an_operation_that_did_not_happen_from_a_lost_success_response()
    {
        await using var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("db"));
        environment.Faults.Add(Rule(phase: FaultPhases.Before));
        environment.Faults.Add(Rule(phase: FaultPhases.After));

        var rejected = Assert.Throws<SimulatedFaultException>(
            () => environment.Faults.ThrowIfTriggered(resource, RecordingResource.Operation, FaultPhases.Before, stateChanged: false));
        var ambiguous = Assert.Throws<SimulatedFaultException>(
            () => environment.Faults.ThrowIfTriggered(resource, RecordingResource.Operation, FaultPhases.After, stateChanged: true));

        Assert.False(rejected.StateChanged);
        Assert.Contains("was not applied", rejected.Message, StringComparison.Ordinal);
        Assert.True(ambiguous.StateChanged);
        Assert.Contains("success response was lost", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal(["fault-1", "fault-2"], environment.Faults.GetFiredFaults().Select(report => report.RuleId));
        Assert.Equal(2, environment.Journal.GetEntries().Count(entry => entry.Outcome == OperationOutcome.Faulted));
    }

    [Fact]
    public async Task Rules_for_undeclared_points_or_unknown_resources_are_rejected_instead_of_never_firing()
    {
        await using var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("db"));

        Assert.Throws<UnsupportedCapabilityException>(() => environment.Faults.Add(Rule() with { Operation = "typo" }));
        Assert.Throws<UnsupportedCapabilityException>(() => environment.Faults.Add(Rule() with { Phase = "during" }));
        Assert.Throws<UnsupportedCapabilityException>(() => environment.Faults.Add(Rule(resource: "missing")));
        Assert.Throws<UnsupportedCapabilityException>(() => environment.Faults.Add(Rule() with { Provider = "other" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.Faults.Add(Rule(occurrence: 0)));
    }

    [Fact]
    public async Task Identical_unfired_rules_are_rejected_but_different_occurrences_are_independent()
    {
        await using var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("db"));
        var first = environment.Faults.Add(Rule(occurrence: 1));
        var third = environment.Faults.Add(Rule(occurrence: 3));

        Assert.Throws<ArgumentException>(() => environment.Faults.Add(Rule(occurrence: 1)));

        var fired = Enumerable.Range(0, 3)
            .Select(_ => environment.Faults.Evaluate(resource, RecordingResource.Operation, FaultPhases.Before, false)?.RuleId)
            .ToList();
        Assert.Equal([first.Id, null, third.Id], fired);
    }

    [Fact]
    public async Task Earliest_registered_rule_wins_and_a_rule_due_at_the_same_evaluation_fires_at_its_next_match()
    {
        await using var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("db"));
        var specific = environment.Faults.Add(Rule(resource: "db"));
        var wildcard = environment.Faults.Add(Rule());

        var first = environment.Faults.Evaluate(resource, RecordingResource.Operation, FaultPhases.Before, false);
        var second = environment.Faults.Evaluate(resource, RecordingResource.Operation, FaultPhases.Before, false);

        Assert.Equal(specific.Id, first!.RuleId);
        Assert.Equal(wildcard.Id, second!.RuleId);
        Assert.Equal(2, second.Occurrence);
    }

    [Fact]
    public async Task Unfired_rules_are_visible_in_diagnostics()
    {
        await using var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("db"));
        var fault = environment.Faults.Add(Rule(occurrence: 2) with { Reason = "second call fails" });

        var diagnostics = environment.CaptureDiagnostics();

        var unfired = Assert.Single(diagnostics.UnfiredFaults);
        Assert.Equal(fault.Id, unfired.RuleId);
        Assert.Contains("Unfired faults", diagnostics.FormatSummary(), StringComparison.Ordinal);
    }
}
