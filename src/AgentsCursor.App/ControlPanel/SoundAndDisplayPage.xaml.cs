using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentsCursor.Core;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// How the token shows and sounds: the master switch, the size (the slider, or double-click the "px"
/// label to type an exact size), where it shows (below, above or beside the pointer, or one of the four
/// screen corners, as one choice), how it moves (Bouncy, Calm or Still; Still, fixed, while Windows'
/// animation effects are off), whether it names the waiting session's project, and a live preview of a
/// pointer gliding over dark and light with the token following it the chosen way, a sample project
/// named under it (the faces and icons are the Customization page's). Then the
/// sounds: on/off, the "Needs you" and "Done" chimes (each with a play button) and their volume, shown
/// only while sounds are on. Every control writes its own fields through
/// <see cref="LiveSettings.Change"/> and the page redraws from the settings on each change, so it also
/// follows edits made elsewhere (the tray's "Enabled").
/// </summary>
public partial class SoundAndDisplayPage : Page
{
    readonly LiveSettings _settings;
    readonly IControlPanelHost _host;

    // The chimes each sound box offers, in order (the default chime of each state is among them).
    static readonly Chime[] Chimes = Enum.GetValues<Chime>();

    public SoundAndDisplayPage(IControlPanelHost host)
    {
        _host = host;
        _settings = host.Settings;
        InitializeComponent();
        NeedsYouBox.ItemsSource = Chimes.Select(ChimeName).ToArray();
        DoneBox.ItemsSource = Chimes.Select(ChimeName).ToArray();
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
        // Choosing a pointer position keeps the corner last chosen, for when a corner comes back.
        PositionBox.SelectionChanged += (_, _) =>
        {
            int index = PositionBox.SelectedIndex;
            if (index < 0 || index == PositionIndex(_settings.Live)) return;
            if (index < PointerPositions.Length) _settings.Change(s => s.Placement = PointerPositions[index]);
            else if (index > PointerPositions.Length)
                _settings.Change(s => { s.Placement = NotifierPlacement.Corner; s.Corner = (ScreenCorner)(index - FirstCorner); });
        };
        ProjectNameSwitch.Checked += (_, _) => SetProjectName(true);
        ProjectNameSwitch.Unchecked += (_, _) => SetProjectName(false);
        ResetButton.Click += (_, _) => _settings.Change(s => s.ResetDisplay());
        MotionBox.SelectionChanged += (_, _) =>
        {
            // Disabled while Windows' animation effects are off, so a choice here is always the user's.
            var motion = (MotionStyle)MotionBox.SelectedIndex;
            if (MotionBox.IsEnabled && MotionBox.SelectedIndex >= 0 && motion != _settings.Live.Motion)
                _settings.Change(s => s.Motion = motion);
        };
        // Windows' animation effects switched on or off: show what applies now.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Refresh();
        };

        SoundsSwitch.Checked += (_, _) => SetSounds(true);
        SoundsSwitch.Unchecked += (_, _) => SetSounds(false);
        NeedsYouBox.SelectionChanged += (_, _) =>
        {
            if (NeedsYouBox.SelectedIndex < 0) return;
            var chime = Chimes[NeedsYouBox.SelectedIndex];
            if (chime != _settings.Live.NeedsYouSound) _settings.Change(s => s.NeedsYouSound = chime);
        };
        DoneBox.SelectionChanged += (_, _) =>
        {
            if (DoneBox.SelectedIndex < 0) return;
            var chime = Chimes[DoneBox.SelectedIndex];
            if (chime != _settings.Live.DoneSound) _settings.Change(s => s.DoneSound = chime);
        };
        PlayNeedsYou.Click += (_, _) => _host.PlayChime(_settings.Live.NeedsYouSound);
        PlayDone.Click += (_, _) => _host.PlayChime(_settings.Live.DoneSound);
        VolumeSlider.ValueChanged += (_, e) =>
        {
            int volume = (int)Math.Round(e.NewValue);
            if (volume != _settings.Live.SoundVolume) _settings.Change(s => s.SoundVolume = volume);
        };

