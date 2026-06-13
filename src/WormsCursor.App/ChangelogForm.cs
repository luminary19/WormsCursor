using System.Drawing.Drawing2D;
using System.Globalization;
using WormsCursor.App.Services;

namespace WormsCursor.App;

/// <summary>
/// "What's new" dialog: a sidebar of releases (newest first) and the selected one's notes.
/// Notes come from the GitHub Releases API via <see cref="UpdateService.FetchReleaseNotesAsync"/>
/// (each release body is the CHANGELOG section the release workflow published). It's hand-styled
/// — owner-drawn version list, light section headers, flat accent buttons — to look a bit less
/// like raw WinForms. On any fetch failure it shows a short message and the "View on GitHub"
/// button stays available.
/// </summary>
public sealed class ChangelogForm : Form
{
    // A small, restrained palette so the dialog reads as a designed surface, not default chrome.
    static readonly Color Accent    = Color.FromArgb(74, 122, 235);
    static readonly Color Ink       = Color.FromArgb(32, 33, 36);
    static readonly Color Subtle    = Color.FromArgb(120, 124, 130);
    static readonly Color DividerCol = Color.FromArgb(228, 230, 234);
    static readonly Color SidebarBg = Color.FromArgb(247, 248, 250);
    static readonly Color SelBg     = Color.FromArgb(233, 239, 252);

    readonly UpdateService _updates;
    readonly string? _highlightVersion;
    readonly string _installedVersion;

    readonly DoubleBufferedListBox _versions;
    readonly RichTextBox _notes;
    readonly Button _githubBtn, _closeBtn;
    readonly Panel _footer;

    IReadOnlyList<ReleaseNote> _loaded = Array.Empty<ReleaseNote>();

    public ChangelogForm(UpdateService updates, string? highlightVersion = null)
    {
        _updates = updates;
        _highlightVersion = highlightVersion;
        _installedVersion = updates.CurrentVersionText;

        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9f);
        Text = "WormsCursor — What's new";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MinimumSize = new Size(520, 380);
        ClientSize = new Size(660, 480);
        BackColor = Color.White;

