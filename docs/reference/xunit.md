# SimForge.Xunit

Namespace `SimForge.Xunit`. Adapter for xUnit v3 (4.x) test projects on Microsoft Testing Platform.

## XunitScenario

| Member | Description |
|---|---|
| `RunAsync(IScenario scenario, ScenarioRunOptions? options = null)` | Runs the scenario with `ScenarioExecutor` under `TestContext.Current.CancellationToken`. Writes `ScenarioResult.FormatReport()` to the test output. Attaches diagnostics when the outcome is not `Passed` or `Skipped`. Then calls `ThrowIfNotPassed`. |
| `ThrowIfNotPassed(ScenarioResult result)` | Maps a result into xUnit's reporting model (table below). |

| Outcome | Effect in xUnit |
|---|---|
| `Passed` | Returns; the test passes. |
| `Skipped` | Throws an xUnit dynamic skip with `SkipReason`; the test is skipped. |
| `Failed`, `Canceled`, `TimedOut` | Throws `ScenarioFailedException`; the test fails. |

Plain `IScenario` objects are not discovered by xUnit. Each scenario needs a `[Fact]` (or `[Theory]`) that calls
`RunAsync`.

## ScenarioFailedException

| Member | Description |
|---|---|
| `Result` | The `ScenarioResult`. |
| `Message` | `Result.FormatReport()`. |
| `InnerException` | `Result.PrimaryError`. |

## Diagnostics attachment

The attachment is named `<scenario-id>.diagnostics.json` and contains `Result.Diagnostics.ToJson()`. A later
non-passing run of the same scenario in the same test replaces it. With `dotnet test`, attachments are saved under
`TestResults/` in the working directory, and their file names get a `.txt` suffix.
