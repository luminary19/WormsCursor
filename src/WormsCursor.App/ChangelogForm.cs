using System.Globalization;
using WormsCursor.App.Services;

namespace WormsCursor.App;

/// <summary>
/// "What's new" dialog: lists the repo's published releases (newest first) and shows the
/// selected one's notes. Notes come from the GitHub Releases API via
/// <see cref="UpdateService.FetchReleaseNotesAsync"/> — each release body is the CHANGELOG
/// section the release workflow published. On any fetch failure (offline / rate-limited /
/// dev build) it shows a short message and the "View on GitHub" button stays available.
/// </summary>
public sealed class ChangelogForm : Form
{
    readonly UpdateService _updates;
    readonly string? _highlightVersion;
    readonly string _installedVersion;

    readonly ListBox _versions;
    readonly RichTextBox _notes;
    readonly Button _githubBtn, _closeBtn;

    IReadOnlyList<ReleaseNote> _loaded = Array.Empty<ReleaseNote>();

    public ChangelogForm(UpdateService updates, string? highlightVersion = null)
    {
        _updates = updates;
        _highlightVersion = highlightVersion;
        _installedVersion = updates.CurrentVersionText;

        AutoScaleMode = AutoScaleMode.Font;
        Text = "WormsCursor — What's new";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = true;
        MinimumSize = new Size(480, 360);
        ClientSize = new Size(620, 460);

        _versions = new ListBox
        {
            IntegralHeight = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _versions.SelectedIndexChanged += (_, _) => ShowSelected();

        _notes = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            DetectUrls = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };

        _githubBtn = new Button { Text = "View on GitHub", Size = new Size(130, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _githubBtn.Click += (_, _) => _updates.OpenReleasesPage();
        _closeBtn = new Button { Text = "Close", DialogResult = DialogResult.OK, Size = new Size(90, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        AcceptButton = _closeBtn;
        CancelButton = _closeBtn;

        Controls.Add(_versions);
        Controls.Add(_notes);
        Controls.Add(_githubBtn);
        Controls.Add(_closeBtn);

        LayoutControls();
        Resize += (_, _) => LayoutControls();

        _notes.Text = "Loading release notes…";
        Shown += async (_, _) => await LoadAsync();
    }

    void LayoutControls()
    {
        const int M = 12, listW = 180, gap = 10, btnRow = 30 + M;
        int contentH = ClientSize.Height - 2 * M - btnRow;
        _versions.SetBounds(M, M, listW, Math.Max(40, contentH));
        _notes.SetBounds(M + listW + gap, M, Math.Max(80, ClientSize.Width - M - (M + listW + gap)), Math.Max(40, contentH));
        int by = ClientSize.Height - M - 30;
        _githubBtn.Location = new Point(M, by);
        _closeBtn.Location = new Point(ClientSize.Width - M - _closeBtn.Width, by);
    }

    async Task LoadAsync()
    {
        try
        {
            var notes = await _updates.FetchReleaseNotesAsync();
            _loaded = notes;
            if (notes.Count == 0)
            {
                _notes.Text = "No releases found yet.";
                return;
            }

            _versions.BeginUpdate();
            _versions.Items.Clear();
            foreach (var n in notes)
                _versions.Items.Add(DisplayName(n));
            _versions.EndUpdate();

            int select = 0;
            if (!string.IsNullOrEmpty(_highlightVersion))
            {
                int i = IndexOfVersion(_highlightVersion!);
                if (i >= 0) select = i;
            }
            _versions.SelectedIndex = select; // fires ShowSelected
        }
        catch (Exception ex)
        {
            // Offline / rate-limited / dev build with no network: degrade to the GitHub link.
            _notes.Clear();
            _notes.Text = "Couldn't load release notes:\n" + ex.Message +
                          "\n\nUse \"View on GitHub\" to see the full changelog online.";
        }
    }

    int IndexOfVersion(string version)
    {
        for (int i = 0; i < _loaded.Count; i++)
            if (string.Equals(_loaded[i].Version, version, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    string DisplayName(ReleaseNote n)
    {
        string date = n.Published is { } p ? p.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        string label = string.IsNullOrEmpty(date) ? n.Version : $"{n.Version} — {date}";
        if (string.Equals(n.Version, _installedVersion, StringComparison.OrdinalIgnoreCase)) label += "  (installed)";
        else if (n.Prerelease) label += "  (pre)";
        return label;
    }

    void ShowSelected()
    {
        int i = _versions.SelectedIndex;
        if (i < 0 || i >= _loaded.Count) return;
        RenderMarkdown(_notes, _loaded[i].Body);
        _notes.SelectionStart = 0;
        _notes.ScrollToCaret();
    }

    // Minimal markdown render into a RichTextBox: ## / ### headers (bold, larger), "- " bullets,
    // and inline **bold**. Enough for our Keep-a-Changelog notes; anything else shows as plain text.
    static void RenderMarkdown(RichTextBox rtb, string body)
    {
        rtb.Clear();
        Font baseFont = rtb.Font;
        using var h2 = new Font(baseFont.FontFamily, baseFont.Size + 2f, FontStyle.Bold);
        using var h3 = new Font(baseFont.FontFamily, baseFont.Size + 0.5f, FontStyle.Bold);
        using var boldFont = new Font(baseFont, FontStyle.Bold);

        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { rtb.AppendText("\n"); continue; }

            if (line.StartsWith("### ")) { AppendRun(rtb, line[4..], h3); rtb.AppendText("\n"); continue; }
            if (line.StartsWith("## ")) { AppendRun(rtb, line[3..], h2); rtb.AppendText("\n"); continue; }
            if (line.StartsWith("# ")) { AppendRun(rtb, line[2..], h2); rtb.AppendText("\n"); continue; }

            string text = line;
            if (text.StartsWith("- ") || text.StartsWith("* ")) { rtb.AppendText("   •  "); text = text[2..]; }
            AppendInline(rtb, text, baseFont, boldFont);
            rtb.AppendText("\n");
        }
    }

    // Appends text splitting on **…** markers, toggling bold for the enclosed runs.
    static void AppendInline(RichTextBox rtb, string text, Font normal, Font bold)
    {
        int i = 0;
        bool isBold = false;
        while (i < text.Length)
        {
            int marker = text.IndexOf("**", i, StringComparison.Ordinal);
            if (marker < 0) { AppendRun(rtb, text[i..], isBold ? bold : normal); break; }
            if (marker > i) AppendRun(rtb, text[i..marker], isBold ? bold : normal);
            isBold = !isBold;
            i = marker + 2;
        }
    }

    static void AppendRun(RichTextBox rtb, string text, Font font)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionFont = font;
        rtb.AppendText(text);
    }
}
