using System.Globalization;
using System.Text;
using Xunit;

namespace SimForge.RabbitMq.Tests;

/// <summary>
/// Seeded random walks over publish, deliver, settle, and channel-close operations, checked against an independent
/// model after every step. The invariants are: each published message is in exactly one state; acknowledged messages are
/// never delivered again; the redelivered flag is set exactly for messages delivered before; ready messages stay in
/// publish order, because requeued messages return to their original position; and no consumer exceeds its prefetch.
/// </summary>
public sealed class DeliveryStateMachineTests
{
    private const int Steps = 250;

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 30)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Messages_are_conserved_and_never_redelivered_after_ack(int seed)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var random = new Random(seed);
        var history = new StringBuilder();
        await using var test = await TestBroker.CreateAsync(
            options: new SimulationEnvironmentOptions { ScenarioId = "rabbitmq-state-machine", Seed = seed },
            cancellationToken: cancellationToken);

        var ready = new HashSet<string>();
        var acked = new HashSet<string>();
        var dead = new HashSet<string>();
        var deliveredBefore = new HashSet<string>();
        var inflight = new List<RabbitMqDelivery>();
        var consumers = new List<(RabbitMqChannel Channel, ushort Prefetch)>();
        var published = 0;
        string? violation = null;

        void OpenConsumer()
        {
            var prefetch = (ushort)random.Next(1, 4);
            var channel = test.Broker.OpenChannel();
            channel.Consume("work", prefetch, (delivery, _) =>
            {
                var id = delivery.Message.MessageId;
                if (!ready.Remove(id))
                {
                    violation ??= $"{id} was delivered but was not ready (acked: {acked.Contains(id)})";
                }

                if (delivery.Redelivered != deliveredBefore.Contains(id))
                {
                    violation ??= $"{id} has redelivered={delivery.Redelivered} but was delivered before: {deliveredBefore.Contains(id)}";
                }

                deliveredBefore.Add(id);
                inflight.Add(delivery);
                return ValueTask.CompletedTask;
            });
            consumers.Add((channel, prefetch));
        }

        OpenConsumer();
        OpenConsumer();
        for (var step = 0; step < Steps; step++)
        {
            var action = random.Next(8);
            history.Append(step).Append(": ");
            switch (action)
            {
                case 0 or 1:
                    var id = $"m-{++published}";
                    history.Append("publish ").Append(id);
                    test.Send("work", id);
                    ready.Add(id);
                    break;
                case 2 or 3:
                    history.Append("drive");
                    await test.Scheduler.RunUntilIdleAsync(cancellationToken);
                    break;
                case 4 or 5 when inflight.Count > 0:
                    var delivery = inflight[random.Next(inflight.Count)];
                    inflight.Remove(delivery);
                    var messageId = delivery.Message.MessageId;
                    switch (random.Next(3))
                    {
                        case 0:
                            history.Append("ack ").Append(messageId);
                            delivery.Ack();
                            acked.Add(messageId);
                            break;
                        case 1:
                            history.Append("requeue ").Append(messageId);
                            delivery.Nack(requeue: true);
                            ready.Add(messageId);
                            break;
                        default:
                            history.Append("reject ").Append(messageId);
                            delivery.Reject(requeue: false);
                            dead.Add(messageId);
                            break;
                    }

                    break;
                case 6 when inflight.Count > 0:
                    var anchor = inflight[random.Next(inflight.Count)];
                    history.Append("ack-multiple ch").Append(anchor.Channel.Number).Append(" tag ").Append(anchor.DeliveryTag);
                    anchor.Channel.Ack(anchor.DeliveryTag, multiple: true);
                    foreach (var settled in inflight.Where(candidate => candidate.Channel == anchor.Channel && candidate.DeliveryTag <= anchor.DeliveryTag).ToList())
                    {
                        inflight.Remove(settled);
                        acked.Add(settled.Message.MessageId);
                    }

                    break;
                case 7:
                    var (channel, _) = consumers[random.Next(consumers.Count)];
                    history.Append("close ch").Append(channel.Number);
                    channel.Close();
                    foreach (var requeued in inflight.Where(candidate => candidate.Channel == channel).ToList())
                    {
                        inflight.Remove(requeued);
                        ready.Add(requeued.Message.MessageId);
                    }

                    consumers.RemoveAll(consumer => consumer.Channel == channel);
                    OpenConsumer();
                    break;
                default:
                    history.Append("noop");
                    break;
            }

            history.AppendLine();
            var failure = violation ?? Check();
            Assert.True(failure is null, $"Seed {seed}, step {step}: {failure}{Environment.NewLine}{history}");
        }

        string? Check()
        {
            var work = test.Broker.GetQueue("work");
            var readyIds = work.ReadyMessages.Select(message => message.MessageId).ToList();
            if (!readyIds.ToHashSet().SetEquals(ready) || work.UnackedCount != inflight.Count)
            {
                return $"queue has ready [{string.Join(",", readyIds)}] and {work.UnackedCount} unacked; model has ready [{string.Join(",", ready)}] and {inflight.Count} in flight";
            }

            var order = readyIds.Select(id => int.Parse(id[2..], CultureInfo.InvariantCulture)).ToList();
            if (!order.SequenceEqual(order.Order()))
            {
                return $"ready messages are out of publish order: [{string.Join(",", readyIds)}]";
            }

            if (!test.Broker.GetQueue("work.dead").ReadyMessages.Select(message => message.MessageId).ToHashSet().SetEquals(dead))
            {
                return "dead-letter queue differs from the model";
            }

            if (ready.Count + inflight.Count + acked.Count + dead.Count != published || acked.Overlaps(ready) || acked.Overlaps(dead))
            {
                return "a message is lost, duplicated, or in two states at once";
            }

            var overPrefetch = consumers.FirstOrDefault(consumer => inflight.Count(delivery => delivery.Channel == consumer.Channel) > consumer.Prefetch);
            return overPrefetch.Channel is null ? null : $"channel {overPrefetch.Channel.Number} exceeds its prefetch of {overPrefetch.Prefetch}";
        }
    }
}
