using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WormsCursor.App;

/// <summary>
/// A detached sibling process — launched as <c>WormsCursor.exe --watchdog &lt;pid&gt;</c> —
/// that waits for the main process to disappear by ANY means and then restores the
/// default system cursors.
///
/// In-process handlers (ProcessExit / UnhandledException / ThreadException) cannot
/// cover an unstoppable <c>TerminateProcess</c> — Task Manager "End Task" or Visual
/// Studio "Stop Debugging" — because no code runs in the victim. An outside observer
/// can: it just blocks on the process handle until the kernel signals exit, then
/// reloads the cursor scheme. Visual Studio does not attach to this child by default,
/// so it survives "Stop Debugging" and cleans up.
/// </summary>
static class Watchdog
{
    const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr ptr, uint winIni);

    /// <summary>Watchdog entry point: wait for <paramref name="mainPid"/> to exit, then restore.</summary>
    public static int Run(int mainPid)
    {
        try
        {
            using var main = Process.GetProcessById(mainPid);
            main.WaitForExit(); // blocks until the main process is gone, however it died
        }
        catch (ArgumentException)
        {
            // Already gone — restore anyway.
        }

        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); // reload the user's cursor scheme
        return 0;
    }

    /// <summary>Launches the detached watchdog for the current process. Best-effort.</summary>
    public static Process? Launch()
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return null;
        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            return null; // the app still works (and self-restores on clean exit) without it
        }
    }
}
