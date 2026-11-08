using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The left side of a settings card: a title over a secondary description line, the way Windows
/// Settings labels its rows. The description hides itself when empty.
/// </summary>
public sealed class SettingHeader : StackPanel
{
    readonly TextBlock _title = new() { FontTypography = FontTypography.Body, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

    public SettingHeader()
    {
        VerticalAlignment = VerticalAlignment.Center;
        _description.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _description.FontTypography = FontTypography.Caption;
        _description.Visibility = Visibility.Collapsed;
        Children.Add(_title);
        Children.Add(_description);
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public string Description
    {
        get => _description.Text;
        set
        {
            _description.Text = value;
            _description.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
