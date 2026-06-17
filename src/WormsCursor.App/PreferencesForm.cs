using System.Diagnostics;
using System.Reflection;
using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// The notifier appearance editor. Works on a clone of the settings (passed in) and exposes the edited
/// copy via <see cref="Settings"/>; the caller applies it on OK (and "Apply" commits live without
/// closing). Controls the bouncing agent token: its size and where it lives (following the cursor, or
/// pinned to a screen corner). The live preview draws the token with the same
/// <see cref="NotifierRenderer"/> the overlay uses, over a dark/light split so it reads either way.
/// Autostart and the agent-notifications dialog hang off here too.
/// </summary>
public sealed class PreferencesForm : Form
{
    const string RepoUrl = "https://github.com/dawidope/WormsCursor";

    const int M = 16;            // outer margin
    const int W = 460;           // client width
    const int PreviewH = 220;    // preview pane height (fits the largest token the slider allows)

    readonly CursorSettings _working;
    public CursorSettings Settings => _working;

    readonly DoubleBufferedPanel _preview;
    readonly Label _previewCap;
    readonly TrackBar _sizeBar;
    readonly Label _sizeVal, _sizeCap;
    readonly Label _placeCap;
    readonly RadioButton _followCursorRadio, _cornerRadio;
    readonly Label _cornerCap;
    readonly ComboBox _cornerCombo;
    readonly Button _defaultsBtn, _applyBtn, _okBtn, _cancelBtn;
    readonly Label _version;
    readonly LinkLabel _link, _whatsNew;
    readonly ToolTip _tip = new();

    readonly Action<CursorSettings> _apply;
    readonly UpdateService _updates;

    // App-level controls (not part of the appearance working-copy): autostart commits immediately to
    // the registry; the agent button opens the notifications dialog.
    readonly IAutostart _autostart;
    readonly Action _openAgentSettings;
    readonly CheckBox _autostartChk;
    readonly Button _agentBtn;
    bool _suppressAutostart; // guards the revert-on-failure write so it doesn't re-enter the handler

    // Corner combo order mirrors the ScreenCorner enum (TopLeft, TopRight, BottomLeft, BottomRight).
    static readonly string[] CornerNames = { "Top-left", "Top-right", "Bottom-left", "Bottom-right" };

