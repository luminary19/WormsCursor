using System.Globalization;
using System.Text;
using WormsCursor.App.Services;

namespace WormsCursor.App;

/// <summary>
/// "What's new" dialog: one scrollable column with every published release (newest first) and
/// its notes. Notes come from the GitHub Releases API via
/// <see cref="UpdateService.FetchReleaseNotesAsync"/> (each release body is the CHANGELOG
/// section the release workflow published), with light formatting. On a fetch failure
/// (offline / rate-limited / dev build) it shows a short message and keeps "View on GitHub".
/// </summary>
public sealed class ChangelogForm : Form
{
    static readonly Color Ink = Color.FromArgb(32, 33, 36);
    static readonly Color Subtle = Color.FromArgb(120, 124, 130);

    readonly UpdateService _updates;
    readonly RichTextBox _notes;

    public ChangelogForm(UpdateService updates)
    {
        _updates = updates;

        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "WormsCursor — What's new";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MinimumSize = new Size(420, 340);
        ClientSize = new Size(540, 520);

        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 8, 8) };
        _notes = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Ink,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
        };
        notesHost.Controls.Add(_notes);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10),
            Height = 52,
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, Size = new Size(92, 30) };
        var github = new Button { Text = "View on GitHub", Size = new Size(132, 30) };
        github.Click += (_, _) => _updates.OpenReleasesPage();
        footer.Controls.Add(close);   // right-most
        footer.Controls.Add(github);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(notesHost); // fill
        Controls.Add(footer);    // bottom

        _notes.Text = "Loading…";
        Shown += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var notes = await _updates.FetchReleaseNotesAsync();
            if (notes.Count == 0) { _notes.Text = "No releases found yet."; return; }
            RenderAll(notes);
        }
        catch (Exception ex)
        {
            _notes.Clear();
            _notes.AppendText("Couldn't load release notes.\n\n" + ex.Message +
                              "\n\nUse “View on GitHub” for the full changelog.");
        }
    }

    // The whole changelog in one scrollable column: a version heading per release, then its notes.
    void RenderAll(IReadOnlyList<ReleaseNote> notes)
    {
        _notes.Clear();
        Font baseFont = _notes.Font;
        using var verFont = new Font(baseFont.FontFamily, baseFont.SizeInPoints + 4f, FontStyle.Bold);
        using var h3 = new Font(baseFont.FontFamily, baseFont.SizeInPoints + 1f, FontStyle.Bold);
        using var bold = new Font(baseFont, FontStyle.Bold);

        bool firstRelease = true;
        foreach (var n in notes)
        {
            if (!firstRelease) _notes.AppendText("\n\n");
            SetParagraph(0, 0);

            AppendRun("v" + n.Version, verFont, Ink);
            if (n.Published is { } p) AppendRun("    " + p.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), baseFont, Subtle);
            if (n.Prerelease) AppendRun("   (pre)", baseFont, Subtle);
            _notes.AppendText("\n");

            RenderBody(n.Body, baseFont, h3, bold);
            firstRelease = false;
        }

        _notes.SelectionStart = 0;
        _notes.SelectionLength = 0;
        _notes.ScrollToCaret();
    }

    // Light markdown with soft-wrap: ### headers (bold), "- " bullets (hanging indent), inline
    // **bold**. The CHANGELOG hard-wraps lines mid-sentence; like GitHub, we treat a single
    // newline as a space and only break on a blank line or a new bullet/header, so each bullet
    // renders as ONE wrapped paragraph (the RichTextBox does the wrapping) instead of many lines.
    void RenderBody(string body, Font baseFont, Font h3, Font bold)
    {
        string? bullet = null;          // text of the bullet item being accumulated (null = none)
        var para = new StringBuilder(); // text of a plain paragraph being accumulated

        void FlushParagraph()
        {
            if (para.Length == 0) return;
            SetParagraph(0, 0);
            AppendInline(para.ToString(), baseFont, bold);
            _notes.AppendText("\n");
            para.Clear();
        }
        void FlushBullet()
        {
            if (bullet is null) return;
            SetParagraph(16, 14);
            AppendRun("•  ", baseFont, Subtle);
            AppendInline(bullet, baseFont, bold);
            _notes.AppendText("\n");
            bullet = null;
        }
        void FlushBlock() { FlushBullet(); FlushParagraph(); }

        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { FlushBlock(); continue; } // blank line ends the current block

            if (line.StartsWith("# "))
            {
                FlushBlock();
                int sp = line.IndexOf(' ');
                SetParagraph(0, 0);
                AppendRun(line[(sp + 1)..], h3, Ink);
                _notes.AppendText("\n");
            }
            else if (line.StartsWith("## ") || line.StartsWith("### "))
            {
                FlushBlock();
                int sp = line.IndexOf(' ');
                SetParagraph(0, 0);
                AppendRun(line[(sp + 1)..], h3, Ink);
                _notes.AppendText("\n");
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushBlock();          // a new bullet ends the previous block
                bullet = line[2..];
            }
            else if (bullet != null)
            {
                bullet += " " + line;  // continuation of the current bullet (soft wrap)
            }
            else
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(line);     // continuation of a plain paragraph
            }
        }
        FlushBlock();
    }

    void AppendInline(string text, Font normal, Font bold)
    {
        int i = 0;
        bool isBold = false;
        while (i < text.Length)
        {
            int m = text.IndexOf("**", i, StringComparison.Ordinal);
            if (m < 0) { AppendRun(text[i..], isBold ? bold : normal, Ink); break; }
            if (m > i) AppendRun(text[i..m], isBold ? bold : normal, Ink);
            isBold = !isBold;
            i = m + 2;
        }
    }

    void AppendRun(string text, Font font, Color color)
    {
        _notes.SelectionStart = _notes.TextLength;
        _notes.SelectionLength = 0;
        _notes.SelectionFont = font;
        _notes.SelectionColor = color;
        _notes.AppendText(text);
    }

    void SetParagraph(int indent, int hanging)
    {
        _notes.SelectionStart = _notes.TextLength;
        _notes.SelectionLength = 0;
        _notes.SelectionIndent = indent;
        _notes.SelectionHangingIndent = hanging;
    }
}
