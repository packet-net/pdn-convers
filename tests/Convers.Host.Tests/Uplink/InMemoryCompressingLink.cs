using System.Threading.Channels;
using Convers.Host.Uplink;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// A pair of in-memory <see cref="IUpstreamLink"/> endpoints wired back-to-back over a byte pipe, each
/// backed by the production <see cref="CompressingLineTransport"/>. Unlike <see cref="ScriptedUpstreamLink"/>
/// (which moves whole lines and cannot exercise compression), this carries <em>bytes</em>, so a full
/// <c>HostLink</c>/<c>DownstreamPeerSession</c> on one end negotiates <c>//COMP</c> and round-trips real
/// Huffman frames against the fake peer on the other — the scripted-peer proof of the compressed host-link
/// exchange (the stock oracle does not negotiate <c>//COMP</c> on a HOST link).
/// </summary>
internal static class InMemoryCompressingLink
{
    /// <summary>One direction's byte channel.</summary>
    private sealed class Pipe
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            _channel.Writer.TryWrite(bytes.ToArray());
            return Task.CompletedTask;
        }

        public async Task<byte[]?> ReadAsync(CancellationToken ct)
        {
            try
            {
                return await _channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void Close() => _channel.Writer.TryComplete();
    }

    /// <summary>An <see cref="IUpstreamLink"/> over one end of the pipe pair.</summary>
    private sealed class Endpoint(CompressingLineTransport transport, Pipe rx, Pipe tx) : IUpstreamLink
    {
        public Task SendLineAsync(string line, CancellationToken ct) => transport.SendLineAsync(line, ct);

        public Task<string?> ReceiveLineAsync(CancellationToken ct) => transport.ReceiveLineAsync(ct);

        public Task OfferCompressionAsync(CancellationToken ct) => transport.OfferCompressionAsync(ct);

        public bool CompressionEngaged => transport.CompressionEngaged;

        public ValueTask DisposeAsync()
        {
            rx.Close();
            tx.Close();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Creates two connected endpoints. <paramref name="ourBuffered"/> are lines already peeked for the
    /// "our" side (a downstream peer session's already-read <c>/..HOST</c>), delivered before fresh reads.
    /// </summary>
    public static (IUpstreamLink Ours, IUpstreamLink Peer) CreatePair(IEnumerable<string>? ourBuffered = null)
    {
        var aToB = new Pipe(); // ours → peer
        var bToA = new Pipe(); // peer → ours
        var ours = new CompressingLineTransport(aToB.WriteAsync, bToA.ReadAsync, '\r', ourBuffered);
        var peer = new CompressingLineTransport(bToA.WriteAsync, aToB.ReadAsync, '\r');
        return (new Endpoint(ours, aToB, bToA), new Endpoint(peer, bToA, aToB));
    }
}
