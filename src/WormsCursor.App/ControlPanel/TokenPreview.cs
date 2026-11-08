using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WormsCursor.Core;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// A live, animated picture of the token, drawn by Core's <see cref="NotifierRenderer"/> (so it is
/// exactly what the overlay draws, badge and all) at its real size, bouncing the way the chosen
/// placement bounces. Transparent around the token: the page puts the backdrop behind it.
///
/// The renderer is this control's own and lives on the UI thread from Loaded to Unloaded, never
/// shared with the overlay's animation thread (GDI+ objects are not thread-safe). Frames come from
/// <see cref="CompositionTarget.Rendering"/> only while the control is visible, so a hidden panel
/// draws nothing. With Windows' animation effects turned off it shows one still frame.
/// </summary>
public sealed class TokenPreview : FrameworkElement
{
    const string Tool = "claude-code";
    const float HeadroomRatio = 1.5f; // token size -> canvas height: the hop plus the badge's reach
    const double Padding = 12;        // DIPs around the token's travel

    NotifierRenderer? _renderer;
    Gdi.Bitmap? _buffer;          // GDI+ back-buffer, premultiplied BGRA like the WPF bitmap
    WriteableBitmap? _bitmap;     // what OnRender shows
    readonly Stopwatch _clock = Stopwatch.StartNew();
    CursorSettings _look = new();
    bool _animating;

    /// <summary>Show the "blocked on you" face (with the yellow "?") instead of the plain one.</summary>
    public bool Blocked { get; set; }

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

    /// <summary>Draws the token with <paramref name="look"/>'s size, placement and colours.</summary>
    public void Show(CursorSettings look)
    {
        _look = look;
        InvalidateMeasure();
        RenderFrame();
    }

    protected override Size MeasureOverride(Size available)
    {
        double side = _look.Size;
        return new Size(side * 1.4 + 2 * Padding, side * HeadroomRatio + 2 * Padding);
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
        bool animate = IsVisible && _renderer is not null && SystemParameters.ClientAreaAnimation;
        if (animate && !_animating) { CompositionTarget.Rendering += OnFrame; _animating = true; }
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

        float tokenPx = (float)(_look.Size * dpi.DpiScaleX);
        float t = _animating ? (float)_clock.Elapsed.TotalSeconds : 0f;
        bool corner = _look.Placement == NotifierPlacement.Corner;
        var pose = corner
            ? TokenMotion.CornerBounce(t, tokenPx)
            : new TokenMotion.Pose(0f, TokenMotion.IdleBob(t, tokenPx), 0f);
        // Centre the token's travel: it hops up from its resting point by up to `hop`.
        float hop = tokenPx * (corner ? TokenMotion.CornerHop : TokenMotion.IdleBobHeight);
        float badgeReach = tokenPx * AttentionBadge.Overhang;
        float cx = w / 2f + pose.OffsetX;
        float cy = h / 2f + (hop + badgeReach) / 2f + pose.OffsetY;

        using (var g = Gdi.Graphics.FromImage(_buffer!))
        {
            g.Clear(Gdi.Color.Transparent);
            _renderer.DrawToken(g, _look, new TokenFace(Tool, Blocked, 0), cx, cy, tokenPx, pose.SwingDeg);
        }

        var rect = new Gdi.Rectangle(0, 0, w, h);
        var data = _buffer!.LockBits(rect, GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppPArgb);
        try { _bitmap!.WritePixels(new Int32Rect(0, 0, w, h), data.Scan0, data.Stride * h, data.Stride); }
        finally { _buffer.UnlockBits(data); }

        if (reallocated) InvalidateVisual();
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
