using System.Runtime.InteropServices;
using System.Text;

namespace AgentsCursor.Core;

/// <summary>A program that owns a visible top-level window: its exe file name, that window's title, and
/// the exe's full path.</summary>
public sealed record WindowProgram(string Program, string Title, string Path);

/// <summary>
/// Maps windows to the programs that own them (Win32 only, no UI framework). The process is opened
/// with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which Windows grants even for elevated processes and
/// most anti-cheat-protected games, so their exe name still resolves from an unelevated tray app.
/// </summary>
public static class WindowPrograms
{
    /// <summary>Exe file name (e.g. "dota2.exe") of the process that owns <paramref name="hwnd"/>, or
    /// null if the window is gone or the process can't be queried.</summary>
    public static string? ProgramForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : ProgramForProcess(pid);
    }

    /// <summary>Exe file name of process <paramref name="pid"/>, or null if it can't be queried.</summary>
    public static string? ProgramForProcess(uint pid)
        => ExePath(pid) is string path ? Path.GetFileName(path) : null;

    // Full exe path of process <pid>, or null if it can't be queried.
    static string? ExePath(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString(0, length) : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// Programs that currently show a window a person would recognise: visible, unowned, titled
    /// top-level windows that aren't tool windows or cloaked by DWM (a suspended Store app's frame, a
    /// window on another virtual desktop), excluding this process and the Windows shell (the process
    /// behind the desktop and taskbar, which also hosts File Explorer windows: hiding the token while it
    /// is in front would hide it on the desktop). One entry per program (the first window found
    /// supplies the title), sorted by program name.
    /// </summary>
    public static IReadOnlyList<WindowProgram> ListVisible()
    {
        uint self = (uint)Environment.ProcessId;
        GetWindowThreadProcessId(GetShellWindow(), out uint shell);
        var byProgram = new Dictionary<string, WindowProgram>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsCandidate(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == self || pid == shell) return true;
            if (ExePath(pid) is string path && Path.GetFileName(path) is var program && !byProgram.ContainsKey(program))
                byProgram[program] = new WindowProgram(program, WindowTitle(hwnd), path);
            return true;
        }, IntPtr.Zero);
        return byProgram.Values
            .OrderBy(p => p.Program, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static bool IsCandidate(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return GetWindowTextLength(hwnd) > 0;
    }

    static string WindowTitle(IntPtr hwnd)
    {
        var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
        GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index); // exists on x86 and x64
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
