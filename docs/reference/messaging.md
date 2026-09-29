# SimForge.Messaging

Namespace `SimForge.Messaging`. Message types shared by broker models. The package contains no broker behavior and has
no dependencies.

## MessageEnvelope

An immutable `sealed record`. The body is copied when the envelope is created, and again on every read.

```csharp
public MessageEnvelope(string messageId, ReadOnlySpan<byte> body)
```

| Member | Description |
|---|---|
| `MessageId` | Required, non-blank. Chosen by the producer. |
| `Type` | Optional logical type, for example `orders.order-placed.v1`. |
| `ContentType` | Optional MIME type. |
| `CorrelationId` | Optional. Broker models record it on their journal entries. |
| `Timestamp` | Optional; stored in UTC. Brokers never set it. |
| `Headers` | String headers ordered by ordinal key. Assigning copies the dictionary; blank names and null values throw `ArgumentException`. |
| `BodyLength` | Body size in bytes. |
| `GetBody()` | A copy of the body. |
| `GetBodyAsText()` | The body decoded as UTF-8. Invalid UTF-8 throws `DecoderFallbackException`. |
| `ReadJson<T>(options = null)` | Deserializes the body. A JSON `null` throws `JsonException`. |
| `FromText(messageId, text)` | UTF-8 body with `ContentType` `text/plain; charset=utf-8`. |
| `FromJson<T>(messageId, value, options = null)` | JSON body with `ContentType` `application/json`. |

`FromJson` and `ReadJson` use `JsonSerializerDefaults.Web` settings when `options` is null.

Use `with` to derive a changed copy, for example
`MessageEnvelope.FromJson(id, payload) with { Type = "orders.order-placed.v1", CorrelationId = "order:42" }`.

Two envelopes are equal when every property, every header, and the body bytes are equal.
