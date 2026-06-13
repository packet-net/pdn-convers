using Convers.Host.Uplink;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Live-oracle interop for the host-link compression codec wired into the transport (the W7-deferred
/// plumbing). The stock conversd-saupp oracle does <b>not</b> negotiate <c>//COMP</c> on a HOST peer link —
/// its <c>/comp</c> command is registered <c>CM_USER | CM_OBSERVER</c> only (conversd.c), so a <c>//COMP</c>
/// on a CT_HOST connection is never dispatched. conversd <em>does</em> speak the exact same Huffman codec on
/// a <b>USER</b> link, so these tests prove our wired transport round-trips compressed frames against the
/// real C: we connect as a USER, enable compression with <c>//comp on</c> (conversd answers
/// <c>\r//COMP 1\r</c>, which our <see cref="CompressingLineTransport"/> consumes and reciprocates), then
/// send and receive lines that travel as real Huffman frames on the wire — transparently to the caller.
/// </summary>
/// <remarks>
/// Tagged <c>Interop</c> so the unit lane excludes it; runs only with the oracle up
/// (<c>docker compose -f docker/compose.oracle.yml up -d --build --wait</c>). Uses a distinct callsign base
/// from the other interop tests (conversd holds one host link per base; user names are independent but kept
/// distinct for cleanliness).
/// </remarks>
[Trait("Category", "Interop")]
public class HostLinkCompressionOracleInteropTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task UserLink_EnablesCompression_AndLinesRoundTripCompressedAgainstTheOracle()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using TcpUpstreamLink link =
            await TcpUpstreamLink.ConnectAsync(OracleEndpoint.Host, OracleEndpoint.Port, cts.Token);

        // Become a USER on a private channel (conversd compresses USER links via //comp).
        await link.SendLineAsync("/name g4comp", cts.Token);
        await link.SendLineAsync("/channel 4471", cts.Token);
        await DrainForAsync(link, TimeSpan.FromMilliseconds(600), cts.Token);

        // Enable compression the conversd-USER way: send the //comp on command (uncompressed), then tell the
        // transport we offered out of band so it arms its transmit side — conversd arms its own compression on
        // receiving //comp on and answers "\r//COMP 1\r" (uncompressed), which the transport swallows to arm
        // its receive side (no reciprocal is sent, since conversd is already armed). The ordering matters:
        // //comp on must reach conversd uncompressed, then our subsequent lines are Huffman frames conversd
        // decodes, and conversd's output is frames we decode.
        await link.SendLineAsync("//comp on", cts.Token);
        await link.NoteExternalCompressionOfferAsync(cts.Token);
        Assert.True(link.CompressionEngaged, "the transport's transmit side is armed after the offer");
        // Drive receives until conversd's "\r//COMP 1\r" answer is consumed and our receive side is armed —
        // crucial to do BEFORE sending compressed content, so the toggle is lifted off the socket on its own
        // read (uncompressed) rather than coalescing with a later compressed frame the un-armed reader would
        // mis-split. conversd sends only the toggle then waits, so this settles promptly.
        await WaitForReceiveCompressionAsync(link, cts.Token);
        Assert.True(link.ReceiveCompressionEngaged, "the transport's receive side engaged on conversd's //COMP 1");

        // Drive a round-trip that crosses the codec both ways: set a topic (our line is compressed on the
        // wire — conversd decodes it) and read conversd's confirmation (a compressed frame we decode).
        string topic = "interop-compression-proof";
        await link.SendLineAsync($"/topic {topic}", cts.Token);

        string confirmation = await ReadUntilAsync(
            link, l => l.Contains("topic set", StringComparison.OrdinalIgnoreCase), cts.Token);

        // The reply arrived as plain text to us only because the transport decompressed it — and our /topic
        // reached conversd only because it decompressed our frame. The link's transmit side is engaged.
        Assert.Contains("4471", confirmation);
        Assert.True(link.CompressionEngaged, "the transport's transmit side is compressing toward conversd");

        // Query the topic back (/topic with no arg) to prove arbitrary text survives the codec both ways: our
        // compressed query reaches conversd, and conversd's compressed answer carries the exact topic string
        // we set — decoded transparently by the transport.
        await link.SendLineAsync("/topic", cts.Token);
        string current = await ReadUntilAsync(
            link, l => l.Contains(topic, StringComparison.Ordinal), cts.Token);
        Assert.Contains(topic, current);
    }

    /// <summary>
    /// Drives short-timeout receives until the transport's receive side engages — i.e. conversd's
    /// (uncompressed) <c>//COMP 1</c> toggle has been lifted off the socket and consumed (it surfaces no
    /// line). Each attempt's read pulls the toggle bytes off the wire and arms decompression; the timeout
    /// then returns control so the loop can observe the armed state.
    /// </summary>
    private static async Task WaitForReceiveCompressionAsync(TcpUpstreamLink link, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 15 && !link.ReceiveCompressionEngaged; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromMilliseconds(300));
            try
            {
                await link.ReceiveLineAsync(attemptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // settle window elapsed; the loop re-checks ReceiveCompressionEngaged
            }
        }
    }

    /// <summary>Reads inbound lines until <paramref name="match"/> is satisfied (or the link/timeout ends).</summary>
    private static async Task<string> ReadUntilAsync(
        TcpUpstreamLink link, Func<string, bool> match, CancellationToken ct)
    {
        var seen = new List<string>();
        while (true)
        {
            string? line;
            try
            {
                line = await link.ReceiveLineAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No matching line. Saw: [{string.Join(" | ", seen)}]");
            }

            Assert.NotNull(line); // the oracle must not hang up mid-exchange
            seen.Add(line!);
            if (match(line!))
            {
                return line!;
            }
        }
    }

    /// <summary>Drains and discards inbound lines for a short settle window (greeting/welcome text).</summary>
    private static async Task DrainForAsync(TcpUpstreamLink link, TimeSpan window, CancellationToken ct)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drain.CancelAfter(window);
        try
        {
            while (true)
            {
                string? line = await link.ReceiveLineAsync(drain.Token);
                if (line is null)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // settle window elapsed
        }
    }
}
