using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WormsCursor.Core;

// WormsCursor.Preview — renders a labelled showcase of all the themed cursors for the
// README, in two variants: a dark card and a transparent background. The composited
// cursors (busy / app-starting / help) are drawn in their "at rest" pose.
//
// Usage: dotnet run --project src/WormsCursor.Preview -- [outputPath] [size]
//   outputPath  PNG to write (default cursors.png); a "<name>-transparent.png" is
//               written alongside it.
//   size        cursor size in px (default 96). Cursors are drawn 1:1 (never upscaled),
//               so bump this for a larger, still-crisp sheet.

string outPath = args.Length > 0 ? args[0] : "cursors.png";
int size = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 96;

var s = new CursorSettings { Size = size };
s.Normalize();

var items = new (TestCursor kind, string label)[]
{
    (TestCursor.Arrow, "arrow"),
    (TestCursor.Hand, "hand / link"),
    (TestCursor.Wait, "busy"),
    (TestCursor.AppStarting, "app-starting"),
    (TestCursor.Help, "help"),
    (TestCursor.Cross, "crosshair"),
    (TestCursor.Ibeam, "text"),
    (TestCursor.SizeWE, "resize ↔"),
    (TestCursor.SizeNS, "resize ↕"),
    (TestCursor.SizeNWSE, "resize ↘↖"),
    (TestCursor.SizeNESW, "resize ↗↙"),
    (TestCursor.SizeAll, "move"),
    (TestCursor.No, "unavailable"),
};

using var arrowBase = ArrowRenderer.DrawArrow(s);
var cursors = new Bitmap[items.Length];
for (int i = 0; i < items.Length; i++)
{
    using Bitmap full = items[i].kind switch
    {
        TestCursor.Arrow => ArrowRenderer.DrawArrow(s),
        TestCursor.Hand => HandRenderer.DrawHand(s),
        var k => ProgressRenderer.RenderRest(s, k, arrowBase),
    };
    cursors[i] = Trim(full);
}

int cols = (items.Length + 1) / 2; // two rows
int rows = (items.Length + cols - 1) / cols;
// Cells are sized to the largest trimmed cursor so each can be drawn 1:1 (crisp, never
// upscaled). True relative sizes are preserved (all share one coordinate space).
int maxW = cursors.Max(b => b.Width);
int maxH = cursors.Max(b => b.Height);
int padX = (int)(size * 0.40f), padY = (int)(size * 0.26f), labelH = (int)(size * 0.34f);
int cellW = maxW + 2 * padX, cellH = maxH + 2 * padY + labelH;
int imgW = cols * cellW, imgH = rows * cellH;

Bitmap BuildSheet(Color? background, Color labelColor)
{
    var img = new Bitmap(imgW, imgH);
    using var g = Graphics.FromImage(img);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TextRenderingHint = TextRenderingHint.AntiAlias; // works over transparency too
    if (background is Color bg)
        using (var bb = new SolidBrush(bg))
            g.FillRectangle(bb, 0, 0, imgW, imgH);

    using var labelBrush = new SolidBrush(labelColor);
    using var font = new Font("Segoe UI", size * 0.135f, FontStyle.Regular, GraphicsUnit.Pixel);
    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    for (int i = 0; i < items.Length; i++)
    {
        int r = i / cols, c = i % cols;
        int cellX = c * cellW, cellY = r * cellH;
        var bmp = cursors[i];
        // 1:1 blit (no scaling) centred in the art area → crisp.
        int dx = cellX + (cellW - bmp.Width) / 2;
        int dy = cellY + padY + (maxH - bmp.Height) / 2;
        g.DrawImageUnscaled(bmp, dx, dy);

        var labelRect = new RectangleF(cellX, cellY + cellH - labelH, cellW, labelH);
        g.DrawString(items[i].label, font, labelBrush, labelRect, fmt);
    }
    return img;
}

string dir = Path.GetDirectoryName(outPath) ?? ".";
string stem = Path.GetFileNameWithoutExtension(outPath);
string transPath = Path.Combine(dir, stem + "-transparent.png");

using (var dark = BuildSheet(Color.FromArgb(43, 43, 46), Color.FromArgb(210, 210, 215)))
    dark.Save(outPath, ImageFormat.Png);
// Transparent variant: no background; mid-grey labels so they read on light or dark.
using (var trans = BuildSheet(null, Color.FromArgb(150, 150, 150)))
    trans.Save(transPath, ImageFormat.Png);

Console.WriteLine($"wrote {outPath} and {transPath} ({imgW}x{imgH}, cursor size {size})");
foreach (var b in cursors) b.Dispose();

// Crops a bitmap to its non-transparent bounds so the cursor's padded canvas doesn't
// dominate the layout (same idea as the in-app preview).
static Bitmap Trim(Bitmap src)
{
    var rect = new Rectangle(0, 0, src.Width, src.Height);
    var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int stride = data.Stride;
    var buf = new byte[stride * src.Height];
    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
    src.UnlockBits(data);

    int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;
    for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width; x++)
            if (buf[y * stride + x * 4 + 3] > 8)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
    if (maxX < minX) return (Bitmap)src.Clone();

    var crop = new Bitmap(maxX - minX + 1, maxY - minY + 1);
    using var cg = Graphics.FromImage(crop);
    cg.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height),
                 Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), GraphicsUnit.Pixel);
    return crop;
}
