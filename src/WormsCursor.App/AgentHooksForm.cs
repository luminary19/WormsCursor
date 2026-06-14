using WormsCursor.App.Services;

namespace WormsCursor.App;

/// <summary>
/// The "Agent notifications" settings panel. Two parts: a display section (turn the dangling
/// worm-charms on/off, choose how many worms before a "+N" tag, and a live preview that fakes
/// a waiting-agent count on the real cursor), and a per-tool registration list showing whether
/// WormsCursor is hooked into each AI tool, with Register / Unregister.
/// Registration edits the tool's own config (backed up first); status is re-read after each action.
/// Display changes are pushed straight to the live engine via <paramref name="applyDisplay"/>;
/// the preview pushes a temporary count via <paramref name="preview"/> (null = end preview / restore).
/// Default-styled WinForms.
/// </summary>
public sealed class AgentHooksForm : Form
{
    readonly Action<bool, int> _applyDisplay;
    readonly Action<int?> _preview;
    readonly CheckBox _enabledChk;
    readonly NumericUpDown _capNum;
    readonly NumericUpDown _previewNum;
    readonly Button _previewShow;
    readonly Button _previewClear;
    readonly List<(HookTool tool, Label status, Button action)> _rows = new();

    /// <param name="preview">Shows a fake waiting count on the live cursor for testing; called with
    /// the count to preview, or <c>null</c> to end the preview and restore the real count.</param>
    public AgentHooksForm(bool charmsEnabled, int charmCap, Action<bool, int> applyDisplay, Action<int?> preview)
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
            Text = "When an AI agent is waiting for you, the cursor sprouts a dangling worm per "
                 + "waiting agent. Register the tools below so they tell WormsCursor what they're doing.",
        };
        Controls.Add(intro);

        // --- display section ---
        _enabledChk = new CheckBox
        {
            AutoSize = false,
            Location = new Point(12, 56),
            Size = new Size(400, 22),
            Text = "Show dangling worm-charms while agents are waiting",
            Checked = charmsEnabled,
        };
        _enabledChk.CheckedChanged += (_, _) => Apply();
        Controls.Add(_enabledChk);

        var capLabel = new Label
        {
            AutoSize = false,
            Location = new Point(30, 82),
            Size = new Size(210, 22),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Worms shown before a “+N” tag:",
        };
        Controls.Add(capLabel);
        _capNum = new NumericUpDown
        {
            Location = new Point(246, 80),
            Size = new Size(48, 23),
            Minimum = 1,
            Maximum = 6,
            Value = Math.Clamp(charmCap, 1, 6),
        };
        _capNum.ValueChanged += (_, _) => Apply();
        Controls.Add(_capNum);

        // Live preview: fake a waiting-agent count on the real cursor so you can see the worms
        // (and the "+N" tag) without wiring up an actual agent. "Clear" ends it; closing the
        // dialog ends it too, so a test never leaves phantom worms stuck on the cursor.
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
            Text = "Show worms",
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
        SyncCapEnabled();
        RefreshAll();
    }

    void Apply()
    {
        SyncCapEnabled();
        _applyDisplay(_enabledChk.Checked, (int)_capNum.Value);
        // Keep an in-flight preview honest if the user just turned charms off, or re-render it
        // with the new cap if they're still on.
        if (_previewClear.Enabled)
        {
            if (_enabledChk.Checked) ShowPreview();
            else EndPreview();
        }
    }

    void SyncCapEnabled()
    {
        bool on = _enabledChk.Checked;
        _capNum.Enabled = on;
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
