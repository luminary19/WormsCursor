using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AgentsCursor.Core;

/// <summary>
/// The local history behind the Analytics page: every ended wait (<see cref="WaitRecord"/>), one
/// compact JSON line each, in a file per month (<c>yyyy-MM.jsonl</c>, by the wait's UTC start) under
/// <c>%LocalAppData%\AgentsCursor\history</c>:
/// <code>{"tool":"codex","kind":"Blocked","start":"2026-11-08T14:03:11.123Z","end":"2026-11-08T14:03:53.456Z","outcome":"Answered"}</code>
/// Nothing else is stored: no session id, prompt, project or path.
///
/// <see cref="Record"/> only queues (it never blocks the caller, which is the UI thread fed by the
/// pipe); one background task appends. Appending is safe against a torn line: a line cut off by a
/// crash is ended before the next is written, and <see cref="Load"/> reads each line on its own, so
/// a bad line costs only itself. Retention: the current month and the <see cref="RetainedMonths"/>
/// before it (about a year); older files are deleted by the writer and ignored by the reader.
/// </summary>
public sealed class WaitHistory : IDisposable
{
    /// <summary>How many whole months before the current one are kept.</summary>
    public const int RetainedMonths = 12;

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsCursor", "history");

    static readonly Regex MonthFile = new(@"^(\d{4})-(\d{2})\.jsonl$", RegexOptions.CultureInvariant);
    const int MaxToolLength = 64;

    readonly string _directory;
    readonly Func<DateTime> _utcNow;
    readonly Action<string>? _log;
    readonly Channel<WaitRecord> _queue = Channel.CreateUnbounded<WaitRecord>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _writer;
    readonly HashSet<string> _logged = new(StringComparer.Ordinal); // one log line per problem
    int _pending;          // queued, not yet written (or given up on)
    (int Year, int Month)? _prunedFor;

    /// <param name="directory">Where the month files live (tests pass a temp folder).</param>
    /// <param name="utcNow">The clock retention is measured from (injected for tests).</param>
    /// <param name="log">Where a failure to read or write is reported, once per problem.</param>
    public WaitHistory(string directory, Func<DateTime>? utcNow = null, Action<string>? log = null)
    {
        _directory = directory;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
        _writer = Task.Run(WriteLoop);
    }

    /// <summary>Raised on the writer's thread after records were appended.</summary>
    public event Action? Recorded;

