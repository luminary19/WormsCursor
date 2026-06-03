using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Live cursor-appearance editor. Works on a clone of the settings (passed in) and
/// exposes the edited copy via <see cref="Settings"/>; the caller applies it on OK.
/// The preview pane renders the arrow with the same <see cref="ArrowRenderer"/> the
/// engine uses, on both a dark and a light background.
/// </summary>
public sealed class PreferencesForm : Form
{
    const string RepoUrl = "https://github.com/dawidope/WormsCursor";

    const int M = 16;            // outer margin
    const int W = 460;           // base client width (the window grows wider if the preview needs it)
    const int PreviewGap = 14;   // gap between the preview and the controls below it
    const int PreviewCols = 5;   // fixed preview columns; rows grow with the cursor count
    const int CellPad = 14;      // breathing room around each cursor (drawn 1:1, never scaled)
    const int SliderRowH = 56;
    const int ColorRowH = 58;

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly DoubleBufferedPanel _preview;
    readonly Panel _body;        // every control below the preview, moved/resized as one block
    readonly System.Windows.Forms.Timer _previewDebounce; // coalesces rapid slider edits into one render
    int _bodyHeight;             // _body content height (computed once when built)
    int _cellW = 1, _cellH = 1, _rows = 1; // preview grid metrics (set by LayoutPreview)
    int _hoverIndex = -1;        // preview tile under the mouse (-1 = none): its cursor is "borrowed" onto the live pointer
    Bitmap?[] _previews = Array.Empty<Bitmap?>();
    static readonly TestCursor[] PreviewKinds =
    {
        TestCursor.Arrow, TestCursor.Hand, TestCursor.Wait, TestCursor.AppStarting, TestCursor.Help, TestCursor.Cross,
        TestCursor.Ibeam, TestCursor.SizeWE, TestCursor.SizeNS, TestCursor.SizeNWSE, TestCursor.SizeNESW, TestCursor.SizeAll,
        TestCursor.No,
    };

    readonly TrackBar _sizeBar, _thickBar, _radiusBar;
    readonly Label _sizeVal, _thickVal, _radiusVal;
    readonly Button _fillBtn, _outlineBtn;
    readonly ComboBox _testCombo;
    readonly Action<TestCursor> _setTest;
    readonly Action<CursorSettings> _apply;
    readonly UpdateService _updates;
    readonly Button _updateBtn;
    readonly Label _updateStatus;

