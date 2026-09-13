using System.Buffers.Binary;

namespace YrrpAnalyser.Map;

/// <summary>
/// The two codecs a TS/RA2 map's packed sections use. Both come as a run of blocks, each a uint16
/// compressed size, a uint16 uncompressed size and then the block: LZO1X for [IsoMapPack5] and
/// [PreviewPack], Westwood's LCW ("format 80") for [OverlayPack] and [OverlayDataPack]. A map is a
/// file people share, so every read is bounds-checked and a bad block ends decoding rather than
/// throwing.
/// </summary>
public static class MapCompression
{
    public enum Codec { Lzo, Lcw }

    /// <summary>Decodes every block of a section's base64 text; null when it is not valid base64.</summary>
    public static byte[]? DecodePack(string base64, Codec codec, int maxOutput = 16 << 20)
    {
        byte[] packed;
        try { packed = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        var output = new List<byte[]>();
        int total = 0;
        for (int p = 0; p + 4 <= packed.Length;)
        {
            int inSize = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(p));
            int outSize = BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan(p + 2));
            p += 4;
            if (inSize == 0 || outSize == 0 || p + inSize > packed.Length || total + outSize > maxOutput)
                break;

            var block = new byte[outSize];
            var source = packed.AsSpan(p, inSize);
            int written = codec == Codec.Lzo ? Lzo1xDecompress(source, block) : LcwDecompress(source, block);
            if (written < 0) break;
            output.Add(block);
            total += outSize;
            p += inSize;
        }

