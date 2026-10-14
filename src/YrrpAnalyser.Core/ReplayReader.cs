using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace YrrpAnalyser;

public enum ReplayLoadStatus
{
    Ok,
    NotAReplay,
    UnsupportedVersion,
    CorruptHeader,
    Truncated,
}

public sealed class ReplayLoadException(ReplayLoadStatus status, string message)
    : Exception(message)
{
    public ReplayLoadStatus Status { get; } = status;
}

/// <summary>
/// Parses a .yrrp into a <see cref="ReplayDocument"/>.
///
/// The header is uncompressed. The two embedded INIs are each raw deflate on their own, and the
/// frame stream after them is one raw deflate stream (RFC 1951, no zlib wrapper), which
/// <see cref="DeflateStream"/> reads directly - that is why the spawner writes them without
/// TDEFL_WRITE_ZLIB_HEADER. <see cref="FrameStream"/> decodes the records.
///
/// A recording that embedded saves carries a checkpoint archive after the finished frame stream,
/// running to EOF; the header's CheckpointArchiveOffset marks where the stream ends.
///
/// A recording that died with the game leaves the stream cut short mid-record. That is not an
/// error: everything up to the last sync flush is good, so the reader keeps what it decoded and
/// reports the truncation rather than throwing it away.
/// </summary>
public static class ReplayReader
{
    public static ReplayDocument Load(string path, IProgress<string>? progress = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16);

        if (file.Length < ReplayFormat.HeaderSize)
            throw new ReplayLoadException(ReplayLoadStatus.NotAReplay,
                "File is smaller than a replay header.");

