using System.Text;
using Convers.Host.Uplink;
using Convers.Protocol;

namespace Convers.Host.Tests.Uplink;

/// <summary>
/// Unit tests for the sans-IO host-link compression stage (<see cref="HostLinkCompression"/>): the
/// <c>//COMP</c> negotiation state machine and the byte-level encode/decode that wires the W7c Huffman codec
/// into the live transport. These pin conversd's in-band-toggle posture — offering arms the transmit side
/// and the toggle marks that boundary on the outbound stream; receiving <c>//COMP 1</c> arms the receive
/// side (and reciprocates when the peer opened the negotiation) — so two cooperating sides end fully
/// compressed both ways, while a never-negotiated link runs uncompressed exactly as today.
/// </summary>
public class HostLinkCompressionTests
{
    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Arms both directions of a fresh stage (peer-opened negotiation + the reciprocal write).</summary>
    private static HostLinkCompression ArmedBoth()
    {
        var c = new HostLinkCompression();
        c.TryApplyInboundToggle("//COMP 1", out _);
        c.EngageTransmitAfterReciprocal();
        return c;
    }

    /// <summary>Concatenates the frames EncodeOutbound produces for one logical write (the wire bytes).</summary>
    private static byte[] Encode(HostLinkCompression c, byte[] framedLine)
    {
        var wire = new List<byte>();
        foreach (byte[] frame in c.EncodeOutbound(framedLine))
        {
            wire.AddRange(frame);
        }

        return wire.ToArray();
    }

    /// <summary>
    /// Decodes a whole wire buffer the way the transport does: one frame per DecodeInbound call, carrying the
    /// remainder, until consumed (or a partial frame is held — asserted absent here for complete inputs).
    /// </summary>
    private static string Decode(HostLinkCompression c, byte[] wire)
    {
        var plain = new List<byte>();
        byte[] buffer = wire;
        while (buffer.Length != 0)
        {
            byte[] chunk = c.DecodeInbound(buffer, out byte[] remainder);
            Assert.True(remainder.Length < buffer.Length || chunk.Length != 0, "decode must make progress");
            if (chunk.Length == 0)
            {
                Assert.True(remainder.Length == buffer.Length, "a no-output pass must be a held partial frame");
                Assert.Fail($"unexpected partial/held frame of {remainder.Length} bytes");
            }

            plain.AddRange(chunk);
            buffer = remainder;
        }

        return Encoding.Latin1.GetString(plain.ToArray());
    }

    [Fact]
    public void Offer_ArmsTransmit_AndMarksTheBoundary()
    {
        var c = new HostLinkCompression();
        byte[] offer = c.BuildOffer();

        // The offer is the uncompressed //COMP 1 toggle and arms the transmit side: everything written after
        // it is compressed, and the toggle tells the peer where that begins (conversd's in-band boundary).
        Assert.Equal("\r//COMP 1\r", Encoding.Latin1.GetString(offer));
        Assert.True(c.TxActive);
        Assert.False(c.RxActive); // our receive only arms once the peer signals //COMP 1 back

        // Outbound is now compressed.
        byte[] line = Latin1("/ÿCMSG g4abc 3333 the quick brown fox jumps over\n");
        Assert.NotEqual(line, Encode(c, line));
    }

    [Fact]
    public void PeerConfirmsOurOffer_ArmsReceive_NoFurtherReply()
    {
        var c = new HostLinkCompression();
        _ = c.BuildOffer(); // we offered → tx armed
        Assert.True(c.TxActive);
        Assert.False(c.RxActive);

        // The peer answers //COMP 1 — its return stream is now compressed, so our receive side arms. We
        // already offered, so no reciprocal answer is owed.
        Assert.True(c.TryApplyInboundToggle("//COMP 1", out byte[]? reply));
        Assert.Null(reply);
        Assert.True(c.TxActive);
        Assert.True(c.RxActive);
        Assert.True(c.FullyActive);
    }

    [Fact]
    public void PeerOpensNegotiation_WeReciprocate_AndArmBoth()
    {
        var c = new HostLinkCompression(); // we did NOT offer first

        Assert.True(c.TryApplyInboundToggle("//COMP 1", out byte[]? reply));
        Assert.NotNull(reply);
        Assert.Equal("\r//COMP 1\r", Encoding.Latin1.GetString(reply!)); // reciprocal offer, uncompressed
        Assert.True(c.RxActive);                                          // receive arms at the toggle
        Assert.False(c.TxActive);                                         // transmit arms only after the reply is on the wire

        c.EngageTransmitAfterReciprocal(); // the transport calls this right after writing the reciprocal
        Assert.True(c.TxActive);
        Assert.True(c.FullyActive);
    }

    [Fact]
    public void Disable_DisarmsBothDirections_AndAllowsRenegotiation()
    {
        var c = new HostLinkCompression();
        c.TryApplyInboundToggle("//COMP 1", out _); // arm rx; reciprocal owed
        c.EngageTransmitAfterReciprocal();          // arm tx (transport wrote the reciprocal)
        Assert.True(c.FullyActive);

        Assert.True(c.TryApplyInboundToggle("//COMP 0", out byte[]? reply));
        Assert.Null(reply);
        Assert.False(c.TxActive);
        Assert.False(c.RxActive);

        // A clean re-enable after the disable must renegotiate (the offered flag was cleared), so the peer's
        // next //COMP 1 owes a fresh reciprocal again.
        Assert.True(c.TryApplyInboundToggle("//COMP 1", out byte[]? reply2));
        Assert.NotNull(reply2);
        Assert.True(c.RxActive);
    }

