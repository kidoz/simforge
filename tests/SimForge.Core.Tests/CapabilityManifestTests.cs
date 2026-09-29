using System.Text.Json;
using Xunit;

namespace SimForge.Core.Tests;

public sealed class CapabilityManifestTests
{
    private static Capability Simulated(string id) => new()
    {
        Id = id,
        Status = CapabilityStatus.SimulatedOnly,
        Level = CompatibilityLevel.ServiceSemantics,
        Summary = "summary",
    };

    [Fact]
    public void Manifest_rejects_duplicate_or_unprefixed_ids()
    {
        Assert.Throws<ArgumentException>(() => new CapabilityManifest("kv", "Key-value", [Simulated("kv.get"), Simulated("kv.get")]));
        Assert.Throws<ArgumentException>(() => new CapabilityManifest("kv", "Key-value", [Simulated("other.get")]));
    }

    [Fact]
    public void Verified_subset_requires_a_reference_version_and_verification_cases()
    {
        var unproven = Simulated("kv.get") with { Status = CapabilityStatus.VerifiedSubset };

        Assert.Throws<ArgumentException>(() => new CapabilityManifest("kv", "Key-value", [unproven]));
        Assert.Throws<ArgumentException>(() => new CapabilityManifest("kv", "Key-value", [unproven with { ReferenceVersion = "7.4" }]));
        _ = new CapabilityManifest("kv", "Key-value", [unproven with { ReferenceVersion = "7.4", VerificationCases = ["kv-get-missing-key"] }]);
    }

    [Fact]
    public void Json_is_stable_and_complete()
    {
        var manifest = new CapabilityManifest("kv", "Key-value", [Simulated("kv.get") with { Deviations = ["no eviction"] }]);

        var json = manifest.ToJson();

        Assert.Equal(json, manifest.ToJson());
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var capability = document.RootElement.GetProperty("capabilities")[0];
        Assert.Equal("SimulatedOnly", capability.GetProperty("status").GetString());
        Assert.Equal("no eviction", capability.GetProperty("deviations")[0].GetString());
        Assert.Equal(JsonValueKind.Null, capability.GetProperty("referenceVersion").ValueKind);
        Assert.Throws<KeyNotFoundException>(() => manifest.Get("kv.set"));
    }
}