        var head = new byte[ReplayFormat.HeaderSize];
        file.ReadExactly(head);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(ReplayFormat.OffsetMagic));
        if (magic != ReplayFormat.Magic)
            throw new ReplayLoadException(ReplayLoadStatus.NotAReplay,
                $"Not a .yrrp replay: magic is 0x{magic:X8}, expected 0x{ReplayFormat.Magic:X8}.");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(ReplayFormat.OffsetVersion));
        if (version < ReplayFormat.MinSupportedVersion || version > ReplayFormat.Version)
            throw new ReplayLoadException(ReplayLoadStatus.UnsupportedVersion,
                $"Replay version {version} is outside the supported range " +
                $"{ReplayFormat.MinSupportedVersion}-{ReplayFormat.Version}. The recording was made " +
                "by a different generation of the format; this build cannot read it.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(ReplayFormat.OffsetHeaderSize));

        // A larger HeaderSize is legal and expected from a later build: every field this build
        // reads is still where it was, and the surplus is skipped. A smaller one, or one that
        // runs past the file, is a layout that predates the released format.
        if (headerSize < ReplayFormat.HeaderSize || headerSize > file.Length)
            throw new ReplayLoadException(ReplayLoadStatus.CorruptHeader,
                $"HeaderSize is {headerSize}, which is not a header this build can seek past " +
                $"(expected at least {ReplayFormat.HeaderSize} and at most the file length). " +
                "That is the signature of a pre-release recording written before the format was pinned.");

        var header = ParseHeader(head);
        // Match ReplayFile.cpp's bounds before allocating embedded files or building analyses.
        if (header.UniqueIDCounter < 0
            || header.RandomNext1 < 0 || header.RandomNext1 >= ReplayFormat.RandomizerTableLength
            || header.RandomNext2 < 0 || header.RandomNext2 >= ReplayFormat.RandomizerTableLength
            || header.SpawnIniSize > ReplayFormat.MaxEmbeddedFileBytes
            || header.SpawnMapSize > ReplayFormat.MaxEmbeddedFileBytes
            || header.SpawnIniRawSize > ReplayFormat.MaxEmbeddedFileBytes
            || header.SpawnMapRawSize > ReplayFormat.MaxEmbeddedFileBytes
            || header.RecordedGameSpeed > ReplayFormat.MaxGameSpeedIndex)
            throw new ReplayLoadException(ReplayLoadStatus.CorruptHeader,
                "The header has an invalid object ID counter, RNG cursor, embedded file size, or game speed.");

        long iniOffset = headerSize;
        long mapOffset = iniOffset + header.SpawnIniSize;
        long streamOffset = mapOffset + header.SpawnMapSize;
        if (streamOffset > file.Length || streamOffset < iniOffset)
            throw new ReplayLoadException(ReplayLoadStatus.CorruptHeader,
                "The embedded spawn.ini and spawnmap.ini run past the end of the file.");

        file.Position = iniOffset;
        var spawnIniBytes = new byte[header.SpawnIniSize];
        file.ReadExactly(spawnIniBytes);
        var spawnMapBytes = new byte[header.SpawnMapSize];
        file.ReadExactly(spawnMapBytes);

        // Bounds are only checked in full by the section readers; this is just where the frame
        // stream's byte count stops - at whichever trailing section comes first.
        long streamEnd = file.Length;
        foreach (var (present, sectionOffset) in new[]
                 {
                     (header.HasCheckpointArchive, header.CheckpointArchiveOffset),
                     (header.HasStatisticsSection, header.StatisticsOffset),
                 })
        {
            if (present && sectionOffset > (ulong)streamOffset && sectionOffset <= (ulong)file.Length)
                streamEnd = Math.Min(streamEnd, (long)sectionOffset);
        }

        var doc = new ReplayDocument
        {
            FilePath = path,
            FileSize = file.Length,
            Header = header,
            SpawnIniText = DecodeIni(InflateEmbedded(spawnIniBytes, header.SpawnIniRawSize, "spawn.ini")),
            SpawnMapBytes = InflateEmbedded(spawnMapBytes, header.SpawnMapRawSize, "spawnmap.ini"),
            CompressedStreamBytes = streamEnd - streamOffset,
        };

        progress?.Report("Inflating frame stream...");
        file.Position = streamOffset;
        ReadFrameStream(file, doc, progress);

        if (header.HasCheckpointArchive)
            progress?.Report("Reading checkpoint archive...");
        doc.Checkpoints = CheckpointArchiveReader.Read(file, header, streamOffset, doc.Warnings);

        if (header.HasStatisticsSection)
            progress?.Report("Reading statistics...");
        doc.Statistics = StatisticsSectionReader.Read(file, header, streamOffset, doc.Warnings);

        doc.GameSpeed = GameSpeedTrack.Build(doc.Header, doc.Frames);
        doc.SpawnMapText = DecodeIni(doc.SpawnMapBytes);
        doc.SpawnIni = IniDocument.Parse(doc.SpawnIniText);
        doc.SpawnMapIni = IniDocument.Parse(doc.SpawnMapText);
        doc.Roster = PlayerRoster.Build(doc.SpawnIni, doc.Header);

        return doc;
    }

    private static byte[] InflateEmbedded(byte[] stored, uint rawSize, string name)
    {
        if (stored.Length == 0)
            return [];

        var raw = new byte[rawSize];
        try
        {
            using var inflate = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
            inflate.ReadExactly(raw);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new ReplayLoadException(ReplayLoadStatus.CorruptHeader,
                $"The embedded {name} does not inflate to the {rawSize:N0} bytes the header gives ({ex.Message.Trim()}).");
        }
        return raw;
    }

    private static string DecodeIni(byte[] bytes)
    {
        // The client writes spawn.ini as UTF-8 (player names can be non-ASCII); a BOM is possible
        // and would otherwise show up as a stray character on the first key.
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static ReplayHeaderInfo ParseHeader(byte[] h)
    {
        var randomizer = new uint[ReplayFormat.RandomizerTableLength];
        for (int i = 0; i < randomizer.Length; i++)
            randomizer[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                h.AsSpan(ReplayFormat.OffsetRandomizerTable + i * 4));

        return new ReplayHeaderInfo
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetMagic)),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetVersion)),
            HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetHeaderSize)),
            GameMode = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetGameMode)),
            UniqueIDCounter = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetUniqueIDCounter)),
            Seed = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetSeed)),
            RandomNext1 = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetRandomNext1)),
            RandomNext2 = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetRandomNext2)),
            RandomizerTable = randomizer,
            SpawnIniSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetSpawnIniSize)),
            SpawnMapSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetSpawnMapSize)),
            RecordedGameSpeed = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetRecordedGameSpeed)),
            RecordedUnixTime = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(ReplayFormat.OffsetRecordedUnixTime)),
            TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetTotalFrames)),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetFlags)),
            CheckpointArchiveOffset = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(ReplayFormat.OffsetCheckpointArchiveOffset)),
            CheckpointArchiveSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetCheckpointArchiveSize)),
            StatisticsOffset = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(ReplayFormat.OffsetStatisticsOffset)),
            StatisticsSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetStatisticsSize)),
            SpawnIniRawSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetSpawnIniRawSize)),
            SpawnMapRawSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetSpawnMapRawSize)),
            StatisticsRawSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetStatisticsRawSize)),
        };
    }

    private static void ReadFrameStream(Stream file, ReplayDocument doc, IProgress<string>? progress)
    {
        var stream = FrameStream.Inflate(file, out string? cutShort);
        progress?.Report("Decoding frame records...");
        var parsed = FrameStream.Parse(stream);

        doc.Frames = parsed.Frames;
        doc.EventBlob = parsed.EventBlob;
        doc.InflatedStreamBytes = stream.Length;
        doc.SawEndOfStream = parsed.SawEndOfStream;
        doc.Truncated = parsed.Truncated || (cutShort is not null && !parsed.SawEndOfStream);
        doc.Warnings.AddRange(parsed.Warnings);

        if (cutShort is not null)
            doc.Warnings.Add($"The compressed frame stream ends early ({cutShort}). " +
                             "Everything decoded up to that point is intact.");
        else if (doc.Truncated)
            doc.Warnings.Add("The frame stream ends part-way through a record. The recording was cut " +
                             "short - the game did not close the file down cleanly.");

        if (!doc.SawEndOfStream && !doc.Truncated && parsed.Warnings.Count == 0)
            doc.Warnings.Add("The frame stream ended without an end-of-stream marker.");
    }
}
