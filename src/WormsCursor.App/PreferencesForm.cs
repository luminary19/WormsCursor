using System.Drawing;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Preferences dialog. This is a placeholder skeleton — the real controls (rotation
/// speed, smoothing, polling rate, arrow size/colour) get wired to
/// <see cref="CursorSettings"/> in the next iteration. Returning <see cref="DialogResult.OK"/>
/// signals the tray context to rebuild the engine with the updated settings.
/// </summary>
public sealed class PreferencesForm : Form
{
    // Kept for the upcoming controls; suppress the "unused" hint for now.
#pragma warning disable IDE0052
    readonly CursorSettings _settings;
#pragma warning restore IDE0052

    public PreferencesForm(CursorSettings settings)
    {
        _settings = settings;

        Text = "WormsCursor — Preferences";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(380, 170);

        var info = new Label
        {
            Text = "Preferences UI coming soon.\r\n\r\n"
                 + "For now the engine uses the prototype defaults.",
            Dock = DockStyle.Top,
            Height = 100,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Width = 80,
            Left = ClientSize.Width - 2 * 80 - 3 * 8,
            Top = ClientSize.Height - 32 - 12,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Width = 80,
            Left = ClientSize.Width - 80 - 8,
            Top = ClientSize.Height - 32 - 12,
        };

        Controls.Add(info);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
