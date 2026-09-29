using System.Text;
using Xunit;

namespace SimForge.Kafka.Tests;

/// <summary>
/// Seeded random walks over produce, poll, commit, join, leave, and scheduler drives, checked against an independent
/// model after every step. The invariants are:
/// - reads continue exactly from each member's position, with no gaps or skips;
/// - rebalances reset positions to committed offsets;
/// - committed offsets change only through commits;
/// - every partition is owned by exactly one member after a rebalance;
/// - everything below a committed offset has been delivered at least once.
/// </summary>
public sealed class ConsumerStateMachineTests
{
    private const int Steps = 250;
    private const int Partitions = 3;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 30)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Positions_commits_and_assignments_match_the_model(int seed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var random = new Random(seed);
        var history = new StringBuilder();
        await using var test = await TestCluster.CreateAsync(
            new SimulationEnvironmentOptions { ScenarioId = "kafka-state-machine", Seed = seed },
            cancellationToken);

        var logEnd = new long[Partitions];
        var committed = new Dictionary<int, long>();
        var delivered = new HashSet<(int Partition, long Offset)>();
        var members = new List<KafkaConsumer>();
        var positions = new Dictionary<KafkaConsumer, Dictionary<int, long>>();
        var rebalancePending = false;

        for (var step = 0; step < Steps; step++)
        {
            history.Append(step).Append(": ");
            switch (random.Next(9))
            {
                case 0 or 1:
                    var partition = random.Next(Partitions);
                    history.Append("produce p").Append(partition);
                    test.Produce(partition);
                    logEnd[partition]++;
                    break;
                case 2 or 3 when members.Count > 0:
                    var reader = members[random.Next(members.Count)];
                    var max = random.Next(1, 5);
                    history.Append("poll ").Append(reader.MemberId).Append(" max ").Append(max);

                    // A poll resolves the position of every assigned partition: the committed offset, else 0 (Earliest).
                    foreach (var owned in reader.Assignment)
                    {
                        positions[reader].TryAdd(owned.Partition, committed.GetValueOrDefault(owned.Partition));
                    }

                    foreach (var record in reader.Poll(max))
                    {
                        var expected = positions[reader].TryGetValue(record.Partition, out var position) ? position : committed.GetValueOrDefault(record.Partition);
                        Assert.True(record.Offset == expected, Fail($"read p{record.Partition}@{record.Offset}, expected @{expected}"));
                        positions[reader][record.Partition] = expected + 1;
                        delivered.Add((record.Partition, record.Offset));
                    }

                    break;
                case 4 when members.Count > 0:
                    var committer = members[random.Next(members.Count)];
                    history.Append("commit ").Append(committer.MemberId);
                    committer.Commit();
                    foreach (var (owned, position) in positions[committer])
                    {
                        committed[owned] = position;
                    }

                    break;
                case 5 when members.Count < 3:
                    var joined = test.Cluster.JoinGroup("g", ["orders"], KafkaOffsetReset.Earliest);
                    history.Append("join ").Append(joined.MemberId);
                    members.Add(joined);
                    positions[joined] = [];
                    rebalancePending = true;
                    break;
                case 6 when members.Count > 0:
                    var leaving = members[random.Next(members.Count)];
                    history.Append("leave ").Append(leaving.MemberId);
                    leaving.Close();
                    members.Remove(leaving);
                    positions.Remove(leaving);
                    rebalancePending = true;
                    break;
                default:
                    history.Append("drive");
                    await test.Scheduler.RunUntilIdleAsync(cancellationToken);
                    if (rebalancePending)
                    {
                        rebalancePending = false;
                        foreach (var member in members)
                        {
                            positions[member].Clear();
                        }

                        CheckAssignment();
                    }

                    break;
            }

            history.AppendLine();
            CheckInvariants();
        }

        string Fail(string message) => $"Seed {seed}: {message}{Environment.NewLine}{history}";

        void CheckAssignment()
        {
            var owners = members.SelectMany(member => member.Assignment.Select(owned => owned.Partition)).ToList();
            Assert.True(owners.Count == owners.Distinct().Count(), Fail("a partition is assigned to more than one member"));
            Assert.True(members.Count == 0 || owners.Count == Partitions, Fail("a partition has no owner"));
            foreach (var member in members)
            {
                positions[member] = positions[member].Where(pair => member.Assignment.Any(owned => owned.Partition == pair.Key)).ToDictionary();
            }
        }

        void CheckInvariants()
        {
            var group = members.Count > 0 || committed.Count > 0 ? test.Cluster.GetGroup("g") : null;
            var actualCommitted = group?.CommittedOffsets.ToDictionary(pair => pair.Key.Partition, pair => pair.Value) ?? [];
            Assert.True(actualCommitted.Count == committed.Count && committed.All(pair => actualCommitted.GetValueOrDefault(pair.Key, -1) == pair.Value), Fail("committed offsets differ from the model"));
            foreach (var (partition, offset) in committed)
            {
                Assert.True(offset <= logEnd[partition], Fail($"committed offset {offset} of p{partition} is beyond the log end"));
                for (var below = 0L; below < offset; below++)
                {
                    Assert.True(delivered.Contains((partition, below)), Fail($"p{partition}@{below} is below the committed offset but was never delivered"));
                }
            }

            for (var partition = 0; partition < Partitions; partition++)
            {
                Assert.True(test.Cluster.GetPartition("orders", partition).LogEndOffset == logEnd[partition], Fail($"log end of p{partition} differs"));
            }
        }
    }
}
