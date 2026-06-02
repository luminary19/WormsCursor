using System.Diagnostics;
using System.Drawing.Drawing2D;
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
    const int PreviewH = 140;
    const int SliderRowH = 56;
    const int ColorRowH = 58;

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly DoubleBufferedPanel _preview;
    Bitmap? _arrowBmp, _handBmp;

    readonly TrackBar _sizeBar, _thickBar, _radiusBar;
    readonly Label _sizeVal, _thickVal, _radiusVal;
    readonly Button _fillBtn, _outlineBtn;
    readonly UpdateService _updates;
    readonly Button _updateBtn;
    readonly Label _updateStatus;

    public PreferencesForm(CursorSettings working, UpdateService updates)
    {
        _working = working;
        _updates = updates;

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

        _thickBar = MakeBar(0, 120, (int)Math.Round(_working.OutlineThickness * 10));
        _thickVal = MakeVal();
        AddSliderRow("Outline thickness", _thickBar, _thickVal, ref y);
        _thickBar.ValueChanged += (_, _) => { _working.OutlineThickness = _thickBar.Value / 10.0; OnEdited(); };

        _radiusBar = MakeBar(0, 120, (int)Math.Round(_working.CornerRadius * 10));
        _radiusVal = MakeVal();
        AddSliderRow("Corner radius", _radiusBar, _radiusVal, ref y);
        _radiusBar.ValueChanged += (_, _) => { _working.CornerRadius = _radiusBar.Value / 10.0; OnEdited(); };

        int btnY = y + 8;
        var defaults = new Button { Text = "Defaults", Location = new Point(M, btnY), Size = new Size(90, 30) };
        defaults.Click += (_, _) => ResetDefaults();
        _updateBtn = new Button { Text = "Check for updates", Location = new Point(M + 98, btnY), Size = new Size(140, 30) };
        _updateBtn.Click += OnCheckUpdates;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 30), Location = new Point(W - M - 84 - 8 - 84, btnY) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 30), Location = new Point(W - M - 84, btnY) };
        Controls.Add(defaults);
        Controls.Add(_updateBtn);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // Footer over two lines: version + update status on the first, repo link on
        // its own line below (so a long URL is never truncated).
        int footerY = btnY + 30 + 16;
        var version = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "v" + AppVersion(), Location = new Point(M, footerY) };
        _updateStatus = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = string.Empty, Location = new Point(M + 56, footerY) };
        var link = new LinkLabel { AutoSize = true, Text = "github.com/dawidope/WormsCursor", Location = new Point(M, footerY + 22) };
        link.LinkClicked += (_, _) => OpenUrl(RepoUrl);
        Controls.Add(version);
        Controls.Add(_updateStatus);
        Controls.Add(link);

        ClientSize = new Size(W, footerY + 22 + link.PreferredHeight + M); // bottom margin under the link line

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
        _arrowBmp?.Dispose();
        _handBmp?.Dispose();
        // Render both cursors at their ACTUAL size and draw 1:1 (DrawImageUnscaled):
        // crisp (no upscaling) and a true WYSIWYG match for what you get on screen.
        _arrowBmp = ArrowRenderer.DrawArrow(_working);
        _handBmp = HandRenderer.DrawHand(_working);
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _preview.ClientSize.Width, h = _preview.ClientSize.Height, half = w / 2;
        using (var dark = new SolidBrush(Color.FromArgb(31, 31, 31)))
            g.FillRectangle(dark, 0, 0, half, h);
        using (var light = new SolidBrush(Color.FromArgb(243, 243, 243)))
            g.FillRectangle(light, half, 0, w - half, h);

        // Arrow + hand side by side on each background.
        float lw = w - half;
        DrawCursorAt(g, _arrowBmp, half * 0.25f, h);
        DrawCursorAt(g, _handBmp, half * 0.75f, h);
        DrawCursorAt(g, _arrowBmp, half + lw * 0.25f, h);
        DrawCursorAt(g, _handBmp, half + lw * 0.75f, h);
    }

    static void DrawCursorAt(Graphics g, Bitmap? bmp, float centerX, int h)
    {
        if (bmp is null) return;
        g.DrawImageUnscaled(bmp, (int)(centerX - bmp.Width / 2f), (h - bmp.Height) / 2);
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
        if (disposing) { _arrowBmp?.Dispose(); _handBmp?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>A panel that double-buffers its client area so the live preview repaints
    /// without flicker while sliders are dragged.</summary>
    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
