using SimForge.Testing;
using Xunit;
using Xunit.Sdk;

namespace SimForge.Xunit;

/// <summary>
/// Thin xUnit v3 adapter. Call it from an ordinary <c>[Fact]</c>; plain <see cref="IScenario"/> objects are not
/// discovered by xUnit on their own.
/// <code>
/// [Fact]
/// public Task Places_order() => XunitScenario.RunAsync(OrderScenarios.PlaceOrder);
/// </code>
/// </summary>
/// <remarks>
/// Outcome mapping: <see cref="ScenarioOutcome.Passed"/> returns normally. <see cref="ScenarioOutcome.Skipped"/> becomes
/// an xUnit dynamic skip. <see cref="ScenarioOutcome.Failed"/>, <see cref="ScenarioOutcome.Canceled"/>, and
/// <see cref="ScenarioOutcome.TimedOut"/> throw <see cref="ScenarioFailedException"/>, so xUnit reports a failure.
/// A result that did not pass therefore never produces a passing test.
/// </remarks>
public static class XunitScenario
{
    private static readonly ScenarioExecutor Executor = new();

    /// <summary>
    /// Runs <paramref name="scenario"/> under the current test's cancellation token. It writes the report to the test
    /// output, attaches diagnostics when the scenario did not pass, and throws unless the scenario passed.
    /// </summary>
    public static async Task RunAsync(IScenario scenario, ScenarioRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var testContext = TestContext.Current;
        var result = await Executor.ExecuteAsync(scenario, options, testContext.CancellationToken).ConfigureAwait(false);

        testContext.TestOutputHelper?.WriteLine(result.FormatReport());
        if (result.Outcome is not (ScenarioOutcome.Passed or ScenarioOutcome.Skipped))
        {
            // Replacing keeps a second non-passing run in the same test from failing with a duplicate-attachment error.
            testContext.AddAttachment($"{result.Descriptor.Id}.diagnostics.json", result.Diagnostics.ToJson(), replaceExistingValue: true);
        }

        ThrowIfNotPassed(result);
    }

    /// <summary>Maps a result into xUnit's reporting model: returns for a pass, throws a dynamic skip for a skip, and throws <see cref="ScenarioFailedException"/> otherwise.</summary>
    public static void ThrowIfNotPassed(ScenarioResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case ScenarioOutcome.Passed:
                return;
            case ScenarioOutcome.Skipped:
                throw SkipException.ForSkip(result.SkipReason ?? $"Scenario '{result.Descriptor.Id}' was skipped.");
            default:
                // Failed, Canceled, TimedOut, and any outcome added later.
                throw new ScenarioFailedException(result);
        }
    }
}

/// <summary>Reports a scenario that did not pass (failed, canceled, or timed out) as an xUnit test failure.</summary>
public sealed class ScenarioFailedException : Exception
{
    public ScenarioFailedException(ScenarioResult result)
        : base(Describe(result), result?.PrimaryError)
    {
        Result = result!;
    }

    public ScenarioResult Result { get; }

    private static string Describe(ScenarioResult? result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.FormatReport();
    }
}