        var result = new byte[total];
        int at = 0;
        foreach (var block in output)
        {
            block.CopyTo(result, at);
            at += block.Length;
        }
        return result;
    }

    /// <summary>
    /// LZO1X, as lzo1x_decompress_safe does it. Returns the bytes written, or -1 on a malformed
    /// block. The labels follow lzo1x_d.ch so the state machine can be checked against it.
    /// </summary>
    public static int Lzo1xDecompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, op = 0, t, mPos;
        int inEnd = input.Length, outEnd = output.Length;

        bool Need(int inBytes) => ip + inBytes <= inEnd;

        if (inEnd == 0) return 0;
        if (input[0] > 17)
        {
            t = input[ip++] - 17;
            if (t < 4) goto MatchNext;
            if (op + t > outEnd || !Need(t)) return -1;
            input.Slice(ip, t).CopyTo(output[op..]);
            op += t; ip += t;
            goto FirstLiteralRun;
        }

    Loop:
        if (!Need(1)) return -1;
        t = input[ip++];
        if (t >= 16) goto Match;
        if (t == 0)
        {
            while (Need(1) && input[ip] == 0) { t += 255; ip++; }
            if (!Need(1)) return -1;
            t += 15 + input[ip++];
        }
        t += 3;
        if (op + t > outEnd || !Need(t)) return -1;
        input.Slice(ip, t).CopyTo(output[op..]);
        op += t; ip += t;

    FirstLiteralRun:
        if (!Need(1)) return -1;
        t = input[ip++];
        if (t >= 16) goto Match;
        if (!Need(1)) return -1;
        mPos = op - (1 + 0x0800) - (t >> 2) - (input[ip++] << 2);
        if (mPos < 0 || op + 3 > outEnd) return -1;
        output[op++] = output[mPos++]; output[op++] = output[mPos++]; output[op++] = output[mPos];
        goto MatchDone;

    Match:
        if (t >= 64)
        {
            if (!Need(1)) return -1;
            mPos = op - 1 - ((t >> 2) & 7) - (input[ip++] << 3);
            t = (t >> 5) - 1;
            goto CopyMatch;
        }
        if (t >= 32)
        {
            t &= 31;
            if (t == 0)
            {
                while (Need(1) && input[ip] == 0) { t += 255; ip++; }
                if (!Need(1)) return -1;
                t += 31 + input[ip++];
            }
            if (!Need(2)) return -1;
            mPos = op - 1 - ((input[ip] >> 2) + (input[ip + 1] << 6));
            ip += 2;
            goto CopyMatch;
        }
        if (t >= 16)
        {
            mPos = op - ((t & 8) << 11);
            t &= 7;
            if (t == 0)
            {
                while (Need(1) && input[ip] == 0) { t += 255; ip++; }
                if (!Need(1)) return -1;
                t += 7 + input[ip++];
            }
            if (!Need(2)) return -1;
            mPos -= (input[ip] >> 2) + (input[ip + 1] << 6);
            ip += 2;
            if (mPos == op) return op; // end of stream
            mPos -= 0x4000;
            goto CopyMatch;
        }
        // A two-byte match straight after a literal run of one to three bytes.
        if (!Need(1)) return -1;
        mPos = op - 1 - (t >> 2) - (input[ip++] << 2);
        if (mPos < 0 || op + 2 > outEnd) return -1;
        output[op++] = output[mPos++]; output[op++] = output[mPos];
        goto MatchDone;

    CopyMatch:
        // t + 2 bytes, copied one at a time: the source may overlap what is being written.
        if (mPos < 0 || op + t + 2 > outEnd) return -1;
        for (int i = 0; i < t + 2; i++) output[op++] = output[mPos++];

    MatchDone:
        t = input[ip - 2] & 3;
        if (t == 0) goto Loop;

    MatchNext:
        if (op + t > outEnd || !Need(t + 1)) return -1;
        input.Slice(ip, t).CopyTo(output[op..]);
        op += t; ip += t;
        t = input[ip++];
        goto Match;
    }

    /// <summary>Westwood LCW ("format 80"). Returns the bytes written, or -1 on a malformed block.</summary>
    public static int LcwDecompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, op = 0;
        while (ip < input.Length)
        {
            byte command = input[ip++];
            if ((command & 0x80) == 0)
            {
                // 0cccpppp pppppppp: copy c+3 bytes from p back.
                if (ip >= input.Length) return -1;
                int count = (command >> 4) + 3;
                int from = op - (((command & 0x0F) << 8) | input[ip++]);
                if (!CopyWithin(output, ref op, from, count)) return -1;
            }
            else if (command == 0x80)
            {
                return op;
            }
            else if ((command & 0x40) == 0)
            {
                // 10cccccc: copy c literal bytes.
                int count = command & 0x3F;
                if (ip + count > input.Length || op + count > output.Length) return -1;
                input.Slice(ip, count).CopyTo(output[op..]);
                ip += count; op += count;
            }
            else if (command == 0xFE)
            {
                if (ip + 3 > input.Length) return -1;
                int count = BinaryPrimitives.ReadUInt16LittleEndian(input[ip..]);
                byte value = input[ip + 2];
                ip += 3;
                if (op + count > output.Length) return -1;
                output.Slice(op, count).Fill(value);
                op += count;
            }
            else
            {
                // 0xFF: 16-bit count; otherwise 11cccccc, c+3. Either way from an absolute position.
                int count;
                if (command == 0xFF)
                {
                    if (ip + 2 > input.Length) return -1;
                    count = BinaryPrimitives.ReadUInt16LittleEndian(input[ip..]);
                    ip += 2;
                }
                else count = (command & 0x3F) + 3;
                if (ip + 2 > input.Length) return -1;
                int from = BinaryPrimitives.ReadUInt16LittleEndian(input[ip..]);
                ip += 2;
                if (!CopyWithin(output, ref op, from, count)) return -1;
            }
        }
        return op;
    }

    private static bool CopyWithin(Span<byte> output, ref int op, int from, int count)
    {
        if (count == 0) return true;
        // The source may overlap what is being written, but must start in what already has been.
        if (from < 0 || from >= op || op + count > output.Length) return false;
        for (int i = 0; i < count; i++) output[op++] = output[from++];
        return true;
    }
}
