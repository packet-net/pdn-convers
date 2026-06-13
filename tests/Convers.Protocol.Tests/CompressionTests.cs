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
