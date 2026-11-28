using System.Text;

namespace Agent42.Backend;

/// <summary>
/// A pure buffer of NDJSON lines awaiting one append (ADR 0153). It knows nothing about time or the store: the writer decides
/// when to flush, which keeps the writer loop small and this unit trivially testable.
/// </summary>
public sealed class EventBatch
{
    private readonly List<string> _lines = [];

    /// <summary>The lines buffered.</summary>
    public int Count => _lines.Count;

    /// <summary>The UTF-8 size of the buffered lines, newlines included.</summary>
    public int Bytes { get; private set; }

    public void Add(string line)
    {
        _lines.Add(line);
        Bytes += Encoding.UTF8.GetByteCount(line) + 1;
    }

    /// <summary>The buffered lines as one append payload, each ending in a newline.</summary>
    public byte[] ToBytes()
    {
        var builder = new StringBuilder(Bytes);
        foreach (var line in _lines)
        {
            builder.Append(line).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public void Clear()
    {
        _lines.Clear();
        Bytes = 0;
    }
}
