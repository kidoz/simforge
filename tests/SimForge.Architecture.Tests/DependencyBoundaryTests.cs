using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Architecture.Tests;

/// <summary>Verifies the documented dependency direction and runner independence from the built assemblies and the project files.</summary>
public sealed class DependencyBoundaryTests
{
    private static readonly string[] TestFrameworkPrefixes = ["xunit", "nunit", "mstest", "microsoft.visualstudio.testplatform", "microsoft.testing."];

    /// <summary>Allowed SimForge project references per source project; anything else is a dependency-direction violation.</summary>
    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["SimForge.Core"] = [],
        ["SimForge.Testing"] = ["SimForge.Core"],
        ["SimForge.Assertions"] = ["SimForge.Core"],
        ["SimForge.PostgreSql"] = ["SimForge.Core"],
        ["SimForge.Messaging"] = [],
        ["SimForge.RabbitMq"] = ["SimForge.Core", "SimForge.Messaging"],
        ["SimForge.Xunit"] = ["SimForge.Testing"],
    };

    private static readonly Dictionary<string, string[]> AllowedPackageReferences = new()
    {
        ["SimForge.Core"] = [],
        ["SimForge.Testing"] = [],
        ["SimForge.Assertions"] = [],
        ["SimForge.PostgreSql"] = [],
        ["SimForge.Messaging"] = [],
        ["SimForge.RabbitMq"] = [],
        ["SimForge.Xunit"] = ["xunit.v3.extensibility.core", "xunit.v3.assert"],
    };

    public static TheoryData<string> SourceProjects => [.. AllowedProjectReferences.Keys];

    [Fact]
    public void Every_source_project_is_covered_by_the_boundary_rules()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepositoryPaths.Root, "src")).Select(Path.GetFileName).Order(StringComparer.Ordinal);

        Assert.Equal(AllowedProjectReferences.Keys.Order(StringComparer.Ordinal), projects);
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Project_files_follow_the_dependency_direction(string project)
    {
        var document = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", project, project + ".csproj"));
        var projectReferences = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(((string)reference.Attribute("Include")!).Replace('\\', '/')))
            .Order(StringComparer.Ordinal);
        var packageReferences = document.Descendants("PackageReference")
            .Select(reference => (string)reference.Attribute("Include")!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(AllowedProjectReferences[project].Order(StringComparer.Ordinal), projectReferences);
        Assert.Equal(AllowedPackageReferences[project].Order(StringComparer.Ordinal), packageReferences);
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Only_the_xunit_adapter_references_a_test_framework_at_runtime(string project)
    {
        var references = Assembly.Load(project).GetReferencedAssemblies().Select(name => name.Name!.ToLowerInvariant()).ToList();
        var testFrameworks = references.Where(name => TestFrameworkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))).ToList();

        if (project == "SimForge.Xunit")
        {
            Assert.All(testFrameworks, name => Assert.StartsWith("xunit.v3.", name, StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(testFrameworks);
        }

        var simForgeReferences = references.Where(name => name.StartsWith("simforge.", StringComparison.Ordinal));
        Assert.All(simForgeReferences, name => Assert.Contains(name, AllowedTransitiveSimForgeReferences(project)));
    }

    [Theory]
    [MemberData(nameof(SourceProjects))]
    public void Source_assemblies_target_net10(string project)
    {
        var framework = Assembly.Load(project).GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETCoreApp,Version=v10.0", framework?.FrameworkName);
    }

    [Fact]
    public void Build_configuration_pins_the_language_runtime_and_sdk()
    {
        var properties = XDocument.Load(Path.Combine(RepositoryPaths.Root, "Directory.Build.props")).Descendants("PropertyGroup").Elements().ToList();
        var globalJson = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "global.json"));

        Assert.Equal("14.0", properties.Single(element => element.Name == "LangVersion").Value);
        Assert.Equal("net10.0", properties.Single(element => element.Name == "TargetFramework").Value);
        Assert.Equal("enable", properties.Single(element => element.Name == "Nullable").Value);
        Assert.Contains("\"version\": \"10.0.", globalJson, StringComparison.Ordinal);
        Assert.Contains("\"allowPrerelease\": false", globalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_application_code_does_not_depend_on_simforge()
    {
        var references = Assembly.Load("Orders.Application").GetReferencedAssemblies().Select(name => name.Name!);

        Assert.DoesNotContain(references, name => name.StartsWith("SimForge", StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> AllowedTransitiveSimForgeReferences(string project)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(AllowedProjectReferences[project]);
        while (pending.TryPop(out var next))
        {
            if (allowed.Add(next.ToLowerInvariant()))
            {
                foreach (var dependency in AllowedProjectReferences[next])
                {
                    pending.Push(dependency);
                }
            }
        }

        return allowed;
    }
}
