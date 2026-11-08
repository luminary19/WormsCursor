using System.Runtime.InteropServices;

namespace AgentsCursor.Core;

/// <summary>
/// Tracks which program owns the foreground window, event-driven (no polling). A dedicated thread
/// installs an out-of-context <c>EVENT_SYSTEM_FOREGROUND</c> WinEvent hook and pumps messages, so
/// Windows calls back on that thread whenever another window comes to the front, in any window mode
/// (exclusive fullscreen, borderless, windowed). Each change resolves the window to its exe name via
/// <see cref="WindowPrograms"/>. The watcher only reports <i>what</i> is in front; deciding whether that
/// should hide the token is the consumer's job (<see cref="ProgramSuppression.Matches"/>), so a settings
/// edit can be re-evaluated against <see cref="ForegroundProgram"/> without touching the watcher.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new(false);
    readonly WinEventProc _callback; // held in a field so the GC can't collect it while Windows calls it
    uint _threadId;
    uint _lastPid;            // the watcher thread's one-entry cache: same pid => same program
    volatile string? _program;
    bool _disposed;

    /// <summary>Raised on the watcher's own thread when the foreground program changes. Handlers must
    /// be thread-safe and quick (they run inside the hook callback), and must not dispose this
    /// watcher (<see cref="Dispose"/> waits for this thread).</summary>
    public event Action? Changed;

    /// <summary>Exe file name of the program that owns the foreground window (e.g. "dota2.exe"), or
    /// null when nothing is in front or it can't be resolved. Safe to read from any thread.</summary>
    public string? ForegroundProgram => _program;

    /// <summary>Starts watching. <see cref="ForegroundProgram"/> holds the current foreground program
    /// by the time the constructor returns.</summary>
    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "AgentsCursorForeground" };
        _thread.Start();
        _ready.Wait();
    }

    void Run()
    {
        // Peeking creates this thread's message queue, so Dispose's WM_QUIT can always be posted to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
        _threadId = GetCurrentThreadId();
        IntPtr hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Without the hook no change would ever arrive, so a program read now would stick forever (a
        // game in front at startup would hide the token for good). Report "unknown" instead: nothing
        // matches, so suppression is simply off.
        if (hook != IntPtr.Zero) Update(GetForegroundWindow(), notify: false);
        _ready.Set();
        try
        {
            // Out-of-context WinEvents are delivered while this thread waits in GetMessage.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        }
    }

    void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => Update(hwnd, notify: true);

    void Update(IntPtr hwnd, bool notify)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && pid == _lastPid) return; // the same process is still in front
        _lastPid = pid;
        string? program = pid == 0 ? null : WindowPrograms.ProgramForProcess(pid);
        if (string.Equals(program, _program, StringComparison.OrdinalIgnoreCase)) return;
        _program = program;
        if (notify) Changed?.Invoke();
    }

    /// <summary>Stops watching. Returns only once the watcher thread has unhooked and exited, so no
    /// <see cref="Changed"/> handler is running or can run afterwards: a consumer may dispose whatever
    /// its handler touches right after this returns. The wait is short: WM_QUIT ends the message loop
    /// as soon as any in-flight callback (a process-name lookup plus the handlers) returns.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join();
        _ready.Dispose();
    }

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint PM_NOREMOVE = 0x0000;
    const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
