using System.Text.RegularExpressions;
using Svg;

namespace AgentsCursor.Core;

/// <summary>
/// Proves that drawing an SVG ends, quickly, before anything draws it. An SVG can make its renderer
/// draw one part of it from another: a <c>&lt;use&gt;</c>, a pattern or marker fill, a clip path, a
/// mask, a filter, a gradient inheriting another. SVG.NET follows every such reference without limit, so
/// a part that reaches itself (a pattern filled with itself, two gradients inheriting each other)
/// recurses until the stack overflows, which kills the process, and a reference fan-out (a
/// <c>&lt;use&gt;</c> of ten copies of a <c>&lt;use&gt;</c> of ten copies…) draws for ever.
///
/// The check reads the document the way SVG.NET will draw it, after its stylesheets are applied, and
/// follows every in-file reference from the root: it refuses a reference that reaches back into what is
/// being drawn (a cycle), a drawing nested deeper than <see cref="SvgSanitizer.MaxDepth"/> levels once
/// references are followed (so the renderer's own recursion stays shallow), and a drawing that would
/// draw more than <see cref="MaxWork"/> elements in all. With those three bounds the render always
/// ends, and in about the time its size suggests.
///
/// The costs follow SVG.NET: a gradient draws nothing but its stops, so only its own
/// <c>href</c> counts; a pattern, marker, clip path, mask, filter or <c>&lt;use&gt;</c> target is drawn
/// again for each element painted with it (a marker once per vertex); and fill, stroke and markers are
/// inherited, so a target's content also picks up the ones its ancestors set.
/// </summary>
internal static class SvgReferences
{
    /// <summary>The most elements one drawing of the icon may draw, references included.</summary>
    public const int MaxWork = 20_000;

    // Reference-bearing properties: the inherited ones apply to a container's whole content.
    static readonly string[] Inherited = { "fill", "stroke", "marker-start", "marker-mid", "marker-end" };
    static readonly string[] Direct = { "clip-path", "mask", "filter", "href" };
    static readonly Regex Fragment = new(@"#([^""'()\s]+)", RegexOptions.CultureInvariant);

    /// <summary>Throws <see cref="InvalidIconException"/> when drawing <paramref name="svgText"/> could
    /// recurse, nest too deep, or draw too much, or when SVG.NET can't read it.</summary>
    public static void Check(string svgText)
    {
        TokenSprites.EnsureConfigured();
        SvgDocument document;
        try { document = SvgDocument.FromSvg<SvgDocument>(svgText); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // SVG.NET reports a malformed value with whatever its parsers throw.
            throw new InvalidIconException("its SVG couldn't be read");
        }
        new Walk(document).Run();
    }

    sealed class Walk
    {
        readonly SvgDocument _document;
        readonly Dictionary<string, List<SvgElement>> _byId = new(StringComparer.Ordinal);
        readonly Dictionary<string, (double Work, int Depth)> _done = new(StringComparer.Ordinal);
        readonly HashSet<string> _open = new(StringComparer.Ordinal); // targets being drawn right now

        public Walk(SvgDocument document)
        {
            _document = document;
            foreach (var element in document.Descendants())
                if (!string.IsNullOrEmpty(element.ID))
                {
                    if (!_byId.TryGetValue(element.ID, out var list)) _byId[element.ID] = list = new List<SvgElement>();
                    list.Add(element);
                }
        }

        public void Run() => Draw(_document, 1);

        // Drawing <element> at nesting level <level>: the elements drawn, and how many levels deep that
        // goes from <element> (1 for a leaf), references included. Throws past the limits, so the walk
        // itself never recurses deeper than MaxDepth levels either.
        (double Work, int Depth) Draw(SvgElement element, int level)
        {
            if (level > SvgSanitizer.MaxDepth)
                throw new InvalidIconException($"its parts nest more than {SvgSanitizer.MaxDepth} deep");
            double work = 1;
            int depth = 1;
            void Add((double Work, int Depth) part) { work += part.Work; depth = Math.Max(depth, 1 + part.Depth); }

            if (element is SvgGradientServer)
            {
                Add(Follow(element, "href", level, 1));
                return (Limit(work), depth);
            }
            foreach (string property in Inherited) Add(Follow(element, property, level, Painted(element, property)));
            foreach (string property in Direct) Add(Follow(element, property, level, 1));
            foreach (var child in element.Children) Add(Draw(child, level + 1));
            return (Limit(work), depth);
        }

        // What <element>'s <property> points at, drawn <times> times inside <element> at <level>.
        (double Work, int Depth) Follow(SvgElement element, string property, int level, double times)
        {
            if (!element.TryGetAttribute(property, out string value) || string.IsNullOrEmpty(value)) return (0, 0);
            var target = Fragment.Match(value);
            if (!target.Success) return (0, 0);
            var (work, depth) = Target(target.Groups[1].Value, level + 1);
            if (level + depth > SvgSanitizer.MaxDepth)
                throw new InvalidIconException($"its parts nest more than {SvgSanitizer.MaxDepth} deep");
            return (Limit(work * times), depth);
        }

        // Everything with this id, drawn as a reference target starting at <level>.
        (double Work, int Depth) Target(string id, int level)
        {
            if (_done.TryGetValue(id, out var known)) return known;
            if (!_byId.TryGetValue(id, out var elements)) return (0, 0); // dangling: draws nothing
            if (!_open.Add(id)) throw new InvalidIconException("part of it refers back to itself");

            double work = 0;
            int depth = 0;
            foreach (var element in elements)
            {
                var drawn = Draw(element, level);
                work += drawn.Work;
                depth = Math.Max(depth, drawn.Depth);
                if (element is SvgGradientServer) continue;
                // Its content inherits paint and markers from its own ancestors, wherever it is drawn.
                for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    foreach (string property in Inherited)
                    {
                        var inherited = Follow(ancestor, property, level, Painted(element, property));
                        work += inherited.Work;
                        depth = Math.Max(depth, 1 + inherited.Depth);
                    }
            }
            _open.Remove(id);
            return _done[id] = (Limit(work), depth);
        }

        // How often an inherited property's target is drawn for <element>: once per vertex for a
        // marker, once per element painted with it for a fill or stroke.
        static double Painted(SvgElement element, string property)
            => Math.Max(1, property.StartsWith("marker", StringComparison.Ordinal) ? Vertices(element) : Count(element));

        static double Count(SvgElement element) => 1 + element.Children.Sum(Count);

        static double Vertices(SvgElement element) => element switch
        {
            SvgPath path => path.PathData?.Count ?? 0,
            SvgPolygon poly => (poly.Points?.Count ?? 0) / 2, // SvgPolyline too
            SvgLine => 2,
            _ => element.Children.Sum(Vertices),
        };

        static double Limit(double work)
            => work > MaxWork ? throw new InvalidIconException("it's too complex to draw") : work;
    }
}
