using System.Text;
using Convers.Protocol;

namespace Convers.Protocol.Tests;

/// <summary>
/// Tests for the host-link Huffman <see cref="Compression"/> facility (W7c), ported from conversd-saupp's
/// <c>compression.c</c>: round-trip fidelity over representative convers text, the stored-copy fallback
/// when coding would not shrink the data, the undecodable-frame resilience the conversd read loop relies
/// on, and the <c>//COMP</c> negotiation token.
/// </summary>
public class CompressionTests
{
    [Theory]
    [InlineData("hello world")]
    [InlineData("/ÿCMSG g4abc 3333 the quick brown fox jumps over the lazy dog")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // highly compressible
    [InlineData("e")]                                  // single byte
    [InlineData("The five boxing wizards jump quickly. 0123456789!?-")]
    public void EncodeThenDecode_RoundTrips(string text)
    {
        byte[] source = Encoding.Latin1.GetBytes(text);

        byte[] frame = Compression.EncodeFrame(source);
        Assert.True(Compression.TryDecodeFrame(frame, out byte[] decoded2));

        Assert.Equal(source, decoded2);
        Assert.Equal(text, Encoding.Latin1.GetString(decoded2));
    }

    [Theory]
    // Golden vectors captured from the reference conversd-saupp compression.c (encstathuf): an encoded
    // frame must be byte-for-byte identical so a real conversd decodes our frames (and vice versa).
    [InlineData("hello world", "0af914b6ea96f0ae")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "1fa5294a5294a5294a5294a5294a5294a5294a5294")]
    [InlineData("e", "0040")]
    public void EncodeFrame_MatchesConversdGoldenVector(string text, string expectedHex)
    {
        byte[] frame = Compression.EncodeFrame(Encoding.Latin1.GetBytes(text));

        Assert.Equal(expectedHex, Convert.ToHexString(frame).ToLowerInvariant());
    }

    [Fact]
    public void DecodeFrame_DecodesConversdGoldenVector()
    {
        Assert.True(Compression.TryDecodeFrame(Convert.FromHexString("0af914b6ea96f0ae"), out byte[] decoded));
        Assert.Equal("hello world", Encoding.Latin1.GetString(decoded));
    }

    [Fact]
    public void RoundTrips_AcrossAllByteValues_InFrameSizedChunks()
    {
        // Every byte 0..255 must encode and decode back, in frames up to the MTU.
        var rng = new Random(20260613);
        for (int trial = 0; trial < 200; trial++)
        {
            int len = rng.Next(1, Compression.MaxFrameLength + 1);
            var source = new byte[len];
            rng.NextBytes(source);

            byte[] frame = Compression.EncodeFrame(source);
            Assert.True(Compression.TryDecodeFrame(frame, out byte[] decoded), $"decode failed for len {len}");
            Assert.Equal(source, decoded);
        }
    }

    [Fact]
    public void IncompressibleData_UsesStoredCopy_AndStillRoundTrips()
    {
        // Random bytes do not compress; the encoder must fall back to a stored copy (header 255), and that
        // must still decode to the original.
        var rng = new Random(42);
        var source = new byte[200];
        rng.NextBytes(source);

        byte[] frame = Compression.EncodeFrame(source);

        // A stored copy is header(255) + verbatim payload.
        if (frame[0] == Compression.StoredMarker)
        {
            Assert.Equal(source.Length + 1, frame.Length);
            Assert.Equal(source, frame[1..]);
        }

        Assert.True(Compression.TryDecodeFrame(frame, out byte[] decoded));
        Assert.Equal(source, decoded);
    }

    [Fact]
    public void CompressibleData_ProducesASmallerFrame()
    {
        // 'a' (0x61) has a short Huffman code, so a long run compresses (matching the conversd golden vector:
        // 32 'a's -> 21 bytes). The codec only compresses when it actually shrinks the data — see the
        // stored-copy fallback test for incompressible input.
        byte[] source = Encoding.Latin1.GetBytes(new string('a', 200));

        byte[] frame = Compression.EncodeFrame(source);

        Assert.NotEqual(Compression.StoredMarker, frame[0]); // it compressed
        Assert.True(frame.Length < source.Length, "a long run of 'a' should compress smaller than the source");
        Assert.True(Compression.TryDecodeFrame(frame, out byte[] decoded));
        Assert.Equal(source, decoded);
    }

