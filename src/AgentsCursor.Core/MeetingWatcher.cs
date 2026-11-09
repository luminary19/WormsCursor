namespace AgentsCursor.Core;

/// <summary>
/// Keeps <see cref="Current"/> (are you in a meeting, and why) up to date while someone asks it to.
/// Windows raises no event for most of the signals (presentation mode, duplicated displays, a device
/// held long enough to count), so while <see cref="Watch"/> is on it checks once at once and then every
/// <see cref="Interval"/>; while it is off nothing runs at all. The overlay turns it on only while a
/// token would otherwise be on screen, so a quiet tray never checks.
/// </summary>
public sealed class MeetingWatcher : IDisposable
{
    /// <summary>How often the signals are checked while watching.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    readonly Func<Meeting?> _probe;
    readonly TimeSpan _interval;
    readonly Action<string> _log;
    readonly Timer _timer;
    readonly object _gate = new();
    volatile Meeting? _current;
    bool _watching;
    bool _failureLogged;
    bool _disposed;

    /// <summary>Watches the live Windows signals (a <see cref="MeetingSignals"/> reader of its own).</summary>
    public MeetingWatcher(Action<string> log) : this(new MeetingSignals().Read, Interval, log) { }

    /// <summary>Watches what <paramref name="probe"/> reports, every <paramref name="interval"/>. A probe
    /// that throws counts as "no meeting" (so a broken signal can never hide the token for good); the
    /// first failure is logged.</summary>
    public MeetingWatcher(Func<Meeting?> probe, TimeSpan interval, Action<string> log)
    {
        _probe = probe;
        _interval = interval;
        _log = log;
        _timer = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The meeting found by the last check, or null (none, or not watching). Safe to read
    /// from any thread.</summary>
    public Meeting? Current => _current;

    /// <summary>Raised on a timer thread when a check finds <see cref="Current"/> changed. Not raised for
    /// the check <see cref="Watch"/> makes itself: its caller reads <see cref="Current"/> right after.
    /// Handlers must be thread-safe.</summary>
    public event Action? Changed;

    /// <summary>Starts (checking right away, on this thread) or stops watching. Stopping clears
    /// <see cref="Current"/>. Thread-safe; repeating the current state does nothing.</summary>
    public void Watch(bool on)
    {
        lock (_gate)
        {
            if (_disposed || on == _watching) return;
            _watching = on;
            if (on)
            {
                _current = Probe();
                _timer.Change(_interval, _interval);
            }
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _current = null;
            }
        }
    }

    void Check()
    {
        bool changed;
        lock (_gate)
        {
            if (!_watching) return;
            var found = Probe();
            changed = found != _current;
            _current = found;
        }
        if (changed) Changed?.Invoke(); // outside the lock: handlers may call Watch
    }

    Meeting? Probe()
    {
        try { return _probe(); }
        catch (Exception ex)
        {
            // A timer callback must never throw (it would end the process), and an unreadable signal
            // must not hide the token: report no meeting, and log the first failure.
            if (!_failureLogged)
            {
                _failureLogged = true;
                _log("meeting check failed (counting it as no meeting): " + ex);
            }
            return null;
        }
    }

    /// <summary>Stops watching. Returns once no check is running and none can start, so no
    /// <see cref="Changed"/> handler runs afterwards.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watching = false;
        }
        using var done = new ManualResetEvent(false);
        if (_timer.Dispose(done)) done.WaitOne();
    }
}
