using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using WormsCursor.Core;

namespace OpenCodeCursorOverlay;

internal static class Program
{
    private const string DefaultPipeName = "OpenCodeCursorOverlay-v1";

    [STAThread]
    private static void Main(string[] args)
    {
        var options = HelperOptions.Parse(args);
        using var mutex = new Mutex(true, $"Local\\OpenCodeCursorOverlay-{HashName(options.PipeName)}", out var ownsMutex);
        if (!ownsMutex) return;

        ApplicationConfiguration.Initialize();
        using var overlay = new CursorOverlay();
        using var server = new OverlayServer(options.PipeName, overlay);
        overlay.InitializeHidden();
        using var suppression = new FocusSuppression(overlay);
        server.Start();
        Application.Run();
    }

    private readonly record struct HelperOptions(string PipeName)
    {
        public static HelperOptions Parse(string[] args)
        {
            if (args.Length == 2 && args[0] == "--pipe" && args[1].Length is > 0 and <= 128 && args[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) return new HelperOptions(args[1]);
            return new HelperOptions(DefaultPipeName);
        }
    }

    private static string HashName(string value)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}:{value}"));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }
}

internal sealed class OverlayServer : IDisposable
{
    private const int StartupTimeoutMs = 5_000;
    private const int LastClientGraceMs = 750;
    private readonly string pipeName;
    private readonly CursorOverlay overlay;
    private readonly CancellationTokenSource shutdown = new();
    private readonly object gate = new();
    private readonly Dictionary<int, ClientState> clients = [];
    // Retain every connected stream explicitly; each client has independent lifetime.
    private readonly Dictionary<int, NamedPipeServerStream> connections = [];
    private System.Threading.Timer? exitTimer;
    private int nextClientId;

    public OverlayServer(string pipeName, CursorOverlay overlay)
    {
        this.pipeName = pipeName;
        this.overlay = overlay;
    }

    public void Start()
    {
        lock (gate) ScheduleExitLocked(StartupTimeoutMs);
        _ = AcceptClientsAsync();
    }

    private async Task AcceptClientsAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token);
                _ = ServeClientAsync(pipe, Interlocked.Increment(ref nextClientId));
                pipe = null; // ownership moved to the connected-client table
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch
            {
                pipe?.Dispose();
                // A transient pipe-instance allocation failure must not evict existing clients.
                try { await Task.Delay(50, shutdown.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, int clientId)
    {
        AddClient(clientId, pipe);
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: false);
            while (!shutdown.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(shutdown.Token);
                if (line is null) break;
                TryApplyState(clientId, line);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* A client pipe failure is equivalent to disconnect. */ }
        finally { RemoveClient(clientId); }
    }

    private void AddClient(int clientId, NamedPipeServerStream pipe)
    {
        lock (gate)
        {
            connections[clientId] = pipe;
            clients[clientId] = new ClientState("hidden", 64);
            CancelExitLocked();
            ApplyAggregateLocked();
        }
    }

    private void RemoveClient(int clientId)
    {
        lock (gate)
        {
            clients.Remove(clientId);
            if (connections.Remove(clientId, out var pipe)) pipe.Dispose();
            ApplyAggregateLocked();
            if (clients.Count == 0) ScheduleExitLocked(LastClientGraceMs);
        }
    }

    private void TryApplyState(int clientId, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("command", out var command) || command.GetString() != "state") return;
            if (!root.TryGetProperty("display", out var displayValue) || displayValue.GetString() is not { } display) return;
            if (display is not ("hidden" or "waiting" or "completion")) return;
            var size = root.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt32(out var parsedSize) ? Math.Clamp(parsedSize, 24, 256) : 64;
            lock (gate)
            {
                if (!clients.ContainsKey(clientId)) return;
                clients[clientId] = new ClientState(display, size);
                ApplyAggregateLocked();
            }
        }
        catch (JsonException) { /* Invalid client input is ignored. */ }
    }

    private void ApplyAggregateLocked()
    {
        var visible = clients.Values.Where(state => state.Display != "hidden").ToArray();
        var display = clients.Values.Any(state => state.Display == "waiting") ? "waiting"
            : clients.Values.Any(state => state.Display == "completion") ? "completion"
            : "hidden";
        var size = visible.Length == 0 ? 64 : visible.Max(state => state.Size);
        // "waiting" means a session is blocked on the user (a permission or a form), which wears the
        // "?" badge; "completion" is a finished turn and shows the plain mark.
        var blocked = display == "waiting";
        overlay.Post(() =>
        {
            if (display == "hidden") overlay.HideToken();
            else overlay.ShowToken(size, blocked);
        });
    }

    private void ScheduleExitLocked(int delayMs)
    {
        CancelExitLocked();
        exitTimer = new System.Threading.Timer(_ =>
        {
            lock (gate)
            {
                if (clients.Count != 0) return;
                RequestShutdown();
            }
        }, null, delayMs, Timeout.Infinite);
    }

    private void CancelExitLocked()
    {
        exitTimer?.Dispose();
        exitTimer = null;
    }

    private void RequestShutdown()
    {
        if (shutdown.IsCancellationRequested) return;
        shutdown.Cancel();
        overlay.Post(overlay.Shutdown);
    }

    public void Dispose()
    {
        shutdown.Cancel();
        lock (gate)
        {
            CancelExitLocked();
            foreach (var pipe in connections.Values) pipe.Dispose();
            connections.Clear();
        }
        shutdown.Dispose();
    }

    private readonly record struct ClientState(string Display, int Size);
}

