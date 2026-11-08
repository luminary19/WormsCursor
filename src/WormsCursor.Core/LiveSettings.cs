namespace WormsCursor.Core;

/// <summary>
/// The one owner of the running app's <see cref="CursorSettings"/>, and the only way to change them.
/// Every edit, whether from the control panel or the tray menu, is a small <see cref="Change"/> that
/// touches just the fields it means to, applied to the live instance and saved straight away. Nobody
/// edits a copy and writes it back, so one editor can never revert another's change with a stale
/// snapshot.
///
/// Single-threaded: call <see cref="Change"/> and read <see cref="Current"/> on the UI thread.
/// <see cref="Live"/> is the instance itself, for the overlay's animation thread, which reads it every
/// frame and never writes it.
/// </summary>
public sealed class LiveSettings
{
    readonly CursorSettings _settings;
    readonly Action<CursorSettings> _save;

    /// <param name="settings">The loaded settings this instance now owns.</param>
    /// <param name="save">Persists the settings after each change. It must not throw for an ordinary
    /// I/O failure: the change has already taken effect for this run.</param>
    public LiveSettings(CursorSettings settings, Action<CursorSettings> save)
    {
        _settings = settings;
        _save = save;
        _settings.Normalize();
    }

    /// <summary>Raised on the calling (UI) thread after each <see cref="Change"/> was applied and saved.</summary>
    public event Action? Changed;

    /// <summary>A copy of the current values, for reading. Changing it changes nothing.</summary>
    public CursorSettings Current => _settings.Clone();

    /// <summary>The live instance, for a reader that must see every change without a copy (the
    /// overlay). Treat it as read-only; edits go through <see cref="Change"/>.</summary>
    public CursorSettings Live => _settings;

    /// <summary>Applies <paramref name="edit"/> to the live settings, clamps them into range, saves
    /// them and raises <see cref="Changed"/>.</summary>
    public void Change(Action<CursorSettings> edit)
    {
        edit(_settings);
        _settings.Normalize();
        _save(_settings);
        Changed?.Invoke();
    }
}
