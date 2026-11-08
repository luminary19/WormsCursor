using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The programs that hide the token while they're in front (a game, say): the list with a Remove per
/// row, an "Add a program" card that offers the programs open right now (exe name and window title)
/// or takes a typed exe name, and a way back to the default list. Each edit replaces
/// <see cref="CursorSettings.SuppressedPrograms"/> through <see cref="LiveSettings.Change"/>, which
/// also re-checks the program in front at once.
/// </summary>
public partial class AppsPage : Page
{
    readonly LiveSettings _settings;

    public AppsPage(LiveSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        AddExpander.Expanded += (_, _) => ShowOpenPrograms();
        RefreshButton.Click += (_, _) => ShowOpenPrograms();
        NameBox.TextChanged += (_, _) => AddNameButton.IsEnabled = TypedName() is not null;
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddTyped(); };
        AddNameButton.Click += (_, _) => AddTyped();
        DefaultsButton.Click += (_, _) => _settings.Change(s => s.SuppressedPrograms = ProgramSuppression.Defaults);
        DefaultsButton.ToolTip = "Replace the list with " + string.Join(", ", ProgramSuppression.Defaults);

        _settings.Changed += Refresh;
        Refresh();
    }

    // Everything here follows the program list, which is replaced (never changed in place) on each
    // edit, so other settings changes (a size drag on the Token page) leave the page alone.
    void Refresh()
    {
        var programs = _settings.Live.SuppressedPrograms;
        if (ReferenceEquals(programs, ProgramList.ItemsSource)) return;
        ProgramList.ItemsSource = programs;
        bool empty = programs.Count == 0;
        ListSurface.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DefaultsButton.IsEnabled = !programs.SequenceEqual(ProgramSuppression.Defaults, StringComparer.OrdinalIgnoreCase);
        AddNameButton.IsEnabled = TypedName() is not null;
        if (AddExpander.IsExpanded) ShowOpenPrograms(); // added ones leave the "open now" list
    }

    // The programs showing a window right now, minus those already listed.
    void ShowOpenPrograms()
    {
        var listed = _settings.Live.SuppressedPrograms;
        var open = WindowPrograms.ListVisible().Where(p => !ProgramSuppression.Matches(p.Program, listed)).ToArray();
        OpenList.ItemsSource = open;
        NothingOpen.Visibility = open.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // The typed name, normalized, if it is a usable program name that isn't listed yet.
    string? TypedName()
    {
        string? name = ProgramSuppression.NormalizeName(NameBox.Text);
        return name is null || ProgramSuppression.Matches(name, _settings.Live.SuppressedPrograms) ? null : name;
    }

    void AddTyped()
    {
        if (TypedName() is not string name) return;
        NameBox.Text = "";
        Add(name);
    }

    void OnAddOpen(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is WindowProgram program) Add(program.Program);
    }

    void Add(string program) =>
        _settings.Change(s => s.SuppressedPrograms = s.SuppressedPrograms.Append(program).ToArray());

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not string program) return;
        _settings.Change(s => s.SuppressedPrograms = s.SuppressedPrograms
            .Where(p => !string.Equals(p, program, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
}