internal sealed class CursorOverlay : Form
{
    private const float TailAnchorX = 0.20f;
    private const float TailAnchorY = 0.60f;
    private const float DropRatio = 0.52f;
    private const float InitialKickRatio = 4.5f;
    private const float Spring = 130f;
    private const float Damping = 3f;
    private const float MaximumTetherRatio = 0.82f;
    private const int RefreshCheckIntervalMilliseconds = 2_000;
    private const int MaximumSimulationSteps = 4;

    private readonly FrameMetrics metrics = new();
    private readonly FrameClock frameClock;
    private LogoAsset? logoAsset;
    private LayeredSurface? surface;
    private int size = 64;
    private bool blocked;    // a client is blocked on the user: draw the "?" badge
    private bool requested;  // some client wants the token shown
    private bool suppressed; // a program on the WormsCursor suppression list is in front
    private bool active;     // the token is actually animating (requested and not suppressed)
    private bool seeded;
    private float bobX, bobY, velocityX, velocityY;
    private float previousBobX, previousBobY;
    private float simulationClock, accumulator;
    private long previousTicks, nextRefreshCheckTicks;
    private string? displayDeviceName;

    public CursorOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        frameClock = new FrameClock(QueueScheduledFrame, HandleFrameClockFailure, metrics);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow | WsExTopmost;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void InitializeHidden()
    {
        _ = Handle;
        ShowWindow(Handle, SwShowNoActivate);
        HideToken();
    }