        _settings.Changed += Refresh;
        Refresh();
    }

    void SetProjectName(bool on)
    {
        if (on != _settings.Live.ShowProjectName) _settings.Change(s => s.ShowProjectName = on);
    }

    void SetSounds(bool on)
    {
        if (on != _settings.Live.SoundsEnabled) _settings.Change(s => s.SoundsEnabled = on);
    }

    static string ChimeName(Chime chime) => chime.ToString();

    void SetEnabled(bool on)
    {
        if (on != _settings.Live.AgentNotifierEnabled) _settings.Change(s => s.AgentNotifierEnabled = on);
    }

    // ---------- typing an exact size ----------

    void OnSizeTextClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        SizeEdit.Text = _settings.Live.Size.ToString(CultureInfo.CurrentCulture);
        SizeText.Visibility = Visibility.Collapsed; // the box takes the label's fixed-width slot
        SizeEdit.Visibility = Visibility.Visible;
        SizeEdit.Focus();
        SizeEdit.SelectAll();
        e.Handled = true;
    }

    void OnSizeEditKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) EndSizeEdit(apply: true);
        else if (e.Key == Key.Escape) EndSizeEdit(apply: false);
        else return;
        e.Handled = true;
    }

    void OnSizeEditLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndSizeEdit(apply: true);

    // A number (a trailing "px" is fine) is clamped into the allowed range; anything else changes nothing.
    void EndSizeEdit(bool apply)
    {
        if (SizeEdit.Visibility != Visibility.Visible) return;
        SizeEdit.Visibility = Visibility.Collapsed;
        SizeText.Visibility = Visibility.Visible;
        string typed = SizeEdit.Text.Trim();
        if (typed.EndsWith("px", StringComparison.OrdinalIgnoreCase)) typed = typed[..^2].TrimEnd();
        if (!apply || !int.TryParse(typed, NumberStyles.Integer, CultureInfo.CurrentCulture, out int size)) return;
        size = Math.Clamp(size, CursorSettings.MinSize, CursorSettings.MaxSize);
        if (size != _settings.Live.Size) _settings.Change(s => s.Size = size);
    }

    void Refresh()
    {
        var s = _settings.Current;
        EnabledSwitch.IsChecked = s.AgentNotifierEnabled;
        SizeSlider.Value = s.Size;
        SizeText.Text = $"{s.Size} px";
        PositionBox.SelectedIndex = PositionIndex(s);
        ProjectNameSwitch.IsChecked = s.ShowProjectName;
        ResetButton.IsEnabled = !s.HasDefaultDisplay();

        bool animations = SystemParameters.ClientAreaAnimation;
        MotionBox.IsEnabled = animations;
        MotionBox.SelectedIndex = (int)MotionProfile.Effective(s.Motion, animations);
        MotionBox.ToolTip = animations ? null : "Animation effects are off in Windows, so the token stays still";

        SoundsSwitch.IsChecked = s.SoundsEnabled;
        SoundSettings.Visibility = s.SoundsEnabled ? Visibility.Visible : Visibility.Collapsed;
        NeedsYouBox.SelectedIndex = Array.IndexOf(Chimes, s.NeedsYouSound);
        DoneBox.SelectedIndex = Array.IndexOf(Chimes, s.DoneSound);
        PlayNeedsYou.IsEnabled = s.NeedsYouSound != Chime.None && s.SoundVolume > 0;
        PlayDone.IsEnabled = s.DoneSound != Chime.None && s.SoundVolume > 0;
        VolumeSlider.Value = s.SoundVolume;
        VolumeText.Text = $"{s.SoundVolume}%";

        Preview.Tool = IconCatalog.Tools[0];
        Preview.Project = TokenLabel.SampleProject;
        Preview.Show(s);
    }

    // The Position list: the pointer positions, a separator, then the corners in ScreenCorner order.
    static readonly NotifierPlacement[] PointerPositions =
        { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer };
    static readonly int FirstCorner = PointerPositions.Length + 1;

    static int PositionIndex(CursorSettings s)
        => s.Placement == NotifierPlacement.Corner ? FirstCorner + (int)s.Corner : Array.IndexOf(PointerPositions, s.Placement);
}
