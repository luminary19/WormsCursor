using Microsoft.Win32;

namespace AgentsCursor.App;

/// <summary>Windows' app mode (Settings &gt; Personalization &gt; Colors), which the control panel follows
/// under <see cref="Core.ThemePreference.System"/>, and so does the token's project label.</summary>
public static class WindowsTheme
{
    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps are dark. Light when the setting can't be read (Windows' own default).</summary>
    public static bool AppsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
