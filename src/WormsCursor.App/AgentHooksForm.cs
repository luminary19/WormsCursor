using WormsCursor.App.Services;

namespace WormsCursor.App;

/// <summary>
/// The "Agent notifications" settings panel. Two parts: a display section (turn the hanging agent
/// logo on/off, set how long a logo lingers before it's cleared, and a live preview that fakes a
/// waiting-agent count on the real cursor), and a per-tool registration list showing whether
/// WormsCursor is hooked into each AI tool, with Register / Unregister.
/// Registration edits the tool's own config (backed up first); status is re-read after each action.
/// Display changes are pushed straight to the live engine via <paramref name="applyDisplay"/>
/// (the bool is "enabled", the int is the linger timeout in seconds); the preview pushes a temporary
/// count via <paramref name="preview"/> (null = end preview / restore). Default-styled WinForms.
/// </summary>
public sealed class AgentHooksForm : Form
{
    readonly Action<bool, int> _applyDisplay;
    readonly Action<int?> _preview;
    readonly CheckBox _enabledChk;
    readonly NumericUpDown _timeoutNum;
    readonly NumericUpDown _previewNum;
    readonly Button _previewShow;
    readonly Button _previewClear;
    readonly List<(HookTool tool, Label status, Button action)> _rows = new();

    /// <param name="timeoutSeconds">How long a waiting logo lingers before being swept, in seconds.</param>
    /// <param name="applyDisplay">Called with (enabled, timeoutSeconds) whenever a display setting changes.</param>
    /// <param name="preview">Shows a fake waiting count on the live cursor for testing; called with
    /// the count to preview, or <c>null</c> to end the preview and restore the real count.</param>
    public AgentHooksForm(bool charmsEnabled, int timeoutSeconds, Action<bool, int> applyDisplay, Action<int?> preview)
    {
        _applyDisplay = applyDisplay;
        _preview = preview;

        Text = "Agent notifications";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont;

        var intro = new Label
        {
            AutoSize = false,
            Location = new Point(12, 12),
            Size = new Size(512, 36),
            Text = "When an AI agent is waiting for you, the cursor hangs that tool's logo — with a "
                 + "“+N” tag when several wait. Register the tools below so they tell WormsCursor what "
                 + "they're doing.",
        };
        Controls.Add(intro);

        // --- display section ---
        _enabledChk = new CheckBox
        {
            AutoSize = false,
            Location = new Point(12, 56),
            Size = new Size(400, 22),
            Text = "Show the agent logo on the cursor while agents are waiting",
            Checked = charmsEnabled,
        };
        _enabledChk.CheckedChanged += (_, _) => Apply();
        Controls.Add(_enabledChk);

        // How long a logo lingers before it's swept (in case an agent never sends a closing event).
        // Shown in minutes; stored as seconds. The engine's sweep clears the logo within a few seconds
        // of this elapsing.
        var timeoutLabel = new Label
        {
            AutoSize = false,
            Location = new Point(30, 82),
            Size = new Size(150, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Clear a stuck logo after:",
        };
        Controls.Add(timeoutLabel);
        _timeoutNum = new NumericUpDown
        {
            Location = new Point(184, 80),
            Size = new Size(60, 23),
            Minimum = 0.5m,
            Maximum = 30m,
            Increment = 0.5m,
            DecimalPlaces = 1,
            Value = Math.Clamp(timeoutSeconds / 60m, 0.5m, 30m),
        };
        _timeoutNum.ValueChanged += (_, _) => Apply();
        Controls.Add(_timeoutNum);
        var minutesLabel = new Label
        {
            AutoSize = false,
            Location = new Point(248, 82),
            Size = new Size(80, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "minutes",
        };
        Controls.Add(minutesLabel);

        // Live preview: fake a waiting-agent count on the real cursor so you can see the logos
        // (and the "+N" tag) without wiring up an actual agent. "Clear" ends it; closing the
        // dialog ends it too, so a test never leaves phantom logos stuck on the cursor.
        var previewLabel = new Label
        {
            AutoSize = false,
            Location = new Point(30, 110),
            Size = new Size(150, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Preview on cursor:",
        };
        Controls.Add(previewLabel);
        _previewNum = new NumericUpDown
        {
            Location = new Point(184, 108),
            Size = new Size(48, 23),
            Minimum = 1,
            Maximum = 9,
            Value = 2,
        };
        Controls.Add(_previewNum);
        _previewShow = new Button
        {
            Location = new Point(240, 107),
            Size = new Size(110, 26),
            Text = "Show logos",
        };
        _previewShow.Click += (_, _) => ShowPreview();
        Controls.Add(_previewShow);
        _previewClear = new Button
        {
            Location = new Point(356, 107),
            Size = new Size(90, 26),
            Text = "Clear",
            Enabled = false,
        };
        _previewClear.Click += (_, _) => EndPreview();
        Controls.Add(_previewClear);
        // Attached after _previewClear exists so the lambda can read it without a null warning.
        // While a preview is live, nudging the count re-renders it at the new number.
        _previewNum.ValueChanged += (_, _) => { if (_previewClear.Enabled) ShowPreview(); };

        var divider = new Label
        {
            AutoSize = false,
            BorderStyle = BorderStyle.Fixed3D,
            Location = new Point(12, 144),
            Size = new Size(512, 2),
        };
        Controls.Add(divider);

        var toolsHeader = new Label
        {
            AutoSize = false,
            Location = new Point(12, 154),
            Size = new Size(512, 20),
            Font = new Font(Font, FontStyle.Bold),
            Text = "Registered tools",
        };
        Controls.Add(toolsHeader);

        // --- registration rows ---
        int y = 180;
        foreach (var tool in AgentHookRegistrar.Tools)
        {
            var name = new Label
            {
                AutoSize = false,
                Location = new Point(12, y),
                Size = new Size(168, 36),
                Text = tool.DisplayName + "\n" + tool.ConfigHint,
            };
            var status = new Label
            {
                AutoSize = false,
                Location = new Point(188, y + 8),
                Size = new Size(176, 20),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var action = new Button
            {
                Location = new Point(372, y + 4),
                Size = new Size(150, 28),
            };
            var captured = tool;
            action.Click += (_, _) => OnAction(captured);

            Controls.Add(name);
            Controls.Add(status);
            Controls.Add(action);
            _rows.Add((tool, status, action));
            y += 44;
        }

        var hint = new Label
        {
            AutoSize = false,
            Location = new Point(12, y + 2),
            Size = new Size(512, 20),
            ForeColor = SystemColors.GrayText,
            Text = "Registering takes effect for new agent sessions; each config is backed up (.bak) first.",
        };
        Controls.Add(hint);

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 28),
            Location = new Point(434, y + 30),
        };
        Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;

        ClientSize = new Size(536, y + 70);
        SyncEnabled();
        RefreshAll();
    }

    void Apply()
    {
        SyncEnabled();
        _applyDisplay(_enabledChk.Checked, (int)Math.Round(_timeoutNum.Value * 60m));
        // Keep an in-flight preview honest if the user just turned charms off, or re-render it if
        // they're still on.
        if (_previewClear.Enabled)
        {
            if (_enabledChk.Checked) ShowPreview();
            else EndPreview();
        }
    }

    void SyncEnabled()
    {
        // The timeout governs the sweep (and the tray count) regardless of whether the logo is drawn,
        // so it stays enabled; only the on-cursor preview is gated on the logo being shown.
        bool on = _enabledChk.Checked;
        _previewNum.Enabled = on;
        _previewShow.Enabled = on;
        // _previewClear stays enabled only while a preview is actually showing.
    }

    void ShowPreview()
    {
        _preview((int)_previewNum.Value);
        _previewClear.Enabled = true;
    }

    void EndPreview()
    {
        _preview(null);
        _previewClear.Enabled = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Never leave a fake count on the cursor after the dialog is gone.
        if (_previewClear.Enabled) _preview(null);
        base.OnFormClosed(e);
    }

    void RefreshAll()
    {
        foreach (var row in _rows) Refresh(row);
    }

    static void Refresh((HookTool tool, Label status, Button action) row)
    {
        switch (AgentHookRegistrar.GetState(row.tool.Id))
        {
            case HookState.Registered:
                row.status.Text = "● Registered";
                row.status.ForeColor = Color.SeaGreen;
                row.action.Text = "Unregister";
                row.action.Tag = "unregister";
                break;
            case HookState.ConfigConflict:
                row.status.Text = "● Manual setup needed";
                row.status.ForeColor = Color.DarkOrange;
                row.action.Text = "Register";
                row.action.Tag = "register";
                break;
            default:
                row.status.Text = "○ Not registered";
                row.status.ForeColor = SystemColors.GrayText;
                row.action.Text = "Register";
                row.action.Tag = "register";
                break;
        }
    }

    void OnAction(HookTool tool)
    {
        var row = _rows.First(r => r.tool.Id == tool.Id);
        bool register = (string?)row.action.Tag == "register";
        try
        {
            if (register) AgentHookRegistrar.Register(tool.Id);
            else AgentHookRegistrar.Unregister(tool.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Agent notifications", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Refresh(row);
    }
}
