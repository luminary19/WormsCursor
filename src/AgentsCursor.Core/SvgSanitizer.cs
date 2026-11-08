using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentsCursor.Core;

/// <summary>An SVG that can't be used as a token icon. <see cref="Exception.Message"/> is the reason,
/// worded to finish the sentence "That file couldn't be used: …".</summary>
public sealed class InvalidIconException(string reason) : Exception(reason);

/// <summary>
/// Turns an untrusted SVG file into one that is safe to keep and draw: nothing in it can run, and
/// nothing in it can make the renderer reach outside the file. The output (not the original) is what
/// <see cref="CustomIconStore"/> keeps, and <see cref="IconSource"/> runs it again on every load.
///
/// Rejected outright (throws <see cref="InvalidIconException"/>): a file over 256 KB, anything that
/// isn't well-formed XML, a DTD (so no entities), a root that isn't an SVG <c>&lt;svg&gt;</c>, more than
/// 5,000 elements or nesting deeper than 64, a stylesheet <c>@import</c>, and a CSS <c>url(…)</c> that
/// points anywhere but inside the file. Removed: scripts and the elements that embed other content
/// (<c>script</c>, <c>foreignObject</c>, <c>iframe</c>, <c>audio</c>, <c>video</c>), every <c>on…</c>
/// event attribute, and every link except an in-file <c>#fragment</c> or, on an <c>&lt;image&gt;</c>, an
/// inline PNG / JPEG / GIF / WebP <c>data:</c> image.
/// </summary>
public static class SvgSanitizer
{
    /// <summary>The largest file accepted, in bytes (UTF-8).</summary>
    public const int MaxBytes = 256 * 1024;
    /// <summary>The most elements accepted.</summary>
    public const int MaxElements = 5000;
    /// <summary>The deepest element nesting accepted.</summary>
    public const int MaxDepth = 64;

    static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase)
        { "script", "foreignObject", "iframe", "audio", "video" };

    static readonly Regex DataImage = new(@"^\s*data:image/(png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // url( … ) with optional quotes; group 1 is the target.
    static readonly Regex CssUrl = new(@"url\(\s*['""]?\s*([^'"")]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex CssImport = new(@"@import", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The sanitised SVG markup for <paramref name="text"/>; throws
    /// <see cref="InvalidIconException"/> with the reason when it can't be used.</summary>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new InvalidIconException("it's larger than 256 KB");

        var root = Parse(text).Root ?? throw new InvalidIconException("it has no content");
        if (root.Name != Svg + "svg")
            throw new InvalidIconException("it isn't an SVG image (its root element must be <svg> in the SVG namespace)");
        CheckSize(root);

        foreach (var element in root.DescendantsAndSelf().ToArray())
        {
            if (element.Document is null) continue; // inside an element already removed
            if (element.Parent is not null && Dropped.Contains(element.Name.LocalName))
            {
                element.Remove();
                continue;
            }
            CleanAttributes(element);
            if (element.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                CheckCss(element.Value);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    static XDocument Parse(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidIconException("it declares a DTD, which isn't allowed");
        }
        catch (XmlException ex)
        {
            throw new InvalidIconException($"it isn't valid XML ({ex.Message.TrimEnd('.')})");
        }
    }

    static void CheckSize(XElement root)
    {
        int count = 0;
        var stack = new Stack<(XElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (++count > MaxElements) throw new InvalidIconException($"it has more than {MaxElements:N0} elements");
            if (depth > MaxDepth) throw new InvalidIconException($"its elements are nested more than {MaxDepth} deep");
            foreach (var child in element.Elements()) stack.Push((child, depth + 1));
        }
    }

    static void CleanAttributes(XElement element)
    {
        bool image = element.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes().ToArray())
        {
            string name = attribute.Name.LocalName;
            if (attribute.IsNamespaceDeclaration) continue;
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                attribute.Remove();
                continue;
            }
            bool link = name == "href" && (attribute.Name.Namespace == XNamespace.None || attribute.Name.Namespace == XLink);
            if (link)
            {
                string target = attribute.Value.Trim();
                bool keep = target.StartsWith('#') || (image && DataImage.IsMatch(target));
                if (!keep) attribute.Remove();
                continue;
            }
            if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)) CheckCss(attribute.Value);
        }
    }

    // Style text, a style attribute or a presentation attribute: no @import, and url() only in-file.
    static void CheckCss(string css)
    {
        if (CssImport.IsMatch(css)) throw new InvalidIconException("its stylesheet imports another file");
        foreach (Match m in CssUrl.Matches(css))
            if (!m.Groups[1].Value.TrimStart().StartsWith('#'))
                throw new InvalidIconException("it refers to an outside file");
    }
}
