using System.Windows.Controls;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The token's look: the master switch, a live preview of both faces (done, and the yellow "?" for
/// "needs you") over dark and light, the size, and where it shows. Every control writes its own field
/// through <see cref="LiveSettings.Change"/> and the page redraws from the settings on each change, so
/// it also follows edits made elsewhere (the tray's "Enabled").
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
        PlacementBox.SelectionChanged += (_, _) =>
        {
            if (PlacementBox.SelectedIndex < 0) return;
            var placement = (NotifierPlacement)PlacementBox.SelectedIndex;
            if (placement != _settings.Live.Placement) _settings.Change(s => s.Placement = placement);
        };
        CornerBox.SelectionChanged += (_, _) =>
        {
            if (CornerBox.SelectedIndex < 0) return;
            var corner = (ScreenCorner)CornerBox.SelectedIndex;
            if (corner != _settings.Live.Corner) _settings.Change(s => s.Corner = corner);
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
        PlacementBox.SelectedIndex = (int)s.Placement;
        CornerBox.SelectedIndex = (int)s.Corner;

        bool corner = s.Placement == NotifierPlacement.Corner;
        CornerCard.IsEnabled = corner;
        CornerHeader.Description = corner ? "Where the token waits." : "Used when the position is a screen corner.";

        ResetButton.IsEnabled = !s.HasDefaultAppearance();

        DonePreview.Show(s);
        BlockedPreview.Show(s);
    }
}
