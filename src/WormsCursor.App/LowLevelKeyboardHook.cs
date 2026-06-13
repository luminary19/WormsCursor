using System.Runtime.InteropServices;

namespace WormsCursor.App;

/// <summary>
/// A process-wide low-level keyboard hook (<c>WH_KEYBOARD_LL</c>) that raises
/// <see cref="KeyDown"/> on each key press. Used only to give the themed I-beam a little
/// "hop" while you type — it reads THAT a key went down, never which key; nothing is read
/// from the payload, logged, or stored.
///
/// A low-level hook is dispatched on the thread that installed it, so this must be created
/// and disposed on a thread with a running message loop (the WinForms UI thread).
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>Raised on the UI thread when any key transitions to down (incl. auto-repeat).</summary>
    public event Action? KeyDown;

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    readonly HookProc _proc;   // hold the delegate so the GC can't collect it while the hook lives
    IntPtr _hook;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
        // Low-level hooks take the module handle of the current process and a 0 thread id.
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    /// <summary>True if the hook installed successfully.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                KeyDown?.Invoke();
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam); // never swallow input
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);
}
