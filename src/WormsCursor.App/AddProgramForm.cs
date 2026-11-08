using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// The "Add program" picker behind Preferences' suppressed-programs list. Lists the programs that
/// currently show a window (exe name + window title) so the user can pick the game they have open, or
/// type an exe name for one that isn't running. Programs already on the list are left out. The chosen,
/// normalized names come back via <see cref="Programs"/> when the dialog returns OK.
/// </summary>
public sealed class AddProgramForm : Form
{
    const int M = 16;   // outer margin (matches PreferencesForm)
    const int W = 440;  // client width
    const int ListH = 200;

    readonly IReadOnlyList<string> _existing;
    readonly Label _pickCap, _typeCap;
    readonly ListView _list;
    readonly Button _refreshBtn, _addBtn, _cancelBtn;
    readonly TextBox _nameBox;

    /// <summary>The programs to add (normalized exe names, no duplicates). Empty unless OK.</summary>
    public IReadOnlyList<string> Programs { get; private set; } = Array.Empty<string>();

    public AddProgramForm(IReadOnlyList<string> existing)
    {
        _existing = existing;

        AutoScaleMode = AutoScaleMode.Font;
        Text = "Add a program";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _pickCap = new Label { Text = "Pick a program that's open now", AutoSize = true };
        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            ShowItemToolTips = true, // a long window title shows in full on hover
        };
        _list.Columns.Add("Program", 180);
        _list.Columns.Add("Window", 150);
        _list.SelectedIndexChanged += (_, _) => SyncAddEnabled();
        _list.ItemActivate += (_, _) => Commit(); // double-click / Enter on a row adds it straight away

        _refreshBtn = MakeButton("Refresh");
        _refreshBtn.Click += (_, _) => Populate();

        _typeCap = new Label { Text = "Or type its exe name", AutoSize = true };
        _nameBox = new TextBox { PlaceholderText = "e.g. dota2.exe" };
        _nameBox.TextChanged += (_, _) => SyncAddEnabled();

        _addBtn = MakeButton("Add");
        _addBtn.Click += (_, _) => Commit();
        _cancelBtn = MakeButton("Cancel");
        _cancelBtn.DialogResult = DialogResult.Cancel;
        AcceptButton = _addBtn;
        CancelButton = _cancelBtn;

        Controls.AddRange(new Control[] { _pickCap, _list, _refreshBtn, _typeCap, _nameBox, _addBtn, _cancelBtn });
        Populate();
        LayoutControls();
    }

    void LayoutControls()
    {
        int innerW = W - 2 * M;
        int y = M;

        _pickCap.Location = new Point(M, y);
        y = _pickCap.Bottom + 4;
        _list.SetBounds(M, y, innerW, ListH);
        _list.Columns[1].Width = innerW - _list.Columns[0].Width - SystemInformation.VerticalScrollBarWidth - 4;
        y = _list.Bottom + 6;
        _refreshBtn.Location = new Point(W - M - _refreshBtn.Width, y);
        y = _refreshBtn.Bottom + 12;

        _typeCap.Location = new Point(M, y);
        y = _typeCap.Bottom + 4;
        _nameBox.SetBounds(M, y, innerW, _nameBox.Height);
        y = _nameBox.Bottom + 18;

        _cancelBtn.Location = new Point(W - M - _cancelBtn.Width, y);
        _addBtn.Location = new Point(_cancelBtn.Left - 8 - _addBtn.Width, y);
        y = _cancelBtn.Bottom + M;

        ClientSize = new Size(W, y);
    }

    static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 5, 12, 5),
        MinimumSize = new Size(88, 0),
    };

    // (Re)fills the list with programs showing a window right now, minus those already listed.
    void Populate()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var program in WindowPrograms.ListVisible())
        {
            if (ProgramSuppression.Matches(program.Program, _existing)) continue;
            _list.Items.Add(new ListViewItem(new[] { program.Program, program.Title }) { ToolTipText = program.Title });
        }
        _list.EndUpdate();
        SyncAddEnabled();
    }

    IReadOnlyList<string> Chosen() => ProgramSuppression.Normalize(
        _list.SelectedItems.Cast<ListViewItem>().Select(item => item.Text).Append(_nameBox.Text));

    void SyncAddEnabled() => _addBtn.Enabled = Chosen().Count > 0;

    void Commit()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) return;
        Programs = chosen;
        DialogResult = DialogResult.OK;
    }
}
