using System.Reflection;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Kafka.Tests;

public sealed class CapabilityDocumentationTests
{
    private const string UpdateVariable = "SIMFORGE_UPDATE_CAPABILITY_DOCS";

    private static string ManifestPath => Path.Combine(RepositoryPaths.Root, "docs", "reference", "kafka.capabilities.json");

    private static string TablePath => Path.Combine(RepositoryPaths.Root, "docs", "reference", "kafka.md");

    [Fact]
    public void Checked_in_manifest_matches_the_code()
    {
        var generated = KafkaCapabilities.Manifest.ToJson();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(ManifestPath, generated);
        }

        Assert.True(
            File.Exists(ManifestPath) && File.ReadAllText(ManifestPath).ReplaceLineEndings("\n") == generated,
            $"{ManifestPath} is out of date. Rerun the tests with {UpdateVariable}=1 to regenerate it, and review the diff.");
    }

    [Fact]
    public void Every_capability_id_constant_is_declared_in_the_manifest()
    {
        var constants = typeof(KafkaCapabilityIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(
            constants.Order(StringComparer.Ordinal),
            KafkaCapabilities.Manifest.Capabilities.Select(capability => capability.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Capabilities_rejected_at_runtime_are_declared_unsupported()
    {
        Assert.Equal(CapabilityStatus.Unsupported, KafkaCapabilities.Manifest.Get(KafkaCapabilityIds.TopicManagement).Status);
    }

    [Fact]
    public void No_capability_claims_verified_status_without_evidence()
    {
        Assert.DoesNotContain(KafkaCapabilities.Manifest.Capabilities, capability => capability.Status == CapabilityStatus.VerifiedSubset);
    }

    [Fact]
    public void Human_readable_table_lists_every_capability_with_its_status()
    {
        var table = File.ReadAllText(TablePath);

        Assert.All(KafkaCapabilities.Manifest.Capabilities, capability =>
            Assert.Contains($"| `{capability.Id}` | {capability.Status} |", table, StringComparison.Ordinal));
    }
}
