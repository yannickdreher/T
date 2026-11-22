using System.Text;

namespace T.VT;

/// <summary>
/// Stores characters that consist of more than one code point - a base character plus
/// combining marks ("e" + U+0301), variation selectors or zero width joiners - so a cell
/// can still hold a single <see cref="int"/>: code points up to U+10FFFF are stored
/// directly, larger values are ids into this table.
/// <para>
/// Entries are interned and never removed (cells in the scrollback may still refer to
/// them). Hostile input is bounded by <see cref="MaxEntries"/> and <see cref="MaxLength"/>:
/// beyond those limits further marks are dropped instead of stored.
/// </para>
/// </summary>
public sealed class GraphemeTable
{
    /// <summary>First id used for clusters (above the Unicode range).</summary>
    public const int FirstId = 0x200000;

    public const int MaxEntries = 1 << 16;

    /// <summary>Maximum length of a cluster in UTF-16 code units.</summary>
    public const int MaxLength = 32;

    private readonly List<string> _entries = [];
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public static bool IsCluster(int codePoint) => codePoint >= FirstId;

    /// <summary>Appends <paramref name="mark"/> to the character in a cell and returns the new cell value.</summary>
    public int Combine(int codePoint, int mark)
    {
        var text = string.Concat(GetText(codePoint), char.ConvertFromUtf32(mark));
        if (text.Length > MaxLength)
            return codePoint;

        if (_ids.TryGetValue(text, out var id))
            return id;
        if (_entries.Count >= MaxEntries)
            return codePoint;

        id = FirstId + _entries.Count;
        _entries.Add(text);
        _ids[text] = id;
        return id;
    }

    /// <summary>Text of a cell value: the cluster, the character, or a space for empty cells.</summary>
    public string GetText(int codePoint)
    {
        if (codePoint >= FirstId)
        {
            int index = codePoint - FirstId;
            return index < _entries.Count ? _entries[index] : "\uFFFD";
        }
        return IsScalar(codePoint) ? char.ConvertFromUtf32(codePoint) : " ";
    }

    public void AppendText(StringBuilder builder, int codePoint)
    {
        if (codePoint >= FirstId)
            builder.Append(GetText(codePoint));
        else if (codePoint is > 0 and < 0x10000 && !char.IsSurrogate((char)codePoint))
            builder.Append((char)codePoint);
        else if (IsScalar(codePoint))
            builder.Append(char.ConvertFromUtf32(codePoint));
        else
            builder.Append(' ');
    }

    /// <summary>The first code point of a cell value (the base character of a cluster).</summary>
    public int GetBaseCodePoint(int codePoint)
    {
        if (codePoint < FirstId)
            return codePoint;
        var text = GetText(codePoint);
        return char.ConvertToUtf32(text, 0);
    }

    private static bool IsScalar(int codePoint) =>
        codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);
}