    [Fact]
    public void EmptyOrOversizedSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => Compression.EncodeFrame([]));
        Assert.Throws<ArgumentException>(() => Compression.EncodeFrame(new byte[Compression.MaxFrameLength + 1]));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x12 })] // a single byte — too short to be a valid frame (< 2)
    public void TooShortFrame_FailsToDecode(byte[] frame)
    {
        Assert.False(Compression.TryDecodeFrame(frame, out byte[] decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void StreamingDecode_ReportsBytesConsumed_AndWalksConcatenatedFrames()
    {
        // The transport decodes a socket read that may carry several back-to-back compressed frames. A
        // compressed frame is self-delimiting (its header gives the decoded length), so bytesConsumed lets
        // the caller advance to the next frame. Build three frames, concatenate, and decode the run.
        string[] texts =
        [
            "hello world\n",
            "/ÿCMSG g4abc 3333 the quick brown fox\n",
            "aaaaaaaaaaaaaaaaaaaa\n",
        ];
        var stream = new List<byte>();
        foreach (string t in texts)
        {
            stream.AddRange(Compression.EncodeFrame(Encoding.Latin1.GetBytes(t)));
        }

        byte[] buffer = stream.ToArray();
        int offset = 0;
        var decodedTexts = new List<string>();
        while (offset < buffer.Length)
        {
            Assert.True(
                Compression.TryDecodeFrame(buffer.AsSpan(offset), out byte[] decoded, out int consumed),
                $"frame at offset {offset} must decode");
            Assert.True(consumed > 0, "a decoded frame must consume at least its header");
            decodedTexts.Add(Encoding.Latin1.GetString(decoded));
            offset += consumed;
        }

        Assert.Equal(texts, decodedTexts);
        Assert.Equal(buffer.Length, offset); // the whole run was consumed exactly
    }

    [Fact]
    public void StreamingDecode_OfASingleFrame_ConsumesItsWholeLength()
    {
        byte[] frame = Compression.EncodeFrame(Encoding.Latin1.GetBytes("hello world"));
        Assert.True(Compression.TryDecodeFrame(frame, out _, out int consumed));
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void StreamingDecode_ReportsExactBytesConsumed_AcrossManyRandomFrames()
    {
        // bytesConsumed must equal the frame's own on-wire length, even when the final Huffman symbol lands
        // mid-byte (a leaf emits before consuming the current bit, so the in-progress byte may or may not
        // belong to the frame). Append a sentinel so the implementation cannot mask an overshoot by clamping
        // to frame.Length; the reported consumed must still be exactly the real frame length. This pins the
        // off-by-one fix that otherwise corrupts ~13% of frames when walking a back-to-back buffer.
        var rng = new Random(20260613);
        for (int trial = 0; trial < 5000; trial++)
        {
            int len = rng.Next(1, 80);
            var source = new byte[len];
            // Compressible bytes (so the encoder produces a real Huffman frame, not a stored copy).
            for (int i = 0; i < len; i++)
            {
                source[i] = (byte)"etaoinshrdlu .,?-0123".ToCharArray()[rng.Next(21)];
            }

            byte[] frame = Compression.EncodeFrame(source);
            if (frame[0] == Compression.StoredMarker)
            {
                continue; // stored frames are sized by the read boundary, not self-delimiting
            }

            var padded = new byte[frame.Length + 1];
            frame.CopyTo(padded, 0);
            padded[frame.Length] = 0xAA; // a sentinel after the frame

            Assert.True(Compression.TryDecodeFrame(padded, out byte[] decoded, out int consumed));
            Assert.Equal(source, decoded);
            Assert.Equal(frame.Length, consumed); // exact — not frame.Length+1 (overshoot) nor clamped
        }
    }

    [Fact]
    public void StreamingDecode_WalksManyConcatenatedCompressibleFrames()
    {
        // The transport walks a coalesced read of several back-to-back compressed frames using bytesConsumed.
        var rng = new Random(7);
        for (int trial = 0; trial < 3000; trial++)
        {
            int n = rng.Next(2, 6);
            var sources = new List<byte[]>();
            var wire = new List<byte>();
            for (int k = 0; k < n; k++)
            {
                int len = rng.Next(1, 60);
                var src = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    src[i] = (byte)"etaoinshrdlu .,?-".ToCharArray()[rng.Next(17)];
                }

                byte[] f = Compression.EncodeFrame(src);
                if (f[0] == Compression.StoredMarker)
                {
                    k--; // retry this slot with different bytes so the whole run is self-delimiting
                    continue;
                }

                sources.Add(src);
                wire.AddRange(f);
            }

            byte[] buffer = wire.ToArray();
            int offset = 0;
            foreach (byte[] expected in sources)
            {
                Assert.True(Compression.TryDecodeFrame(buffer.AsSpan(offset), out byte[] decoded, out int consumed));
                Assert.Equal(expected, decoded);
                offset += consumed;
            }

            Assert.Equal(buffer.Length, offset); // the whole run consumed exactly, no drift
        }
    }

    [Fact]
    public void StreamingDecode_OfAStoredFrame_ClaimsTheWholeSpan()
    {
        // A stored (incompressible) frame is sized by the read boundary, so it consumes everything it is given.
        var rng = new Random(99);
        var source = new byte[120];
        rng.NextBytes(source);
        byte[] frame = Compression.EncodeFrame(source);
        Assert.Equal(Compression.StoredMarker, frame[0]); // random bytes do not compress → stored copy

        Assert.True(Compression.TryDecodeFrame(frame, out byte[] decoded, out int consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(source, decoded);
    }

    [Theory]
    [InlineData("//COMP 1", true)]
    [InlineData("//COMP 0", false)]
    [InlineData("\r//COMP 1\r", true)]
    [InlineData("  //comp 0  ", false)]
    public void Negotiation_ParsesCompToggle(string line, bool expected)
    {
        Assert.True(CompressionNegotiation.TryParse(line, out bool enabled));
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData("//COMP 2")]
    [InlineData("/..CMSG g4abc 3333 hi")]
    [InlineData("hello")]
    [InlineData(null)]
    public void Negotiation_RejectsNonCompLines(string? line)
    {
        Assert.False(CompressionNegotiation.TryParse(line, out _));
    }
}
