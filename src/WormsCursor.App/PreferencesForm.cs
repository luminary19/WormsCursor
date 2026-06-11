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
    const int W = 460;           // minimum client width (the window grows wider for the 7-col preview)
    const int PreviewGap = 14;   // gap between the preview and the controls below it
    const int PreviewCols = 7;   // fixed preview columns: 13 cursors -> 2 tidy rows (matches the README sheet)
    const int CellPad = 14;      // breathing room around each cursor (drawn 1:1, never scaled)
    const int ColGap = 28;       // gap between the two control columns below the preview
    const int RowH = 58;         // height of one control row (caption + control + gap)
    const int ShowtimeLeadIn = 3; // seconds counted down before Showtime forces the first cursor
    const int ShowtimeStepMs = 1500; // dwell on each cursor once cycling starts

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly DoubleBufferedPanel _preview;
    readonly Panel _body;        // every control below the preview, moved/resized as one block
    readonly System.Windows.Forms.Timer _previewDebounce; // coalesces rapid slider edits into one render
    readonly System.Windows.Forms.Timer _showtime;        // hands-free demo: forces each test cursor in turn
    int _bodyHeight;             // _body content height (computed once when built)
    int _cellW = 1, _cellH = 1, _rows = 1; // preview grid metrics (set by LayoutPreview)
    int _hoverIndex = -1;        // preview tile under the mouse (-1 = none): its cursor is "borrowed" onto the live pointer

    // --- showtime state (the auto-cycling demo) ---
    readonly bool _showtimeLoops = true; // false = one pass through all cursors, then auto-stop
    bool _showtimeRunning;       // active (covers both the lead-in countdown and the cycling)
    bool _suppressTestCombo;     // guard: programmatic combo moves during showtime mustn't re-force a cursor
    int _showtimeStep = -1;      // -1 during the lead-in, else the index into PreviewKinds
    int _showtimeCountdown;      // seconds left in the lead-in
    int _preShowtimeIndex;       // combo selection to restore when showtime ends
    Bitmap?[] _previews = Array.Empty<Bitmap?>();
    static readonly TestCursor[] PreviewKinds =
    {
        TestCursor.Arrow, TestCursor.Hand, TestCursor.Wait, TestCursor.AppStarting, TestCursor.Help, TestCursor.Cross,
        TestCursor.Ibeam, TestCursor.SizeWE, TestCursor.SizeNS, TestCursor.SizeNWSE, TestCursor.SizeNESW, TestCursor.SizeAll,
        TestCursor.No,
    };

    // Cursors shown in the showtime cycle, in order — currently the full preview set. To leave
    // one out of the demo, drop it here (e.g. `k => k != TestCursor.Wait` skips the plain spinner).
    static readonly TestCursor[] ShowtimeKinds = Array.FindAll(PreviewKinds, _ => true);

    readonly TrackBar _sizeBar, _thickBar, _radiusBar;
    readonly Label _sizeVal, _thickVal, _radiusVal;
    readonly Label _sizeCap, _thickCap, _radiusCap, _fillCap, _outlineCap, _testCap;
    readonly Button _fillBtn, _outlineBtn;
    readonly ComboBox _testCombo;
    readonly Button _showtimeBtn;
    readonly Label _tip;
    readonly Button _defaultsBtn, _applyBtn, _okBtn, _cancelBtn;
    readonly Label _version;
    readonly LinkLabel _link;
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

        // The controls are CREATED here but POSITIONED by LayoutBody once the final window
        // width is known (the window widens to fit the 7-column preview). Layout is two
        // columns — sliders left, colours + test on the right — so the dialog stays wide
        // and short rather than a tall single stack.

        // Full-width tip above the two columns.
        _tip = MakeCaption("Tip: hover a cursor tile above to try it live on your pointer.");
        _tip.ForeColor = SystemColors.GrayText;

        // --- left column: the three numeric sliders ---
        _sizeBar = MakeBar(24, 128, _working.Size);
        _sizeVal = MakeVal();
        _sizeCap = MakeCaption("Cursor size");
        _sizeBar.ValueChanged += (_, _) => { _working.Size = _sizeBar.Value; OnEdited(); };

        _thickBar = MakeBar(0, 40, (int)Math.Round(_working.OutlineThickness * 10));
        _thickVal = MakeVal();
        _thickCap = MakeCaption("Outline thickness");
        _thickBar.ValueChanged += (_, _) => { _working.OutlineThickness = _thickBar.Value / 10.0; OnEdited(); };

        _radiusBar = MakeBar(0, 120, (int)Math.Round(_working.CornerRadius * 10));
        _radiusVal = MakeVal();
        _radiusCap = MakeCaption("Corner radius (arrow only)");
        _radiusBar.ValueChanged += (_, _) => { _working.CornerRadius = _radiusBar.Value / 10.0; OnEdited(); };

        // --- right column: the two colours + the test-cursor combo ---
        _fillBtn = MakeColorButton(ParseOr(_working.FillColor, Color.White));
        _fillBtn.Click += (_, _) => PickColor(_fillBtn, c => _working.FillColor = ToHex(c));
        _fillCap = MakeCaption("Fill colour");

        _outlineBtn = MakeColorButton(ParseOr(_working.OutlineColor, Color.Black));
        _outlineBtn.Click += (_, _) => PickColor(_outlineBtn, c => _working.OutlineColor = ToHex(c));
        _outlineCap = MakeCaption("Outline colour");

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
        _testCombo.SelectedIndexChanged += (_, _) => { if (!_suppressTestCombo) _setTest(MapTest(_testCombo.SelectedIndex)); };
        _testCap = MakeCaption("Test cursor (force on screen)");

        // Showtime: a hands-free demo for recording. After a 3-2-1 lead-in (time to start your
        // capture) it forces each test cursor in turn, one per second, looping until you Stop.
        // Move the mouse while it runs so the motion-driven animations actually play. The combo
        // follows along (disabled) to show the live cursor's name; the button doubles as Stop.
        _showtimeBtn = new Button { Text = "▶ Showtime" };
        _showtimeBtn.Click += (_, _) => ToggleShowtime();
        _showtime = new System.Windows.Forms.Timer { Interval = 1000 }; // 1s during the lead-in, then ShowtimeStepMs
        _showtime.Tick += OnShowtimeTick;

        // Stop forcing any test cursor — and halt showtime — when the dialog closes.
        FormClosed += (_, _) => { _showtime.Stop(); _setTest(TestCursor.Off); };

        // --- action buttons: Defaults + Check-for-updates (left) | Apply OK Cancel (right) ---
        // Apply commits edits to the live cursor without closing, so you can tweak
        // size/colour and watch the test cursor update.
        _defaultsBtn = new Button { Text = "Defaults", Size = new Size(90, 30) };
        _defaultsBtn.Click += (_, _) => ResetDefaults();
        _cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 30) };
        _okBtn = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 30) };
        _applyBtn = new Button { Text = "Apply", Size = new Size(84, 30) };
        _applyBtn.Click += (_, _) => _apply(_working);
        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;

        // --- footer: version + the repo link on one line ---
        _version = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "v" + AppVersion() };
        _updateBtn = new Button { Text = "Check for updates", Size = new Size(140, 26) };
        _updateBtn.Click += OnCheckUpdates;
        _updateStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty };
        _link = new LinkLabel { AutoSize = true, Text = "github.com/dawidope/WormsCursor" };
        _link.LinkClicked += (_, _) => OpenUrl(RepoUrl);

        foreach (Control c in new Control[]
        {
            _tip,
            _sizeCap, _sizeBar, _sizeVal, _thickCap, _thickBar, _thickVal, _radiusCap, _radiusBar, _radiusVal,
            _fillCap, _fillBtn, _outlineCap, _outlineBtn, _testCap, _testCombo, _showtimeBtn,
            _defaultsBtn, _applyBtn, _okBtn, _cancelBtn,
            _version, _updateBtn, _updateStatus, _link,
        })
            _body.Controls.Add(c);

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
        var b = new Button { FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Size = new Size(150, 28) };
        b.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
        b.FlatAppearance.BorderSize = 1;
        StyleSwatch(b, c);
        return b;
    }

    // Paints a colour button with its colour plus the hex value in a contrasting ink, so a
    // pure black/white swatch still reads as a clickable colour picker, not a blank bar.
    static void StyleSwatch(Button b, Color c)
    {
        b.BackColor = c;
        b.Text = ToHex(c);
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        b.ForeColor = lum > 0.55 ? Color.FromArgb(45, 45, 45) : Color.White;
    }

    static Label MakeCaption(string text) => new() { Text = text, AutoSize = true };

    // Positions every control inside _body for a given body width: two columns of three
    // rows each (sliders left, colours + test on the right) keep the dialog wide-and-short,
    // with the action buttons and footer spanning the full width below. Row heights don't
    // depend on the width, so LayoutWindow can call this to learn _bodyHeight and again
    // once the final width is settled.
    void LayoutBody(int width)
    {
        int colW = (width - 2 * M - ColGap) / 2;
        int leftX = M, rightX = M + colW + ColGap;

        int top = 8;
        _tip.Location = new Point(M, top);
        top += 28;

        // left column — the numeric sliders
        int ly = top;
        ly = PlaceSlider(_sizeCap, _sizeBar, _sizeVal, leftX, ly, colW);
        ly = PlaceSlider(_thickCap, _thickBar, _thickVal, leftX, ly, colW);
        ly = PlaceSlider(_radiusCap, _radiusBar, _radiusVal, leftX, ly, colW);

        // right column — colours + the test combo
        int ry = top;
        ry = PlaceField(_fillCap, _fillBtn, rightX, ry, colW, 28);
        ry = PlaceField(_outlineCap, _outlineBtn, rightX, ry, colW, 28);

        // test row: caption, then the combo and the Showtime button sharing the column width
        _testCap.Location = new Point(rightX, ry);
        const int stW = 104, stGap = 6;
        int comboW = colW - stW - stGap;
        _testCombo.SetBounds(rightX, ry + 22, comboW, 24);
        _showtimeBtn.SetBounds(rightX + comboW + stGap, ry + 20, stW, 27);
        ry += RowH;

        // action buttons — Defaults + Check-for-updates on the left, Apply|OK|Cancel clustered
        // at the right. The wide window leaves a comfortable gap in the middle, so the update
        // status text sits right beside its button again.
        int btnY = Math.Max(ly, ry) + 6;
        _defaultsBtn.Location = new Point(M, btnY);
        _updateBtn.Location = new Point(_defaultsBtn.Right + 8, btnY + 2); // 26-tall button centred on the 30-tall row
        _updateStatus.Location = new Point(_updateBtn.Right + 12, btnY + 8);
        _cancelBtn.Location = new Point(width - M - _cancelBtn.Width, btnY);
        _okBtn.Location = new Point(_cancelBtn.Left - 8 - _okBtn.Width, btnY);
        _applyBtn.Location = new Point(_okBtn.Left - 8 - _applyBtn.Width, btnY);

        // footer — version + repo link on a single line below the buttons
        int footerY = btnY + 30 + 14;
        _version.Location = new Point(M, footerY);
        _link.Location = new Point(_version.Right + 14, footerY);

        // Fixed line height (~20): autosize PreferredHeight is unreliable before the form gets
        // a handle, which would clip the footer off the bottom.
        _bodyHeight = footerY + 20 + M;
    }

    // One slider row: caption, then a full-column-width bar with a right-aligned value label.
    static int PlaceSlider(Label cap, TrackBar bar, Label val, int x, int y, int colW)
    {
        const int valW = 56;
        cap.Location = new Point(x, y);
        bar.SetBounds(x, y + 22, colW - valW - 6, 28);
        val.SetBounds(x + colW - valW, y + 27, valW, 18);
        return y + RowH;
    }

    // One captioned control filling the column width (colour button / combo).
    static int PlaceField(Label cap, Control field, int x, int y, int colW, int h)
    {
        cap.Location = new Point(x, y);
        field.SetBounds(x, y + 22, colW, h);
        return y + RowH;
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

    // Sizes the window once for the fixed 7-column grid (real size, never scaled). Capped to the
    // screen so it can't run off-screen; if the grid is taller than that, the preview scrolls.
    void LayoutWindow()
    {
        int n = PreviewKinds.Length;
        _rows = Math.Max(1, (n + PreviewCols - 1) / PreviewCols);
        int contentW = PreviewCols * _cellW, contentH = _rows * _cellH;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int maxPanelW = wa.Width - 2 * M;

        // Body height is width-independent, so lay it out at a provisional width to learn
        // _bodyHeight (needed for the vertical-overflow check), then again at the final width.
        LayoutBody(Math.Max(W, Math.Min(contentW, maxPanelW) + 2 * M));

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

        LayoutBody(clientW); // final control positions at the real width

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
        if (_showtimeRunning) return; // showtime owns the cursor — ignore hover-to-try
        int idx = HitTestPreview(e.Location);
        if (idx == _hoverIndex) return;
        _hoverIndex = idx;
        _setTest(idx >= 0 ? PreviewKinds[idx] : MapTest(_testCombo.SelectedIndex));
        _preview.Invalidate();
    }

    // Left the grid entirely: restore whatever the Test-cursor combo last selected.
    void PreviewMouseLeave(object? sender, EventArgs e)
    {
        if (_showtimeRunning) return; // showtime owns the cursor — ignore hover-to-try
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        _setTest(MapTest(_testCombo.SelectedIndex));
        _preview.Invalidate();
    }

    // ---------- showtime (the hands-free demo cycle) ----------
    // Click to start: a 3-2-1 lead-in, then every test cursor in turn (one per second),
    // looping until you click Stop or close the dialog. Move the mouse while it runs so the
    // motion-driven cursors (rotation, swing, jelly, taffy) animate as they pass by.
    void ToggleShowtime()
    {
        if (_showtimeRunning) { StopShowtime(); return; }

        _preShowtimeIndex = _testCombo.SelectedIndex; // restore the prior selection on Stop
        _showtimeRunning = true;
        _showtimeStep = -1;                           // still in the lead-in
        _showtimeCountdown = ShowtimeLeadIn;
        _testCombo.Enabled = false;                   // showtime owns the cursor while it runs
        SetTestComboSilently(0);                      // normal cursor during the countdown
        _setTest(TestCursor.Off);
        _showtimeBtn.Text = $"Starting {_showtimeCountdown}…";
        _showtime.Interval = 1000;                    // tick the lead-in countdown once per second
        _showtime.Start();
    }

    void StopShowtime()
    {
        _showtime.Stop();
        _showtimeRunning = false;
        _showtimeStep = -1;
        _showtimeBtn.Text = "▶ Showtime";
        _testCombo.Enabled = true;
        int restore = Math.Clamp(_preShowtimeIndex, 0, _testCombo.Items.Count - 1);
        SetTestComboSilently(restore);
        _setTest(MapTest(restore));                   // hand the cursor back to the combo's selection
    }

    void OnShowtimeTick(object? sender, EventArgs e)
    {
        // Lead-in: count down 3… 2… 1… before the first cursor appears.
        if (_showtimeStep < 0)
        {
            _showtimeCountdown--;
            if (_showtimeCountdown > 0) { _showtimeBtn.Text = $"Starting {_showtimeCountdown}…"; return; }
            _showtimeStep = 0;
            _showtime.Interval = ShowtimeStepMs;      // lead-in done — slow to the per-cursor dwell
        }
        else if (++_showtimeStep >= ShowtimeKinds.Length)
        {
            if (!_showtimeLoops) { StopShowtime(); return; }
            _showtimeStep = 0;                        // loop back to the first cursor
        }

        var kind = ShowtimeKinds[_showtimeStep];
        SetTestComboSilently(Array.IndexOf(PreviewKinds, kind) + 1); // mirror into the combo so its name shows
        _setTest(kind);
        _showtimeBtn.Text = $"■ Stop ({_showtimeStep + 1}/{ShowtimeKinds.Length})";
    }

    // Moves the combo selection for display only — showtime drives the cursor itself, so the
    // combo's change handler must not also force one (and fight the timer).
    void SetTestComboSilently(int index)
    {
        _suppressTestCombo = true;
        _testCombo.SelectedIndex = index;
        _suppressTestCombo = false;
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
            StyleSwatch(btn, dlg.Color);
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
        StyleSwatch(_fillBtn, ParseOr(_working.FillColor, Color.White));
        StyleSwatch(_outlineBtn, ParseOr(_working.OutlineColor, Color.Black));
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
            _showtime?.Dispose();
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