    public void Post(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
        }
        catch (InvalidOperationException) { }
    }

    public void ShowToken(int requestedSize, bool blockedOnUser)
    {
        size = Math.Clamp(requestedSize, 24, 256);
        blocked = blockedOnUser;
        requested = true;
        UpdateVisibility();
    }

    public void HideToken()
    {
        requested = false;
        UpdateVisibility();
    }

    /// <summary>Hide the token while a listed program is in front, without forgetting that clients
    /// still want it: when suppression ends it comes back with its entrance kick.</summary>
    public void SetSuppressed(bool value)
    {
        suppressed = value;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (requested && !suppressed) StartAnimating();
        else Park();
    }

    private void StartAnimating()
    {
        if (active) return;

        active = true;
        seeded = false;
        accumulator = 0;
        simulationClock = 0;
        previousTicks = Stopwatch.GetTimestamp();
        nextRefreshCheckTicks = 0;
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        frameClock.Start(ResolveCurrentDisplayRefreshRate());
    }

    // Stops the frame clock and moves the window off-screen.
    private void Park()
    {
        active = false;
        frameClock.Stop();
        SetWindowPos(Handle, HwndTopmost, -32000, -32000, 0, 0, SwpNoSize | SwpNoActivate);
    }

    public void Shutdown()
    {
        HideToken();
        Close();
        Application.ExitThread();
    }

    private void QueueScheduledFrame(int generation)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
            {
                frameClock.CompleteFrame(generation);
                return;
            }

            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (active && frameClock.IsCurrent(generation)) Tick();
                }
                finally
                {
                    frameClock.CompleteFrame(generation);
                }
            }));
        }
        catch (InvalidOperationException)
        {
            frameClock.CompleteFrame(generation);
        }
    }

    private void HandleFrameClockFailure(Exception _)
    {
        Post(() =>
        {
            if (active) Park();
        });
    }
    private void Tick()
    {
        var renderStarted = metrics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Clamp((float)(now - previousTicks) / Stopwatch.Frequency, 0f, 0.25f);
            previousTicks = now;
            if (!GetCursorPos(out var cursor)) return;

            RefreshDisplayRate(cursor, now);
            var cursorSize = SystemInformation.CursorSize;
            var tailX = cursor.X + cursorSize.Width * TailAnchorX;
            var tailY = cursor.Y + cursorSize.Height * TailAnchorY;
            var timestep = 1f / frameClock.RefreshRate;
            accumulator = Math.Min(accumulator + elapsed, timestep * MaximumSimulationSteps);

            if (!seeded)
            {
                var drop = size * DropRatio;
                bobX = previousBobX = tailX;
                bobY = previousBobY = tailY + drop;
                velocityX = 0;
                velocityY = -size * InitialKickRatio;
                seeded = true;
            }

            while (accumulator >= timestep)
            {
                previousBobX = bobX;
                previousBobY = bobY;
                Integrate(tailX, tailY, timestep);
                simulationClock += timestep;
                accumulator -= timestep;
            }

            var interpolation = timestep <= 0 ? 0 : accumulator / timestep;
            var renderX = previousBobX + (bobX - previousBobX) * interpolation;
            var renderY = previousBobY + (bobY - previousBobY) * interpolation;
            Render(tailX, tailY, renderX, renderY);
        }
        finally
        {
            if (metrics.IsEnabled) metrics.RecordRender(Stopwatch.GetTimestamp() - renderStarted);
        }
    }

    private void RefreshDisplayRate(PointNative cursor, long now)
    {
        if (now < nextRefreshCheckTicks) return;
        nextRefreshCheckTicks = now + Stopwatch.Frequency * RefreshCheckIntervalMilliseconds / 1_000;
        ApplyDisplayRefreshRate(Screen.FromPoint(new Point(cursor.X, cursor.Y)));
    }

    private int ResolveCurrentDisplayRefreshRate()
    {
        if (!GetCursorPos(out var cursor)) return RefreshFallbackHz;
        var screen = Screen.FromPoint(new Point(cursor.X, cursor.Y));
        ApplyDisplayRefreshRate(screen);
        return GetDisplayRefreshRate(screen.DeviceName);
    }

    private void ApplyDisplayRefreshRate(Screen screen)
    {
        var refreshRate = GetDisplayRefreshRate(screen.DeviceName);
        if (!string.Equals(displayDeviceName, screen.DeviceName, StringComparison.Ordinal) || refreshRate != frameClock.RefreshRate)
        {
            displayDeviceName = screen.DeviceName;
            frameClock.UpdateRefreshRate(refreshRate);
        }
    }

    private void Integrate(float tailX, float tailY, float delta)
    {
        var tokenPixels = size;
        var drop = tokenPixels * DropRatio;
        velocityX += ((tailX - bobX) * Spring - velocityX * Damping) * delta;
        velocityY += ((tailY - bobY) * Spring + drop * Spring - velocityY * Damping) * delta;
        bobX += velocityX * delta;
        bobY += velocityY * delta;

        var dx = bobX - tailX;
        var dy = bobY - tailY;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        var maximumLength = tokenPixels * MaximumTetherRatio;
        if (length <= maximumLength) return;

        var nx = dx / length;
        var ny = dy / length;
        bobX = tailX + nx * maximumLength;
        bobY = tailY + ny * maximumLength;
        var outwardVelocity = velocityX * nx + velocityY * ny;
        if (outwardVelocity > 0)
        {
            velocityX -= outwardVelocity * nx;
            velocityY -= outwardVelocity * ny;
        }
    }

    private void Render(float tailX, float tailY, float renderX, float renderY)
    {
        var tokenPixels = size;
        var asset = GetLogoAsset(tokenPixels, blocked);
        var canvas = Math.Max(16, (int)MathF.Ceiling(tokenPixels * 2.8f));
        var canvasLeft = (int)MathF.Round(tailX - canvas / 2f);
        var canvasTop = (int)MathF.Round(tailY - canvas / 2f);
        var target = EnsureSurface(canvas);
        var idleBob = -tokenPixels * 0.05f * MathF.Abs(MathF.Sin(simulationClock * 3.2f));
        var swing = MathF.Atan2(renderY - tailY, renderX - tailX) * 180f / MathF.PI - 90f;

        var graphics = target.Graphics;
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TranslateTransform(renderX - canvasLeft, renderY - canvasTop + idleBob);
        graphics.RotateTransform(swing);
        graphics.DrawImage(
            asset.Bitmap,
            new RectangleF(-asset.Width / 2f, -asset.Height / 2f, asset.Width, asset.Height));
        graphics.ResetTransform();

        var uploadStarted = metrics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        target.Upload(Handle, canvasLeft, canvasTop);
        if (metrics.IsEnabled) metrics.RecordUpload(Stopwatch.GetTimestamp() - uploadStarted);
    }

    private LayeredSurface EnsureSurface(int requestedCanvas)
    {
        if (surface is not null && surface.Width == requestedCanvas && surface.Height == requestedCanvas) return surface;
        surface?.Dispose();
        surface = new LayeredSurface(requestedCanvas, requestedCanvas);
        return surface;
    }

    private LogoAsset GetLogoAsset(int tokenPixels, bool badged)
    {
        if (logoAsset is not null && logoAsset.TokenPixels == tokenPixels && logoAsset.Badged == badged) return logoAsset;
        logoAsset?.Dispose();
        logoAsset = LogoAsset.Create(tokenPixels, badged);
        return logoAsset;
    }

    private static int GetDisplayRefreshRate(string deviceName)
    {
        var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency is >= 20 and <= 1_000
            ? (int)mode.dmDisplayFrequency
            : RefreshFallbackHz;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameClock.Dispose();
            surface?.Dispose();
            logoAsset?.Dispose();
            metrics.Dispose();
        }
        base.Dispose(disposing);
    }

    private const int RefreshFallbackHz = 60;
    private const int WsExLayered = 0x80000, WsExTransparent = 0x20, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTopmost = 0x8;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
    private const int EnumCurrentSettings = -1;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode deviceMode);
}

