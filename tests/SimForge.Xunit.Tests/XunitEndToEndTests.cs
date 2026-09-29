using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace SimForge.Xunit.Tests;

/// <summary>
/// Runs SimForge.Xunit.Fixtures with xUnit's native runner and checks xUnit's own XML report. This verifies the adapter
/// through the real xUnit execution path instead of calling the mapping directly.
/// </summary>
public sealed class XunitEndToEndTests
{
    private const string FixtureClass = "SimForge.Xunit.Fixtures.OutcomeFixtures";

    [Fact]
    public async Task Xunit_reports_every_scenario_outcome_correctly()
    {
        var report = await RunFixturesAsync(TestContext.Current.CancellationToken);
        var tests = report.Tests.ToDictionary(test => (string)test.Attribute("method")!);

        Assert.NotEqual(0, report.ExitCode);
        Assert.Equal(9, tests.Count);
        Assert.Equal((1, 7, 1), (report.Passed, report.Failed, report.Skipped));

        Assert.Equal("Pass", Result(tests["Passing"]));
        AssertFailed(tests["AssertionFailure"], "Failed (Assertion)", "deliberate assertion failure");
        AssertFailed(tests["UnsupportedCapability"], "Failed (UnsupportedCapability)", "fixture.feature");
        AssertFailed(tests["Canceled"], "Canceled");
        AssertFailed(tests["TimedOut"], "TimedOut", "real-time timeout of 100 ms");
        AssertFailed(tests["Abandoned"], "TimedOut", "must not be reused");
        AssertFailed(tests["CleanupFailure"], "Failed (Cleanup)", "deliberate cleanup failure");
        AssertFailed(tests["InitializationFailure"], "Failed (Initialization)", "deliberate initialization failure");
        Assert.Equal("Skip", Result(tests["Skipped"]));
        Assert.Equal("deliberately skipped", (string?)tests["Skipped"].Element("reason"));
        Assert.Contains("Scenario fixture.pass: Passed", (string?)tests["Passing"].Element("output"), StringComparison.Ordinal);
    }

    private static string Result(XElement test) => (string)test.Attribute("result")!;

    private static void AssertFailed(XElement test, params string[] expectedMessageParts)
    {
        Assert.Equal("Fail", Result(test));
        var failure = test.Element("failure")!;
        Assert.Equal("SimForge.Xunit.ScenarioFailedException", (string?)failure.Attribute("exception-type"));
        var message = (string?)failure.Element("message") ?? string.Empty;
        Assert.All(expectedMessageParts, part => Assert.Contains(part, message, StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, IReadOnlyList<XElement> Tests, int Passed, int Failed, int Skipped)> RunFixturesAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(XunitEndToEndTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SimForge.Xunit.Fixtures.Assembly")
            .Value!;
        var executable = OperatingSystem.IsWindows() ? Path.ChangeExtension(assembly, ".exe") : Path.ChangeExtension(assembly, null);
        Assert.True(File.Exists(executable), $"Fixture executable not found at {executable}; build the solution first.");

        var reportPath = Path.Combine(Path.GetTempPath(), "simforge-tests", $"{Guid.NewGuid():N}.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-noLogo", "-noColor", "-noAutoReporters", "-class", FixtureClass, "-xml", reportPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            Assert.True(File.Exists(reportPath), $"xUnit wrote no report. Output:{Environment.NewLine}{await output}{await error}");

            var assemblyElement = XDocument.Load(reportPath).Root!.Element("assembly")!;
            return (
                process.ExitCode,
                [.. assemblyElement.Descendants("test")],
                (int)assemblyElement.Attribute("passed")!,
                (int)assemblyElement.Attribute("failed")!,
                (int)assemblyElement.Attribute("skipped")!);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }
}
