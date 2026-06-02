using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Live cursor-appearance editor. Works on a clone of the settings (passed in) and
/// exposes the edited copy via <see cref="Settings"/>; the caller applies it on OK.
/// A preview pane renders the arrow with the same <see cref="ArrowRenderer"/> the
/// engine uses, on both a dark and a light background.
/// </summary>
public sealed class PreferencesForm : Form
{
    const string RepoUrl = "https://github.com/dawidope/WormsCursor";

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly Panel _preview;
    Bitmap? _previewBmp;

    readonly TrackBar _sizeBar, _thickBar, _radiusBar;
    readonly Label _sizeVal, _thickVal, _radiusVal;
    readonly Button _fillBtn, _outlineBtn;

    public PreferencesForm(CursorSettings working)
    {
        _working = working;

        Text = "WormsCursor — Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 500);

        _preview = new Panel { Location = new Point(12, 12), Size = new Size(416, 150), BorderStyle = BorderStyle.FixedSingle };
        _preview.Paint += PreviewPaint;
        Controls.Add(_preview);

        int y = 176;
        const int row = 52;

        _sizeBar = MakeBar(24, 128, _working.Size);
        _sizeVal = MakeVal();
        AddSliderRow("Cursor size", _sizeBar, _sizeVal, ref y, row);
        _sizeBar.ValueChanged += (_, _) => { _working.Size = _sizeBar.Value; OnEdited(); };

        _fillBtn = MakeColorButton(ParseOr(_working.FillColor, Color.White));
        _fillBtn.Click += (_, _) => PickColor(_fillBtn, c => _working.FillColor = ToHex(c));
        AddColorRow("Fill colour", _fillBtn, ref y);

        _outlineBtn = MakeColorButton(ParseOr(_working.OutlineColor, Color.Black));
        _outlineBtn.Click += (_, _) => PickColor(_outlineBtn, c => _working.OutlineColor = ToHex(c));
        AddColorRow("Outline colour", _outlineBtn, ref y);

        _thickBar = MakeBar(0, 120, (int)Math.Round(_working.OutlineThickness * 10));
        _thickVal = MakeVal();
        AddSliderRow("Outline thickness", _thickBar, _thickVal, ref y, row);
        _thickBar.ValueChanged += (_, _) => { _working.OutlineThickness = _thickBar.Value / 10.0; OnEdited(); };

        _radiusBar = MakeBar(0, 120, (int)Math.Round(_working.CornerRadius * 10));
        _radiusVal = MakeVal();
        AddSliderRow("Corner radius", _radiusBar, _radiusVal, ref y, row);
        _radiusBar.ValueChanged += (_, _) => { _working.CornerRadius = _radiusBar.Value / 10.0; OnEdited(); };

        var defaults = new Button { Text = "Defaults", Location = new Point(12, y + 8), Size = new Size(90, 30) };
        defaults.Click += (_, _) => ResetDefaults();
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(80, 30), Location = new Point(258, y + 8) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(80, 30), Location = new Point(346, y + 8) };
        Controls.Add(defaults);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        int footerY = y + 50;
        var version = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "v" + AppVersion(), Location = new Point(12, footerY) };
        var link = new LinkLabel { AutoSize = true, Text = "github.com/dawidope/WormsCursor", Location = new Point(0, footerY) };
        link.LinkClicked += (_, _) => OpenUrl(RepoUrl);
        Controls.Add(version);
        Controls.Add(link);
        link.Left = ClientSize.Width - link.PreferredWidth - 12; // right-align

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

    static Label MakeVal() => new() { AutoSize = true };

    static Button MakeColorButton(Color c) => new()
    {
        BackColor = c,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        Size = new Size(120, 26),
    };

    void AddSliderRow(string caption, TrackBar bar, Label val, ref int y, int row)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(12, y) });
        bar.Location = new Point(12, y + 18);
        bar.Size = new Size(330, 30);
        val.Location = new Point(352, y + 24);
        Controls.Add(bar);
        Controls.Add(val);
        y += row;
    }

    void AddColorRow(string caption, Button btn, ref int y)
    {
        Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(12, y) });
        btn.Location = new Point(12, y + 18);
        Controls.Add(btn);
        y += 50;
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
        _previewBmp?.Dispose();
        _previewBmp = ArrowRenderer.DrawArrow(_working);
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _preview.ClientSize.Width, h = _preview.ClientSize.Height, half = w / 2;
        using (var dark = new SolidBrush(Color.FromArgb(31, 31, 31)))
            g.FillRectangle(dark, 0, 0, half, h);
        using (var light = new SolidBrush(Color.FromArgb(243, 243, 243)))
            g.FillRectangle(light, half, 0, w - half, h);

        if (_previewBmp is null) return;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        const float zoom = 1.6f;
        int dw = (int)(_previewBmp.Width * zoom), dh = (int)(_previewBmp.Height * zoom);
        g.DrawImage(_previewBmp, (half - dw) / 2, (h - dh) / 2, dw, dh);                 // dark half
        g.DrawImage(_previewBmp, half + (w - half - dw) / 2, (h - dh) / 2, dw, dh);      // light half
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
        if (disposing) _previewBmp?.Dispose();
        base.Dispose(disposing);
    }
}