internal sealed class LogoAsset : IDisposable
{
    private const int RenderScale = 4;

    // The badge's soft shadow reaches this far past its radius (see AttentionBadge.Draw).
    private const float BadgeShadowReach = 1.3f;

    private LogoAsset(Bitmap bitmap, int tokenPixels, bool badged, float width, float height)
    {
        Bitmap = bitmap;
        TokenPixels = tokenPixels;
        Badged = badged;
        Width = width;
        Height = height;
    }

    public Bitmap Bitmap { get; }
    public int TokenPixels { get; }
    public bool Badged { get; }
    public float Width { get; }
    public float Height { get; }

    /// <summary>The mark at <paramref name="tokenPixels"/> tall, with the shared "?" badge on its
    /// top-right when <paramref name="badged"/>. The bitmap stays centred on the mark (the overlay
    /// rotates it about its centre), so a badge grows it symmetrically.</summary>
    public static LogoAsset Create(int tokenPixels, bool badged)
    {
        var logoHeight = (float)tokenPixels;
        var logoWidth = logoHeight * 0.8f;
        var padding = Math.Max(3f, tokenPixels * 0.025f);
        var halfWidth = logoWidth / 2f + padding;
        var halfHeight = logoHeight / 2f + padding;
        var (badgeCenter, badgeRadius) = AttentionBadge.Place(
            new RectangleF(-logoWidth / 2f, -logoHeight / 2f, logoWidth, logoHeight), tokenPixels);
        if (badged)
        {
            halfWidth = Math.Max(halfWidth, MathF.Abs(badgeCenter.X) + badgeRadius * BadgeShadowReach);
            halfHeight = Math.Max(halfHeight, MathF.Abs(badgeCenter.Y) + badgeRadius * BadgeShadowReach);
        }
        var width = halfWidth * 2f;
        var height = halfHeight * 2f;
        using var supersampled = new Bitmap(
            (int)MathF.Ceiling(width * RenderScale),
            (int)MathF.Ceiling(height * RenderScale),
            PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(supersampled))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawLogo(graphics, supersampled.Width / 2f, supersampled.Height / 2f, tokenPixels * RenderScale);
            if (badged)
                AttentionBadge.Draw(graphics,
                    supersampled.Width / 2f + badgeCenter.X * RenderScale,
                    supersampled.Height / 2f + badgeCenter.Y * RenderScale,
                    badgeRadius * RenderScale);
        }

