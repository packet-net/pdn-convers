using System.Threading.Channels;
using Convers.Host.Uplink;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// End-to-end tests for the shared <see cref="CompressingLineTransport"/> over an in-memory byte duplex —
/// the scripted/fake-peer proof of the negotiate → compress → decompress path (the stock conversd oracle
/// does not negotiate <c>//COMP</c> on a HOST peer link, so the real-oracle proof runs on a USER link; see
/// <c>HostLinkCompressionOracleInteropTests</c>). Two transports are wired back-to-back so one's writes are
/// the other's reads; the tests prove the lines round-trip both before negotiation (uncompressed) and after
/// (compressed on the wire, transparent to the caller).
/// </summary>
public class CompressingLineTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>An in-memory simplex byte pipe: one side writes chunks, the other reads them in order.</summary>
    private sealed class BytePipe
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
                return await _channel.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void Close() => _channel.Writer.TryComplete();
    }

    /// <summary>Builds two transports whose writes feed each other (a→b and b→a).</summary>
    private static (CompressingLineTransport A, CompressingLineTransport B, BytePipe AtoB, BytePipe BtoA) Pair()
    {
        var aToB = new BytePipe();
        var bToA = new BytePipe();
        var a = new CompressingLineTransport(aToB.WriteAsync, bToA.ReadAsync, terminator: '\n');
        var b = new CompressingLineTransport(bToA.WriteAsync, aToB.ReadAsync, terminator: '\n');
        return (a, b, aToB, bToA);
    }

    [Fact]
    public async Task WithoutNegotiation_LinesRoundTripUncompressed()
    {
        (CompressingLineTransport a, CompressingLineTransport b, _, _) = Pair();
        using var cts = new CancellationTokenSource(Timeout);

        await a.SendLineAsync("/ÿHOST PDNCONV pdnconv1 Aampun", cts.Token);
        Assert.Equal("/ÿHOST PDNCONV pdnconv1 Aampun", await b.ReceiveLineAsync(cts.Token));
        Assert.False(a.CompressionEngaged);
        Assert.False(b.CompressionEngaged);
    }

    [Fact]
    public async Task AfterOffer_BothSidesCompress_AndLinesRoundTripTransparently()
    {
        (CompressingLineTransport a, CompressingLineTransport b, _, _) = Pair();
        using var cts = new CancellationTokenSource(Timeout);

        // A offers and immediately arms its transmit side — the //COMP toggle marks that boundary on A's
        // outbound byte stream, so the first line A sends after it is already compressed.
        await a.OfferCompressionAsync(cts.Token);
        Assert.True(a.CompressionEngaged);
        await a.SendLineAsync("/ÿCMSG g4abc 3333 first line after the offer", cts.Token);

        // B reads: the //COMP toggle is swallowed (never surfaced as a line) and arms B's receive side, so B
        // decodes A's already-compressed first line transparently. B reciprocates on the same receive call.
        string? firstOnB = await b.ReceiveLineAsync(cts.Token);
        Assert.Equal("/ÿCMSG g4abc 3333 first line after the offer", firstOnB);
        Assert.True(b.CompressionEngaged, "B armed (tx+rx) on receiving A's //COMP 1");

        // A reads B's reciprocal //COMP 1 (swallowed → arms A's receive). Drive a B→A line so A's receive runs.
        await b.SendLineAsync("/ÿCMSG m0xyz 3333 reply from B", cts.Token);
        string? replyOnA = await a.ReceiveLineAsync(cts.Token);
        Assert.Equal("/ÿCMSG m0xyz 3333 reply from B", replyOnA);

        // Both fully armed now: fresh round-trips are compressed on the wire yet identical to the caller.
        await a.SendLineAsync("/ÿCMSG g4abc 3333 the quick brown fox jumps over the lazy dog", cts.Token);
        Assert.Equal(
            "/ÿCMSG g4abc 3333 the quick brown fox jumps over the lazy dog",
            await b.ReceiveLineAsync(cts.Token));

        await b.SendLineAsync("/ÿUMSG conversd g4abc compressed both ways now", cts.Token);
        Assert.Equal(
            "/ÿUMSG conversd g4abc compressed both ways now",
            await a.ReceiveLineAsync(cts.Token));
    }

    [Fact]
    public async Task PeerOpensNegotiation_WeReciprocateAndCompress()
    {
        // The mirror of the offer flow: B opens the negotiation (as a conversd USER that ran `/comp on`
        // would), and A reciprocates transparently through its receive path.
        (CompressingLineTransport a, CompressingLineTransport b, _, _) = Pair();
        using var cts = new CancellationTokenSource(Timeout);

        await b.OfferCompressionAsync(cts.Token);
        await b.SendLineAsync("/ÿCMSG m0xyz 3333 B speaks first, compressed", cts.Token);

        // A receives B's offer (swallowed, arms A rx+tx, A writes its reciprocal //COMP 1) and decodes the line.
        Assert.Equal("/ÿCMSG m0xyz 3333 B speaks first, compressed", await a.ReceiveLineAsync(cts.Token));
        Assert.True(a.CompressionEngaged, "A armed and reciprocated on B's //COMP 1");

        // B receives A's reciprocal (swallowed, arms B rx). A→B round-trip is now compressed and transparent.
        await a.SendLineAsync("/ÿCMSG g4abc 3333 and A answers compressed", cts.Token);
        Assert.Equal("/ÿCMSG g4abc 3333 and A answers compressed", await b.ReceiveLineAsync(cts.Token));
    }

    [Fact]
    public async Task CompressionDisabled_NeverOffered_LinkStaysUncompressed()
    {
        // No offer from either side → the link runs exactly as today (the no-regression default for a peer
        // that does not engage host-link compression).
        (CompressingLineTransport a, CompressingLineTransport b, _, _) = Pair();
        using var cts = new CancellationTokenSource(Timeout);

        for (int i = 0; i < 5; i++)
        {
            await a.SendLineAsync($"/ÿCMSG g4abc 3333 plain line {i}", cts.Token);
            Assert.Equal($"/ÿCMSG g4abc 3333 plain line {i}", await b.ReceiveLineAsync(cts.Token));
        }

        Assert.False(a.CompressionEngaged);
        Assert.False(b.CompressionEngaged);
    }
}
