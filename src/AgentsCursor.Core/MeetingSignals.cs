using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AgentsCursor.Core;

/// <summary>An app holding a device right now, as the consent store lists it: which signal, which app
/// (exe path or package family name), since when.</summary>
public readonly record struct HeldDevice(MeetingSignal Signal, string App, DateTime StartUtc);

/// <summary>
/// Reads the live Windows signals <see cref="MeetingDetection"/> decides from (Win32 only, read-only,
/// no UI framework):
/// <list type="bullet">
/// <item>presentation mode: <c>SHQueryUserNotificationState</c> = <c>QUNS_PRESENTATION_MODE</c>;</item>
/// <item>duplicated displays: <c>QueryDisplayConfig(QDC_DATABASE_CURRENT)</c> reports the clone topology;</item>
/// <item>the camera and screen capture in use: the capability consent store under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore</c>
/// (<c>webcam</c>, <c>graphicsCaptureProgrammatic</c>, <c>graphicsCaptureWithoutBorder</c>), one key per
/// packaged app and one per exe under <c>NonPackaged</c> (its path with '#' for '\'), whose
/// <c>LastUsedTimeStop</c> is 0 while the app holds the device. Each such app is then looked up among
/// the running processes (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>): by exe path, or by package family
/// name.</item>
/// </list>
/// Looking processes up is the expensive part (a full process snapshot), so a reader remembers each use
/// it found left behind (<see cref="MeetingDetection.IsLeftBehind"/>, which never changes) and only
/// looks up the apps of the others: with nothing but stale entries in the store, a check is a registry
/// read. One reader is used by one watcher at a time; it is not thread-safe.
/// </summary>
public sealed class MeetingSignals
{
    const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    static readonly (string Capability, MeetingSignal Signal)[] Capabilities =
    {
        ("webcam", MeetingSignal.Camera),
        ("graphicsCaptureProgrammatic", MeetingSignal.ScreenShared),
        ("graphicsCaptureWithoutBorder", MeetingSignal.ScreenShared),
    };

    readonly Func<IReadOnlyList<HeldDevice>> _held;
    readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, DateTime>> _runningSince;
    readonly HashSet<(string App, DateTime Start)> _leftBehind = new();

    /// <summary>Reads the live consent store and process list.</summary>
    public MeetingSignals() : this(HeldDevices, RunningSince) { }

    /// <summary>Reads what <paramref name="held"/> lists, looking apps up with
    /// <paramref name="runningSince"/> (for each app, when its oldest running copy started; apps with no
    /// running copy left out).</summary>
    public MeetingSignals(Func<IReadOnlyList<HeldDevice>> held,
        Func<IEnumerable<string>, IReadOnlyDictionary<string, DateTime>> runningSince)
    {
        _held = held;
        _runningSince = runningSince;
    }

    /// <summary>The meeting Windows' signals add up to right now, or null.</summary>
    public Meeting? Read()
        => MeetingDetection.Detect(PresentationMode(), DisplaysDuplicated(), DevicesInUse(), DateTime.UtcNow);

    /// <summary>Every device use the consent store lists as ongoing that isn't known to be left behind,
    /// with since when its app runs. A use found left behind now is remembered and skipped from then
    /// on (while the store still lists it).</summary>
    public IReadOnlyList<DeviceUse> DevicesInUse()
    {
        var held = _held();
        var listed = held.Select(h => (h.App, h.StartUtc)).ToHashSet();
        _leftBehind.IntersectWith(listed); // forget uses the store no longer lists
        var open = held.Where(h => !_leftBehind.Contains((h.App, h.StartUtc))).ToArray();
        if (open.Length == 0) return Array.Empty<DeviceUse>();

        var running = _runningSince(open.Select(h => h.App).Distinct(StringComparer.OrdinalIgnoreCase));
        var uses = new List<DeviceUse>();
        foreach (var h in open)
        {
            var use = new DeviceUse(h.Signal, h.App, h.StartUtc, running.TryGetValue(h.App, out var since) ? since : null);
            if (MeetingDetection.IsLeftBehind(use)) _leftBehind.Add((h.App, h.StartUtc));
            else uses.Add(use);
        }
        return uses;
    }

    /// <summary>True while Windows' presentation mode is on.</summary>
    public static bool PresentationMode()
        => SHQueryUserNotificationState(out int state) == 0 && state == QUNS_PRESENTATION_MODE;