        var bitmap = new Bitmap(
            (int)MathF.Ceiling(width),
            (int)MathF.Ceiling(height),
            PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                supersampled,
                new RectangleF(0, 0, width, height),
                new RectangleF(0, 0, supersampled.Width, supersampled.Height),
                GraphicsUnit.Pixel);
            return new LogoAsset(bitmap, tokenPixels, badged, width, height);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public void Dispose() => Bitmap.Dispose();

    private static void DrawLogo(Graphics graphics, float centerX, float centerY, float tokenSize)
    {
        // Exact OpenCode mark geometry (16 x 20) from packages/ui/src/components/logo.tsx.
        // The upstream dark-brand colors and full SVG are retained in Assets/.
        // Shadow: M12 16H4V8H12V16Z. Ring: M12 4H4V16H12V4ZM16 20H0V0H16V20Z.
        var unit = tokenSize / 20f;
        graphics.TranslateTransform(centerX - unit * 8f, centerY - unit * 10f);
        using var ring = new GraphicsPath(FillMode.Alternate);
        ring.AddRectangle(new RectangleF(0, 0, unit * 16f, unit * 20f));
        ring.AddRectangle(new RectangleF(unit * 4f, unit * 4f, unit * 8f, unit * 12f));
        try
        {
            using (var shadow = new SolidBrush(Color.FromArgb(155, Color.Black)))
            {
                graphics.TranslateTransform(tokenSize * 0.008f, tokenSize * 0.011f);
                graphics.FillPath(shadow, ring);
                graphics.TranslateTransform(-tokenSize * 0.008f, -tokenSize * 0.011f);
            }
            using var rim = new Pen(Color.FromArgb(210, Color.Black), Math.Max(1.4f, tokenSize * 0.01f));
            graphics.DrawPath(rim, ring);
            using var inset = new SolidBrush(Color.FromArgb(0x4B, 0x46, 0x46));
            using var mark = new SolidBrush(Color.FromArgb(0xF1, 0xEC, 0xEC));
            graphics.FillRectangle(inset, unit * 4f, unit * 8f, unit * 8f, unit * 8f);
            graphics.FillPath(mark, ring);
        }
        finally
        {
            graphics.ResetTransform();
        }
    }
}
internal sealed class FrameClock : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly object gate = new();
    private readonly Action<int> queueFrame;
    private readonly Action<Exception> reportFailure;
    private readonly FrameMetrics metrics;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private int generation;
    private int pendingGeneration;
    private int refreshRate = 60;
    private bool running, disposed;

    public FrameClock(Action<int> queueFrame, Action<Exception> reportFailure, FrameMetrics metrics)
    {
        this.queueFrame = queueFrame;
        this.reportFailure = reportFailure;
        this.metrics = metrics;
    }

    public int RefreshRate => Volatile.Read(ref refreshRate);

    public void Start(int initialRefreshRate)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (running) return;
            Volatile.Write(ref refreshRate, NormalizeRefreshRate(initialRefreshRate));
            Interlocked.Exchange(ref pendingGeneration, 0);
            running = true;
            var tokenSource = new CancellationTokenSource();
            cancellation = tokenSource;
            var currentGeneration = ++generation;
            worker = Task.Factory.StartNew(
                () => Run(currentGeneration, tokenSource),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }

    public void UpdateRefreshRate(int rate) => Volatile.Write(ref refreshRate, NormalizeRefreshRate(rate));

    public bool IsCurrent(int callbackGeneration) =>
        Volatile.Read(ref generation) == callbackGeneration && Volatile.Read(ref running);

    public void CompleteFrame(int callbackGeneration) =>
        Interlocked.CompareExchange(ref pendingGeneration, 0, callbackGeneration);

    public void Stop()
    {
        CancellationTokenSource? tokenSource;
        Task? activeWorker;
        int stoppingGeneration;
        lock (gate)
        {
            if (!running) return;
            running = false;
            stoppingGeneration = generation;
            generation++;
            tokenSource = cancellation;
            activeWorker = worker;
            cancellation = null;
            worker = null;
        }

        Interlocked.CompareExchange(ref pendingGeneration, 0, stoppingGeneration);
        tokenSource?.Cancel();
        if (tokenSource is null) return;
        if (activeWorker is null)
        {
            tokenSource.Dispose();
            return;
        }

        if (Task.CurrentId == activeWorker.Id)
        {
            DisposeWhenFinished(activeWorker, tokenSource);
            return;
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Ceiling(1_000d / RefreshRate) + 20);
        var completed = false;
        try
        {
            completed = activeWorker.Wait(timeout);
        }
        catch (AggregateException)
        {
            completed = true;
        }

        if (completed)
        {
            Observe(activeWorker);
            activeWorker.Dispose();
            tokenSource.Dispose();
        }
        else
        {
            DisposeWhenFinished(activeWorker, tokenSource);
        }
    }

    private void Run(int currentGeneration, CancellationTokenSource tokenSource)
    {
        try
        {
            try
            {
                using var timer = WaitableTimer.Create();
                RunLoop(currentGeneration, tokenSource.Token, timer.WaitUntil);
                return;
            }
            catch (Exception) when (!tokenSource.IsCancellationRequested)
            {
                metrics.RecordClockFallback();
            }

            try
            {
                RunLoop(currentGeneration, tokenSource.Token, FallbackWaitUntil);
            }
            catch (Exception error) when (!tokenSource.IsCancellationRequested)
            {
                ReportFailure(currentGeneration, error);
            }
        }
        catch (Exception error) when (!tokenSource.IsCancellationRequested)
        {
            ReportFailure(currentGeneration, error);
        }
    }

    private void RunLoop(int currentGeneration, CancellationToken cancellationToken, Action<double, CancellationToken> waitUntil)
    {
        var nextDeadline = Stopwatch.GetTimestamp() + (double)Stopwatch.Frequency / RefreshRate;
        long previousWake = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrent(currentGeneration))
        {
            var period = (double)Stopwatch.Frequency / RefreshRate;
            waitUntil(nextDeadline, cancellationToken);
            var woke = Stopwatch.GetTimestamp();
            if (cancellationToken.IsCancellationRequested || !IsCurrent(currentGeneration)) break;

            var skipped = Math.Max(0L, (long)Math.Floor((woke - nextDeadline) / period));
            if (skipped > 0) metrics.RecordSkippedDeadlines(skipped);
            if (metrics.IsEnabled) metrics.RecordPacing(previousWake == 0 ? 0 : woke - previousWake, woke - (long)nextDeadline);
            previousWake = woke;
            nextDeadline += (skipped + 1) * period;

            if (Interlocked.CompareExchange(ref pendingGeneration, currentGeneration, 0) != 0)
            {
                metrics.RecordCoalescedFrame();
                continue;
            }

            if (!IsCurrent(currentGeneration))
            {
                CompleteFrame(currentGeneration);
                break;
            }

            try { queueFrame(currentGeneration); }
            catch
            {
                CompleteFrame(currentGeneration);
                throw;
            }
        }
    }

    private void ReportFailure(int currentGeneration, Exception error)
    {
        if (IsCurrent(currentGeneration)) reportFailure(error);
    }

    private static void DisposeWhenFinished(Task task, CancellationTokenSource tokenSource)
    {
        _ = task.ContinueWith(
            completed =>
            {
                Observe(completed);
                completed.Dispose();
                tokenSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted) _ = task.Exception;
    }

    private static void FallbackWaitUntil(double deadline, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return;
            var remainingMilliseconds = remainingTicks * 1_000d / Stopwatch.Frequency;
            if (remainingMilliseconds > 1.5)
            {
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remainingMilliseconds - 0.5))) return;
            }
            else
            {
                Thread.SpinWait(64);
                Thread.Yield();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop();
    }

    private static int NormalizeRefreshRate(int rate) => rate is >= 20 and <= 1_000 ? rate : 60;
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(FrameClock)); }

    private sealed class WaitableTimer : IDisposable
    {
        private IntPtr handle;
        private WaitableTimer(IntPtr handle) => this.handle = handle;

        public static WaitableTimer Create()
        {
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (handle != IntPtr.Zero) return new WaitableTimer(handle);
            var highResolutionError = Marshal.GetLastWin32Error();
            handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TimerAllAccess);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Unable to create waitable timer (high-resolution attempt failed with {highResolutionError}).");
            return new WaitableTimer(handle);
        }

        public void WaitUntil(double deadline, CancellationToken cancellationToken)
        {
            var remainingTicks = Math.Max(1, deadline - Stopwatch.GetTimestamp());
            var relativeDueTime = -(long)Math.Ceiling(remainingTicks * 10_000_000d / Stopwatch.Frequency);
            if (!SetWaitableTimerEx(handle, ref relativeDueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to schedule waitable timer.");
            var wait = WaitForSingleObject(handle, uint.MaxValue);
            if (wait == WaitFailed) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Waitable timer wait failed.");
            if (wait != WaitObject0 && !cancellationToken.IsCancellationRequested)
                throw new System.ComponentModel.Win32Exception($"Unexpected waitable timer result: {wait}.");
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint desiredAccess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}

internal sealed class LayeredSurface : IDisposable
{
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int UlwAlpha = 0x02;
    private const byte AcSrcOver = 0, AcSrcAlpha = 1;
    private IntPtr memoryDc, bitmapHandle, previousObject;
    private Bitmap? bitmap;
    private Graphics? graphics;

    public LayeredSurface(int width, int height)
    {
        Width = width;
        Height = height;
        try
        {
            memoryDc = CreateCompatibleDC(IntPtr.Zero);
            if (memoryDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to create layered-surface memory DC.");

            var info = new BitmapInfo { bmiHeader = new BitmapInfoHeader { biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32, biCompression = BiRgb } };
            bitmapHandle = CreateDIBSection(memoryDc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (bitmapHandle == IntPtr.Zero || bits == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to create layered-surface DIB section.");

            var selectedObject = SelectObject(memoryDc, bitmapHandle);
            if (selectedObject == IntPtr.Zero || selectedObject == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to select layered-surface DIB section.");
            previousObject = selectedObject;
            bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            graphics = Graphics.FromImage(bitmap);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }
    public int Height { get; }
    public Graphics Graphics => graphics ?? throw new ObjectDisposedException(nameof(LayeredSurface));

    public void Upload(IntPtr window, int x, int y)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Unable to acquire screen DC for layered upload.");
        try
        {
            var destination = new PointNative { X = x, Y = y };
            var source = new PointNative();
            var surfaceSize = new SizeNative { Width = Width, Height = Height };
            var blend = new BlendFunction { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            if (!UpdateLayeredWindow(window, screenDc, ref destination, ref surfaceSize, memoryDc, ref source, 0, ref blend, UlwAlpha))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Layered-window upload failed.");
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public void Dispose()
    {
        graphics?.Dispose();
        graphics = null;
        bitmap?.Dispose();
        bitmap = null;
        if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previousObject);
        previousObject = IntPtr.Zero;
        if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
        bitmapHandle = IntPtr.Zero;
        if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
        memoryDc = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader bmiHeader; public uint bmiColors; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref PointNative destination, ref SizeNative size, IntPtr sourceDc, ref PointNative source, int colorKey, ref BlendFunction blend, int flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);
}

internal sealed class FrameMetrics : IDisposable
{
    private const int MaximumSamples = 2_048;
    private readonly object gate = new();
    private readonly List<double> intervals = [];
    private readonly List<double> jitters = [];
    private readonly List<double> renders = [];
    private readonly List<double> uploads = [];
    private readonly System.Threading.Timer? writeTimer;
    private long coalesced, skippedDeadlines, clockFallbacks;
    public FrameMetrics()
    {
        IsEnabled = string.Equals(Environment.GetEnvironmentVariable("OPENCODE_CURSOR_PROFILE"), "1", StringComparison.Ordinal);
        if (IsEnabled) writeTimer = new System.Threading.Timer(_ => WriteSnapshot(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public bool IsEnabled { get; }

    public void RecordPacing(long intervalTicks, long jitterTicks)
    {
        if (!IsEnabled) return;
        lock (gate)
        {
            if (intervalTicks > 0) AddSample(intervals, ToMilliseconds(intervalTicks));
            AddSample(jitters, ToMilliseconds(Math.Abs(jitterTicks)));
        }
    }

    public void RecordRender(long ticks) { if (IsEnabled) lock (gate) AddSample(renders, ToMilliseconds(ticks)); }
    public void RecordUpload(long ticks) { if (IsEnabled) lock (gate) AddSample(uploads, ToMilliseconds(ticks)); }
    public void RecordCoalescedFrame() { if (IsEnabled) Interlocked.Increment(ref coalesced); }
    public void RecordSkippedDeadlines(long count) { if (IsEnabled) Interlocked.Add(ref skippedDeadlines, count); }
    public void RecordClockFallback() { if (IsEnabled) Interlocked.Increment(ref clockFallbacks); }

    private void WriteSnapshot()
    {
        if (!IsEnabled) return;
        string contents;
        lock (gate)
        {
            contents = $"utc={DateTime.UtcNow:O}{Environment.NewLine}" +
                $"dropped_or_coalesced={Interlocked.Read(ref coalesced)}{Environment.NewLine}" +
                $"skipped_deadlines={Interlocked.Read(ref skippedDeadlines)}{Environment.NewLine}" +
                $"clock_fallbacks={Interlocked.Read(ref clockFallbacks)}{Environment.NewLine}" +
                Summarize("frame_interval_ms", intervals) +
                Summarize("frame_jitter_ms", jitters) +
                Summarize("render_ms", renders) +
                Summarize("layered_upload_ms", uploads);
        }
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"opencode-cursor-profile-{Environment.ProcessId}.log"), contents);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        writeTimer?.Dispose();
        if (IsEnabled) WriteSnapshot();
    }

    private static void AddSample(List<double> samples, double value)
    {
        if (samples.Count == MaximumSamples) samples.RemoveAt(0);
        samples.Add(value);
    }

    private static string Summarize(string name, List<double> samples)
    {
        if (samples.Count == 0) return $"{name}=no samples{Environment.NewLine}";
        var ordered = samples.OrderBy(value => value).ToArray();
        return $"{name}: p50={Percentile(ordered, 0.50):F3} p95={Percentile(ordered, 0.95):F3} p99={Percentile(ordered, 0.99):F3} max={ordered[^1]:F3}{Environment.NewLine}";
    }

    private static double Percentile(double[] values, double percentile) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];
    private static double ToMilliseconds(long ticks) => ticks * 1_000d / Stopwatch.Frequency;
}

[StructLayout(LayoutKind.Sequential)] internal struct PointNative { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] internal struct SizeNative { public int Width, Height; }
[StructLayout(LayoutKind.Sequential)] internal struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
