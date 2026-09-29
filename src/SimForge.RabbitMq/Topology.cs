using System.Collections.Immutable;
using System.Text;

namespace SimForge.RabbitMq;

/// <summary>Exchange types. Only <see cref="Direct"/> and <see cref="Fanout"/> are modeled.</summary>
public enum ExchangeType
{
    /// <summary>Routes to queues bound with a routing key equal to the message's routing key.</summary>
    Direct,

    /// <summary>Routes to every bound queue and ignores the routing key.</summary>
    Fanout,

    /// <summary>Not supported; declaring one throws <see cref="UnsupportedCapabilityException"/>.</summary>
    Topic,

    /// <summary>Not supported; declaring one throws <see cref="UnsupportedCapabilityException"/>.</summary>
    Headers,
}

public sealed record ExchangeDefinition(string Name, ExchangeType Type);

/// <summary>A queue. <see cref="DeadLetterExchange"/> of <c>""</c> means the default exchange; null disables dead-lettering.</summary>
public sealed record QueueDefinition(string Name, string? DeadLetterExchange, string? DeadLetterRoutingKey);

public sealed record BindingDefinition(string Queue, string Exchange, string RoutingKey);

/// <summary>Immutable broker topology, fixed when the broker is added to an environment.</summary>
public sealed class RabbitMqTopology
{
    /// <summary>Name of the default exchange, to which every queue is implicitly bound with its own name as routing key.</summary>
    public const string DefaultExchange = "";

    internal RabbitMqTopology(ImmutableArray<ExchangeDefinition> exchanges, ImmutableArray<QueueDefinition> queues, ImmutableArray<BindingDefinition> bindings)
    {
        Exchanges = exchanges;
        Queues = queues;
        Bindings = bindings;
    }

    /// <summary>Declared exchanges, excluding the implicit default exchange.</summary>
    public ImmutableArray<ExchangeDefinition> Exchanges { get; }

    public ImmutableArray<QueueDefinition> Queues { get; }

    /// <summary>Explicit bindings, in declaration order.</summary>
    public ImmutableArray<BindingDefinition> Bindings { get; }
}

/// <summary>Declares the exchanges, queues, and bindings of a simulated broker.</summary>
public sealed class RabbitMqTopologyBuilder
{
    private readonly List<ExchangeDefinition> _exchanges = [];
    private readonly List<QueueDefinition> _queues = [];
    private readonly List<BindingDefinition> _bindings = [];

    public RabbitMqTopologyBuilder Exchange(string name, ExchangeType type)
    {
        Names.Validate(name, "exchange");
        if (type is ExchangeType.Topic or ExchangeType.Headers)
        {
            throw new UnsupportedCapabilityException(
                type == ExchangeType.Topic ? RabbitMqCapabilityIds.TopicRouting : RabbitMqCapabilityIds.HeadersRouting,
                $"Exchange '{name}' is a {type} exchange; only direct and fanout exchanges are modeled");
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown exchange type.");
        }

        if (_exchanges.Exists(exchange => exchange.Name == name))
        {
            throw new ArgumentException($"Exchange '{name}' is declared twice.", nameof(name));
        }

        _exchanges.Add(new ExchangeDefinition(name, type));
        return this;
    }

    public RabbitMqTopologyBuilder Queue(string name, Action<QueueBuilder>? configure = null)
    {
        Names.Validate(name, "queue");
        if (_queues.Exists(queue => queue.Name == name))
        {
            throw new ArgumentException($"Queue '{name}' is declared twice.", nameof(name));
        }

        var builder = new QueueBuilder(name);
        configure?.Invoke(builder);
        _queues.Add(builder.Build());
        return this;
    }

    /// <summary>
    /// Binds a queue to a declared exchange. Both must already be declared. Binding to the default exchange is not
    /// allowed (it is implicit), and a repeated binding is ignored.
    /// </summary>
    public RabbitMqTopologyBuilder Bind(string queue, string exchange, string routingKey = "")
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        if (exchange == RabbitMqTopology.DefaultExchange)
        {
            throw new ArgumentException("Queues are bound to the default exchange implicitly; explicit bindings to it are not allowed.", nameof(exchange));
        }

        if (!_queues.Exists(definition => definition.Name == queue))
        {
            throw new ArgumentException($"Queue '{queue}' is not declared.", nameof(queue));
        }

        if (!_exchanges.Exists(definition => definition.Name == exchange))
        {
            throw new ArgumentException($"Exchange '{exchange}' is not declared.", nameof(exchange));
        }

        Names.ValidateRoutingKey(routingKey);
        var binding = new BindingDefinition(queue, exchange, routingKey);
        if (!_bindings.Contains(binding))
        {
            _bindings.Add(binding);
        }

        return this;
    }

    internal RabbitMqTopology Build()
    {
        if (_queues.Count == 0)
        {
            throw new ArgumentException("A simulated broker needs at least one queue.");
        }

        return new RabbitMqTopology([.. _exchanges], [.. _queues], [.. _bindings]);
    }
}

/// <summary>Configures one queue.</summary>
public sealed class QueueBuilder
{
    private readonly string _name;
    private string? _deadLetterExchange;
    private string? _deadLetterRoutingKey;

    internal QueueBuilder(string name)
    {
        _name = name;
    }

    /// <summary>
    /// Dead-letters rejected messages (reject or nack with <c>requeue: false</c>) to <paramref name="exchange"/>, using
    /// <paramref name="routingKey"/> or, when null, the message's current routing key. The exchange does not have to be
    /// declared; messages dead-lettered to a missing exchange are dropped, as in RabbitMQ.
    /// </summary>
    public QueueBuilder DeadLetterTo(string exchange, string? routingKey = null)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (exchange.Length > 0)
        {
            Names.Validate(exchange, "dead-letter exchange");
        }

        if (routingKey is not null)
        {
            Names.ValidateRoutingKey(routingKey);
        }

        _deadLetterExchange = exchange;
        _deadLetterRoutingKey = routingKey;
        return this;
    }

    internal QueueDefinition Build() => new(_name, _deadLetterExchange, _deadLetterRoutingKey);
}

internal static class Names
{
    // AMQP 0-9-1 short strings are limited to 255 bytes.
    private const int MaxBytes = 255;

    public static void Validate(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Encoding.UTF8.GetByteCount(name) > MaxBytes)
        {
            throw new ArgumentException($"The {kind} name '{name}' exceeds {MaxBytes} UTF-8 bytes.", nameof(name));
        }

        if (name.StartsWith("amq.", StringComparison.Ordinal))
        {
            throw new UnsupportedCapabilityException(
                RabbitMqCapabilityIds.ReservedNames,
                $"The {kind} name '{name}' uses the reserved 'amq.' prefix; predeclared and server-named entities are not modeled");
        }
    }

    public static void ValidateRoutingKey(string routingKey)
    {
        if (Encoding.UTF8.GetByteCount(routingKey) > MaxBytes)
        {
            throw new ArgumentException($"Routing key '{routingKey}' exceeds {MaxBytes} UTF-8 bytes.", nameof(routingKey));
        }
    }
}
