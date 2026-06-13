namespace Convers.Protocol;

/// <summary>
/// The conversd-saupp host-link compression facility (W7c): the static Huffman codec from
/// <c>reference/conversd-saupp/compression.c</c> + <c>compression.h</c>, ported byte-for-byte so a
/// compressed frame interoperates with a real conversd. It is <b>sans-IO and frame-oriented</b>: a frame
/// carries up to <see cref="MaxFrameLength"/> source bytes, Huffman-coded with a one-byte length header,
/// and falls back to a verbatim "stored" copy when coding would not shrink the data (header byte
/// <see cref="StoredMarker"/>). The link layer negotiates it out of band with <c>//COMP 1</c> / <c>//COMP 0</c>
/// (see <see cref="CompressionNegotiation"/>); this class only encodes/decodes individual frames.
/// </summary>
/// <remarks>
/// conversd applies this per write-chunk: a line longer than <see cref="MaxFrameLength"/> is split into
/// frames, each encoded independently (<c>fast_write</c>), and the reader decodes one frame per socket read
/// (<c>COMP_MTU</c> reads). Decoding falls back to treating the bytes as raw text when the frame is too
/// short, starts with <c>"*** "</c> (a system notice conversd never compresses), or fails to decode — the
/// exact resilience conversd's read loop has. <see cref="TryDecodeFrame"/> mirrors that: it returns false
/// for an undecodable frame so the caller can use the bytes verbatim.
/// </remarks>
public static class Compression
{
    /// <summary>The maximum number of source bytes in one compressed frame (conversd <c>COMP_MTU</c>).</summary>
    public const int MaxFrameLength = 255;

    /// <summary>The header byte value that marks a stored (uncompressed) frame (conversd <c>dest[0] = 255</c>).</summary>
    public const byte StoredMarker = 255;

    /// <summary>
    /// Encode up to <see cref="MaxFrameLength"/> source bytes into one compressed frame (a length header
    /// byte followed by the Huffman bit-stream, or a stored copy when coding is ineffective). Mirrors
    /// conversd <c>encstathuf</c> exactly. Throws <see cref="ArgumentException"/> if
    /// <paramref name="source"/> is empty or longer than <see cref="MaxFrameLength"/> (callers chunk first).
    /// </summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0 || source.Length > MaxFrameLength)
        {
            throw new ArgumentException(
                $"A compression frame carries 1..{MaxFrameLength} source bytes (got {source.Length}).", nameof(source));
        }

        int srcLen = source.Length;
        // dest[0] header + payload. Worst Huffman expansion is bounded; the stored-copy fallback caps it at
        // srcLen + 1, so srcLen + 2 is always enough scratch.
        var dest = new byte[srcLen + 2];

        int destLen = 0;     // count of payload bytes written (matches conversd *destlen pre-final bumps)
        int destPtr = 1;     // dest[0] is the header; payload starts at index 1
        int bit8 = 0;
        dest[destPtr] = 0;

        int srcIndex = 0;
        ushort huffCode = HuffmanTables.EncodeCode[source[srcIndex]];
        int huffLen = HuffmanTables.EncodeLen[source[srcIndex]];
        int bit16 = 0;
        int written = 0;

        while (true)
        {
            if ((huffCode & Mask16[bit16]) != 0)
            {
                dest[destPtr] |= Mask8[bit8];
            }

            bit8++;
            if (bit8 > 7)
            {
                destPtr++;
                destLen++;
                if (destLen >= srcLen)
                {
                    // Coding ineffective → stored copy: dest[0] = 255, dest[1..] = source verbatim.
                    var stored = new byte[srcLen + 1];
                    stored[0] = StoredMarker;
                    source.CopyTo(stored.AsSpan(1));
                    return stored;
                }

                bit8 = 0;
                dest[destPtr] = 0;
            }

            bit16++;
            if (bit16 == huffLen)
            {
                srcIndex++;
                written++;
                if (written == srcLen)
                {
                    break;
                }

                huffCode = HuffmanTables.EncodeCode[source[srcIndex]];
                huffLen = HuffmanTables.EncodeLen[source[srcIndex]];
                bit16 = 0;
            }
        }