    public PreferencesForm(CursorSettings working, UpdateService updates,
                           Action<CursorSettings> applyLive, IAutostart autostart, Action openAgentSettings)
    {
        _working = working;
        _updates = updates;
        _apply = applyLive;
        _autostart = autostart;
        _openAgentSettings = openAgentSettings;

        AutoScaleMode = AutoScaleMode.Font;
        Text = "WormsCursor — Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        _tip.ShowAlways = true;

        // --- live token preview ---
        _previewCap = MakeCaption("Preview");
        _preview = new DoubleBufferedPanel { BackColor = Color.FromArgb(60, 60, 60) };
        _preview.Paint += PreviewPaint;

        // --- token size ---
        _sizeCap = MakeCaption("Token size");
        _sizeBar = new TrackBar
        {
            Minimum = 48, Maximum = 192, TickStyle = TickStyle.None, AutoSize = false,
            Value = Math.Clamp(_working.Size, 48, 192),
        };
        _sizeBar.ValueChanged += (_, _) => { _working.Size = _sizeBar.Value; UpdateLabels(); _preview.Invalidate(); };
        _sizeVal = new Label { AutoSize = false, Size = new Size(64, 18), TextAlign = ContentAlignment.MiddleRight };

        // --- placement ---
        _placeCap = MakeCaption("Show the token");
        _followCursorRadio = new RadioButton { Text = "Next to the mouse cursor", AutoSize = true, Checked = _working.Placement == NotifierPlacement.Cursor };
        _cornerRadio = new RadioButton { Text = "Pinned to a screen corner", AutoSize = true, Checked = _working.Placement == NotifierPlacement.Corner };
        _followCursorRadio.CheckedChanged += (_, _) => { if (_followCursorRadio.Checked) { _working.Placement = NotifierPlacement.Cursor; SyncCornerEnabled(); } };
        _cornerRadio.CheckedChanged += (_, _) => { if (_cornerRadio.Checked) { _working.Placement = NotifierPlacement.Corner; SyncCornerEnabled(); } };
        _tip.SetToolTip(_followCursorRadio, "The token hangs off the pointer and follows it, bouncing as you move.");
        _tip.SetToolTip(_cornerRadio, "The token sits in a fixed screen corner and bounces in place.");

        _cornerCap = MakeCaption("Corner");
        _cornerCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        _cornerCombo.Items.AddRange(CornerNames);
        _cornerCombo.SelectedIndex = Math.Clamp((int)_working.Corner, 0, CornerNames.Length - 1);
        _cornerCombo.SelectedIndexChanged += (_, _) => _working.Corner = (ScreenCorner)_cornerCombo.SelectedIndex;

        // --- app-level row ---
        _autostartChk = new CheckBox { Text = "Start with Windows", AutoSize = true, Checked = ReadAutostart() };
        _autostartChk.CheckedChanged += (_, _) => ToggleAutostart();
        _tip.SetToolTip(_autostartChk, "Launch WormsCursor automatically when you sign in.");
        _agentBtn = MakeButton("Agent settings…", minWidth: 130);
        _agentBtn.Click += (_, _) => _openAgentSettings();
        _tip.SetToolTip(_agentBtn, "Turn the waiting-agent token on/off, set how long it lingers, and register tools.");

        // --- action buttons ---
        _defaultsBtn = MakeButton("Defaults");
        _defaultsBtn.Click += (_, _) => ResetDefaults();
        _cancelBtn = MakeButton("Cancel"); _cancelBtn.DialogResult = DialogResult.Cancel;
        _okBtn = MakeButton("OK"); _okBtn.DialogResult = DialogResult.OK;
        _applyBtn = MakeButton("Apply");
        _applyBtn.Click += (_, _) => _apply(_working);
        AcceptButton = _okBtn;
        CancelButton = _cancelBtn;

        // --- footer ---
        _version = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Text = "v" + AppVersion() };
        _link = new LinkLabel { AutoSize = true, Text = "github.com/dawidope/WormsCursor" };
        _link.LinkClicked += (_, _) => OpenUrl(RepoUrl);
        _whatsNew = new LinkLabel { AutoSize = true, Text = "What's new" };
        _whatsNew.LinkClicked += (_, _) => { using var dlg = new ChangelogForm(_updates); dlg.ShowDialog(this); };

        Controls.AddRange(new Control[]
        {
            _previewCap, _preview,
            _sizeCap, _sizeBar, _sizeVal,
            _placeCap, _followCursorRadio, _cornerRadio, _cornerCap, _cornerCombo,
            _autostartChk, _agentBtn,
            _defaultsBtn, _applyBtn, _okBtn, _cancelBtn,
            _version, _link, _whatsNew,
        });

