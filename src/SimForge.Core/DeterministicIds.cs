namespace SimForge;

/// <summary>
/// Environment-owned source of reproducible identifiers. The same seed yields the same sequence, and separate
/// environments never share counters.
/// </summary>
public sealed class DeterministicIds
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);
    private readonly ulong _seedKey;
    private ulong _guidCounter;

    internal DeterministicIds(int seed)
    {
        _seedKey = Mix((ulong)(uint)seed ^ 0x5DEECE66DUL);
    }

    /// <summary>
    /// Returns the next GUID of this environment's sequence. The values are RFC 9562 version 8 (custom) UUIDs derived
    /// from the seed. They are unique within the sequence but not random, so do not use them as secrets.
    /// </summary>
    public Guid NewGuid()
    {
        ulong counter;
        lock (_gate)
        {
            counter = ++_guidCounter;
        }

        Span<byte> bytes = stackalloc byte[16];
        var high = Mix(_seedKey + (2 * counter));
        var low = Mix(_seedKey + (2 * counter) + 1);
        for (var index = 0; index < 8; index++)
        {
            bytes[index] = (byte)(high >> (56 - (8 * index)));
            bytes[8 + index] = (byte)(low >> (56 - (8 * index)));
        }

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Returns the next value (starting at 1) of a named per-environment counter.</summary>
    public long NextValue(string sequenceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName);
        lock (_gate)
        {
            _sequences.TryGetValue(sequenceName, out var current);
            _sequences[sequenceName] = ++current;
            return current;
        }
    }

    // SplitMix64 finalizer: a well-distributed bijective mix with no process-wide state.
    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
