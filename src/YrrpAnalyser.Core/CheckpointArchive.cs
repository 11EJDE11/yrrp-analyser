using System.Buffers.Binary;
using System.IO.Compression;

namespace YrrpAnalyser;

/// <summary>
/// A save made during the recorded game, embedded so playback can seek to it without simulating
/// the frames before it. Mirrors RecordedCheckpoint in yrpp-spawner/src/Replay/ReplayRecordedCheckpoint.h.
/// </summary>
public sealed class RecordedCheckpoint
{
    public int Frame { get; init; }
    /// <summary>Uncompressed payload size: both length prefixes, the save, and the sidecar.</summary>
    public uint RawSize { get; init; }
    public uint Crc { get; init; }
    /// <summary>Absolute file offset of this checkpoint's raw deflate bytes.</summary>
    public long CompressedOffset { get; init; }
    public int CompressedSize { get; init; }

    public long SaveBytes { get; set; }
    public long SidecarBytes { get; set; }

    /// <summary>
    /// Why playback would skip this checkpoint, or null when it would import it. The sidecar's
    /// contents are not checked: only the game can deserialize them.
    /// </summary>
    public string? Problem { get; set; }

    public bool Usable => Problem is null;
}

/// <summary>
/// Reads the optional checkpoint archive the spawner appends after the finished frame stream,
/// applying the same checks as ImportRecordedCheckpoints in ReplaySeek.cpp. A fault in the
/// envelope makes playback ignore the whole archive; a fault in one payload skips that
/// checkpoint only. Neither affects event playback.
///
/// All integers are little-endian. The archive is <c>uint32 count</c>, then per checkpoint
/// <c>int32 frame, uint32 rawSize, uint32 crc32, uint32 byteCount, byteCount raw-deflate bytes</c>.
/// Each payload inflates to <c>uint32 saveBytes, save, uint32 sidecarBytes, sidecar</c>.
/// </summary>
public static class CheckpointArchiveReader
{
    public static List<RecordedCheckpoint> Read(Stream file, ReplayHeaderInfo header, long streamOffset,
        List<string> warnings)
    {
        if (!header.HasCheckpointArchive)
            return [];

        ulong offset = header.CheckpointArchiveOffset;
        uint size = header.CheckpointArchiveSize;
        ulong fileLength = (ulong)file.Length;

        // The archive runs from just past the frame stream to EOF, exactly.
        if (size == 0 || size > ReplayFormat.MaxCheckpointArchiveBytes
            || offset <= (ulong)streamOffset || offset > fileLength || size != fileLength - offset)
        {
            warnings.Add($"The header points at a {size:N0}-byte checkpoint archive at offset {offset:N0}, " +
                         "which does not run from the end of the frame stream to the end of the file. " +
                         "Playback ignores it and plays without the embedded saves.");
            return [];
        }

        var archive = new byte[size];
        file.Position = (long)offset;
        file.ReadExactly(archive);

        var entries = ReadEnvelope(archive, (long)offset, header.TotalFrames, out var envelopeProblem);
        if (envelopeProblem is not null)
        {
            warnings.Add($"The checkpoint archive is malformed: {envelopeProblem}. Playback ignores all of it " +
                         "and plays without the embedded saves.");
            return [];
        }

        foreach (var (checkpoint, compressed) in entries)
            InspectPayload(checkpoint, compressed);

        int unusable = entries.Count(e => !e.Checkpoint.Usable);
        if (unusable > 0)
            warnings.Add($"{unusable} of {entries.Count} embedded checkpoint(s) cannot be used; playback " +
                         "skips them and seeks from the rest.");

        return entries.Select(e => e.Checkpoint).ToList();
    }

    private static List<(RecordedCheckpoint Checkpoint, ArraySegment<byte> Compressed)> ReadEnvelope(
        byte[] archive, long archiveOffset, uint totalFrames, out string? problem)
    {
        problem = null;
        var result = new List<(RecordedCheckpoint, ArraySegment<byte>)>();
        int pos = 0;

        bool TryU32(out uint value)
        {
            if (archive.Length - pos < 4) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(pos));
            pos += 4;
            return true;
        }

