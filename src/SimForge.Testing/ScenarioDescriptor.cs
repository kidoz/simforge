using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimForge.Testing;

/// <summary>Optional source location of a scenario definition.</summary>
public sealed record ScenarioSourceLocation(string FilePath, int LineNumber)
{
    /// <summary>Captures the caller's source location.</summary>
    public static ScenarioSourceLocation Capture([CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0) =>
        new(filePath, lineNumber);
}

/// <summary>Stable identity and metadata of a scenario, independent of any test runner.</summary>
public sealed record ScenarioDescriptor
{
    private const int MaxIdLength = 256;

    /// <param name="id">Stable identifier: ASCII letters, digits, and <c>. _ : / -</c>, starting with a letter or digit.</param>
    /// <param name="displayName">Human-readable name; defaults to <paramref name="id"/>.</param>
    /// <param name="tags">Tags without whitespace; stored de-duplicated in ordinal order.</param>
    /// <param name="source">Where the scenario is defined, if known.</param>
    public ScenarioDescriptor(string id, string? displayName = null, IEnumerable<string>? tags = null, ScenarioSourceLocation? source = null)
    {
        ValidateId(id);
        Id = id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        Tags = [.. (tags ?? []).Select(ValidateTag).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Source = source;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public ImmutableArray<string> Tags { get; }

    public ScenarioSourceLocation? Source { get; }

    /// <summary>
    /// Seed used when a run does not specify one: a stable 32-bit FNV-1a hash of <see cref="Id"/>. It is identical across
    /// processes and machines, unlike <see cref="string.GetHashCode()"/>.
    /// </summary>
    public int DefaultSeed
    {
        get
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            var hash = offsetBasis;
            foreach (var value in Encoding.UTF8.GetBytes(Id))
            {
                hash = (hash ^ value) * prime;
            }

            return unchecked((int)hash);
        }
    }

    public bool Equals(ScenarioDescriptor? other) =>
        other is not null &&
        Id == other.Id &&
        DisplayName == other.DisplayName &&
        Tags.SequenceEqual(other.Tags) &&
        Source == other.Source;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(DisplayName, StringComparer.Ordinal);
        foreach (var tag in Tags)
        {
            hash.Add(tag, StringComparer.Ordinal);
        }

        hash.Add(Source);
        return hash.ToHashCode();
    }

    public override string ToString() => DisplayName == Id ? Id : $"{DisplayName} ({Id})";

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var valid = id.Length <= MaxIdLength &&
                    char.IsAsciiLetterOrDigit(id[0]) &&
                    id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-');
        if (!valid)
        {
            throw new ArgumentException(
                $"Scenario ID '{id}' is invalid. Use at most {MaxIdLength} ASCII letters, digits, '.', '_', ':', '/' or '-', starting with a letter or digit.",
                nameof(id));
        }
    }

    private static string ValidateTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"Tag '{tag}' must be non-empty and contain no whitespace.", nameof(tag));
        }

        return tag;
    }
}
