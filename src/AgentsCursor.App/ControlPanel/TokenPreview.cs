using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentsCursor.Core;
using Gdi = System.Drawing;
using GdiDrawing2D = System.Drawing.Drawing2D;
using GdiImaging = System.Drawing.Imaging;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// A live, animated picture of the token, drawn by Core's <see cref="NotifierRenderer"/> (so it is
/// exactly what the overlay draws, badge, "+N" and chosen icon included) at its real size, moving with
/// Core's <see cref="TokenMotion"/> in the chosen <see cref="CursorSettings.Motion"/>. Transparent around the token: the page puts the backdrop behind it.
///
/// Two modes. By default the token stays in place and only does its idle motion (a bob along its
/// string, or the corner hop): the Customization page uses this. With <see cref="Roam"/> a small drawn pointer
/// glides along a slow loop and the token follows it the way the chosen placement does (below, above,
/// beside, mirrored at the preview's edges), or waits in that corner of the preview: the Sound and Display page uses
/// this to show where the token goes.
///
/// The renderer is this control's own and lives on the UI thread from Loaded to Unloaded, never
/// shared with the overlay's animation thread (GDI+ objects are not thread-safe). Frames come from
/// <see cref="CompositionTarget.Rendering"/> only while the control is visible and something in it
/// moves, so a hidden panel draws nothing: in place under the Still motion (or with Windows' animation
/// effects off, which makes every style Still) the token and its mascot stand still, so it shows one
/// frame, redrawn only when the look changes. Roaming, the pointer keeps gliding (while animation
/// effects are on) and the token follows it. A mascot plays its idle loop while
/// <see cref="MascotLoops.Animate"/> allows.
/// </summary>
public sealed class TokenPreview : FrameworkElement
{
    const double Padding = 12;        // DIPs around the token's travel
    const float MaxDt = 0.05f;        // as the overlay: a hiccup can't blow up the spring
    const float LoopSeconds = 9f;     // one lap of the roaming pointer

    NotifierRenderer? _renderer;
    Gdi.Bitmap? _buffer;          // GDI+ back-buffer, premultiplied BGRA like the WPF bitmap
    WriteableBitmap? _bitmap;     // what OnRender shows
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last;                 // clock reading of the previous frame
    CursorSettings _look = new();
    bool _animating;
    TetherState _tether;
    Flip _flip;
    NotifierPlacement _motionFor; // the placement the tether belongs to

    /// <summary>The agent whose icon is shown.</summary>
    public string Tool { get; set; } = "claude-code";

    /// <summary>Show the "blocked on you" face (yellow "?" cloud) instead of the finished one (green check cloud).</summary>
    public bool Blocked { get; set; }

    /// <summary>How many other sessions the "+N" pill counts (0 = no pill).</summary>
    public int Others { get; set; }

    /// <summary>Draw a roaming pointer and follow it the way the placement does.</summary>
    public bool Roam { get; set; }

    public TokenPreview()
    {
        Focusable = false;
        Loaded += (_, _) => { _renderer ??= new NotifierRenderer(); UpdateAnimation(); };
        Unloaded += (_, _) =>
        {
            StopAnimation();
            _renderer?.Dispose();
            _renderer = null;
            _buffer?.Dispose();
            _buffer = null;
            _bitmap = null;
        };
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    /// <summary>Draws the token with <paramref name="look"/>'s size, placement and icons.</summary>
    public void Show(CursorSettings look)
    {
        _look = look;
        InvalidateMeasure();
        UpdateAnimation(); // the motion may have changed
    }

    /// <summary>The width, in DIPs, a preview without <see cref="Roam"/> needs for a token of
    /// <paramref name="size"/>: the token, its badges and its bob.</summary>
    public static double IdleWidth(int size) => size * 1.5 + 2 * Padding;

    protected override Size MeasureOverride(Size available)
    {
        double side = _look.Size;
        if (!Roam) return new Size(IdleWidth(_look.Size), side * 1.55 + 2 * Padding);
        // Roaming fills the area the page gives it (a fixed height, so a size change moves nothing).
        double width = double.IsInfinity(available.Width) ? side * 4 : available.Width;
        double height = double.IsInfinity(available.Height) ? side * 2.5 + 2 * Padding : available.Height;
        return new Size(width, height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RenderFrame();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => RenderFrame();

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth / dpi.DpiScaleX, _bitmap.PixelHeight / dpi.DpiScaleY));
    }

