using System.Text.Json;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Core.Tests;

public sealed class JournalAndDiagnosticsTests
{
    [Fact]
    public async Task Entries_are_ordered_and_carry_scenario_seed_virtual_time_and_outcome()
    {
        await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { ScenarioId = "journal", Seed = 42 });
        environment.Journal.Record("p", "r", "first", FaultPhases.After, OperationOutcome.Succeeded);
        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        environment.Journal.Record("p", "r", "second", FaultPhases.Before, OperationOutcome.Rejected, target: "orders", error: "nope");

        var entries = environment.Journal.GetEntries();

        Assert.Equal([1L, 2L], entries.Select(entry => entry.Sequence));
        Assert.All(entries, entry => Assert.Equal(("journal", 42), (entry.ScenarioId, entry.Seed)));
        Assert.Equal(SimulationEnvironmentOptions.DefaultStartTime.AddSeconds(3), entries[1].VirtualTime);
        Assert.Equal((OperationOutcome.Rejected, "orders", "nope"), (entries[1].Outcome, entries[1].Target, entries[1].Error));
    }

    [Fact]
    public async Task Snapshots_are_immutable()
    {
        await using var environment = new SimulationEnvironment();
        environment.Journal.Record("p", "r", "one", FaultPhases.After, OperationOutcome.Succeeded);
        var snapshot = environment.Journal.GetEntries();
        var diagnostics = environment.CaptureDiagnostics();

        environment.Journal.Record("p", "r", "two", FaultPhases.After, OperationOutcome.Succeeded);

        Assert.Single(snapshot);
        Assert.Single(diagnostics.Journal);
        Assert.Equal(2, environment.Journal.Count);
    }

    [Fact]
    public async Task Correlation_scopes_nest_flow_across_awaits_and_do_not_leak_to_other_flows()
    {
        await using var environment = new SimulationEnvironment();
        var journal = environment.Journal;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallelFlow = Task.Run(async () =>
        {
            await signal.Task;
            return journal.CurrentCorrelationId;
        }, TestContext.Current.CancellationToken);
        string? observedInParallelFlow;

        using (journal.BeginCorrelation("outer"))
        {
            await Task.Yield();
            using (journal.BeginCorrelation("inner"))
            {
                journal.Record("p", "r", "inner-op", FaultPhases.After, OperationOutcome.Succeeded);
            }

            journal.Record("p", "r", "outer-op", FaultPhases.After, OperationOutcome.Succeeded);
            signal.SetResult();
            observedInParallelFlow = await parallelFlow;
        }

        journal.Record("p", "r", "none", FaultPhases.After, OperationOutcome.Succeeded);

        Assert.Equal(["inner", "outer", null], journal.GetEntries().Select(entry => entry.CorrelationId));
        Assert.Null(observedInParallelFlow);
    }

    [Fact]
    public async Task Payloads_are_not_captured_or_even_evaluated_unless_enabled()
    {
        await using var quiet = new SimulationEnvironment();
        await using var capturing = new SimulationEnvironment(new SimulationEnvironmentOptions { CapturePayloads = true });
        var evaluations = 0;
        string Payload()
        {
            evaluations++;
            return "secret=value";
        }

        var withoutCapture = quiet.Journal.Record("p", "r", "op", FaultPhases.After, OperationOutcome.Succeeded, payload: Payload);
        var withCapture = capturing.Journal.Record("p", "r", "op", FaultPhases.After, OperationOutcome.Succeeded, payload: Payload);

        Assert.Null(withoutCapture.Payload);
        Assert.Equal("secret=value", withCapture.Payload);
        Assert.Equal(1, evaluations);
    }

    [Fact]
    public async Task Diagnostic_snapshot_renders_json_and_summary_and_exports_to_the_selected_directory()
    {
        await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { ScenarioId = "orders/export", Seed = 7 });
        var resource = environment.AddResource(new RecordingResource("db"));
        environment.Faults.Add(new FaultRule { Provider = RecordingResource.ProviderName, Operation = RecordingResource.Operation, Reason = "planned" });
        environment.Faults.Evaluate(resource, RecordingResource.Operation, FaultPhases.Before, stateChanged: false);
        environment.Scheduler.Schedule("later", TimeSpan.FromMinutes(1), () => { });
        var diagnostics = environment.CaptureDiagnostics();

        using var json = JsonDocument.Parse(diagnostics.ToJson());
        Assert.Equal("orders/export", json.RootElement.GetProperty("scenarioId").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("seed").GetInt32());
        Assert.False(json.RootElement.GetProperty("firedFaults")[0].GetProperty("stateChanged").GetBoolean());
        Assert.Equal("later", json.RootElement.GetProperty("pendingWork")[0].GetProperty("name").GetString());

        var summary = diagnostics.FormatSummary();
        Assert.Contains("seed 7", summary, StringComparison.Ordinal);
        Assert.Contains("fault-1", summary, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "simforge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = await diagnostics.ExportAsync(directory, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(directory, Path.GetDirectoryName(path));
            Assert.Equal("orders_export.seed-7.diagnostics.json", Path.GetFileName(path));
            Assert.Equal(diagnostics.ToJson(), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => diagnostics.ExportAsync(directory, "../escape.json", TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
