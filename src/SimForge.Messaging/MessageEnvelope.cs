using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace SimForge.Messaging;

/// <summary>
/// Immutable message shared by SimForge broker models. The body is copied when the envelope is created and on every
/// read, so neither the producer nor any consumer can change a stored or delivered message.
/// </summary>
/// <remarks>
/// Use <c>with</c> to derive a modified copy, for example <c>envelope with { CorrelationId = "order:42" }</c>. Equality
/// compares every property, the headers, and the body bytes.
/// </remarks>
public sealed record MessageEnvelope
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions DefaultJsonOptions = CreateDefaultJsonOptions();
    private readonly byte[] _body;

    public MessageEnvelope(string messageId, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        MessageId = messageId;
        _body = body.ToArray();
    }

    /// <summary>Identifier of the message, chosen by the producer.</summary>
    public string MessageId { get; }

    /// <summary>Logical message type, for example <c>orders.order-placed.v1</c>.</summary>
    public string? Type { get; init; }

    /// <summary>MIME content type of the body.</summary>
    public string? ContentType { get; init; }

    /// <summary>Correlation ID; broker models also record it on their journal entries.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Producer-supplied timestamp, stored in UTC. Brokers do not stamp messages.</summary>
    public DateTimeOffset? Timestamp
    {
        get;
        init => field = value?.ToUniversalTime();
    }

    /// <summary>String headers, ordered by ordinal key. Assigning copies the dictionary.</summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get;
        init => field = NormalizeHeaders(value);
    } = ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    public int BodyLength => _body.Length;

    /// <summary>Creates a UTF-8 text message with content type <c>text/plain; charset=utf-8</c>.</summary>
    public static MessageEnvelope FromText(string messageId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new MessageEnvelope(messageId, StrictUtf8.GetBytes(text)) { ContentType = "text/plain; charset=utf-8" };
    }

    /// <summary>
    /// Creates a JSON message with content type <c>application/json</c>. Without <paramref name="options"/>,
    /// <see cref="JsonSerializerDefaults.Web"/> settings are used.
    /// </summary>
    public static MessageEnvelope FromJson<T>(string messageId, T value, JsonSerializerOptions? options = null) =>
        new(messageId, JsonSerializer.SerializeToUtf8Bytes(value, options ?? DefaultJsonOptions)) { ContentType = "application/json" };

    /// <summary>Returns a copy of the body.</summary>
    public byte[] GetBody() => _body.ToArray();

    /// <summary>Decodes the body as UTF-8. Invalid UTF-8 throws <see cref="DecoderFallbackException"/>.</summary>
    public string GetBodyAsText() => StrictUtf8.GetString(_body);

    /// <summary>Deserializes the JSON body. A JSON <c>null</c> body throws <see cref="JsonException"/>.</summary>
    public T ReadJson<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(_body, options ?? DefaultJsonOptions)
        ?? throw new JsonException($"Message '{MessageId}' contains a JSON null body.");

    public bool Equals(MessageEnvelope? other) =>
        other is not null &&
        MessageId == other.MessageId &&
        Type == other.Type &&
        ContentType == other.ContentType &&
        CorrelationId == other.CorrelationId &&
        Timestamp == other.Timestamp &&
        Headers.Count == other.Headers.Count &&
        Headers.All(pair => other.Headers.TryGetValue(pair.Key, out var value) && value == pair.Value) &&
        _body.AsSpan().SequenceEqual(other._body);

    public override int GetHashCode() => HashCode.Combine(MessageId, Type, ContentType, CorrelationId, Timestamp, _body.Length);

    public override string ToString() => $"Message {MessageId} ({Type ?? "untyped"}, {_body.Length} bytes)";

    private static ImmutableSortedDictionary<string, string> NormalizeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                throw new ArgumentException("Header names must be non-empty and header values must not be null.", nameof(headers));
            }
        }

        return headers.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
