using System.Globalization;
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

    public ChangelogForm(UpdateService updates, string? highlightVersion = null)
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
        BackColor = Color.White;

        var notesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 10, 8), BackColor = Color.White };
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
            BackColor = Color.White,
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

    // Light markdown: ### headers (bold), "- " bullets with a hanging indent, inline **bold**.
    void RenderBody(string body, Font baseFont, Font h3, Font bold)
    {
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            SetParagraph(0, 0);
            if (line.Length == 0) { _notes.AppendText("\n"); continue; }

            if (line.StartsWith("### ")) { AppendRun(line[4..], h3, Ink); _notes.AppendText("\n"); continue; }
            if (line.StartsWith("## "))  { AppendRun(line[3..], h3, Ink); _notes.AppendText("\n"); continue; }
            if (line.StartsWith("# "))   { AppendRun(line[2..], h3, Ink); _notes.AppendText("\n"); continue; }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                SetParagraph(16, 14);
                AppendRun("•  ", baseFont, Subtle);
                AppendInline(line[2..], baseFont, bold);
                _notes.AppendText("\n");
            }
            else
            {
                AppendInline(line, baseFont, bold);
                _notes.AppendText("\n");
            }
        }
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
