using WormsCursor.Core;

namespace OpenCodeCursorOverlay;

/// <summary>
/// Hides the OpenCode token while a program on WormsCursor's suppression list owns the foreground
/// window, using the same Core pieces as the tray app (linked source, no DLL): a
/// <see cref="ForegroundWatcher"/> for focus changes and the list from the tray's own
/// <c>%LocalAppData%\WormsCursor\settings.json</c>. The file is re-read on a focus change only when its
/// write time moved, which is enough: the list is edited in the tray's Preferences, and switching back
/// to the game afterwards is itself a focus change.
/// </summary>
internal sealed class FocusSuppression : IDisposable
{
    private readonly CursorOverlay overlay;
    private readonly ForegroundWatcher watcher = new();
    private readonly object gate = new();
    private IReadOnlyList<string> programs = [];
    private DateTime settingsWritten = DateTime.MinValue; // never a real write time, so the first check reads

    /// <summary>Call after the overlay's handle exists: decisions are posted to its UI thread.</summary>
    public FocusSuppression(CursorOverlay overlay)
    {
        this.overlay = overlay;
        watcher.Changed += Evaluate;
        Evaluate();
    }

    // Runs on the constructing thread once, then on the watcher's thread for each focus change.
    private void Evaluate()
    {
        lock (gate) // posting under the lock keeps decisions in order
        {
            RefreshPrograms();
            var suppressed = ProgramSuppression.Matches(watcher.ForegroundProgram, programs);
            overlay.Post(() => overlay.SetSuppressed(suppressed));
        }
    }

    private void RefreshPrograms()
    {
        // A missing file reports a fixed sentinel time, and Read then yields the defaults. If even the
        // time can't be read (ACL-denied), the last list read stays in force.
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(SettingsStore.DefaultPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (written == settingsWritten) return;
        settingsWritten = written;
        programs = SettingsStore.Read().SuppressedPrograms;
    }

    public void Dispose() => watcher.Dispose();
}