    [Fact]
    public void NonToggleLine_IsNotConsumedByNegotiation()
    {
        var c = new HostLinkCompression();
        Assert.False(c.TryApplyInboundToggle("/ÿCMSG a 1 hello", out _));
    }

    [Fact]
    public void ArmedRoundTrip_EncodeThenDecode_RecoversTheLine()
    {
        // Two peers that have both armed: A's compressed output decodes on B and vice versa.
        HostLinkCompression a = ArmedBoth();
        HostLinkCompression b = ArmedBoth();

        byte[] line = Latin1("/ÿCMSG g4abc 3333 the quick brown fox jumps over the lazy dog\n");
        byte[] onWire = Encode(a, line);
        Assert.NotEqual(line, onWire);            // it actually compressed
        Assert.True(onWire.Length < line.Length); // and shrank

        Assert.Equal(Encoding.Latin1.GetString(line), Decode(b, onWire));
    }

    [Fact]
    public void ArmedOutbound_LongerThanTheMtu_SplitsIntoFramesThatDecodeBack()
    {
        HostLinkCompression c = ArmedBoth();

        // A line well over the 255-byte frame MTU must split into multiple frames (each its own write) that
        // decode back whole — the transport walks them one per DecodeInbound call.
        string text = new string('a', 600) + "\n";
        Assert.Equal(3, c.EncodeOutbound(Latin1(text)).Count); // 600+1 bytes → ceil/255 = 3 frames
        byte[] onWire = Encode(c, Latin1(text));

        HostLinkCompression peer = ArmedBoth();
        Assert.Equal(text, Decode(peer, onWire));
    }

    [Fact]
    public void Inbound_SystemNotice_IsPassedThroughVerbatim()
    {
        // conversd never compresses a "*** " notice; the reader treats it as raw text even when armed.
        HostLinkCompression c = ArmedBoth();

        byte[] notice = Latin1("*** Welcome to the oracle\n");
        Assert.Equal("*** Welcome to the oracle\n", Decode(c, notice));
    }

    [Fact]
    public void Inbound_ToggleCoalescedWithFirstCompressedFrame_DecodesBothInOneRead()
    {
        // A peer's "\r//COMP 1\r" toggle and its first compressed frame arrive in ONE (TCP-coalesced) read.
        // When rx is still un-armed, DecodeInbound must isolate the toggle line so the receiver arms rx and
        // re-decodes the tail as a frame — otherwise the whole buffer is mis-read as raw text and the frame
        // is lost. We replicate the transport's receive loop here: decode one unit at a time, applying any
        // toggle, until the buffer is consumed.
        var sender = new HostLinkCompression(); // arms tx + rx, would send //COMP 1 then compress
        sender.TryApplyInboundToggle("//COMP 1", out _);
        sender.EngageTransmitAfterReciprocal();
        byte[] compressedLine = Encode(sender, Latin1("/ÿCMSG g4abc 3333 hi from the peer\n"));

        var coalesced = new List<byte>();
        coalesced.AddRange(Latin1("\r//COMP 1\r")); // the toggle, uncompressed
        coalesced.AddRange(compressedLine);          // the first compressed frame, same read

        var receiver = new HostLinkCompression(); // un-armed; the peer is opening negotiation
        var assembled = new List<string>();
        byte[] buffer = coalesced.ToArray();
        while (buffer.Length != 0)
        {
            byte[] plain = receiver.DecodeInbound(buffer, out byte[] remainder);
            Assert.True(remainder.Length < buffer.Length || plain.Length != 0, "must make progress");
            buffer = remainder;
            // emulate the assembler: split on terminators and route //COMP toggles into the negotiation
            foreach (string line in SplitLines(plain))
            {
                if (receiver.TryApplyInboundToggle(line, out _))
                {
                    continue; // a toggle: consumed, arms rx
                }

                assembled.Add(line);
            }
        }

        Assert.True(receiver.RxActive, "the coalesced toggle armed the receive side");
        Assert.Equal(["/ÿCMSG g4abc 3333 hi from the peer"], assembled); // the frame decoded, not mis-read
    }

    private static IEnumerable<string> SplitLines(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (byte b in bytes)
        {
            if (b is (byte)'\r' or (byte)'\n')
            {
                if (sb.Length != 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            else
            {
                sb.Append((char)b);
            }
        }

        if (sb.Length != 0)
        {
            yield return sb.ToString();
        }
    }

    [Fact]
    public void Inbound_SplitFrame_AcrossTwoReads_IsCarriedAndCompleted()
    {
        HostLinkCompression sender = ArmedBoth();
        HostLinkCompression receiver = ArmedBoth();

        byte[] frame = Encode(sender, Latin1("hello world\n"));
        // Deliver the frame in two halves — the receiver must hold the partial head and finish on the tail.
        int split = frame.Length / 2;
        byte[] head = receiver.DecodeInbound(frame.AsSpan(0, split).ToArray(), out byte[] carry1);
        Assert.Empty(head);
        Assert.NotEmpty(carry1);

        byte[] combined = new byte[carry1.Length + (frame.Length - split)];
        carry1.CopyTo(combined, 0);
        frame.AsSpan(split).CopyTo(combined.AsSpan(carry1.Length));
        byte[] tail = receiver.DecodeInbound(combined, out byte[] carry2);
        Assert.Empty(carry2);
        Assert.Equal("hello world\n", Encoding.Latin1.GetString(tail));
    }
}
