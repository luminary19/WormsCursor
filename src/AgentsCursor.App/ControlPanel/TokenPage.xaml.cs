using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentsCursor.Core;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// The token's look: the master switch, the size (the slider, or double-click the "px" label to type
/// an exact size), where it shows (below, above or beside the pointer,
/// or one of the four screen corners, as one choice), and a live preview of a pointer gliding over dark
/// and light with the token following it the chosen way (the faces and icons are the Icons page's). Every control writes its own fields through
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
        // Choosing a pointer position keeps the corner last chosen, for when a corner comes back.
        PositionBox.SelectionChanged += (_, _) =>
        {
            int index = PositionBox.SelectedIndex;
            if (index < 0 || index == PositionIndex(_settings.Live)) return;
            if (index < PointerPositions.Length) _settings.Change(s => s.Placement = PointerPositions[index]);
            else if (index > PointerPositions.Length)
                _settings.Change(s => { s.Placement = NotifierPlacement.Corner; s.Corner = (ScreenCorner)(index - FirstCorner); });
        };
        ResetButton.Click += (_, _) => _settings.Change(s => s.ResetSizeAndPosition());

        _settings.Changed += Refresh;
        Refresh();
    }

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
        ResetButton.IsEnabled = !s.HasDefaultSizeAndPosition();

        Preview.Tool = IconCatalog.Tools[0];
        Preview.Show(s);
    }

    // The Position list: the pointer positions, a separator, then the corners in ScreenCorner order.
    static readonly NotifierPlacement[] PointerPositions =
        { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer };
    static readonly int FirstCorner = PointerPositions.Length + 1;

    static int PositionIndex(CursorSettings s)
        => s.Placement == NotifierPlacement.Corner ? FirstCorner + (int)s.Corner : Array.IndexOf(PointerPositions, s.Placement);
}