        if (!TryU32(out uint count))
        {
            problem = "it is too short to hold a checkpoint count";
            return result;
        }
        if (count > ReplayFormat.MaxRecordedCheckpoints)
        {
            problem = $"it claims {count} checkpoints, more than the {ReplayFormat.MaxRecordedCheckpoints} a recording keeps";
            return result;
        }

        int previous = -1;
        for (int i = 0; i < count; i++)
        {
            if (!TryU32(out uint frameBits) || !TryU32(out uint rawSize) || !TryU32(out uint crc)
                || !TryU32(out uint compressedSize) || compressedSize > (uint)(archive.Length - pos))
            {
                problem = $"checkpoint {i} runs past the end of the archive";
                return result;
            }

            int frame = unchecked((int)frameBits);
            if (frame <= previous)
                problem = $"checkpoint {i} at frame {frame} does not follow frame {previous}";
            else if (frame > totalFrames)
                problem = $"checkpoint {i} at frame {frame} is past the last recorded frame, {totalFrames}";
            else if (rawSize == 0 || rawSize > ReplayFormat.MaxCheckpointPayloadBytes)
                problem = $"checkpoint {i} claims {rawSize:N0} uncompressed bytes";
            else if (compressedSize == 0)
                problem = $"checkpoint {i} has no compressed bytes";
            if (problem is not null)
                return result;

            var checkpoint = new RecordedCheckpoint
            {
                Frame = frame,
                RawSize = rawSize,
                Crc = crc,
                CompressedOffset = archiveOffset + pos,
                CompressedSize = (int)compressedSize,
            };
            result.Add((checkpoint, new ArraySegment<byte>(archive, pos, (int)compressedSize)));
            pos += (int)compressedSize;
            previous = frame;
        }

        if (pos != archive.Length)
            problem = $"{archive.Length - pos:N0} bytes follow the last checkpoint";
        return result;
    }

    private static void InspectPayload(RecordedCheckpoint checkpoint, ArraySegment<byte> compressed)
    {
        var raw = new byte[checkpoint.RawSize];
        try
        {
            using var inflate = new DeflateStream(
                new MemoryStream(compressed.Array!, compressed.Offset, compressed.Count, writable: false),
                CompressionMode.Decompress);
            int got = 0;
            while (got < raw.Length)
            {
                int n = inflate.Read(raw, got, raw.Length - got);
                if (n <= 0) break;
                got += n;
            }
            if (got != raw.Length)
            {
                checkpoint.Problem = $"inflates to {got:N0} bytes, not the {raw.Length:N0} its index claims";
                return;
            }
            if (inflate.ReadByte() >= 0)
            {
                checkpoint.Problem = $"inflates to more than the {raw.Length:N0} bytes its index claims";
                return;
            }
        }
        catch (InvalidDataException)
        {
            checkpoint.Problem = "the compressed payload is corrupt";
            return;
        }

        uint actual = Crc32.Compute(raw);
        if (actual != checkpoint.Crc)
        {
            checkpoint.Problem = $"CRC-32 mismatch (payload 0x{actual:X8}, index 0x{checkpoint.Crc:X8})";
            return;
        }

        long saveBytes = raw.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(raw) : -1;
        long sidecarAt = 4 + saveBytes;
        if (saveBytes < 0 || raw.Length - sidecarAt < 4)
        {
            checkpoint.Problem = "the save's length prefix runs past the payload";
            return;
        }

        long sidecarBytes = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)sidecarAt));
        long trailing = raw.Length - (sidecarAt + 4) - sidecarBytes;
        if (trailing != 0)
        {
            checkpoint.Problem = trailing < 0
                ? "the sidecar's length prefix runs past the payload"
                : $"{trailing:N0} bytes follow the sidecar";
            return;
        }

        checkpoint.SaveBytes = saveBytes;
        checkpoint.SidecarBytes = sidecarBytes;
        if (saveBytes == 0)
            checkpoint.Problem = "the embedded save is empty";
    }
}

/// <summary>CRC-32 (IEEE, reflected), the value miniz's mz_crc32(0, ...) produces.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data)
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
