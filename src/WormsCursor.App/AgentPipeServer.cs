using System.IO.Pipes;
using System.Threading;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Hosts the named pipe that <c>WormsCursor.exe hook …</c> invocations write agent events to.
/// One background accept-loop thread handles one short-lived client at a time; each client streams
/// newline-delimited JSON (<see cref="AgentEventMessage"/>) which we hand to the callback.
///
/// The tray owns it directly (not the overlay), so it keeps listening across settings changes and
/// overlay enable/disable toggles. The pipe is per-user
/// (its name carries the user name) and inherits the default ACL (same-user access only), which is
/// exactly what we want: the bridge runs as the same user in the same session.
/// </summary>
public sealed class AgentPipeServer : IDisposable
{
    public static readonly string PipeName = "WormsCursor." + Environment.UserName;

    readonly Action<AgentEventMessage> _onMessage;
    Thread? _thread;
    volatile bool _stopping;

    public AgentPipeServer(Action<AgentEventMessage> onMessage) => _onMessage = onMessage;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "WormsCursor.AgentPipe" };
        _thread.Start();
    }

    void AcceptLoop()
    {
        while (!_stopping)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                if (_stopping) break;

                using var reader = new StreamReader(server);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var msg = AgentEventMessage.FromJson(line);
                    if (msg is null) continue;
                    try { _onMessage(msg); }
                    catch { /* a handler hiccup must never kill the accept loop */ }
                }
            }
            catch when (!_stopping)
            {
                // Broken/aborted client connection — re-listen after a short breather.
                Thread.Sleep(50);
            }
            catch { /* stopping: fall out of the loop */ }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        // Unblock a parked WaitForConnection by poking the pipe with a throwaway client.
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(200);
        }
        catch { /* nothing parked / already gone */ }
        try { _thread?.Join(TimeSpan.FromSeconds(1)); } catch { /* best effort */ }
    }
}
