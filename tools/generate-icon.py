"""
Builds the WormsCursor app/tray icon: a monochrome white silhouette of the
same aim-arrow we use as the rotating cursor, packed into a multi-size ICO
(plus a PNG for the Store/README).

Why monochrome white: Windows 11 notification-area icons are flat single-colour
glyphs (see the system tray). A white silhouette on transparent matches that
vocabulary on the default dark taskbar. The cursor itself keeps its black
outline for visibility over arbitrary content; the icon drops it on purpose.

The arrow geometry is the same 7-point polygon as
src/WormsCursor.Core/ArrowRenderer.cs, so the icon reads as "that cursor".
It's drawn supersampled and LANCZOS-downscaled for clean antialiased edges
(PIL's polygon fill is not antialiased), rotated to point up-right so the
square canvas is used well.

ICO is assembled by hand (ICONDIR + ICONDIRENTRY x N + PNG blobs) because
Pillow's ICO writer downscales a single bitmap instead of storing crisp
per-size frames. Pattern mirrored from C:\\W\\C\\PowerLink\\tools\\generate-overlay-icon.py.

Run from anywhere (Windows or not, pure Pillow):
    python tools/generate-icon.py
"""

import io
import math
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "WormsCursor.App" / "Assets"
ICO_OUT = ASSETS / "Icon.ico"
PNG_OUT = ASSETS / "icon.png"

# Same arrow as the cursor: tip at local origin, pointing +X (right).
# (tip, upper barb, shaft top, shaft back-top, shaft back-bottom, shaft bottom, lower barb)
ARROW = [(0, 0), (-14, -9), (-14, -3), (-22, -3), (-22, 3), (-14, 3), (-14, 9)]

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
ANGLE_DEG = -45          # rotate so the arrow points up-right
PAD_FRAC = 0.16          # empty border as a fraction of the canvas
SUPERSAMPLE = 4          # draw NxN then downscale for antialiasing

FILL = (255, 255, 255, 255)   # white interior
OUTLINE = (0, 0, 0, 255)      # black frame — keeps the glyph readable on BOTH
                              # dark (tray) and light (Explorer) backgrounds
OUTLINE_FRAC = 0.07           # outline thickness as a fraction of icon size,
OUTLINE_MIN = 1.6             # clamped so it stays visible when tiny
OUTLINE_MAX = 12.0            # and doesn't get chunky at 256px


def fit_points(size: int, angle_deg: float) -> list[tuple[float, float]]:
    """Rotate the arrow, then scale+centre it to fill `size` with PAD_FRAC border."""
    a = math.radians(angle_deg)
    ca, sa = math.cos(a), math.sin(a)

    # Centre the base polygon on its bounding box, then rotate (screen coords: y down).
    xs = [p[0] for p in ARROW]
    ys = [p[1] for p in ARROW]
    bx = (min(xs) + max(xs)) / 2
    by = (min(ys) + max(ys)) / 2
    rot = [((x - bx) * ca - (y - by) * sa, (x - bx) * sa + (y - by) * ca) for x, y in ARROW]

    rxs = [p[0] for p in rot]
    rys = [p[1] for p in rot]
    w = max(rxs) - min(rxs)
    h = max(rys) - min(rys)
    pad = size * PAD_FRAC
    scale = (size - 2 * pad) / max(w, h)

    mx = (min(rxs) + max(rxs)) / 2
    my = (min(rys) + max(rys)) / 2
    c = size / 2
    return [((px - mx) * scale + c, (py - my) * scale + c) for px, py in rot]


def _clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pts = fit_points(big, ANGLE_DEG)
    # White fill, then a black frame stroked along the boundary (joint='curve'
    # rounds the corners so the sharp tip stays clean).
    width = round(_clamp(size * OUTLINE_FRAC, OUTLINE_MIN, OUTLINE_MAX) * SUPERSAMPLE)
    d.polygon(pts, fill=FILL)
    d.line(pts + [pts[0]], fill=OUTLINE, width=width, joint="curve")
    return img.resize((size, size), Image.LANCZOS)


def write_ico(frames: list[Image.Image], path: Path) -> None:
    """ICONDIR header + ICONDIRENTRY x N + PNG blobs (per-size crisp frames)."""
    blobs: list[tuple[int, bytes]] = []
    for frame in frames:
        buf = io.BytesIO()
        frame.save(buf, format="PNG", optimize=True)
        blobs.append((frame.width, buf.getvalue()))

    out = io.BytesIO()
    out.write(struct.pack("<HHH", 0, 1, len(blobs)))
    data_offset = 6 + 16 * len(blobs)
    for size, png in blobs:
        w_byte = 0 if size >= 256 else size  # 0 encodes 256
        out.write(struct.pack("<BBBBHHII", w_byte, w_byte, 0, 0, 1, 32, len(png), data_offset))
        data_offset += len(png)
    for _, png in blobs:
        out.write(png)

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(out.getvalue())


def main() -> None:
    frames = [render(s) for s in SIZES]
    write_ico(frames, ICO_OUT)
    frames[-1].save(PNG_OUT, format="PNG", optimize=True)  # 256px for README/Store
    print(f"Wrote {ICO_OUT} ({ICO_OUT.stat().st_size} bytes, sizes: {', '.join(map(str, SIZES))})")
    print(f"Wrote {PNG_OUT} ({PNG_OUT.stat().st_size} bytes, 256x256)")


if __name__ == "__main__":
    main()
