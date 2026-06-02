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
    const int W = 460;           // client width
    const int PreviewH = 210;
    const int SliderRowH = 56;
    const int ColorRowH = 58;

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly DoubleBufferedPanel _preview;
    Bitmap?[] _previews = Array.Empty<Bitmap?>();
    static readonly TestCursor[] PreviewKinds =
    {
        TestCursor.Arrow, TestCursor.Hand, TestCursor.Wait, TestCursor.AppStarting, TestCursor.Help, TestCursor.Cross,
        TestCursor.Ibeam, TestCursor.SizeWE, TestCursor.SizeNS, TestCursor.SizeNWSE, TestCursor.SizeNESW, TestCursor.SizeAll,
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

        Text = "WormsCursor — Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(W, 600); // height finalized at the end

        _preview = new DoubleBufferedPanel { Bounds = new Rectangle(M, M, W - 2 * M, PreviewH), BorderStyle = BorderStyle.FixedSingle };
        _preview.Paint += PreviewPaint;
        Controls.Add(_preview);

        int y = M + PreviewH + 18;

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
            "Resize ↔", "Resize ↕", "Resize ↘↖", "Resize ↗↙", "Move ✥",
        });
        _testCombo.SelectedIndex = 0;
        _testCombo.SelectedIndexChanged += (_, _) => _setTest(MapTest(_testCombo.SelectedIndex));
        AddComboRow("Test cursor (force on screen)", _testCombo, ref y);
        FormClosed += (_, _) => _setTest(TestCursor.Off);

        int btnY = y + 8;
        var defaults = new Button { Text = "Defaults", Location = new Point(M, btnY), Size = new Size(90, 30) };
        defaults.Click += (_, _) => ResetDefaults();
        // Right cluster: Apply | OK | Cancel. Apply commits the edits to the live cursor
        // without closing, so you can tweak size/colour and watch the test cursor update.
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 30), Location = new Point(W - M - 84, btnY) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 30), Location = new Point(W - M - 176, btnY) };
        var apply = new Button { Text = "Apply", Size = new Size(84, 30), Location = new Point(W - M - 268, btnY) };
        apply.Click += (_, _) => _apply(_working);
        Controls.Add(defaults);
        Controls.Add(apply);
        Controls.Add(ok);
        Controls.Add(cancel);
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
        Controls.Add(version);
        Controls.Add(_updateBtn);
        Controls.Add(_updateStatus);
        Controls.Add(link);

        ClientSize = new Size(W, footerY + 38 + link.PreferredHeight + M); // bottom margin under the link line

        UpdateLabels();
        RenderPreview();
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
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(M, y) });
        int barY = y + 22;
        bar.SetBounds(M, barY, W - 2 * M - 68, 28);
        val.SetBounds(W - M - 56, barY + 5, 56, 18);
        Controls.Add(bar);
        Controls.Add(val);
        y += SliderRowH;
    }

    void AddComboRow(string caption, ComboBox combo, ref int y)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(M, y) });
        combo.SetBounds(M, y + 22, W - 2 * M, 24);
        Controls.Add(combo);
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
        _ => TestCursor.Off,
    };

    // Two colour pickers side by side on one row (Fill | Outline).
    void AddTwoColorRow(string capA, Button btnA, string capB, Button btnB, ref int y)
    {
        int half = (W - 2 * M) / 2;
        int swatchW = half - 20;
        Controls.Add(new Label { Text = capA, AutoSize = true, Location = new Point(M, y) });
        btnA.SetBounds(M, y + 24, swatchW, 28);
        Controls.Add(btnA);

        int x2 = M + half;
        Controls.Add(new Label { Text = capB, AutoSize = true, Location = new Point(x2, y) });
        btnB.SetBounds(x2, y + 24, swatchW, 28);
        Controls.Add(btnB);

        y += ColorRowH;
    }

    // ---------- behaviour ----------
    void OnEdited()
    {
        UpdateLabels();
        RenderPreview();
        _preview.Invalidate();
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
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int w = _preview.ClientSize.Width, h = _preview.ClientSize.Height;
        // One neutral mid-grey strip: shows both a light fill and a dark outline without
        // needing separate dark/light panels.
        using (var bg = new SolidBrush(Color.FromArgb(122, 122, 122)))
            g.FillRectangle(bg, 0, 0, w, h);

        int n = _previews.Length;
        if (n == 0) return;
        int cols = (int)Math.Ceiling(Math.Sqrt(n)); // a roughly-square grid (grows with the count)
        int rows = (n + cols - 1) / cols;
        float cellW = w / (float)cols, cellH = h / (float)rows;

        // One shared scale so the cells keep the cursors' TRUE relative sizes: fit the
        // largest trimmed cursor into a cell, capped at 1:1 (never upscale).
        float maxW = 1f, maxH = 1f;
        foreach (var b in _previews)
            if (b is not null) { maxW = Math.Max(maxW, b.Width); maxH = Math.Max(maxH, b.Height); }
        const float pad = 16f;
        float scale = Math.Min(1f, Math.Min((cellW - pad) / maxW, (cellH - pad) / maxH));

        for (int i = 0; i < n; i++)
        {
            var bmp = _previews[i];
            if (bmp is null) continue;
            float cx = (i % cols) * cellW + cellW / 2f;
            float cy = (i / cols) * cellH + cellH / 2f;
            float dw = bmp.Width * scale, dh = bmp.Height * scale;
            if (scale >= 0.999f)
                g.DrawImageUnscaled(bmp, (int)(cx - bmp.Width / 2f), (int)(cy - bmp.Height / 2f));
            else
                g.DrawImage(bmp, cx - dw / 2f, cy - dh / 2f, dw, dh);
        }
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
        if (disposing) foreach (var b in _previews) b?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A panel that double-buffers its client area so the live preview repaints
    /// without flicker while sliders are dragged.</summary>
    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
