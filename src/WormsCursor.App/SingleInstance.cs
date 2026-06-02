using System.Threading;

namespace WormsCursor.App;

/// <summary>
/// Per-user single-instance guard. Only one WormsCursor may run for a given Windows
/// user; a second launch signals the first instance to open its Preferences dialog and
/// then exits without taking over the cursor or adding a second tray icon.
///
/// Mechanism:
///  - A named <see cref="Mutex"/> (per user) detects an already-running instance.
///  - A named <see cref="EventWaitHandle"/> (auto-reset) carries the "show preferences"
///    signal across processes. The owning instance runs a background thread that waits
///    on it and invokes a callback; a second instance just opens the handle and Set()s it.
///
/// Usage from Main:
///  1. Call <see cref="Acquire"/> FIRST, before constructing any UI. If it returns null,
///     another instance was running (and has now been signalled) — exit immediately.
///  2. Otherwise build the tray, then call <see cref="StartListening"/> with the
///     "open preferences" callback, and hold the guard for the app's lifetime.
///  3. Dispose the guard on exit (releases the mutex, stops the listener thread).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per-user names: two different Windows users can each run their own instance.
    static readonly string MutexName = @"Local\WormsCursor.SingleInstance." + Environment.UserName;
    static readonly string EventName = @"Local\WormsCursor.ShowPreferences." + Environment.UserName;

    readonly Mutex _mutex;
    readonly EventWaitHandle _showPreferences;
    Thread? _listener;
    volatile bool _stopping;

    SingleInstance(Mutex mutex, EventWaitHandle showPreferences)
    {
        _mutex = mutex;
        _showPreferences = showPreferences;
    }

    /// <summary>
    /// Tries to become the single running instance, WITHOUT starting any UI.
    /// <para>
    /// If no instance is running: acquires the mutex and returns a non-null guard. The
    /// caller owns it, should build its UI, then call <see cref="StartListening"/>, and
    /// must dispose the guard on exit.
    /// </para>
    /// <para>
    /// If an instance is already running: signals that instance to open Preferences and
    /// returns <c>null</c>. The caller should exit immediately (no UI was created).
    /// </para>
    /// </summary>
    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance owns the mutex. Wake it, then bow out.
            mutex.Dispose();
            SignalExistingInstance();
            return null;
        }

        var showPreferences = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, EventName);
        return new SingleInstance(mutex, showPreferences);
    }

    /// <summary>
    /// Starts the background listener. Each time a second launch signals us,
    /// <paramref name="onShowPreferences"/> is invoked on a background thread; it is
    /// responsible for hopping to the UI thread before touching WinForms.
    /// </summary>
    public void StartListening(Action onShowPreferences)
    {
        ArgumentNullException.ThrowIfNull(onShowPreferences);
        if (_listener is not null) return; // already listening

        _listener = new Thread(() =>
        {
            while (!_stopping)
            {
                _showPreferences.WaitOne();
                if (_stopping) break;
                try { onShowPreferences(); }
                catch { /* never let the listener thread die on a UI hiccup */ }
            }
        })
        {
            IsBackground = true,
            Name = "WormsCursor.SingleInstance.Listener",
        };
        _listener.Start();
    }

    /// <summary>Opens the existing instance's signal event and sets it. Best-effort.</summary>
    static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch
        {
            // If the first instance is mid-teardown the handle may be gone; nothing to do.
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _showPreferences.Set(); } catch { /* unblock the listener */ }
        try { _listener?.Join(TimeSpan.FromSeconds(1)); } catch { }
        _showPreferences.Dispose();

        try { _mutex.ReleaseMutex(); } catch { /* not owned / already released */ }
        _mutex.Dispose();
    }
}
