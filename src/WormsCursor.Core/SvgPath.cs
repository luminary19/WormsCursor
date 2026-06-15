using System.Drawing.Drawing2D;
using System.Globalization;

namespace WormsCursor.Core;

/// <summary>
/// A tiny SVG path-data ("d" attribute) → <see cref="GraphicsPath"/> parser — just enough to bake the
/// agent tool logos (see <see cref="AgentLogos"/>) into vector geometry, so they scale crisply to any
/// cursor size instead of being rasterised. Mirrors how <c>HandShape</c> bakes its silhouette.
///
/// Supports M/L/H/V/C/S/Q/T/A/Z in both absolute (upper) and relative (lower) form, implicit command
/// repetition, and SVG's packed arc flags; arcs are flattened to cubic Béziers. Not a general SVG
/// engine — no transforms, units, or styling. Coordinates stay in the source viewBox; callers fit via
/// <see cref="GraphicsPath.GetBounds()"/>.
/// </summary>
static class SvgPath
{
    public static GraphicsPath Parse(string d, FillMode fill)
    {
        var path = new GraphicsPath { FillMode = fill };
        var r = new Reader(d);
        float cx = 0, cy = 0;     // current point
        float sx = 0, sy = 0;     // start of the current subpath (for Z)
        float ctrlX = 0, ctrlY = 0; // last cubic/quad control point (for S/T reflection)
        char cmd = ' ', lastKind = ' ';

        while (true)
        {
            r.SkipSep();
            if (r.End) break;
            char c = r.Peek;
            if (char.IsLetter(c)) { cmd = c; r.Next(); }
            else if (cmd == ' ') { r.Next(); continue; } // junk before the first command
            else if (cmd == 'M') cmd = 'L';              // extra coords after M are implicit L
            else if (cmd == 'm') cmd = 'l';

            bool rel = char.IsLower(cmd);
            char k = char.ToUpperInvariant(cmd);
            switch (k)
            {
                case 'M':
                {
                    float x = r.Num(), y = r.Num();
                    if (rel) { x += cx; y += cy; }
                    path.StartFigure();
                    cx = sx = x; cy = sy = y;
                    break;
                }
                case 'L':
                {
                    float x = r.Num(), y = r.Num();
                    if (rel) { x += cx; y += cy; }
                    path.AddLine(cx, cy, x, y); cx = x; cy = y;
                    break;
                }
                case 'H':
                {
                    float x = r.Num(); if (rel) x += cx;
                    path.AddLine(cx, cy, x, cy); cx = x;
                    break;
                }
                case 'V':
                {
                    float y = r.Num(); if (rel) y += cy;
                    path.AddLine(cx, cy, cx, y); cy = y;
                    break;
                }
                case 'C':
                {
                    float x1 = r.Num(), y1 = r.Num(), x2 = r.Num(), y2 = r.Num(), x = r.Num(), y = r.Num();
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    ctrlX = x2; ctrlY = y2; cx = x; cy = y;
                    break;
                }
                case 'S':
                {
                    float x2 = r.Num(), y2 = r.Num(), x = r.Num(), y = r.Num();
                    if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                    float x1 = cx, y1 = cy;
                    if (lastKind is 'C' or 'S') { x1 = 2 * cx - ctrlX; y1 = 2 * cy - ctrlY; }
                    path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                    ctrlX = x2; ctrlY = y2; cx = x; cy = y;
                    break;
                }
                case 'Q':
                {
                    float qx = r.Num(), qy = r.Num(), x = r.Num(), y = r.Num();
                    if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                    AddQuad(path, cx, cy, qx, qy, x, y);
                    ctrlX = qx; ctrlY = qy; cx = x; cy = y;
                    break;
                }
                case 'T':
                {
                    float x = r.Num(), y = r.Num();
                    if (rel) { x += cx; y += cy; }
                    float qx = cx, qy = cy;
                    if (lastKind is 'Q' or 'T') { qx = 2 * cx - ctrlX; qy = 2 * cy - ctrlY; }
                    AddQuad(path, cx, cy, qx, qy, x, y);
                    ctrlX = qx; ctrlY = qy; cx = x; cy = y;
                    break;
                }
                case 'A':
                {
                    float rx = r.Num(), ry = r.Num(), rot = r.Num();
                    bool large = r.Flag(), sweep = r.Flag();
                    float x = r.Num(), y = r.Num();
                    if (rel) { x += cx; y += cy; }
                    AddArc(path, cx, cy, rx, ry, rot, large, sweep, x, y);
                    cx = x; cy = y;
                    break;
                }
                case 'Z':
                    path.CloseFigure(); cx = sx; cy = sy;
                    break;
                default:
                    r.Next(); // unknown command: skip a char and carry on
                    break;
            }
            lastKind = k;
        }
        return path;
    }