    /// <summary>Queues <paramref name="record"/> to be appended. Never blocks; safe from any thread.</summary>
    public void Record(WaitRecord record)
    {
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(record)) Interlocked.Decrement(ref _pending); // disposed
    }

    /// <summary>Waits up to <paramref name="timeout"/> for everything queued so far to be written.
    /// Returns whether it was.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Stops accepting records and gives the writer up to a second to finish the queue.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { /* the loop logs its own failures */ }
    }

    async Task WriteLoop()
    {
        Prune();
        var batch = new List<WaitRecord>(); // what is left to write: a month that couldn't be appended stays
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var record)) batch.Add(record);
            Write(batch);
        }
        if (batch.Count == 0) return;
        Write(batch); // stopping: one last try for what couldn't be written before
        if (batch.Count == 0) return;
        LogOnce("lost", $"gave up on {batch.Count} wait(s) the history couldn't write");
        Interlocked.Add(ref _pending, -batch.Count);
    }

    // Appends the batch, month by month, and leaves in it only the records that couldn't be written
    // (a file held by another program, say), to try again on the next wake or when stopping.
    void Write(List<WaitRecord> batch)
    {
        Prune();
        var failed = new List<WaitRecord>();
        foreach (var month in batch.GroupBy(r => FileName(r.StartUtc)))
            if (!Append(month.Key, month)) failed.AddRange(month);
        int written = batch.Count - failed.Count;
        if (written > 0) Recorded?.Invoke();
        Interlocked.Add(ref _pending, -written); // after Recorded: Flush covers its handlers too
        batch.Clear();
        batch.AddRange(failed);
    }

    bool Append(string fileName, IEnumerable<WaitRecord> records)
    {
        string path = Path.Combine(_directory, fileName);
        try
        {
            Directory.CreateDirectory(_directory);
            var text = new StringBuilder();
            foreach (var record in records) text.Append(Format(record)).Append('\n');
            using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (file.Length > 0)
            {
                // A line cut off by a crash: end it, so it stays one bad line and doesn't swallow ours.
                file.Seek(-1, SeekOrigin.End);
                if (file.ReadByte() != '\n') text.Insert(0, '\n');
            }
            file.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            file.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOnce("write:" + fileName, $"couldn't add to the history file {fileName} (will try again): {ex.Message}");
            return false;
        }
    }

    // Deletes month files older than the retention, once per month this process sees.
    void Prune()
    {
        var now = _utcNow();
        var current = (now.Year, now.Month);
        if (_prunedFor == current) return;
        _prunedFor = current;
        foreach (var (path, year, month) in MonthFiles())
        {
            if (Retained(year, month, now)) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce("prune:" + path, $"couldn't delete the old history file {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    static bool Retained(int year, int month, DateTime nowUtc)
        => (nowUtc.Year * 12 + nowUtc.Month) - (year * 12 + month) <= RetainedMonths;

    IEnumerable<(string Path, int Year, int Month)> MonthFiles()
    {
        string[] files;
        try { files = Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.jsonl") : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOnce("list", "couldn't list the history folder: " + ex.Message);
            yield break;
        }
        foreach (var path in files)
        {
            var match = MonthFile.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            int year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12) yield return (path, year, month);
        }
    }

    /// <summary>Every retained record, oldest first. Lines that don't read as a record are skipped
    /// (and logged once per file); everything else in the file still loads. Safe from any thread,
    /// also while the writer appends (a half-written last line is just skipped).</summary>
    public IReadOnlyList<WaitRecord> Load()
    {
        var now = _utcNow();
        var records = new List<WaitRecord>();
        foreach (var (path, year, month) in MonthFiles())
        {
            if (!Retained(year, month, now)) continue;
            string text;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce("read:" + path, $"couldn't read the history file {Path.GetFileName(path)}: {ex.Message}");
                continue;
            }
            int bad = 0;
            foreach (var line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryParse(line, out var record)) records.Add(record);
                else bad++;
            }
            if (bad > 0) LogOnce($"bad:{path}:{bad}", $"skipped {bad} unreadable line(s) in the history file {Path.GetFileName(path)}");
        }
        records.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return records;
    }

    static string FileName(DateTime startUtc) => startUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>One record as its history line (without the newline).</summary>
    public static string Format(WaitRecord record)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("tool", record.Tool);
            json.WriteString("kind", record.Kind.ToString());
            json.WriteString("start", Stamp(record.StartUtc));
            json.WriteString("end", Stamp(record.EndUtc));
            json.WriteString("outcome", record.Outcome.ToString());
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Reads one history line; false for anything that isn't a whole, sensible record.</summary>
    public static bool TryParse(string line, out WaitRecord record)
    {
        record = default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!Text(root, "tool", out var tool) || tool.Length is 0 or > MaxToolLength) return false;
            if (!Text(root, "kind", out var kindText) || !Enum.TryParse<WaitingState>(kindText, false, out var kind) || !Enum.IsDefined(kind)) return false;
            if (!Text(root, "outcome", out var outcomeText) || !Enum.TryParse<WaitEnd>(outcomeText, false, out var outcome) || !Enum.IsDefined(outcome)) return false;
            if (!Time(root, "start", out var start) || !Time(root, "end", out var end)) return false;
            // An end before the start means the clock stepped back during the wait: it still happened.
            record = new WaitRecord(tool, kind, start, end < start ? start : end, outcome);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool Text(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return true;
    }

    static bool Time(JsonElement root, string name, out DateTime utc)
    {
        utc = default;
        return Text(root, name, out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
    }

    void LogOnce(string key, string message)
    {
        lock (_logged)
            if (!_logged.Add(key)) return;
        _log?.Invoke(message);
    }
}
