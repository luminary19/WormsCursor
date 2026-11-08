using System.Text;

namespace WormsCursor.App.ControlPanel;

/// <summary>One stretch of text in a release-note line: plain, bold, or <c>code</c>.</summary>
public sealed record NoteRun(string Text, bool Bold, bool Code = false);

/// <summary>The kind of a release-note block.</summary>
public enum NoteBlockKind { Heading, Bullet, Paragraph }

/// <summary>One block of a release note: a heading, a bullet item, or a plain paragraph.</summary>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteRun> Runs);

/// <summary>
/// Reads the light Markdown the CHANGELOG (and so each GitHub release body) uses: <c>#</c>/<c>##</c>/
/// <c>###</c> headings, <c>- </c>/<c>* </c> bullets, inline <c>**bold**</c> and <c>`code`</c>. The CHANGELOG
/// hard-wraps lines mid-sentence, so, like GitHub, a single newline is a space and a block ends only
/// at a blank line, a new bullet or a heading. Pure; the panel turns the blocks into text.
/// </summary>
public static class ReleaseNotesFormat
{
    public static IReadOnlyList<NoteBlock> Parse(string body)
    {
        var blocks = new List<NoteBlock>();
        var text = new StringBuilder();
        NoteBlockKind? open = null; // the block being gathered, if any

        void Flush()
        {
            if (open is { } kind && text.Length > 0) blocks.Add(new NoteBlock(kind, Inline(text.ToString())));
            text.Clear();
            open = null;
        }

        foreach (string raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { Flush(); continue; }

            if (line.StartsWith("# ") || line.StartsWith("## ") || line.StartsWith("### "))
            {
                Flush();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, Inline(line[(line.IndexOf(' ') + 1)..])));
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                Flush();
                open = NoteBlockKind.Bullet;
                text.Append(line[2..]);
            }
            else
            {
                if (open is null) open = NoteBlockKind.Paragraph;
                else text.Append(' ');
                text.Append(line);
            }
        }
        Flush();
        return blocks;
    }

    // Splits a line into runs at "**" (bold on/off) and "`" (code on/off; "**" inside code is literal).
    // Empty stretches are dropped; an unclosed marker runs to the end of the line.
    static IReadOnlyList<NoteRun> Inline(string text)
    {
        var runs = new List<NoteRun>();
        var current = new StringBuilder();
        bool bold = false, code = false;

        void Cut()
        {
            if (current.Length > 0) runs.Add(new NoteRun(current.ToString(), bold, code));
            current.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '`') { Cut(); code = !code; }
            else if (!code && text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*') { Cut(); bold = !bold; i++; }
            else current.Append(text[i]);
        }
        Cut();
        return runs;
    }
}