    static void AddQuad(GraphicsPath p, float x0, float y0, float qx, float qy, float x, float y)
    {
        // Elevate the quadratic to an equivalent cubic for GDI+'s AddBezier.
        float c1x = x0 + 2f / 3f * (qx - x0), c1y = y0 + 2f / 3f * (qy - y0);
        float c2x = x + 2f / 3f * (qx - x), c2y = y + 2f / 3f * (qy - y);
        p.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x, y);
    }

    // Endpoint-parameterised SVG arc → cubic Béziers (the standard SVG-spec conversion).
    static void AddArc(GraphicsPath p, float x1f, float y1f, float rxf, float ryf,
                       float phiDeg, bool large, bool sweep, float x2f, float y2f)
    {
        double x1 = x1f, y1 = y1f, x2 = x2f, y2 = y2f, rx = Math.Abs(rxf), ry = Math.Abs(ryf);
        if (rx < 1e-6 || ry < 1e-6) { p.AddLine(x1f, y1f, x2f, y2f); return; }

        double phi = phiDeg * Math.PI / 180.0;
        double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double x1p = cosP * dx + sinP * dy;
        double y1p = -sinP * dx + cosP * dy;

        double rxs = rx * rx, rys = ry * ry, x1ps = x1p * x1p, y1ps = y1p * y1p;
        double lambda = x1ps / rxs + y1ps / rys;
        if (lambda > 1) { double s = Math.Sqrt(lambda); rx *= s; ry *= s; rxs = rx * rx; rys = ry * ry; }

        double sign = large != sweep ? 1 : -1;
        double num = rxs * rys - rxs * y1ps - rys * x1ps;
        double den = rxs * y1ps + rys * x1ps;
        double co = sign * Math.Sqrt(Math.Max(0, num / den));
        double cxp = co * (rx * y1p / ry);
        double cyp = co * (-ry * x1p / rx);
        double cx0 = cosP * cxp - sinP * cyp + (x1 + x2) / 2.0;
        double cy0 = sinP * cxp + cosP * cyp + (y1 + y2) / 2.0;

        double th1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dth = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dth > 0) dth -= 2 * Math.PI;
        if (sweep && dth < 0) dth += 2 * Math.PI;

        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2)));
        double delta = dth / n;
        double t = 8.0 / 3.0 * Math.Sin(delta / 4) * Math.Sin(delta / 4) / Math.Sin(delta / 2);
        double a = th1, px = x1, py = y1;
        for (int i = 0; i < n; i++)
        {
            double a2 = a + delta;
            double sinA = Math.Sin(a), cosA = Math.Cos(a), sinA2 = Math.Sin(a2), cosA2 = Math.Cos(a2);
            double ex = cx0 + rx * cosP * cosA2 - ry * sinP * sinA2;
            double ey = cy0 + rx * sinP * cosA2 + ry * cosP * sinA2;
            double d1x = -rx * cosP * sinA - ry * sinP * cosA;
            double d1y = -rx * sinP * sinA + ry * cosP * cosA;
            double d2x = -rx * cosP * sinA2 - ry * sinP * cosA2;
            double d2y = -rx * sinP * sinA2 + ry * cosP * cosA2;
            p.AddBezier((float)px, (float)py, (float)(px + t * d1x), (float)(py + t * d1y),
                        (float)(ex - t * d2x), (float)(ey - t * d2y), (float)ex, (float)ey);
            px = ex; py = ey; a = a2;
        }
    }

    static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double ang = Math.Acos(Math.Clamp(len < 1e-12 ? 1 : dot / len, -1, 1));
        return ux * vy - uy * vx < 0 ? -ang : ang;
    }

    sealed class Reader
    {
        readonly string _s;
        int _i;
        public Reader(string s) { _s = s; }
        public bool End => _i >= _s.Length;
        public char Peek => _s[_i];
        public void Next() => _i++;

        public void SkipSep()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c is ' ' or '\t' or '\n' or '\r' or ',') _i++;
                else break;
            }
        }

        public float Num()
        {
            SkipSep();
            int start = _i;
            if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i < _s.Length && _s[_i] == '.') { _i++; while (_i < _s.Length && char.IsDigit(_s[_i])) _i++; }
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            }
            return float.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // Arc flags are single '0'/'1' chars, often packed with no separators ("00-16.687").
        public bool Flag()
        {
            SkipSep();
            char c = _s[_i]; _i++;
            return c == '1';
        }
    }
}
