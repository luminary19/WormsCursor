using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// One app on the Apps page, in the list or in the "Open now" picker: its exe name (what the list
/// stores and matches), a friendlier name and icon when they're known, and a second line.
/// </summary>
public sealed record AppRow(string Program, string Name, string? Detail, ImageSource? Icon)
{
    /// <summary>A listed app: named by its exe's description and shown with its icon when the exe is
    /// known (it has a window open now, or had one earlier while the page was open), else just its exe
    /// name with a generic glyph.</summary>
    public static AppRow Listed(string program, AppIdentity? known)
        => known is { } k
            ? new AppRow(program, k.Description ?? program, k.Description is null ? null : program, k.Icon)
            : new AppRow(program, program, null, null);

    /// <summary>An app with a window open now, with that window's title.</summary>
    public static AppRow Open(WindowProgram window, AppIdentity identity)
        => new(window.Program, identity.Description ?? window.Program,
               identity.Description is null ? window.Title : $"{window.Program} · {window.Title}", identity.Icon);
}

/// <summary>What an exe file says about itself: its description ("Dota 2") and icon, either of which
/// can be missing.</summary>
public sealed record AppIdentity(string? Description, ImageSource? Icon)
{
    /// <summary>Reads <paramref name="exePath"/>'s version-info description and its icon. Best-effort:
    /// a file that can't be read (gone, or locked down) just has neither, which is logged.</summary>
    public static AppIdentity Read(string exePath)
    {
        string? description = null;
        try { description = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim(); }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"no description for {exePath}: {ex.Message}");
        }

        ImageSource? icon = null;
        try
        {
            using var gdiIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (gdiIcon is not null)
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(gdiIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                icon = bitmap;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"no icon for {exePath}: {ex.Message}");
        }

        return new AppIdentity(string.IsNullOrEmpty(description) ? null : description, icon);
    }
}
