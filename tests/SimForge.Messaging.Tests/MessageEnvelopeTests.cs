using System.Text;
using System.Text.Json;
using Xunit;

namespace SimForge.Messaging.Tests;

public sealed class MessageEnvelopeTests
{
    private sealed record OrderPlaced(Guid OrderId, long TotalCents);

    [Fact]
    public void Body_is_copied_on_creation_and_on_every_read()
    {
        var source = Encoding.UTF8.GetBytes("original");
        var envelope = new MessageEnvelope("m-1", source);

        source[0] = (byte)'X';
        var firstRead = envelope.GetBody();
        firstRead[1] = (byte)'Y';

        Assert.Equal("original", envelope.GetBodyAsText());
        Assert.Equal(8, envelope.BodyLength);
        Assert.NotSame(firstRead, envelope.GetBody());
    }

    [Fact]
    public void Headers_are_copied_ordered_and_validated()
    {
        var headers = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" };
        var envelope = new MessageEnvelope("m-1", []) { Headers = headers };

        headers["c"] = "3";

        Assert.Equal(["a", "b"], envelope.Headers.Keys);
        Assert.Throws<ArgumentException>(() => new MessageEnvelope("m-1", []) { Headers = new Dictionary<string, string> { [" "] = "x" } });
        Assert.Throws<ArgumentException>(() => new MessageEnvelope("m-1", []) { Headers = new Dictionary<string, string> { ["k"] = null! } });
    }

    [Fact]
    public void Timestamps_are_stored_in_utc()
    {
        var local = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.FromHours(3));

        var envelope = new MessageEnvelope("m-1", []) { Timestamp = local };

        Assert.Equal(local, envelope.Timestamp);
        Assert.Equal(TimeSpan.Zero, envelope.Timestamp!.Value.Offset);
    }

    [Fact]
    public void With_derives_an_independent_copy_and_equality_compares_content()
    {
        var original = MessageEnvelope.FromText("m-1", "hello") with { Type = "greeting", CorrelationId = "c-1" };

        var derived = original with { CorrelationId = "c-2", Headers = new Dictionary<string, string> { ["x"] = "1" } };
        var sameContent = MessageEnvelope.FromText("m-1", "hello") with { Type = "greeting", CorrelationId = "c-1" };

        Assert.Equal("c-1", original.CorrelationId);
        Assert.Empty(original.Headers);
        Assert.Equal("hello", derived.GetBodyAsText());
        Assert.Equal(original, sameContent);
        Assert.Equal(original.GetHashCode(), sameContent.GetHashCode());
        Assert.NotEqual(original, derived);
        Assert.NotEqual(original, MessageEnvelope.FromText("m-1", "hello!") with { Type = "greeting", CorrelationId = "c-1" });
    }

    [Fact]
    public void Json_messages_round_trip_with_web_defaults()
    {
        var payload = new OrderPlaced(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), 4_250);

        var envelope = MessageEnvelope.FromJson("m-1", payload);

        Assert.Equal("application/json", envelope.ContentType);
        Assert.Contains("\"orderId\"", envelope.GetBodyAsText(), StringComparison.Ordinal);
        Assert.Equal(payload, envelope.ReadJson<OrderPlaced>());
        Assert.Throws<JsonException>(() => new MessageEnvelope("m-2", "null"u8).ReadJson<OrderPlaced>());
    }

    [Fact]
    public void Text_messages_use_strict_utf8()
    {
        Assert.Equal("text/plain; charset=utf-8", MessageEnvelope.FromText("m-1", "ok").ContentType);
        Assert.Throws<DecoderFallbackException>(() => new MessageEnvelope("m-1", [0xFF, 0xFE]).GetBodyAsText());
    }

    [Fact]
    public void Message_id_is_required()
    {
        Assert.Throws<ArgumentException>(() => new MessageEnvelope(" ", []));
        Assert.Throws<ArgumentNullException>(() => MessageEnvelope.FromText("m-1", null!));
    }
}