    public PreferencesForm(CursorSettings working, UpdateService updates,
                           Action<TestCursor> setTestCursor, Action<CursorSettings> applyLive)
    {
        _working = working;
        _updates = updates;
        _setTest = setTestCursor;
        _apply = applyLive;

        // Auto-rescale the whole dialog when it crosses to a monitor with a different scale
        // (the process is PerMonitorV2, but WinForms only auto-scales a form on a DPI change
        // when AutoScaleMode is Font/Dpi — the hand-coded default is Inherit ≈ none, so the
        // window otherwise stays at the DPI it opened on and looks cut off on the other screen).
        AutoScaleMode = AutoScaleMode.Font;

        Text = "WormsCursor — Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        _preview = new DoubleBufferedPanel
        {
            Location = new Point(M, M),
            BackColor = Color.FromArgb(122, 122, 122),
            // No border (it would eat client pixels and trip the scrollbar). AutoScroll is turned
            // on in LayoutWindow ONLY when the grid genuinely can't fit on screen.
        };
        _preview.Paint += PreviewPaint;
        // Hover-to-try: moving over a tile borrows that cursor onto the real pointer
        // (and hides the tile, since the cursor is "now on your pointer"); leaving the
        // grid hands the pointer back to whatever the Test-cursor combo has selected.
        _preview.MouseMove += PreviewMouseMove;
        _preview.MouseLeave += PreviewMouseLeave;
        Controls.Add(_preview);

        // Everything below the preview lives in _body, so the preview can grow (real-size, never
        // scaled) and just push the controls down / widen the window as a single block.
        _body = new Panel { Left = 0, Width = W };
        Controls.Add(_body);

        // Re-rendering all the cursors at real size is cheap when small but heavy at large sizes,
        // so dragging the size slider is debounced: redraw once the slider settles, not per tick.
        _previewDebounce = new System.Windows.Forms.Timer { Interval = 70 };
        _previewDebounce.Tick += (_, _) => { _previewDebounce.Stop(); RenderPreview(); _preview.Invalidate(); };

        int y = 8; // relative to _body's top

        _body.Controls.Add(new Label
        {
            Text = "Tip: hover a cursor tile above to try it live on your pointer.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(M, y),
        });
        y += 24;

        _sizeBar = MakeBar(24, 128, _working.Size);
        _sizeVal = MakeVal();
        AddSliderRow("Cursor size", _sizeBar, _sizeVal, ref y);
        _sizeBar.ValueChanged += (_, _) => { _working.Size = _sizeBar.Value; OnEdited(); };

        _fillBtn = MakeColorButton(ParseOr(_working.FillColor, Color.White));
        _fillBtn.Click += (_, _) => PickColor(_fillBtn, c => _working.FillColor = ToHex(c));
        _outlineBtn = MakeColorButton(ParseOr(_working.OutlineColor, Color.Black));
        _outlineBtn.Click += (_, _) => PickColor(_outlineBtn, c => _working.OutlineColor = ToHex(c));
        AddTwoColorRow("Fill colour", _fillBtn, "Outline colour", _outlineBtn, ref y);

        _thickBar = MakeBar(0, 40, (int)Math.Round(_working.OutlineThickness * 10));
        _thickVal = MakeVal();
        AddSliderRow("Outline thickness", _thickBar, _thickVal, ref y);
        _thickBar.ValueChanged += (_, _) => { _working.OutlineThickness = _thickBar.Value / 10.0; OnEdited(); };

        _radiusBar = MakeBar(0, 120, (int)Math.Round(_working.CornerRadius * 10));
        _radiusVal = MakeVal();
        AddSliderRow("Corner radius (arrow only)", _radiusBar, _radiusVal, ref y);
        _radiusBar.ValueChanged += (_, _) => { _working.CornerRadius = _radiusBar.Value / 10.0; OnEdited(); };

        // Test cursor: forces the chosen cursor on screen so you can see the busy /
        // progress animation on demand (it normally only shows when the OS decides).
        // It reflects the currently APPLIED appearance, not unsaved edits — OK first to
        // test new colours/size. Cleared automatically when this dialog closes.
        _testCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _testCombo.Items.AddRange(new object[]
        {
            "Off (normal)", "Arrow", "Hand", "Busy (wait)", "App starting", "Help (arrow + ?)", "Crosshair", "Text (I-beam)",
            "Resize ↔", "Resize ↕", "Resize ↘↖", "Resize ↗↙", "Move ✥", "Unavailable ⊘",
        });
        _testCombo.SelectedIndex = 0;
        _testCombo.SelectedIndexChanged += (_, _) => _setTest(MapTest(_testCombo.SelectedIndex));
        AddComboRow("Test cursor (force on screen)", _testCombo, ref y);
        FormClosed += (_, _) => _setTest(TestCursor.Off);

        int btnY = y + 8;
        var defaults = new Button { Text = "Defaults", Location = new Point(M, btnY), Size = new Size(90, 30) };
        defaults.Click += (_, _) => ResetDefaults();
        // Right cluster: Apply | OK | Cancel (anchored right so they follow the window edge as it
        // widens). Apply commits edits to the live cursor without closing, so you can tweak
        // size/colour and watch the test cursor update.
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 30), Location = new Point(W - M - 84, btnY), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 30), Location = new Point(W - M - 176, btnY), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var apply = new Button { Text = "Apply", Size = new Size(84, 30), Location = new Point(W - M - 268, btnY), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        apply.Click += (_, _) => _apply(_working);
        _body.Controls.Add(defaults);
        _body.Controls.Add(apply);
        _body.Controls.Add(ok);
        _body.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // Footer line 1: version + "Check for updates" (moved here off the button row to
        // make room for Apply) + update status. Line 2: the repo link on its own line.
        int footerY = btnY + 30 + 14;
        var version = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "v" + AppVersion(), Location = new Point(M, footerY + 5) };
        _updateBtn = new Button { Text = "Check for updates", Location = new Point(M + 50, footerY), Size = new Size(140, 26) };
        _updateBtn.Click += OnCheckUpdates;
        _updateStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty, Location = new Point(M + 50 + 148, footerY + 5) };
        var link = new LinkLabel { AutoSize = true, Text = "github.com/dawidope/WormsCursor", Location = new Point(M, footerY + 38) };
        link.LinkClicked += (_, _) => OpenUrl(RepoUrl);
        _body.Controls.Add(version);
        _body.Controls.Add(_updateBtn);
        _body.Controls.Add(_updateStatus);
        _body.Controls.Add(link);

        _bodyHeight = footerY + 38 + link.PreferredHeight + M; // bottom margin under the link line

        UpdateLabels();
        MeasureCells();  // fix the grid cell size for the largest cursor the slider allows (128 px)
        LayoutWindow();  // set the window size ONCE for that grid — no dynamic resizing afterwards
        RenderPreview();
        _preview.Invalidate();
    }

    // ---------- layout helpers ----------
    static TrackBar MakeBar(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        TickStyle = TickStyle.None,
        AutoSize = false,
    };

    static Label MakeVal() => new()
    {
        AutoSize = false,
        Size = new Size(56, 18),
        TextAlign = ContentAlignment.MiddleRight,
        ForeColor = SystemColors.ControlText,
    };

    static Button MakeColorButton(Color c)
    {
        var b = new Button { BackColor = c, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Size = new Size(150, 28) };
        b.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    void AddSliderRow(string caption, TrackBar bar, Label val, ref int y)
    {
        _body.Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(M, y) });
        int barY = y + 22;
        bar.SetBounds(M, barY, W - 2 * M - 68, 28);
        bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; // stretch with the window
        val.SetBounds(W - M - 56, barY + 5, 56, 18);
        val.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _body.Controls.Add(bar);
        _body.Controls.Add(val);
        y += SliderRowH;
    }

    void AddComboRow(string caption, ComboBox combo, ref int y)
    {
        _body.Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(M, y) });
        combo.SetBounds(M, y + 22, W - 2 * M, 24);
        combo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _body.Controls.Add(combo);
        y += SliderRowH;
    }

    static TestCursor MapTest(int index) => index switch
    {
        1 => TestCursor.Arrow,
        2 => TestCursor.Hand,
        3 => TestCursor.Wait,
        4 => TestCursor.AppStarting,
        5 => TestCursor.Help,
        6 => TestCursor.Cross,
        7 => TestCursor.Ibeam,
        8 => TestCursor.SizeWE,
        9 => TestCursor.SizeNS,
        10 => TestCursor.SizeNWSE,
        11 => TestCursor.SizeNESW,
        12 => TestCursor.SizeAll,
        13 => TestCursor.No,
        _ => TestCursor.Off,
    };

    // Two colour pickers side by side on one row (Fill | Outline).
    void AddTwoColorRow(string capA, Button btnA, string capB, Button btnB, ref int y)
    {
        int half = (W - 2 * M) / 2;
        int swatchW = half - 20;
        _body.Controls.Add(new Label { Text = capA, AutoSize = true, Location = new Point(M, y) });
        btnA.SetBounds(M, y + 24, swatchW, 28);
        _body.Controls.Add(btnA);

        int x2 = M + half;
        _body.Controls.Add(new Label { Text = capB, AutoSize = true, Location = new Point(x2, y) });
        btnB.SetBounds(x2, y + 24, swatchW, 28);
        _body.Controls.Add(btnB);

        y += ColorRowH;
    }

    // ---------- behaviour ----------
    void OnEdited()
    {
        UpdateLabels();         // instant feedback on the value label
        _previewDebounce.Stop();
        _previewDebounce.Start(); // heavy re-render fires once the slider settles (no per-tick lag)
    }

    // Fixes the grid cell to the largest cursor the size slider allows (128 px), so the window
    // can be sized ONCE and never has to resize. Smaller sizes just draw smaller, centred in the
    // (fixed) cell — real size, never scaled.
    void MeasureCells()
    {
        var probe = _working.Clone();
        probe.Size = 128;
        probe.Normalize();
        using var arrowBase = ArrowRenderer.DrawArrow(probe);
        int maxW = 1, maxH = 1;
        foreach (var kind in PreviewKinds)
        {
            using Bitmap full = kind switch
            {
                TestCursor.Arrow => ArrowRenderer.DrawArrow(probe),
                TestCursor.Hand => HandRenderer.DrawHand(probe),
                var k => ProgressRenderer.RenderRest(probe, k, arrowBase),
            };
            using var t = Trim(full);
            maxW = Math.Max(maxW, t.Width);
            maxH = Math.Max(maxH, t.Height);
        }
        _cellW = maxW + 2 * CellPad;
        _cellH = maxH + 2 * CellPad;
    }

    // Sizes the window once for the fixed 5-column grid (real size, never scaled). Capped to the
    // screen so it can't run off-screen; if the grid is taller than that, the preview scrolls.
    void LayoutWindow()
    {
        int n = PreviewKinds.Length;
        _rows = Math.Max(1, (n + PreviewCols - 1) / PreviewCols);
        int contentW = PreviewCols * _cellW, contentH = _rows * _cellH;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int maxPanelW = wa.Width - 2 * M;
        int maxPanelH = wa.Height - 2 * M - PreviewGap - _bodyHeight - 64; // title bar + taskbar headroom
        bool overflow = contentW > maxPanelW || contentH > maxPanelH;

        // Scroll ONLY when the grid can't fit (otherwise no scrollbar at all). When it fits, the
        // panel is exactly the content size, so nothing overflows.
        _preview.AutoScroll = overflow;
        _preview.AutoScrollMinSize = overflow ? new Size(contentW, contentH) : Size.Empty;
        int sb = overflow ? SystemInformation.VerticalScrollBarWidth + 2 : 0;

        int panelW = Math.Min(contentW + sb, maxPanelW);
        int panelH = Math.Min(contentH, Math.Max(160, maxPanelH));
        int clientW = Math.Max(W, panelW + 2 * M);
        int previewX = (clientW - panelW) / 2; // centre the grid if the window's min width is wider

        _preview.SetBounds(previewX, M, panelW, panelH);
        _body.SetBounds(0, M + panelH + PreviewGap, clientW, _bodyHeight);
        ClientSize = new Size(clientW, _body.Bottom);
    }

    void UpdateLabels()
    {
        _sizeVal.Text = $"{_working.Size} px";
        _thickVal.Text = _working.OutlineThickness.ToString("0.0");
        _radiusVal.Text = _working.CornerRadius.ToString("0.0");
    }

    void RenderPreview()
    {
        foreach (var b in _previews) b?.Dispose();
        // All cursors at their ACTUAL size. Arrow/Hand from their own renderers; the
        // composited ones rendered in their "at rest" pose. Each is then trimmed to its
        // ink so the padded 2x canvas (for the swing) doesn't shrink it in the preview.
        using var arrowBase = ArrowRenderer.DrawArrow(_working); // base for the composited cursors
        _previews = new Bitmap?[PreviewKinds.Length];
        for (int i = 0; i < PreviewKinds.Length; i++)
        {
            using Bitmap full = PreviewKinds[i] switch
            {
                TestCursor.Arrow => ArrowRenderer.DrawArrow(_working),
                TestCursor.Hand => HandRenderer.DrawHand(_working),
                var k => ProgressRenderer.RenderRest(_working, k, arrowBase),
            };
            _previews[i] = Trim(full);
        }
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TranslateTransform(_preview.AutoScrollPosition.X, _preview.AutoScrollPosition.Y); // honour scroll

        int n = _previews.Length;
        if (n == 0) return;
        int cols = PreviewCols;
        // Mid-grey backdrop over the whole grid (shows a light fill and a dark outline at once).
        using (var bg = new SolidBrush(Color.FromArgb(122, 122, 122)))
            g.FillRectangle(bg, 0, 0, cols * _cellW, _rows * _cellH);

        for (int i = 0; i < n; i++)
        {
            int col = i % cols, row = i / cols;
            if (i == _hoverIndex)
            {
                // This tile's cursor is borrowed onto the live pointer — leave an empty
                // dashed pocket so it's obvious where it went (and that hovering did something).
                var cell = new Rectangle(col * _cellW + 3, row * _cellH + 3, _cellW - 7, _cellH - 7);
                using (var hole = new SolidBrush(Color.FromArgb(96, 96, 96)))
                    g.FillRectangle(hole, cell);
                using (var pen = new Pen(Color.FromArgb(185, 185, 185)) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(pen, cell);
                continue;
            }
            var bmp = _previews[i];
            if (bmp is null) continue;
            int cx = col * _cellW + _cellW / 2;
            int cy = row * _cellH + _cellH / 2;
            g.DrawImageUnscaled(bmp, cx - bmp.Width / 2, cy - bmp.Height / 2); // real size, never scaled
        }
    }

    // Hovering a tile "borrows" that cursor onto the real pointer (live preview); moving
    // off a tile within the grid (idx < 0) hands the pointer back to the Test-cursor combo.
    void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        int idx = HitTestPreview(e.Location);
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
        _setTest(idx >= 0 ? PreviewKinds[idx] : MapTest(_testCombo.SelectedIndex));
        _preview.Invalidate();
    }

    // Left the grid entirely: restore whatever the Test-cursor combo last selected.
    void PreviewMouseLeave(object? sender, EventArgs e)
    {
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        _setTest(MapTest(_testCombo.SelectedIndex));
        _preview.Invalidate();
    }

    // Maps a point in the preview panel (client coords) to a cursor-tile index, or -1 for
    // none. AutoScrollPosition is <= 0 when scrolled, so subtracting it yields content coords
    // (mirrors the TranslateTransform in PreviewPaint).
    int HitTestPreview(Point pt)
    {
        int x = pt.X - _preview.AutoScrollPosition.X;
        int y = pt.Y - _preview.AutoScrollPosition.Y;
        if (x < 0 || y < 0) return -1;
        int col = x / _cellW, row = y / _cellH;
        if (col < 0 || col >= PreviewCols) return -1;
        int idx = row * PreviewCols + col;
        return idx >= 0 && idx < _previews.Length ? idx : -1;
    }

    // Crops a bitmap to its non-transparent bounds (so the cursor's big transparent
    // canvas doesn't dominate the preview layout). Returns a clone if fully transparent.
    static Bitmap Trim(Bitmap src)
    {
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf = new byte[stride * src.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        src.UnlockBits(data);

        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (buf[y * stride + x * 4 + 3] > 8) // alpha
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < minX) return (Bitmap)src.Clone();

        var crop = new Bitmap(maxX - minX + 1, maxY - minY + 1);
        using var cg = Graphics.FromImage(crop);
        cg.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height),
                     Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), GraphicsUnit.Pixel);
        return crop;
    }

    void PickColor(Button btn, Action<Color> assign)
    {
        using var dlg = new ColorDialog { Color = btn.BackColor, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            btn.BackColor = dlg.Color;
            assign(dlg.Color);
            OnEdited();
        }
    }

    void ResetDefaults()
    {
        _working.CopyFrom(new CursorSettings());
        _sizeBar.Value = Math.Clamp(_working.Size, _sizeBar.Minimum, _sizeBar.Maximum);
        _thickBar.Value = Math.Clamp((int)Math.Round(_working.OutlineThickness * 10), _thickBar.Minimum, _thickBar.Maximum);
        _radiusBar.Value = Math.Clamp((int)Math.Round(_working.CornerRadius * 10), _radiusBar.Minimum, _radiusBar.Maximum);
        _fillBtn.BackColor = ParseOr(_working.FillColor, Color.White);
        _outlineBtn.BackColor = ParseOr(_working.OutlineColor, Color.Black);
        OnEdited();
    }

    async void OnCheckUpdates(object? sender, EventArgs e)
    {
        _updateBtn.Enabled = false;
        SetStatus("Checking…", error: false);
        try
        {
            var r = await _updates.CheckAsync();
            switch (r.Availability)
            {
                case UpdateAvailability.NotInstalled:
                    SetStatus("Dev build — opening Releases…", error: false);
                    _updates.OpenReleasesPage();
                    break;
                case UpdateAvailability.UpToDate:
                    SetStatus($"Up to date (v{_updates.CurrentVersionText})", error: false);
                    break;
                case UpdateAvailability.Available:
                    SetStatus($"Update v{r.AvailableVersion} — installing, will restart…", error: false);
                    await _updates.ApplyAsync(r.VelopackInfo!); // downloads + restarts the app
                    SetStatus("Downloaded, but the restart didn't happen", error: true);
                    break;
                case UpdateAvailability.Failed:
                    SetStatus("Update check failed", error: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Update error: " + ex.Message, error: true);
        }
        finally
        {
            _updateBtn.Enabled = true;
        }
    }

    void SetStatus(string text, bool error)
    {
        _updateStatus.Text = text;
        _updateStatus.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    // ---------- small utilities ----------
    static Color ParseOr(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static string AppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewDebounce?.Dispose();
            foreach (var b in _previews) b?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A panel that double-buffers its client area so the live preview repaints
    /// without flicker while sliders are dragged.</summary>
    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
