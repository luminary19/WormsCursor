using System.IO.Pipes;
using System.Text;
using System.Threading;
using AgentsCursor.Core;

namespace AgentsCursor.App;

/// <summary>
/// Hosts the named pipe agents report to. It serves many connections at once, of two kinds:
/// <list type="bullet">
/// <item>the throwaway <c>AgentsCursor.exe hook …</c> bridge (Claude Code, Codex): one line, then it
/// exits, so its disconnect means nothing;</item>
/// <item>the Pi and OpenCode plugins: one connection for their host's lifetime, opened with an
/// <see cref="AgentEventMessage.AttachEvent"/> line. That makes the connection a <b>lease</b>: every
/// later message carries its id, and when it closes (EOF, error, an oversized line, or Dispose)
/// <c>onLeaseReleased</c> fires after its last message, so the tray drops exactly that host's
/// sessions.</item>
/// </list>
/// A listening instance always exists while connections are being served, so a persistent plugin
/// connection never delays a hook. Each connection streams newline-delimited JSON
/// (<see cref="AgentEventMessage"/>); a malformed line is skipped, and a line over
/// <see cref="MaxLineBytes"/> drops that connection.
///
/// The tray owns it directly (not the overlay), so it keeps listening across settings changes. The
/// pipe is per-user (its name carries the user name) and <see cref="PipeOptions.CurrentUserOnly"/>, so
/// only this user's processes can connect.
/// </summary>
public sealed class AgentPipeServer : IDisposable
{
    /// <summary>The tray's pipe: <c>AgentsCursor.&lt;user name&gt;</c>. The hook bridge and the plugins
    /// derive the same name.</summary>
    public static string DefaultPipeName { get; } = "AgentsCursor." + Environment.UserName;

    /// <summary>At most this many connections are served at once; past that, accepting waits for one
    /// to close.</summary>
    public const int MaxConnections = 64;

    /// <summary>The longest line accepted (UTF-8 bytes, without the newline).</summary>
    public const int MaxLineBytes = 64 * 1024;

    readonly Action<AgentEventMessage, long?> _onMessage;
    readonly Action<long> _onLeaseReleased;
    readonly string _pipeName;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    readonly object _gate = new();
    readonly HashSet<NamedPipeServerStream> _live = new();
    Task? _acceptLoop;
    long _nextConnection;

    /// <param name="onMessage">Each message, with the lease id of the connection it came over (null for
    /// a connection that never attached, i.e. a hook). Called on a pipe thread, in order per connection.</param>
    /// <param name="onLeaseReleased">A leased connection closed. Called after its last message, on the
    /// same pipe thread.</param>
    /// <param name="pipeName">The pipe to serve; tests pass a private name.</param>
    public AgentPipeServer(Action<AgentEventMessage, long?> onMessage, Action<long> onLeaseReleased, string? pipeName = null)
    {
        _onMessage = onMessage;
        _onLeaseReleased = onLeaseReleased;
        _pipeName = pipeName ?? DefaultPipeName;
    }

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            bool holdsSlot = false;
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                holdsSlot = true;
                // Duplex although nothing is ever written back: Bun, which runs OpenCode's plugins,
                // can't write to an inbound-only pipe (Node, which runs Pi's, can).
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (!Track(server)) break; // stopping
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                long id = Interlocked.Increment(ref _nextConnection);
                var connection = server;
                server = null;      // ServeAsync owns the stream and the slot now
                holdsSlot = false;
                _ = ServeAsync(connection, id);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A client that vanished mid-handshake, or the name briefly unavailable: re-listen
                // after a short breather.
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (server is not null) { Untrack(server); server.Dispose(); }
                if (holdsSlot) _slots.Release();
            }
        }
    }

    async Task ServeAsync(NamedPipeServerStream stream, long id)
    {
        bool leased = false;
        try
        {
            bool first = true;
            await foreach (string line in ReadLinesAsync(stream, _stop.Token).ConfigureAwait(false))
            {
                var msg = AgentEventMessage.FromJson(line);
                bool isFirst = first;
                first = false;
                if (msg is null) continue;
                if (msg.Event == AgentEventMessage.AttachEvent)
                {
                    leased |= isFirst; // a connection-level line: only the first line opens a lease
                    continue;
                }
                Deliver(() => _onMessage(msg, leased ? id : null));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                      or InvalidDataException)
        {
            // The client went away, the server is stopping, or the line was too long: the connection ends.
        }
        finally
        {
            Untrack(stream);
            stream.Dispose();
            _slots.Release();
            if (leased) Deliver(() => _onLeaseReleased(id));
        }
    }

    // A handler hiccup must never take a connection (or the server) down.
    static void Deliver(Action callback)
    {
        try { callback(); }
        catch { /* the tray logs its own failures; the pipe keeps serving */ }
    }

    // Splits the stream into UTF-8 lines (a trailing '\r' dropped, blank lines skipped). A line longer
    // than MaxLineBytes throws InvalidDataException, which ends the connection.
    static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[4096];
        var line = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                if (TakeLine(line) is { } text) yield return text;
            }
            line.Write(buffer, start, read - start);
            if (line.Length > MaxLineBytes) throw new InvalidDataException("line too long");
        }
        if (TakeLine(line) is { } last) yield return last; // a final line without a newline
    }

    static string? TakeLine(MemoryStream line)
    {
        if (line.Length > MaxLineBytes) throw new InvalidDataException("line too long");
        string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
        line.SetLength(0);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    bool Track(NamedPipeServerStream stream)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return false;
            _live.Add(stream);
            return true;
        }
    }

    void Untrack(NamedPipeServerStream stream)
    {
        lock (_gate) _live.Remove(stream);
    }

    /// <summary>Stops accepting, closes every live connection (each leased one reports its release), and
    /// waits up to a second for the accept loop to finish.</summary>
    public void Dispose()
    {
        NamedPipeServerStream[] live;
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            live = _live.ToArray();
        }
        foreach (var stream in live)
        {
            try { stream.Dispose(); } catch { /* already closing */ }
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { /* best effort */ }
    }
}
