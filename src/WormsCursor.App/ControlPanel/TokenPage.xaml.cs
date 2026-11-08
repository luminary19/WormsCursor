using System.Windows.Controls;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The token's look: the master switch, the size, where it shows (following the pointer, or one of the
/// four screen corners, as one choice), and a live preview of both faces (done, and the yellow "?" for
/// "needs you") over dark and light. Every control writes its own fields through
/// <see cref="LiveSettings.Change"/> and the page redraws from the settings on each change, so it also
/// follows edits made elsewhere (the tray's "Enabled").
/// </summary>
public partial class TokenPage : Page
{
    readonly LiveSettings _settings;

    public TokenPage(LiveSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        SizeSlider.Minimum = CursorSettings.MinSize;
        SizeSlider.Maximum = CursorSettings.MaxSize;

        // Handlers compare against the live value, so the page setting a control in Refresh is a no-op.
        EnabledSwitch.Checked += (_, _) => SetEnabled(true);
        EnabledSwitch.Unchecked += (_, _) => SetEnabled(false);
        SizeSlider.ValueChanged += (_, e) =>
        {
            int size = (int)Math.Round(e.NewValue);
            if (size != _settings.Live.Size) _settings.Change(s => s.Size = size);
        };
        // Choosing "Follow the pointer" keeps the corner last chosen, for when a corner comes back.
        PositionBox.SelectionChanged += (_, _) =>
        {
            int index = PositionBox.SelectedIndex;
            if (index < 0 || index == PositionIndex(_settings.Live)) return;
            if (index == 0) _settings.Change(s => s.Placement = NotifierPlacement.Cursor);
            else _settings.Change(s => { s.Placement = NotifierPlacement.Corner; s.Corner = (ScreenCorner)(index - 1); });
        };
        ResetButton.Click += (_, _) => _settings.Change(s => s.ResetAppearance());

        _settings.Changed += Refresh;
        Refresh();
    }

    void SetEnabled(bool on)
    {
        if (on != _settings.Live.AgentNotifierEnabled) _settings.Change(s => s.AgentNotifierEnabled = on);
    }

    void Refresh()
    {
        var s = _settings.Current;
        EnabledSwitch.IsChecked = s.AgentNotifierEnabled;
        SizeSlider.Value = s.Size;
        SizeText.Text = $"{s.Size} px";
        PositionBox.SelectedIndex = PositionIndex(s);
        ResetButton.IsEnabled = !s.HasDefaultAppearance();

        DonePreview.Show(s);
        BlockedPreview.Show(s);
    }

    // The Position list: "Follow the pointer", then the corners in ScreenCorner order.
    static int PositionIndex(CursorSettings s)
        => s.Placement == NotifierPlacement.Corner ? 1 + (int)s.Corner : 0;
}
