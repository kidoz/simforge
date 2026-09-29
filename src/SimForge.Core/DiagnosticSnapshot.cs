using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SimForge;

/// <summary>Immutable diagnostic view of an environment: identity, virtual time, journal, pending work, and faults.</summary>
public sealed record DiagnosticSnapshot(
    string ScenarioId,
    int Seed,
    DateTimeOffset VirtualTime,
    SimulationEnvironmentState EnvironmentState,
    ImmutableArray<OperationJournalEntry> Journal,
    ImmutableArray<PendingWork> PendingWork,
    ImmutableArray<FaultReport> FiredFaults,
    ImmutableArray<UnfiredFault> UnfiredFaults)
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Human-readable summary for failure messages, showing the most recent journal entries.</summary>
    public string FormatSummary(int maxJournalEntries = 25)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxJournalEntries);
        var builder = new StringBuilder()
            .Append("Scenario '").Append(ScenarioId).Append("' seed ").Append(Seed.ToString(CultureInfo.InvariantCulture))
            .Append(", virtual time ").Append(Format(VirtualTime))
            .Append(", environment ").Append(EnvironmentState).AppendLine();

        if (!FiredFaults.IsEmpty)
        {
            builder.AppendLine("Fired faults:");
            foreach (var fault in FiredFaults)
            {
                builder.Append("  ").Append(fault.RuleId).Append(' ').Append(fault.Provider).Append('/').Append(fault.Resource)
                    .Append(' ').Append(fault.Operation).Append(':').Append(fault.Phase)
                    .Append(" occurrence ").Append(fault.Occurrence.ToString(CultureInfo.InvariantCulture))
                    .Append(fault.StateChanged ? " (state changed)" : " (state unchanged)").AppendLine();
            }
        }

        if (!UnfiredFaults.IsEmpty)
        {
            builder.AppendLine("Unfired faults:");
            foreach (var fault in UnfiredFaults)
            {
                builder.Append("  ").Append(fault.RuleId).Append(' ').Append(fault.Rule)
                    .Append(" (matched ").Append(fault.Matches.ToString(CultureInfo.InvariantCulture)).Append(" time(s))").AppendLine();
            }
        }

        if (!PendingWork.IsEmpty)
        {
            builder.Append("Pending work (").Append(PendingWork.Length.ToString(CultureInfo.InvariantCulture)).AppendLine("):");
            foreach (var work in PendingWork.Take(10))
            {
                builder.Append("  ").Append(work).AppendLine();
            }
        }

        var shown = Journal.Length <= maxJournalEntries ? Journal : Journal[^maxJournalEntries..];
        builder.Append("Journal (").Append(shown.Length.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(Journal.Length.ToString(CultureInfo.InvariantCulture)).AppendLine(" entries):");
        foreach (var entry in shown)
        {
            builder.Append("  #").Append(entry.Sequence.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(Format(entry.VirtualTime))
                .Append(' ').Append(entry.Provider).Append('/').Append(entry.Resource)
                .Append(' ').Append(entry.Operation).Append(':').Append(entry.Phase)
                .Append(' ').Append(entry.Outcome);
            AppendField(builder, "target", entry.Target);
            AppendField(builder, "correlation", entry.CorrelationId);
            AppendField(builder, "details", entry.Details);
            AppendField(builder, "error", entry.Error);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        WriteJson(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void WriteJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("scenarioId", ScenarioId);
        writer.WriteNumber("seed", Seed);
        writer.WriteString("virtualTime", Format(VirtualTime));
        writer.WriteString("environmentState", EnvironmentState.ToString());

        writer.WriteStartArray("firedFaults");
        foreach (var fault in FiredFaults)
        {
            writer.WriteStartObject();
            writer.WriteString("ruleId", fault.RuleId);
            writer.WriteString("provider", fault.Provider);
            writer.WriteString("resource", fault.Resource);
            writer.WriteString("operation", fault.Operation);
            writer.WriteString("phase", fault.Phase);
            writer.WriteNumber("occurrence", fault.Occurrence);
            writer.WriteBoolean("stateChanged", fault.StateChanged);
            writer.WriteString("virtualTime", Format(fault.VirtualTime));
            WriteOptional(writer, "correlationId", fault.CorrelationId);
            WriteOptional(writer, "reason", fault.Reason);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("unfiredFaults");
        foreach (var fault in UnfiredFaults)
        {
            writer.WriteStartObject();
            writer.WriteString("ruleId", fault.RuleId);
            writer.WriteString("rule", fault.Rule.ToString());
            writer.WriteNumber("matches", fault.Matches);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("pendingWork");
        foreach (var work in PendingWork)
        {
            writer.WriteStartObject();
            writer.WriteString("name", work.Name);
            writer.WriteString("dueTime", Format(work.DueTime));
            writer.WriteNumber("sequence", work.Sequence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("journal");
        foreach (var entry in Journal)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", entry.Sequence);
            writer.WriteString("provider", entry.Provider);
            writer.WriteString("resource", entry.Resource);
            writer.WriteString("operation", entry.Operation);
            writer.WriteString("phase", entry.Phase);
            writer.WriteString("virtualTime", Format(entry.VirtualTime));
            writer.WriteString("outcome", entry.Outcome.ToString());
            WriteOptional(writer, "correlationId", entry.CorrelationId);
            WriteOptional(writer, "target", entry.Target);
            WriteOptional(writer, "details", entry.Details);
            WriteOptional(writer, "error", entry.Error);
            WriteOptional(writer, "payload", entry.Payload);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Writes the snapshot as JSON into <paramref name="directory"/>, which the caller selects, and returns the file path.</summary>
    public async Task<string> ExportAsync(string directory, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        fileName ??= $"{SanitizeFileName(ScenarioId)}.seed-{Seed.ToString(CultureInfo.InvariantCulture)}.diagnostics.json";
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{fileName}' is not a valid diagnostics file name.", nameof(fileName));
        }

        var path = Path.Combine(directory, fileName);
        using var buffer = new MemoryStream();
        WriteJson(buffer);
        await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        if (value is not null)
        {
            builder.Append(' ').Append(name).Append('=').Append(value);
        }
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) || character == '.' ? '_' : character));
    }
}
