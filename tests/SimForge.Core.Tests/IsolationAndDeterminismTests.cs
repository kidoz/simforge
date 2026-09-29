using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Core.Tests;

public sealed class IsolationAndDeterminismTests
{
    [Fact]
    public async Task Same_seed_produces_the_same_identifier_sequence_and_different_seeds_differ()
    {
        await using var first = new SimulationEnvironment(new SimulationEnvironmentOptions { Seed = 123 });
        await using var second = new SimulationEnvironment(new SimulationEnvironmentOptions { Seed = 123 });
        await using var other = new SimulationEnvironment(new SimulationEnvironmentOptions { Seed = 124 });

        var firstIds = Enumerable.Range(0, 20).Select(_ => first.Ids.NewGuid()).ToList();
        var secondIds = Enumerable.Range(0, 20).Select(_ => second.Ids.NewGuid()).ToList();
        var otherIds = Enumerable.Range(0, 20).Select(_ => other.Ids.NewGuid()).ToList();

        Assert.Equal(firstIds, secondIds);
        Assert.Empty(firstIds.Intersect(otherIds));
    }

    [Fact]
    public async Task Generated_guids_are_unique_version_8_uuids()
    {
        await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { Seed = 5 });

        var ids = Enumerable.Range(0, 10_000).Select(_ => environment.Ids.NewGuid()).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(8, id.Version);
            Assert.Equal('8', id.ToString()[14]);
            Assert.Contains(id.ToString()[19..20], "89ab", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Named_sequences_start_at_one_and_are_independent()
    {
        await using var environment = new SimulationEnvironment();

        Assert.Equal(1, environment.Ids.NextValue("orders"));
        Assert.Equal(2, environment.Ids.NextValue("orders"));
        Assert.Equal(1, environment.Ids.NextValue("invoices"));
    }

    [Fact]
    public async Task Parallel_environments_with_identical_resource_names_share_no_state()
    {
        async Task<(string[] Journal, DateTimeOffset Now, Guid FirstId)> RunAsync(string scenarioId, TimeSpan advance)
        {
            await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { ScenarioId = scenarioId, Seed = 1 });
            environment.AddResource(new RecordingResource("db"));
            await environment.InitializeAsync(TestContext.Current.CancellationToken);
            for (var step = 0; step < 50; step++)
            {
                environment.Scheduler.Schedule($"{scenarioId}-{step}", TimeSpan.FromMilliseconds(step), () => { });
                await Task.Yield();
            }

            await environment.Scheduler.AdvanceByAsync(advance, TestContext.Current.CancellationToken);
            return ([.. environment.Journal.GetEntries().Select(entry => $"{entry.ScenarioId}:{entry.Operation}")], environment.Clock.GetUtcNow(), environment.Ids.NewGuid());
        }

        var results = await Task.WhenAll(
            Task.Run(() => RunAsync("left", TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken),
            Task.Run(() => RunAsync("right", TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken));

        Assert.All(results[0].Journal, entry => Assert.StartsWith("left:", entry, StringComparison.Ordinal));
        Assert.All(results[1].Journal, entry => Assert.StartsWith("right:", entry, StringComparison.Ordinal));
        Assert.Equal(51, results[0].Journal.Length);
        Assert.Equal(SimulationEnvironmentOptions.DefaultStartTime.AddSeconds(1), results[0].Now);
        Assert.Equal(SimulationEnvironmentOptions.DefaultStartTime.AddSeconds(2), results[1].Now);
        Assert.Equal(results[0].FirstId, results[1].FirstId);
    }
}
