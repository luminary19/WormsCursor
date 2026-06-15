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

        // The whole dialog flows top-to-bottom and every text control is AutoSize, so nothing clips
        // when the system font / "Make text bigger" is enlarged, and rows self-align to their tallest
        // control. `pad` is the outer margin; `innerW` the content column; `y` the running cursor.
        const int pad = 14, innerW = 560, indent = 16;
        int rightEdge = pad + innerW;
        int y = pad;

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(innerW, 0), // wrap to the column, grow as tall as needed
            Location = new Point(pad, y),
            Text = "When an AI agent is waiting for you, the cursor hangs that tool's logo — with a "
                 + "“+N” tag when several wait. Register the tools below so they tell WormsCursor what "
                 + "they're doing.",
        };
        Controls.Add(intro);
        y = intro.Bottom + 14;

        // --- display section ---
        _enabledChk = new CheckBox
        {
            AutoSize = true,
            Location = new Point(pad, y),
            Text = "Show the agent logo on the cursor while agents are waiting",
            Checked = charmsEnabled,
        };
        _enabledChk.CheckedChanged += (_, _) => Apply();
        Controls.Add(_enabledChk);
        y = _enabledChk.Bottom + 12;

        // The timeout and the preview count share ONE line: the two numeric boxes sit on the same
        // row — the timeout group flush-left, the preview group flush-right (its box ends at the
        // content's right edge). The preview's action buttons go right-aligned on the row just
        // below, lined up under that box.

        // How long a logo lingers before it's swept (in case an agent never sends a closing event).
        // Shown in minutes; stored as seconds.
        var timeoutLabel = new Label { AutoSize = true, Text = "Clear a stuck logo after:" };
        _timeoutNum = new NumericUpDown
        {
            Width = 64,
            Minimum = 0.5m, Maximum = 30m, Increment = 0.5m, DecimalPlaces = 1,
            Value = Math.Clamp(timeoutSeconds / 60m, 0.5m, 30m),
        };
        var minutesLabel = new Label { AutoSize = true, Text = "minutes" };

        // Live preview: fake a waiting-agent count on the real cursor so you can see the logo (and the
        // "+N" tag) without wiring up an actual agent. "Clear" ends it; closing the dialog ends it too.
        var previewLabel = new Label { AutoSize = true, Text = "Preview:" };
        _previewNum = new NumericUpDown { Width = 52, Minimum = 1, Maximum = 9, Value = 2 };
        _previewShow = MakeButton("Show logos");
        _previewShow.Click += (_, _) => ShowPreview();
        _previewClear = MakeButton("Clear");
        _previewClear.Enabled = false;
        _previewClear.Click += (_, _) => EndPreview();
        Controls.Add(timeoutLabel); Controls.Add(_timeoutNum); Controls.Add(minutesLabel);
        Controls.Add(previewLabel); Controls.Add(_previewNum); Controls.Add(_previewShow); Controls.Add(_previewClear);

        // Both numeric boxes on one line. rowH is the tallest control so an enlarged font centres
        // rather than clips; the preview group is right-aligned so its box ends at rightEdge.
        int numsRowH = Math.Max(Math.Max(_timeoutNum.Height, _previewNum.Height),
                                Math.Max(timeoutLabel.Height, previewLabel.Height));
        PlaceRow(pad + indent, y, numsRowH, 6, timeoutLabel, _timeoutNum, minutesLabel);
        PlaceRow(rightEdge - GroupWidth(6, previewLabel, _previewNum), y, numsRowH, 6, previewLabel, _previewNum);
        y += numsRowH + 10;

        // Preview action buttons, right-aligned under the preview box.
        int previewBtnH = Math.Max(_previewShow.Height, _previewClear.Height);
        PlaceRow(rightEdge - GroupWidth(8, _previewShow, _previewClear), y, previewBtnH, 8, _previewShow, _previewClear);
        y += previewBtnH + 16;

        _timeoutNum.ValueChanged += (_, _) => Apply();
        // Attached after _previewClear exists so the lambda can read it; nudging the count while a
        // preview is live re-renders it at the new number.
        _previewNum.ValueChanged += (_, _) => { if (_previewClear.Enabled) ShowPreview(); };

        var divider = new Label
        {
            AutoSize = false,
            BorderStyle = BorderStyle.Fixed3D,
            Location = new Point(pad, y),
            Size = new Size(innerW, 2),
        };
        Controls.Add(divider);
        y = divider.Bottom + 14;

        var toolsHeader = new Label
        {
            AutoSize = true,
            Location = new Point(pad, y),
            Font = new Font(Font, FontStyle.Bold),
            Text = "Registered tools",
        };
        Controls.Add(toolsHeader);
        y = toolsHeader.Bottom + 10;

        // --- registration rows: name (left), status (middle), Register/Unregister (right) ---
        foreach (var tool in AgentHookRegistrar.Tools)
        {
            var name = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(180, 0),
                Text = tool.DisplayName + "\n" + tool.ConfigHint,
            };
            var status = new Label { AutoSize = true };
            var action = MakeButton(""); // text + width set by Refresh below, before we position it
            var captured = tool;
            action.Click += (_, _) => OnAction(captured);
            Controls.Add(name); Controls.Add(status); Controls.Add(action);
            var row = (tool, status, action);
            _rows.Add(row);
            Refresh(row); // populate text + size first, so right-alignment uses the real button width

            int rowH = Math.Max(Math.Max(name.Height, status.Height), action.Height);
            name.Location = new Point(pad, y + (rowH - name.Height) / 2);
            status.Location = new Point(pad + 186, y + (rowH - status.Height) / 2);
            action.Location = new Point(rightEdge - action.Width, y + (rowH - action.Height) / 2);
            y += rowH + 12;
        }

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(innerW, 0),
            ForeColor = SystemColors.GrayText,
            Location = new Point(pad, y),
            Text = "Registering takes effect for new agent sessions; each config is backed up (.bak) first.",
        };
        Controls.Add(hint);
        y = hint.Bottom + 16;

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 4, 14, 4),
        };
        Controls.Add(close);
        close.Location = new Point(rightEdge - close.Width, y);
        AcceptButton = close;
        CancelButton = close;
        y = close.Bottom + pad;

        ClientSize = new Size(rightEdge + pad, y);
        SyncEnabled();
    }

    // A push-button that grows to fit its label + the system font (never clips), with a sensible
    // minimum width so short labels still look like buttons.
    static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 3, 12, 3),
        MinimumSize = new Size(84, 0),
    };

    // Lays controls left-to-right from x=left, `gap` px apart, each vertically centred within
    // `rowH`. Heights come from the controls themselves, so an enlarged font centres instead of
    // clipping. Pair with GroupWidth to right-align a group: PlaceRow(rightEdge - GroupWidth(...), …).
    static void PlaceRow(int left, int top, int rowH, int gap, params Control[] controls)
    {
        int x = left;
        foreach (var c in controls)
        {
            c.Location = new Point(x, top + (rowH - c.Height) / 2);
            x += c.Width + gap;
        }
    }

    // Total width a horizontal group of controls occupies, `gap` px between each.
    static int GroupWidth(int gap, params Control[] controls)
    {
        int w = 0;
        for (int i = 0; i < controls.Length; i++) w += controls[i].Width + (i > 0 ? gap : 0);
        return w;
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
            case HookState.Outdated:
                row.status.Text = "● Update available";
                row.status.ForeColor = Color.DarkOrange;
                row.action.Text = "Re-register";
                row.action.Tag = "register";
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
