using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SimForge;

/// <summary>Evidence status of a capability.</summary>
public enum CapabilityStatus
{
    /// <summary>Implemented and tested against SimForge's own model only; not verified against the real service.</summary>
    SimulatedOnly,

    /// <summary>Implemented, with passing contract cases against a pinned reference service version.</summary>
    VerifiedSubset,

    /// <summary>Not implemented. Requests fail explicitly with <see cref="UnsupportedCapabilityException"/>.</summary>
    Unsupported,
}

/// <summary>How closely a capability approaches the real service.</summary>
public enum CompatibilityLevel
{
    /// <summary>Replaces an application-owned interface such as a repository, producer, or consumer.</summary>
    ApplicationContract,

    /// <summary>Implements a declared subset of observable service behavior.</summary>
    ServiceSemantics,

    /// <summary>Supports a named API surface of a production client library.</summary>
    ClientApi,

    /// <summary>Accepts requests from an unchanged production driver.</summary>
    WireProtocol,

    /// <summary>Executes the actual service engine.</summary>
    RealEngine,
}

/// <summary>One entry of a provider's capability manifest.</summary>
public sealed record Capability
{
    public required string Id { get; init; }

    public required CapabilityStatus Status { get; init; }

    public required CompatibilityLevel Level { get; init; }

    public required string Summary { get; init; }

    public ImmutableArray<string> Operations { get; init; } = [];

    public ImmutableArray<string> DataTypes { get; init; } = [];

    public ImmutableArray<string> Boundaries { get; init; } = [];

    /// <summary>Known differences from the real service.</summary>
    public ImmutableArray<string> Deviations { get; init; } = [];

    /// <summary>Ordering and concurrency assumptions.</summary>
    public ImmutableArray<string> Assumptions { get; init; } = [];

    /// <summary>Reference service and client versions, required for <see cref="CapabilityStatus.VerifiedSubset"/>.</summary>
    public string? ReferenceVersion { get; init; }

    /// <summary>Verification cases proving the claim, required for <see cref="CapabilityStatus.VerifiedSubset"/>.</summary>
    public ImmutableArray<string> VerificationCases { get; init; } = [];
}

/// <summary>Machine-readable capability manifest published by every provider.</summary>
public sealed class CapabilityManifest
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CapabilityManifest(string provider, string title, IEnumerable<Capability> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(capabilities);
        Provider = provider;
        Title = title;
        Capabilities = [.. capabilities];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in Capabilities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capability.Id, nameof(capabilities));
            if (!capability.Id.StartsWith(provider + ".", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Capability '{capability.Id}' must be prefixed with '{provider}.'.", nameof(capabilities));
            }

            if (!seen.Add(capability.Id))
            {
                throw new ArgumentException($"Capability '{capability.Id}' is declared twice.", nameof(capabilities));
            }

            if (capability.Status == CapabilityStatus.VerifiedSubset &&
                (string.IsNullOrWhiteSpace(capability.ReferenceVersion) || capability.VerificationCases.IsDefaultOrEmpty))
            {
                throw new ArgumentException(
                    $"Capability '{capability.Id}' claims VerifiedSubset without a reference version and verification cases.",
                    nameof(capabilities));
            }
        }
    }

    public string Provider { get; }

    public string Title { get; }

    public ImmutableArray<Capability> Capabilities { get; }

    public Capability Get(string id) =>
        Capabilities.FirstOrDefault(capability => capability.Id == id)
        ?? throw new KeyNotFoundException($"Capability '{id}' is not declared by the '{Provider}' manifest.");

    /// <summary>Stable JSON rendering (LF line endings, declaration order) suitable for checked-in documentation.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("provider", Provider);
            writer.WriteString("title", Title);
            writer.WriteStartArray("capabilities");
            foreach (var capability in Capabilities)
            {
                writer.WriteStartObject();
                writer.WriteString("id", capability.Id);
                writer.WriteString("status", capability.Status.ToString());
                writer.WriteString("level", capability.Level.ToString());
                writer.WriteString("summary", capability.Summary);
                WriteList(writer, "operations", capability.Operations);
                WriteList(writer, "dataTypes", capability.DataTypes);
                WriteList(writer, "boundaries", capability.Boundaries);
                WriteList(writer, "deviations", capability.Deviations);
                WriteList(writer, "assumptions", capability.Assumptions);
                if (capability.ReferenceVersion is not null)
                {
                    writer.WriteString("referenceVersion", capability.ReferenceVersion);
                }
                else
                {
                    writer.WriteNull("referenceVersion");
                }

                WriteList(writer, "verificationCases", capability.VerificationCases);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteList(Utf8JsonWriter writer, string name, ImmutableArray<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.IsDefault ? [] : values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