        UpdateLabels();
        SyncCornerEnabled();
        LayoutControls();
    }

    // ---------- layout ----------
    void LayoutControls()
    {
        int innerW = W - 2 * M;
        int y = M;

        _previewCap.Location = new Point(M, y);
        y = _previewCap.Bottom + 4;
        _preview.SetBounds(M, y, innerW, PreviewH);
        y = _preview.Bottom + 16;

        // size: caption, then a full-width bar with a right-aligned value
        _sizeCap.Location = new Point(M, y);
        int barTop = _sizeCap.Bottom + 4;
        _sizeBar.SetBounds(M, barTop, innerW - 70, 28);
        _sizeVal.SetBounds(M + innerW - 64, barTop + 5, 64, 18);
        y = barTop + 28 + 14;

        // placement: caption then two radios stacked
        _placeCap.Location = new Point(M, y);
        y = _placeCap.Bottom + 4;
        _followCursorRadio.Location = new Point(M + 4, y);
        y = _followCursorRadio.Bottom + 6;
        _cornerRadio.Location = new Point(M + 4, y);

        // corner picker: combo right-aligned to the margin (so it can't run off the edge), with its
        // caption just to its left, both on the corner-radio's baseline
        int comboX = W - M - _cornerCombo.Width;
        _cornerCombo.Location = new Point(comboX, y + Math.Max(0, (_cornerRadio.Height - _cornerCombo.Height) / 2));
        _cornerCap.Location = new Point(comboX - 6 - _cornerCap.Width, y + Math.Max(0, (_cornerRadio.Height - _cornerCap.Height) / 2));
        y = Math.Max(_cornerRadio.Bottom, _cornerCombo.Bottom) + 18;

        // app row: autostart (left) + agent settings (right)
        _agentBtn.SetBounds(W - M - _agentBtn.Width, y, _agentBtn.Width, _agentBtn.Height);
        _autostartChk.Location = new Point(M, y + Math.Max(0, (_agentBtn.Height - _autostartChk.Height) / 2));
        y = Math.Max(_agentBtn.Bottom, _autostartChk.Bottom) + 18;

        // action buttons: Defaults (left) | Apply OK Cancel (right)
        int btnH = _okBtn.Height;
        _defaultsBtn.Location = new Point(M, y);
        _cancelBtn.Location = new Point(W - M - _cancelBtn.Width, y);
        _okBtn.Location = new Point(_cancelBtn.Left - 8 - _okBtn.Width, y);
        _applyBtn.Location = new Point(_okBtn.Left - 8 - _applyBtn.Width, y);
        y += btnH + 16;

        // footer: version + links
        _version.Location = new Point(M, y);
        _link.Location = new Point(_version.Right + 14, y);
        _whatsNew.Location = new Point(_link.Right + 14, y);
        y += 20 + M;

        ClientSize = new Size(W, y);
    }

    static Label MakeCaption(string text) => new() { Text = text, AutoSize = true };

    static Button MakeButton(string text, int minWidth = 88) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 5, 12, 5),
        MinimumSize = new Size(minWidth, 0),
    };

    // ---------- behaviour ----------
    void UpdateLabels() => _sizeVal.Text = $"{_working.Size} px";

    void SyncCornerEnabled()
    {
        bool corner = _working.Placement == NotifierPlacement.Corner;
        _cornerCap.Enabled = corner;
        _cornerCombo.Enabled = corner;
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = _preview.ClientSize.Width, h = _preview.ClientSize.Height;
        // Dark left half, light right half — the token's contrast rim flips per side, so it reads on both.
        using (var dark = new SolidBrush(Color.FromArgb(56, 56, 56)))
            g.FillRectangle(dark, 0, 0, w / 2, h);
        using (var light = new SolidBrush(Color.FromArgb(218, 218, 218)))
            g.FillRectangle(light, w / 2, 0, w - w / 2, h);

        // Draw the token at its real configured size, centred, upright (swing 0).
        float side = Math.Min(_working.Size, Math.Min(w, h) - 12);
        NotifierRenderer.DrawToken(g, _working, new[] { "claude-code" }, w / 2f, h / 2f, side, 0f);
    }

    void ResetDefaults()
    {
        var d = new CursorSettings();
        _working.Size = d.Size;
        _working.Placement = d.Placement;
        _working.Corner = d.Corner;
        _working.OutlineColor = d.OutlineColor;

        _sizeBar.Value = Math.Clamp(d.Size, _sizeBar.Minimum, _sizeBar.Maximum);
        _followCursorRadio.Checked = d.Placement == NotifierPlacement.Cursor;
        _cornerRadio.Checked = d.Placement == NotifierPlacement.Corner;
        _cornerCombo.SelectedIndex = Math.Clamp((int)d.Corner, 0, CornerNames.Length - 1);
        UpdateLabels();
        SyncCornerEnabled();
        _preview.Invalidate();
    }

    // Reads the current autostart state for the checkbox (best-effort: a registry hiccup just
    // shows it unticked rather than failing the whole dialog).
    bool ReadAutostart()
    {
        try { return _autostart.IsEnabled; }
        catch { return false; }
    }

    // Commits the autostart change to the registry the moment the box is toggled (it's a system
    // pref, not an appearance edit, so it doesn't ride Apply/Cancel). On failure we warn and snap
    // the box back to the real state without re-entering this handler.
    void ToggleAutostart()
    {
        if (_suppressAutostart) return;
        try
        {
            if (_autostartChk.Checked) _autostart.Enable();
            else _autostart.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't change autostart: " + ex.Message, "WormsCursor",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _suppressAutostart = true;
            _autostartChk.Checked = ReadAutostart();
            _suppressAutostart = false;
        }
    }

    // ---------- small utilities ----------
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
        if (disposing) _tip?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A panel that double-buffers its client area so the live preview repaints
    /// without flicker while the size slider is dragged.</summary>
    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
