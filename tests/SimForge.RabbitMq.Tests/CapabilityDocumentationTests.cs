using System.Reflection;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.RabbitMq.Tests;

public sealed class CapabilityDocumentationTests
{
    private const string UpdateVariable = "SIMFORGE_UPDATE_CAPABILITY_DOCS";

    private static string ManifestPath => Path.Combine(RepositoryPaths.Root, "docs", "reference", "rabbitmq.capabilities.json");

    private static string TablePath => Path.Combine(RepositoryPaths.Root, "docs", "reference", "rabbitmq.md");

    [Fact]
    public void Checked_in_manifest_matches_the_code()
    {
        var generated = RabbitMqCapabilities.Manifest.ToJson();
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
        var constants = typeof(RabbitMqCapabilityIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(
            constants.Order(StringComparer.Ordinal),
            RabbitMqCapabilities.Manifest.Capabilities.Select(capability => capability.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(RabbitMqCapabilityIds.TopicRouting)]
    [InlineData(RabbitMqCapabilityIds.HeadersRouting)]
    [InlineData(RabbitMqCapabilityIds.ReservedNames)]
    [InlineData(RabbitMqCapabilityIds.UnlimitedPrefetch)]
    public void Capabilities_rejected_at_runtime_are_declared_unsupported(string id)
    {
        Assert.Equal(CapabilityStatus.Unsupported, RabbitMqCapabilities.Manifest.Get(id).Status);
    }

    [Fact]
    public void No_capability_claims_verified_status_without_evidence()
    {
        Assert.DoesNotContain(RabbitMqCapabilities.Manifest.Capabilities, capability => capability.Status == CapabilityStatus.VerifiedSubset);
    }

    [Fact]
    public void Human_readable_table_lists_every_capability_with_its_status()
    {
        var table = File.ReadAllText(TablePath);

        Assert.All(RabbitMqCapabilities.Manifest.Capabilities, capability =>
            Assert.Contains($"| `{capability.Id}` | {capability.Status} |", table, StringComparison.Ordinal));
    }
}
