using System.Collections.Immutable;

namespace SimForge.Kafka;

public sealed record TopicDefinition(string Name, int Partitions);

/// <summary>Immutable cluster topology, fixed when the cluster is added to an environment.</summary>
public sealed class KafkaTopology
{
    internal KafkaTopology(ImmutableArray<TopicDefinition> topics)
    {
        Topics = topics;
    }

    public ImmutableArray<TopicDefinition> Topics { get; }
}

/// <summary>Declares the topics of a simulated cluster.</summary>
public sealed class KafkaTopologyBuilder
{
    // Kafka's legal topic name characters and length limit.
    private const int MaxNameLength = 249;

    private readonly List<TopicDefinition> _topics = [];

    /// <summary>Declares a topic with a fixed number of partitions (at least 1).</summary>
    public KafkaTopologyBuilder Topic(string name, int partitions)
    {
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitions, 1);
        if (_topics.Exists(topic => topic.Name == name))
        {
            throw new ArgumentException($"Topic '{name}' is declared twice.", nameof(name));
        }

        _topics.Add(new TopicDefinition(name, partitions));
        return this;
    }

    internal KafkaTopology Build()
    {
        if (_topics.Count == 0)
        {
            throw new ArgumentException("A simulated cluster needs at least one topic.");
        }

        return new KafkaTopology([.. _topics]);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var legal = name.Length <= MaxNameLength &&
                    name is not "." and not ".." &&
                    name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
        if (!legal)
        {
            throw new ArgumentException($"Topic name '{name}' is not legal: use at most {MaxNameLength} ASCII letters, digits, '.', '_' or '-'.", nameof(name));
        }

        if (name.StartsWith("__", StringComparison.Ordinal))
        {
            throw new UnsupportedCapabilityException(
                KafkaCapabilityIds.TopicManagement,
                $"Topic name '{name}' starts with '__', which Kafka reserves for internal topics");
        }
    }
}