    void UpdateAnimation()
    {
        bool effects = SystemParameters.ClientAreaAnimation;
        bool moves = Roam ? effects : MotionProfile.Effective(_look.Motion, effects) != MotionStyle.Still;
        bool animate = IsVisible && _renderer is not null && moves;
        if (animate && !_animating)
        {
            CompositionTarget.Rendering += OnFrame;
            _animating = true;
            _last = _clock.Elapsed.TotalSeconds;
        }
        else if (!animate) StopAnimation();
        RenderFrame();
    }

    void StopAnimation()
    {
        if (!_animating) return;
        CompositionTarget.Rendering -= OnFrame;
        _animating = false;
    }

    void OnFrame(object? sender, EventArgs e) => RenderFrame();

    void RenderFrame()
    {
        if (_renderer is null || ActualWidth < 1 || ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        bool reallocated = EnsureBuffers(w, h, dpi);

        double now = _clock.Elapsed.TotalSeconds;
        float dt = _animating ? (float)Math.Min(MaxDt, now - _last) : 0f;
        _last = now;
        float t = _animating ? (float)now : 0f;
        float tokenPx = (float)(_look.Size * dpi.DpiScaleX), scale = (float)dpi.DpiScaleX;
        var motion = MotionProfile.For(_look.Motion, SystemParameters.ClientAreaAnimation);

        using (var g = Gdi.Graphics.FromImage(_buffer!))
        {
            g.Clear(Gdi.Color.Transparent);
            g.SmoothingMode = GdiDrawing2D.SmoothingMode.AntiAlias;
            var (cx, cy, swing, pointer) = Roam ? RoamPose(w, h, t, dt, tokenPx, scale, motion) : IdlePose(w, h, t, tokenPx, scale, motion);
            double? mascotClock = _animating && MascotLoops.Animate(_look, SystemParameters.ClientAreaAnimation) ? now : null;
            _renderer.DrawToken(g, _look, new TokenFace(Tool, Blocked, Others), cx, cy, tokenPx, swing, scale, mascotClock);
            if (pointer is { } p) DrawPointer(g, p, scale); // the real pointer is always on top too
        }

        var rect = new Gdi.Rectangle(0, 0, w, h);
        var data = _buffer!.LockBits(rect, GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppPArgb);
        try { _bitmap!.WritePixels(new Int32Rect(0, 0, w, h), data.Scan0, data.Stride * h, data.Stride); }
        finally { _buffer.UnlockBits(data); }

        if (reallocated) InvalidateVisual();
    }

    // In place: the corner hop, or a bob along the placement's string (from an imaginary pointer).
    (float X, float Y, float Swing, Gdi.PointF? Pointer) IdlePose(int w, int h, float t, float tokenPx, float scale, MotionProfile motion)
    {
        float badge = tokenPx * StatusBadge.Overhang;
        if (_look.Placement == NotifierPlacement.Corner)
        {
            var pose = TokenMotion.CornerBounce(t, tokenPx, motion);
            float hop = tokenPx * motion.HopHeight;
            return (w / 2f + pose.OffsetX, h / 2f + (hop + badge) / 2f + pose.OffsetY, pose.SwingDeg, null);
        }
        var rest = TokenMotion.RestFor(_look.Placement, tokenPx, scale, Flip.None);
        float bob = TokenMotion.IdleBob(t, tokenPx, motion), len = Math.Max(1e-3f, rest.Length);
        float travel = tokenPx * motion.BobHeight;
        return (w / 2f + rest.Dx / len * (bob + travel / 2f), h / 2f + badge / 2f + rest.Dy / len * (bob + travel / 2f), 0f, null);
    }

    // Following a pointer that glides along a slow loop through the preview.
    (float X, float Y, float Swing, Gdi.PointF? Pointer) RoamPose(int w, int h, float t, float dt, float tokenPx, float scale, MotionProfile motion)
    {
        var placement = _look.Placement;
        if (placement != _motionFor) { _tether = default; _flip = Flip.None; _motionFor = placement; }

        // The loop: wide and slow side to side, and a gentle rise and fall. Vertically it keeps the
        // pointer and the token resting by it centred in the preview, and rises and falls only as far
        // as leaves the token room to spare, so it doesn't flip above or below the pointer on its own
        // (as it would at a screen edge) unless the token is too big for the preview.
        var plain = TokenMotion.RestFor(placement == NotifierPlacement.Corner ? NotifierPlacement.BelowPointer : placement, tokenPx, scale, Flip.None);
        float phase = t / LoopSeconds * 2f * MathF.PI;
        float spanX = Math.Max(0f, w / 2f - tokenPx * 0.9f - 24f * scale);
        float ax = w / 2f + spanX * MathF.Sin(phase) - plain.Dx / 2f;
        float half = TokenMotion.RestHalfExtent(tokenPx);
        float top = Math.Min(0f, plain.Dy - half), bottom = Math.Max(TokenMotion.PointerClearance * scale, plain.Dy + half);
        float slack = (h - (bottom - top)) / 2f;
        float rise = Math.Clamp(slack - 0.25f * tokenPx, 0f, tokenPx * 0.12f);
        float ay = slack - top + rise * MathF.Sin(phase * 2f);
        var pointer = new Gdi.PointF(ax, ay);

        if (placement == NotifierPlacement.Corner)
        {
            var pose = TokenMotion.CornerBounce(t, tokenPx, motion);
            float inset = tokenPx * (0.5f + StatusBadge.Overhang) + 8f * scale;
            float x = _look.Corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft ? inset : w - inset;
            float y = _look.Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight
                ? inset + tokenPx * TokenMotion.CornerHop : h - inset;
            return (x + pose.OffsetX, y + pose.OffsetY, pose.SwingDeg, pointer);
        }

        var area = new Gdi.RectangleF(0, 0, w, h);
        _flip = TokenMotion.Edges(_flip, pointer, plain, tokenPx, area);
        var rest = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, tokenPx, scale, _flip), pointer, tokenPx, area);
        var step = TokenMotion.Step(ref _tether, ax, ay, rest, tokenPx, dt, entrance: false, motion, motion.MaxSwingFor(placement), area);
        return (ax + step.OffsetX, ay + step.OffsetY, step.SwingDeg, pointer);
    }

    // The standard Windows arrow, white with a dark edge, its tip at the pointer.
    static void DrawPointer(Gdi.Graphics g, Gdi.PointF tip, float scale)
    {
        Gdi.PointF[] arrow =
        {
            new(0, 0), new(0, 16), new(4, 12.2f), new(6.9f, 18.6f), new(9.3f, 17.6f), new(6.5f, 11.3f), new(11.6f, 11.3f),
        };
        for (int i = 0; i < arrow.Length; i++)
            arrow[i] = new Gdi.PointF(tip.X + arrow[i].X * scale * 1.15f, tip.Y + arrow[i].Y * scale * 1.15f);
        using var path = new GdiDrawing2D.GraphicsPath();
        path.AddPolygon(arrow);
        using (var fill = new Gdi.SolidBrush(Gdi.Color.White)) g.FillPath(fill, path);
        using (var edge = new Gdi.Pen(Gdi.Color.Black, 1.2f * scale) { LineJoin = GdiDrawing2D.LineJoin.Miter }) g.DrawPath(edge, path);
    }

    // (Re)allocates both buffers when the pixel size or DPI changed; true when it did.
    bool EnsureBuffers(int w, int h, DpiScale dpi)
    {
        if (_bitmap is not null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h
            && Math.Abs(_bitmap.DpiX - dpi.PixelsPerInchX) < 0.5) return false;
        _buffer?.Dispose();
        _buffer = new Gdi.Bitmap(w, h, GdiImaging.PixelFormat.Format32bppPArgb);
        _bitmap = new WriteableBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
        return true;
    }
}