        if (bit8 != 0)
        {
            destLen++;
        }

        destLen++; // the header byte
        dest[0] = (byte)(srcLen - 1);
        return dest.AsSpan(0, destLen).ToArray();
    }

    /// <summary>
    /// Decode one compressed frame back to its source bytes. Returns <see langword="true"/> with the decoded
    /// bytes in <paramref name="decoded"/> on success; <see langword="false"/> when the frame is too short
    /// to be valid or the Huffman stream does not decode (the caller then uses the raw bytes, exactly as
    /// conversd's read loop does). A stored frame (header <see cref="StoredMarker"/>) decodes to the verbatim
    /// payload. Mirrors conversd <c>decstathuf</c>.
    /// </summary>
    public static bool TryDecodeFrame(ReadOnlySpan<byte> frame, out byte[] decoded)
    {
        decoded = [];
        if (frame.Length < 2)
        {
            return false; // conversd: clen < 2 is treated as undecodable
        }

        int destLen = frame[0] + 1;
        int srcLen = frame.Length - 1; // payload length after the header
        if (destLen == 256)
        {
            // Stored (uncompressed) copy.
            decoded = frame.Slice(1).ToArray();
            return true;
        }

        var output = new byte[destLen];
        int srcIndex = 1;
        int outIndex = 0;
        int written = 0;
        int decod = 0;
        int bitmask = 0x80;
        int remaining = srcLen;

        // The conversd decode reads one payload byte per outer iteration, walking the Huffman tree bit by
        // bit; a leaf does NOT advance the bit position (decod stays 0, bitmask unchanged) so the next
        // symbol resumes on the same bit — the quirk that makes this codec exact. The decode completes the
        // instant `written` reaches `destLen`; a well-formed frame always reaches it within its payload.
        // Past-end reads are treated as 0 (conversd reads scratch there but always finishes first); a
        // bounded iteration guard makes a corrupt frame fail rather than spin.
        int guard = (destLen + 2) * 16;
        while (true)
        {
            int currentByte = srcIndex < frame.Length ? frame[srcIndex] : 0;
            while (bitmask > 0)
            {
                if (guard-- <= 0)
                {
                    return false;
                }

                bool oneBit = (currentByte & bitmask) != 0;

                // A node with no second child is a leaf carrying symbol node1 (conversd decstathuf tests
                // node2 == 0 in BOTH bit branches). A non-leaf walks to node2 on a 1-bit, node1 on a 0-bit.
                bool isLeaf = HuffmanTables.DecodeNode2[decod] == 0;
                if (isLeaf)
                {
                    output[outIndex++] = (byte)HuffmanTables.DecodeNode1[decod];
                    written++;
                    if (written >= destLen)
                    {
                        decoded = output;
                        return true;
                    }

                    decod = 0;
                }
                else
                {
                    decod = oneBit ? HuffmanTables.DecodeNode2[decod] : HuffmanTables.DecodeNode1[decod];
                }

                if (decod != 0)
                {
                    bitmask >>= 1;
                }
            }

            bitmask = 0x80;
            srcIndex++;
            if (remaining == 0)
            {
                return false; // ran out of payload before the output was complete
            }

            remaining--;
        }
    }

    /// <summary>conversd <c>mask8tab</c>: the bit within a byte, MSB-first.</summary>
    private static readonly byte[] Mask8 = [0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01];

    /// <summary>conversd <c>mask16tab</c>: the bit within a 16-bit Huffman code, MSB-first.</summary>
    private static readonly ushort[] Mask16 =
    [
        0x8000, 0x4000, 0x2000, 0x1000, 0x0800, 0x0400, 0x0200, 0x0100,
        0x0080, 0x0040, 0x0020, 0x0010, 0x0008, 0x0004, 0x0002, 0x0001,
    ];
}
