using System.Text;
using Convers.Console;
using Convers.Host.Rhp;

namespace Convers.Host.Sessions;

/// <summary>
/// <see cref="IConverseTerminal"/> over an RHP child connection: Latin-1 both ways, inbound
/// CR/CRLF/LF-tolerant line reads, outbound text sent as-is (the console engine already emits the CR
/// discipline the RF side wants). The <see cref="RemoteCallsign"/> is the node-reported far-station
/// callsign the console auto-logs in (design decisions 3 + 4 — the user never types <c>/name</c>).
/// Mirrors pdn-bbs <c>RhpTerminal</c>, minus the FBB-vs-console first-line gate (a convers leaf treats
/// every inbound connect as a USER — decision 3 — so there is no peek/gate).
/// </summary>
public sealed class RhpTerminal : IConverseTerminal
{
    private readonly RhpChildConnection _child;
    private readonly LineAssembler _assembler = new();
    private readonly Queue<string> _pending = new();

    /// <summary>Wraps <paramref name="child"/>.</summary>
    public RhpTerminal(RhpChildConnection child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = child;
    }

    /// <inheritdoc/>
    public string RemoteCallsign => _child.RemoteCallsign;

    /// <inheritdoc/>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            byte[]? data = await _child.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return null;
            }

            foreach (string line in _assembler.Feed(data))
            {
                _pending.Enqueue(line);
            }
        }

        return _pending.Dequeue();
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            await _child.SendAsync(Encoding.Latin1.GetBytes(text), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The link or stream died under the session — surface it the way the console engine expects
            // (it converts this to ConverseSessionEndReason.Drop).
            throw new ConverseTerminalClosedException("The RHP stream closed.", ex);
        }
    }
}
