using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentsCursor.App.Services;

/// <summary>
/// Comment-preserving edits of a JSONC config's top-level <c>plugins</c> array (OpenCode's
/// <c>opencode.jsonc</c>). The file is never re-serialised: the edits splice only the text of one
/// array element (or of the <c>plugins</c> property itself), so comments, trailing commas, formatting
/// and line endings everywhere else stay byte-identical. Offsets are UTF-8 byte offsets from
/// <see cref="Utf8JsonReader"/> (which reports comments as tokens); the bytes are decoded once at the
/// end of each edit.
/// </summary>
internal static class JsoncEdit
{
    /// <summary>One element of the <c>plugins</c> array: its byte span, its plugin specifier (the
    /// string itself, or an object's <c>package</c>), and whether it is a bare string.</summary>
    public sealed record Element(int Start, int End, string? Specifier, bool IsString);

    /// <summary>A parsed config: where its root object and its <c>plugins</c> property are.</summary>
    public sealed class Document
    {
        public required byte[] Bytes { get; init; }
        public int RootOpen { get; init; }
        public int RootClose { get; init; }
        /// <summary>The first top-level property's key, or -1 for an empty object.</summary>
        public int FirstKey { get; init; } = -1;
        /// <summary>The <c>plugins</c> key, its <c>[</c> and <c>]</c>; -1 when the key is missing.</summary>
        public int PluginsKey { get; init; } = -1;
        public int Open { get; init; } = -1;
        public int Close { get; init; } = -1;
        /// <summary>Where the value of the property before <c>plugins</c> ends; -1 when it is the first.</summary>
        public int PreviousValueEnd { get; init; } = -1;
        public IReadOnlyList<Element> Elements { get; init; } = Array.Empty<Element>();
        public bool HasPlugins => PluginsKey >= 0;
    }