        // --- centre: a content band (sidebar + notes) that fills between header and footer ---
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        var notesHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18, 14, 16, 12) };
        _notes = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Ink,
            DetectUrls = false,
        };
        notesHost.Controls.Add(_notes);

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 196, BackColor = SidebarBg, Padding = new Padding(8, 8, 0, 8) };
        sidebar.Paint += (_, e) => DrawDivider(e, sidebar, right: true);
        _versions = new DoubleBufferedListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = SidebarBg,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 48,
            IntegralHeight = false,
        };
        _versions.DrawItem += DrawVersionItem;
        _versions.SelectedIndexChanged += (_, _) => ShowSelected();
        sidebar.Controls.Add(_versions);

        content.Controls.Add(notesHost); // Fill first…
        content.Controls.Add(sidebar);   // …then the left edge

        // --- header ---
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.White };
        header.Paint += (_, e) => DrawDivider(e, header, bottom: true);
        header.Controls.Add(new Label
        {
            Text = "What's new", AutoSize = true, ForeColor = Ink,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold), Location = new Point(18, 11),
        });
        header.Controls.Add(new Label
        {
            Text = "WormsCursor " + _installedVersion, AutoSize = true,
            ForeColor = Subtle, Location = new Point(20, 41),
        });

        // --- footer ---
        _footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.White };
        _footer.Paint += (_, e) => DrawDivider(e, _footer, top: true);
        _githubBtn = new Button { Text = "View on GitHub", Size = new Size(140, 32), Anchor = AnchorStyles.Left | AnchorStyles.Top };
        _githubBtn.Click += (_, _) => _updates.OpenReleasesPage();
        StyleSecondary(_githubBtn);
        _closeBtn = new Button { Text = "Close", Size = new Size(96, 32), DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        StyleAccent(_closeBtn);
        _footer.Controls.Add(_githubBtn);
        _footer.Controls.Add(_closeBtn);
        _footer.Resize += (_, _) => PositionFooter();

        AcceptButton = _closeBtn;
        CancelButton = _closeBtn;

        Controls.Add(content); // Fill first…
        Controls.Add(header);  // …then top
        Controls.Add(_footer); // …then bottom

        PositionFooter();
        _notes.Text = "Loading release notes…";
        Shown += async (_, _) => await LoadAsync();
    }

    void PositionFooter()
    {
        int y = (_footer.ClientSize.Height - _closeBtn.Height) / 2;
        _githubBtn.Location = new Point(16, y);
        _closeBtn.Location = new Point(_footer.ClientSize.Width - 16 - _closeBtn.Width, y);
    }

    async Task LoadAsync()
    {
        try
        {
            var notes = await _updates.FetchReleaseNotesAsync();
            _loaded = notes;
            if (notes.Count == 0) { _notes.Text = "No releases found yet."; return; }

            _versions.BeginUpdate();
            _versions.Items.Clear();
            foreach (var n in notes) _versions.Items.Add(n.Version); // owner-drawn; item text is unused
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
            _notes.Clear();
            _notes.SelectionColor = Ink;
            _notes.AppendText("Couldn't load release notes.\n\n" + ex.Message +
                              "\n\nUse “View on GitHub” to see the full changelog online.");
        }
    }

    int IndexOfVersion(string version)
    {
        for (int i = 0; i < _loaded.Count; i++)
            if (string.Equals(_loaded[i].Version, version, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    void ShowSelected()
    {
        int i = _versions.SelectedIndex;
        if (i < 0 || i >= _loaded.Count) return;
        RenderMarkdown(_notes, _loaded[i].Body);
    }

    // ---------- owner-drawn version row ----------
    void DrawVersionItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _loaded.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var n = _loaded[e.Index];
        var r = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(selected ? SelBg : SidebarBg))
            g.FillRectangle(bg, r);
        if (selected)
            using (var bar = new SolidBrush(Accent))
                g.FillRectangle(bar, r.X, r.Y, 3, r.Height);

        int tx = r.X + 14;
        using var vf = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, "v" + n.Version, vf, new Point(tx, r.Y + 7), Ink, TextFormatFlags.NoPrefix);

        string date = n.Published is { } p ? p.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "";
        TextRenderer.DrawText(g, date, Font, new Point(tx, r.Y + 26), Subtle, TextFormatFlags.NoPrefix);

        if (string.Equals(n.Version, _installedVersion, StringComparison.OrdinalIgnoreCase))
            DrawPill(g, r, "installed", Accent);
        else if (n.Prerelease)
            DrawPill(g, r, "pre", Subtle);
    }

    static void DrawPill(Graphics g, Rectangle row, string text, Color color)
    {
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        var sz = TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPrefix);
        int h = sz.Height + 4, w = sz.Width + 14;
        var box = new Rectangle(row.Right - 12 - w, row.Y + 9, w, h);
        using (var path = RoundedRect(box, h / 2))
        using (var b = new SolidBrush(Color.FromArgb(30, color)))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, text, f, box, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    // ---------- minimal markdown -> RichTextBox ----------
    // ## / ### headers (bold; ### in the accent colour), "- " bullets with a hanging indent, and
    // inline **bold**. Enough for our Keep-a-Changelog notes; anything else renders as plain text.
    static void RenderMarkdown(RichTextBox rtb, string body)
    {
        rtb.Clear();
        Font baseFont = rtb.Font;
        using var h2 = new Font(baseFont.FontFamily, baseFont.SizeInPoints + 3f, FontStyle.Bold);
        using var h3 = new Font(baseFont.FontFamily, baseFont.SizeInPoints + 1f, FontStyle.Bold);
        using var boldFont = new Font(baseFont, FontStyle.Bold);

        bool first = true;
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd();
            SetParagraph(rtb, 0, 0);

            if (line.Length == 0) { rtb.AppendText("\n"); continue; }

            if (line.StartsWith("### ")) { if (!first) rtb.AppendText("\n"); AppendRun(rtb, line[4..], h3, Accent); rtb.AppendText("\n"); first = false; continue; }
            if (line.StartsWith("## "))  { if (!first) rtb.AppendText("\n"); AppendRun(rtb, line[3..], h2, Ink);    rtb.AppendText("\n"); first = false; continue; }
            if (line.StartsWith("# "))   { if (!first) rtb.AppendText("\n"); AppendRun(rtb, line[2..], h2, Ink);    rtb.AppendText("\n"); first = false; continue; }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                SetParagraph(rtb, 16, 14);
                AppendRun(rtb, "•  ", baseFont, Accent);
                AppendInline(rtb, line[2..], baseFont, boldFont, Ink);
                rtb.AppendText("\n");
            }
            else
            {
                AppendInline(rtb, line, baseFont, boldFont, Ink);
                rtb.AppendText("\n");
            }
            first = false;
        }

        rtb.SelectionStart = 0;
        rtb.SelectionLength = 0;
        rtb.ScrollToCaret();
    }

    static void SetParagraph(RichTextBox rtb, int indent, int hanging)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionIndent = indent;
        rtb.SelectionHangingIndent = hanging;
    }

    static void AppendInline(RichTextBox rtb, string text, Font normal, Font bold, Color color)
    {
        int i = 0;
        bool isBold = false;
        while (i < text.Length)
        {
            int m = text.IndexOf("**", i, StringComparison.Ordinal);
            if (m < 0) { AppendRun(rtb, text[i..], isBold ? bold : normal, color); break; }
            if (m > i) AppendRun(rtb, text[i..m], isBold ? bold : normal, color);
            isBold = !isBold;
            i = m + 2;
        }
    }

    static void AppendRun(RichTextBox rtb, string text, Font font, Color color)
    {
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionFont = font;
        rtb.SelectionColor = color;
        rtb.AppendText(text);
    }

    // ---------- styling helpers ----------
    static void DrawDivider(PaintEventArgs e, Control c, bool top = false, bool bottom = false, bool right = false)
    {
        using var p = new Pen(DividerCol);
        if (top) e.Graphics.DrawLine(p, 0, 0, c.Width, 0);
        if (bottom) e.Graphics.DrawLine(p, 0, c.Height - 1, c.Width, c.Height - 1);
        if (right) e.Graphics.DrawLine(p, c.Width - 1, 0, c.Width - 1, c.Height);
    }

    static void StyleAccent(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Accent, 0.15f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Accent, 0.05f);
    }

    static void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = DividerCol;
        b.FlatAppearance.BorderSize = 1;
        b.BackColor = Color.White;
        b.ForeColor = Ink;
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = SidebarBg;
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    sealed class DoubleBufferedListBox : ListBox
    {
        public DoubleBufferedListBox() => DoubleBuffered = true;
    }
}
