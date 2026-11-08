using WormsCursor.Core;

namespace OpenCodeCursorOverlay;

/// <summary>
/// The suppressed-program list as the tray app last saved it to its settings file, read only (the
/// tray owns the file). <see cref="Current"/> re-reads the file whenever its write time has moved, and
/// <see cref="Changed"/> fires when the file is written, so a list edited in the tray's Preferences
/// takes effect at once, whatever window has focus at that moment. A read that fails (the file locked
/// mid-save, or corrupt) keeps the last good list and is retried on the next call.
/// </summary>
internal sealed class SuppressionListFile : IDisposable
{
    private readonly string path;
    private readonly FileSystemWatcher watcher;
    private readonly object gate = new();
    private IReadOnlyList<string> programs = [];
    private DateTime readWriteTime = DateTime.MinValue; // never a real write time, so the first call reads

    /// <summary>Raised on a thread-pool thread when the settings file is written, created, replaced or
    /// deleted. Handlers call <see cref="Current"/> for the new list.</summary>
    public event Action? Changed;

    public SuppressionListFile(string path)
    {
        this.path = path;
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory); // the watcher needs the folder, and the tray may not have saved yet
        watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        // The tray saves by replacing the file with a temp file, which reports as a rename.
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += OnFileEvent;
        watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke();

    /// <summary>The list in force now. Safe to call from any thread.</summary>
    public IReadOnlyList<string> Current()
    {
        lock (gate)
        {
            // A missing file reports a fixed sentinel time, and TryRead then yields the defaults. If even
            // the time can't be read (ACL-denied), the last list read stays in force.
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return programs; }
            if (written == readWriteTime) return programs;
            var settings = SettingsStore.TryRead(path);
            if (settings is null) return programs; // not readable right now: keep the last good list, retry next call
            programs = settings.SuppressedPrograms;
            readWriteTime = written;
            return programs;
        }
    }

    public void Dispose() => watcher.Dispose();
}