    static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Parses <paramref name="text"/>; throws <see cref="InvalidOperationException"/> when it isn't
    /// a JSONC object, or its <c>plugins</c> isn't an array (it then can't be edited safely).</summary>
    public static Document Parse(string text, string file)
    {
        try
        {
            using var check = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (check.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{file} must hold a JSON object; it wasn't changed.");
            // The edits splice the one plugins property, so there must be at most one, and it a list.
            var plugins = check.RootElement.EnumerateObject().Where(p => p.NameEquals("plugins")).ToList();
            if (plugins.Count > 1)
                throw new InvalidOperationException($"{file} lists plugins more than once; it wasn't changed.");
            if (plugins.Count == 1 && plugins[0].Value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"{file}: plugins must be a list; it wasn't changed.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{file} isn't valid JSONC — fix or remove it, then retry.");
        }
        return Locate(Encoding.UTF8.GetBytes(text));
    }

    static Document Locate(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true,
        });
        int rootOpen = -1, rootClose = -1, firstKey = -1, pluginsKey = -1, open = -1, close = -1;
        int previousValueEnd = -1, lastValueEnd = -1;
        var elements = new List<Element>();
        while (Next(ref reader))
        {
            if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 0) { rootOpen = (int)reader.TokenStartIndex; continue; }
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) { rootClose = (int)reader.TokenStartIndex; break; }
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;

            int key = (int)reader.TokenStartIndex;
            if (firstKey < 0) firstKey = key;
            bool isPlugins = pluginsKey < 0 && reader.ValueTextEquals("plugins");
            Next(ref reader);
            if (isPlugins)
            {
                pluginsKey = key;
                previousValueEnd = lastValueEnd;
                open = (int)reader.TokenStartIndex;
                while (Next(ref reader) && reader.TokenType != JsonTokenType.EndArray)
                    elements.Add(ReadElement(ref reader));
                close = (int)reader.TokenStartIndex;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                SkipValue(ref reader);
            lastValueEnd = (int)reader.BytesConsumed;
        }
        return new Document
        {
            Bytes = bytes, RootOpen = rootOpen, RootClose = rootClose, FirstKey = firstKey,
            PluginsKey = pluginsKey, Open = open, Close = close, PreviousValueEnd = previousValueEnd,
            Elements = elements,
        };
    }

    // The next token that isn't a comment.
    static bool Next(ref Utf8JsonReader reader)
    {
        while (reader.Read())
            if (reader.TokenType != JsonTokenType.Comment) return true;
        return false;
    }

    // Reads past the object or array the reader is on, comments included.
    static void SkipValue(ref Utf8JsonReader reader)
    {
        int depth = reader.CurrentDepth;
        while (reader.Read())
            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray && reader.CurrentDepth == depth) return;
    }

    static Element ReadElement(ref Utf8JsonReader reader)
    {
        int start = (int)reader.TokenStartIndex;
        string? specifier = null;
        bool isString = reader.TokenType == JsonTokenType.String;
        if (isString) specifier = reader.GetString();
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            int depth = reader.CurrentDepth;
            while (Next(ref reader) && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth))
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != depth + 1) continue;
                bool isPackage = reader.ValueTextEquals("package");
                Next(ref reader);
                if (isPackage && reader.TokenType == JsonTokenType.String) specifier ??= reader.GetString();
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) SkipValue(ref reader);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray) SkipValue(ref reader);
        return new Element(start, (int)reader.BytesConsumed, specifier, isString);
    }

    // ---------- edits ----------

    /// <summary>Replaces <paramref name="element"/> with the string <paramref name="value"/>.</summary>
    public static string Replace(Document doc, Element element, string value)
        => Splice(doc.Bytes, (element.Start, element.End - element.Start, Quote(value)));

    /// <summary>Adds the string <paramref name="value"/> as the last element of <c>plugins</c>, creating
    /// the property when it is missing. Follows the array's own layout: an inline array gets
    /// <c>, "value"</c>; a multi-line one gets a new line indented like its elements (keeping a trailing
    /// comma style).</summary>
    public static string Append(Document doc, string value)
    {
        var b = doc.Bytes;
        string eol = Eol(b);
        if (!doc.HasPlugins) return InsertProperty(doc, value, eol);

        string unit = IndentUnit(doc);
        if (doc.Elements.Count == 0)
        {
            bool blank = IsBlank(b, doc.Open + 1, doc.Close);
            if (blank && !Contains(b, doc.Open + 1, doc.Close, (byte)'\n'))
                return Splice(b, (doc.Open + 1, doc.Close - doc.Open - 1, Quote(value)));
            string inner = (IndentAt(b, doc.PluginsKey) ?? "") + unit;
            return blank
                ? Splice(b, (doc.Open + 1, doc.Close - doc.Open - 1,
                    eol + inner + Quote(value) + eol + (IndentAt(b, doc.Close) ?? "")))
                : Splice(b, (doc.Open + 1, 0, eol + inner + Quote(value)));
        }

        var last = doc.Elements[^1];
        if (!Contains(b, doc.Open, doc.Close, (byte)'\n'))
            return Splice(b, (last.End, 0, ", " + Quote(value)));

        string indent = IndentAt(b, last.Start) ?? (IndentAt(b, doc.PluginsKey) ?? "") + unit;
        int next = NextSignificant(b, last.End);
        bool trailingComma = next < b.Length && b[next] == (byte)',';
        int after = trailingComma ? next + 1 : last.End;
        // The new element goes on its own line after the last one's line (any comment ending that line
        // stays with the last element); a comma is added after the last element if it had none.
        int lineEnd = LineEnd(b, after);
        string rest = Encoding.UTF8.GetString(b, after, lineEnd - after).Trim();
        int insertAt = rest.Length == 0 || rest.StartsWith("//", StringComparison.Ordinal) ? lineEnd : after;
        string added = eol + indent + Quote(value) + (trailingComma ? "," : "");
        return trailingComma
            ? Splice(b, (insertAt, 0, added))
            : Splice(b, (last.End, 0, ","), (insertAt, 0, added)); // same position: the comma first
    }

    /// <summary>Removes <paramref name="element"/> with one adjacent comma (its whole line when it is
    /// alone on it). Removing the only element removes the <c>plugins</c> property too.</summary>
    public static string Remove(Document doc, Element element)
    {
        var b = doc.Bytes;
        int index = IndexOf(doc, element);
        if (doc.Elements.Count == 1) return RemoveProperty(doc);
        int previousEnd = index > 0 ? doc.Elements[index - 1].End : doc.Open + 1;
        return RemoveSpan(b, element.Start, element.End, previousEnd);
    }

    static string RemoveProperty(Document doc)
        => RemoveSpan(doc.Bytes, doc.PluginsKey, doc.Close + 1, doc.PreviousValueEnd >= 0 ? doc.PreviousValueEnd : doc.RootOpen + 1);

    // Removes [start, end) — a list item — with one adjacent comma. Alone on its line(s), the whole
    // lines go; otherwise just the item, its comma and the spaces after that comma. The comma removed
    // is the one after the item, or else the one before it (searched from previousEnd), so the list
    // stays valid.
    static string RemoveSpan(byte[] b, int start, int end, int previousEnd)
    {
        int next = NextSignificant(b, end);
        bool followingComma = next < b.Length && b[next] == (byte)',';
        int afterItem = followingComma ? next + 1 : end;
        int comma = followingComma ? -1 : FindComma(b, previousEnd, start);

        int lineEnd = LineEnd(b, afterItem);
        if (IndentAt(b, start) is not null && IsBlank(b, afterItem, lineEnd))
        {
            int from = LineStart(b, start), to = NextLineStart(b, lineEnd);
            return comma >= 0 ? Splice(b, (from, to - from, ""), (comma, 1, "")) : Splice(b, (from, to - from, ""));
        }
        if (followingComma)
        {
            int to = afterItem;
            while (to < b.Length && b[to] is (byte)' ' or (byte)'\t') to++;
            return Splice(b, (start, to - start, ""));
        }
        return comma >= 0 ? Splice(b, (comma, end - comma, "")) : Splice(b, (start, end - start, ""));
    }

    static string InsertProperty(Document doc, string value, string eol)
    {
        var b = doc.Bytes;
        string property = "\"plugins\": [" + Quote(value) + "]";
        if (doc.FirstKey >= 0)
        {
            // On its own line right after the brace when the members are one per line (any comment
            // above the first member stays with it), else inline just before the first member.
            string? indent = IndentAt(b, doc.FirstKey);
            return indent is null
                ? Splice(b, (doc.FirstKey, 0, property + ", "))
                : Splice(b, (doc.RootOpen + 1, 0, eol + indent + property + ","));
        }
        return IsBlank(b, doc.RootOpen + 1, doc.RootClose)
            ? Splice(b, (doc.RootOpen + 1, doc.RootClose - doc.RootOpen - 1, eol + "  " + property + eol))
            : Splice(b, (doc.RootOpen + 1, 0, eol + "  " + property));
    }

    // ---------- text helpers (byte offsets; the characters looked at are all ASCII) ----------

    static string Quote(string value) => JsonSerializer.Serialize(value, StringOptions);

    static int IndexOf(Document doc, Element element)
    {
        for (int i = 0; i < doc.Elements.Count; i++)
            if (doc.Elements[i].Start == element.Start) return i;
        throw new ArgumentException("The element isn't in this document.", nameof(element));
    }

    // Applies non-overlapping edits (position, bytes removed, text inserted) and decodes once.
    static string Splice(byte[] b, params (int At, int Remove, string Insert)[] edits)
    {
        var output = new List<byte>(b.Length + 64);
        int cursor = 0;
        foreach (var (at, remove, insert) in edits.OrderBy(e => e.At))
        {
            output.AddRange(b.AsSpan(cursor, at - cursor).ToArray());
            output.AddRange(Encoding.UTF8.GetBytes(insert));
            cursor = at + remove;
        }
        output.AddRange(b.AsSpan(cursor).ToArray());
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static string Eol(byte[] b)
    {
        int lf = Array.IndexOf(b, (byte)'\n');
        return lf > 0 && b[lf - 1] == (byte)'\r' ? "\r\n" : "\n";
    }

    // The indentation step: how much deeper the elements sit than the plugins key (two spaces when
    // there is nothing to measure).
    static string IndentUnit(Document doc)
    {
        string? key = IndentAt(doc.Bytes, doc.PluginsKey);
        string? item = doc.Elements.Count > 0 ? IndentAt(doc.Bytes, doc.Elements[0].Start) : null;
        return key is not null && item is not null && item.Length > key.Length && item.StartsWith(key, StringComparison.Ordinal)
            ? item[key.Length..]
            : "  ";
    }

    static int LineStart(byte[] b, int pos)
    {
        int i = pos;
        while (i > 0 && b[i - 1] != (byte)'\n') i--;
        return i;
    }

    // The end of the line containing pos, before its "\r\n" or "\n" (or the end of the text).
    static int LineEnd(byte[] b, int pos)
    {
        int i = pos;
        while (i < b.Length && b[i] != (byte)'\n') i++;
        return i > pos && b[i - 1] == (byte)'\r' ? i - 1 : i;
    }

    static int NextLineStart(byte[] b, int lineEnd)
    {
        int i = lineEnd;
        if (i < b.Length && b[i] == (byte)'\r') i++;
        if (i < b.Length && b[i] == (byte)'\n') i++;
        return i;
    }

    // The whitespace before pos on its line, or null when something else precedes it there.
    static string? IndentAt(byte[] b, int pos)
    {
        if (pos < 0) return null;
        int start = LineStart(b, pos);
        for (int i = start; i < pos; i++)
            if (b[i] is not ((byte)' ' or (byte)'\t')) return null;
        return Encoding.UTF8.GetString(b, start, pos - start);
    }

    static bool IsBlank(byte[] b, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (b[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        return true;
    }

    static bool Contains(byte[] b, int from, int to, byte value) => Array.IndexOf(b, value, from, to - from) >= 0;

    // The next byte at or after pos that isn't whitespace or inside a comment.
    static int NextSignificant(byte[] b, int pos)
    {
        int i = pos;
        while (i < b.Length)
        {
            if (b[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { i++; continue; }
            if (b[i] == (byte)'/' && i + 1 < b.Length && b[i + 1] == (byte)'/')
            {
                while (i < b.Length && b[i] != (byte)'\n') i++;
                continue;
            }
            if (b[i] == (byte)'/' && i + 1 < b.Length && b[i + 1] == (byte)'*')
            {
                int close = IndexOf(b, "*/", i + 2);
                i = close < 0 ? b.Length : close + 2;
                continue;
            }
            return i;
        }
        return b.Length;
    }

    // The first comma in [from, to) that isn't inside a comment; -1 when there is none.
    static int FindComma(byte[] b, int from, int to)
    {
        int i = from;
        while (i < to)
        {
            i = NextSignificant(b, i);
            if (i >= to) return -1;
            if (b[i] == (byte)',') return i;
            i++;
        }
        return -1;
    }

    static int IndexOf(byte[] b, string text, int from)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        return b.AsSpan(from).IndexOf(needle) is var at && at >= 0 ? from + at : -1;
    }
}
