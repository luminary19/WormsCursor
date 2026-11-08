using WormsCursor.Core;

namespace OpenCodeCursorOverlay;

/// <summary>
/// Hides the OpenCode token while a program on WormsCursor's suppression list owns the foreground
/// window, using the same Core pieces as the tray app (linked source, no DLL): a
/// <see cref="ForegroundWatcher"/> for focus changes and the list from the tray's own
/// <c>%LocalAppData%\WormsCursor\settings.json</c>. Both a focus change and a settings save re-check, so
/// a list edited in the tray's Preferences applies even when focus returned to the game before the save.
/// </summary>
internal sealed class FocusSuppression : IDisposable
{
    private readonly CursorOverlay overlay;
    private readonly ForegroundWatcher watcher = new();
    private readonly SuppressionListFile settings = new(SettingsStore.DefaultPath);
    private readonly object gate = new();

    /// <summary>Call after the overlay's handle exists: decisions are posted to its UI thread.</summary>
    public FocusSuppression(CursorOverlay overlay)
    {
        this.overlay = overlay;
        watcher.Changed += Evaluate;
        settings.Changed += Evaluate;
        Evaluate();
    }

    // Runs on the constructing thread once, then on the watcher threads for each focus change or save.
    private void Evaluate()
    {
        lock (gate) // posting under the lock keeps decisions in order
        {
            var suppressed = ProgramSuppression.Matches(watcher.ForegroundProgram, settings.Current());
            overlay.Post(() => overlay.SetSuppressed(suppressed));
        }
    }

    public void Dispose()
    {
        settings.Dispose();
        watcher.Dispose();
    }
}