    /// <summary>True while the displays are duplicated (Win+P "Duplicate").</summary>
    public static bool DisplaysDuplicated()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_DATABASE_CURRENT, out uint paths, out uint modes) != 0) return false;
            IntPtr pathBuffer = Marshal.AllocHGlobal((int)Math.Max(1, paths) * PathInfoSize);
            IntPtr modeBuffer = Marshal.AllocHGlobal((int)Math.Max(1, modes) * ModeInfoSize);
            try
            {
                int result = QueryDisplayConfig(QDC_DATABASE_CURRENT, ref paths, pathBuffer, ref modes, modeBuffer, out uint topology);
                if (result == ERROR_INSUFFICIENT_BUFFER) continue; // the displays changed in between
                return result == 0 && topology == DISPLAYCONFIG_TOPOLOGY_CLONE;
            }
            finally
            {
                Marshal.FreeHGlobal(pathBuffer);
                Marshal.FreeHGlobal(modeBuffer);
            }
        }
        return false;
    }

    /// <summary>Every device use the consent store lists as ongoing (a start time, no stop time).</summary>
    public static IReadOnlyList<HeldDevice> HeldDevices()
    {
        var held = new List<HeldDevice>();
        using var store = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (store is null) return held;
        foreach (var (capability, signal) in Capabilities)
        {
            using var key = store.OpenSubKey(capability);
            if (key is null) continue;
            foreach (string app in key.GetSubKeyNames())
            {
                if (app == "NonPackaged")
                {
                    using var exes = key.OpenSubKey(app);
                    if (exes is null) continue;
                    foreach (string exe in exes.GetSubKeyNames())
                        if (HeldSince(exes, exe) is { } since) held.Add(new HeldDevice(signal, exe.Replace('#', '\\'), since));
                }
                else if (HeldSince(key, app) is { } since) held.Add(new HeldDevice(signal, app, since));
            }
        }
        return held;
    }

    // When the app under `name` started holding the device, or null when it isn't holding it.
    static DateTime? HeldSince(RegistryKey parent, string name)
    {
        using var key = parent.OpenSubKey(name);
        if (key?.GetValue("LastUsedTimeStart") is not long start || start <= 0) return null;
        return key.GetValue("LastUsedTimeStop") is long stop && stop == 0 ? DateTime.FromFileTimeUtc(start) : null;
    }

    // For each app (exe path or package family name), when its oldest running copy started; apps with
    // no running copy are left out.
    public static IReadOnlyDictionary<string, DateTime> RunningSince(IEnumerable<string> apps)
    {
        var wanted = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase);
        var since = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var names = wanted.Where(MeetingDetection.IsExePath).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool packaged = wanted.Any(a => !MeetingDetection.IsExePath(a));
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // Only processes that can be the app: an exe by its name, or any process for a package.
                if (!packaged && !names.Contains(NameOf(process))) continue;
                IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)process.Id);
                if (handle == IntPtr.Zero) continue;
                try
                {
                    if (!GetProcessTimes(handle, out long created, out _, out _, out _)) continue;
                    var start = DateTime.FromFileTimeUtc(created);
                    foreach (string? app in new[] { ExePath(handle), PackageFamily(handle) })
                        if (app is not null && wanted.Contains(app) && (!since.TryGetValue(app, out var earlier) || start < earlier))
                            since[app] = start;
                }
                finally { CloseHandle(handle); }
            }
        }
        return since;
    }

    // The process's name, or "" once it has exited.
    static string NameOf(Process process)
    {
        try { return process.ProcessName; }
        catch (InvalidOperationException) { return ""; }
    }

    static string? ExePath(IntPtr process)
    {
        var path = new StringBuilder(1024);
        int length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString(0, length) : null;
    }

    static string? PackageFamily(IntPtr process)
    {
        uint length = 0;
        if (GetPackageFamilyName(process, ref length, null) != ERROR_INSUFFICIENT_BUFFER) return null; // not packaged
        var name = new StringBuilder((int)length);
        return GetPackageFamilyName(process, ref length, name) == 0 ? name.ToString() : null;
    }

    const int QUNS_PRESENTATION_MODE = 4;
    const uint QDC_DATABASE_CURRENT = 0x4;
    const uint DISPLAYCONFIG_TOPOLOGY_CLONE = 0x2;
    const int PathInfoSize = 72, ModeInfoSize = 64; // DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_MODE_INFO
    const int ERROR_INSUFFICIENT_BUFFER = 122;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topology);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
}
