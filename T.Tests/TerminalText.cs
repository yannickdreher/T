using System.Text;
using T.VT;

namespace T.Tests;

/// <summary>Reads screen content back as text (wide characters once, clusters in full, trailing blanks trimmed).</summary>
internal static class TerminalText
{
    public static string Row(VirtualTerminal vt, int row) => Line(vt.Buffer[row], vt.Graphemes);

    public static string Line(TerminalLine line, GraphemeTable graphemes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            var cell = line[i];
            if (cell.IsWideContinuation) continue;
            graphemes.AppendText(sb, cell.CodePoint);
        }
        return sb.ToString().TrimEnd();
    }
}
