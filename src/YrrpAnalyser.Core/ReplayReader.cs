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
/// The header and the two embedded INIs are stored uncompressed, so they come out of a plain
/// read. Everything after them is one raw deflate stream (RFC 1951, no zlib wrapper), which
/// <see cref="DeflateStream"/> reads directly - that is why the spawner writes it without
/// TDEFL_WRITE_ZLIB_HEADER.
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
            SpawnIniText = DecodeIni(spawnIniBytes),
            SpawnMapText = DecodeIni(spawnMapBytes),
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
        doc.CensusFrameCount = doc.Frames.Count(f => f.Census.HasValue);
        doc.SpawnIni = IniDocument.Parse(doc.SpawnIniText);
        doc.SpawnMapIni = IniDocument.Parse(doc.SpawnMapText);
        doc.Roster = PlayerRoster.Build(doc.SpawnIni, doc.Header);

        return doc;
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
        };
    }

    private static void ReadFrameStream(Stream file, ReplayDocument doc, IProgress<string>? progress)
    {
        using var inflate = new DeflateStream(file, CompressionMode.Decompress, leaveOpen: true);
        var reader = new StreamCursor(inflate);

        var frames = new List<FrameRecord>(capacity: 4096);
        var eventBlob = new GrowableBlob();

        try
        {
            while (true)
            {
                if (!reader.TryRead(ReplayFormat.FrameRecordHeaderSize, out var fh))
                {
                    doc.Truncated = fh.Length > 0;
                    break;
                }

                int frameNumber = BinaryPrimitives.ReadInt32LittleEndian(fh);
                int eventCount = BinaryPrimitives.ReadInt32LittleEndian(fh[4..]);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fh[8..]);

                if (frameNumber == -1)
                {
                    if (eventCount == 0 && flags == 0)
                        doc.SawEndOfStream = true;
                    else
                        doc.Warnings.Add("The end-of-stream marker has a nonzero event count or flags.");
                    break;
                }

                if (frameNumber < 0 || eventCount < 0 || eventCount > ReplayFormat.MaxEventsPerFrame)
                {
                    doc.Warnings.Add($"Frame record {frames.Count} has an invalid frame number or " +
                                     $"event count ({frameNumber}, {eventCount}); stopped reading here.");
                    break;
                }

                if (frames.Count > 0 && frameNumber <= frames[^1].FrameNumber)
                {
                    doc.Warnings.Add($"Frame {frameNumber} does not follow frame {frames[^1].FrameNumber}; " +
                                     "stopped reading here.");
                    break;
                }

                if ((flags & ~(uint)FrameRecordFlags.Known) != 0)
                {
                    // Blocks are stored bare and in write order, so an unknown flag means the end
                    // of that block is written down nowhere and nothing after it can be located.
                    doc.Warnings.Add($"Frame {frameNumber} carries unknown record flags 0x{flags:X8}; " +
                                     "the rest of the stream cannot be located and was not read.");
                    break;
                }

                var record = new FrameRecord { FrameNumber = frameNumber, Flags = flags };

                if ((flags & (uint)FrameRecordFlags.TacticalPos) != 0)
                {
                    if (!reader.TryRead(8, out var tp)) { doc.Truncated = true; break; }
                    record.TacticalPos = new Point2D(
                        BinaryPrimitives.ReadInt32LittleEndian(tp),
                        BinaryPrimitives.ReadInt32LittleEndian(tp[4..]));
                }

                if ((flags & (uint)FrameRecordFlags.Selection) != 0)
                {
                    if (!reader.TryRead(4, out var sc)) { doc.Truncated = true; break; }
                    int count = BinaryPrimitives.ReadInt32LittleEndian(sc);
                    if (count < 0 || count > ReplayFormat.MaxSelectionCount)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} claims {count} selected objects, " +
                                         $"outside 0..{ReplayFormat.MaxSelectionCount}; stopped reading here.");
                        break;
                    }
                    var ids = new uint[count];
                    if (count > 0)
                    {
                        if (!reader.TryRead(count * 4, out var idb)) { doc.Truncated = true; break; }
                        for (int i = 0; i < count; i++)
                            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(idb[(i * 4)..]);
                    }
                    record.SelectionIds = ids;
                }

                if ((flags & (uint)FrameRecordFlags.SideChannel) != 0)
                {
                    if (!reader.TryRead(4, out var scc)) { doc.Truncated = true; break; }
                    int count = BinaryPrimitives.ReadInt32LittleEndian(scc);
                    if (count < 0 || count > ReplayFormat.SideChannelMaxEventsPerFrame)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} claims {count} side-channel records, " +
                                         $"outside 0..{ReplayFormat.SideChannelMaxEventsPerFrame}; stopped reading here.");
                        break;
                    }
                    var list = new SideChannelEvent[count];
                    bool ok = true;
                    for (int i = 0; i < count; i++)
                    {
                        if (!reader.TryRead(ReplayFormat.SideChannelRecordSize, out var sr))
                        { doc.Truncated = true; ok = false; break; }
                        list[i] = ParseSideChannel(sr);
                    }
                    if (!ok) break;
                    record.SideChannel = list;
                }

                if ((flags & (uint)FrameRecordFlags.GameCrc) != 0)
                {
                    if (!reader.TryRead(4, out var cb)) { doc.Truncated = true; break; }
                    record.GameCrc = BinaryPrimitives.ReadUInt32LittleEndian(cb);
                }

                // ReplayFrameCodec.cpp reads census, RNG, speed, selection triggers, house stats,
                // then extensions before gameplay events. This is not numeric flag-bit order.
                // Census and RNG blocks are still readable but no longer written.
                if ((flags & (uint)FrameRecordFlags.ObjectCensus) != 0)
                {
                    if (!reader.TryRead(ReplayFormat.FrameObjectCensusSize, out var nb))
                    { doc.Truncated = true; break; }
                    record.Census = new FrameObjectCensus(
                        BinaryPrimitives.ReadInt32LittleEndian(nb),
                        BinaryPrimitives.ReadInt32LittleEndian(nb[4..]));
                }

                if ((flags & (uint)FrameRecordFlags.RandomState) != 0)
                {
                    if (!reader.TryRead(ReplayFormat.FrameRandomStateSize, out var rb))
                    { doc.Truncated = true; break; }
                    record.RandomState = new FrameRandomState(
                        BinaryPrimitives.ReadInt32LittleEndian(rb),
                        BinaryPrimitives.ReadInt32LittleEndian(rb[4..]));
                }

                if ((flags & (uint)FrameRecordFlags.GameSpeed) != 0)
                {
                    if (!reader.TryRead(4, out var sb)) { doc.Truncated = true; break; }
                    int speed = BinaryPrimitives.ReadInt32LittleEndian(sb);
                    if (speed < 0 || speed > ReplayFormat.MaxGameSpeedIndex)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} has invalid game speed {speed}; stopped reading here.");
                        break;
                    }
                    record.GameSpeed = speed;
                }

                if ((flags & (uint)FrameRecordFlags.SelectionTriggers) != 0)
                {
                    if (!reader.TryRead(4, out var tc)) { doc.Truncated = true; break; }
                    int count = BinaryPrimitives.ReadInt32LittleEndian(tc);
                    if (count <= 0 || count > ReplayFormat.MaxSelectionTriggersPerFrame)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} claims {count} selection triggers, " +
                                         $"outside 1..{ReplayFormat.MaxSelectionTriggersPerFrame}; stopped reading here.");
                        break;
                    }
                    if (!reader.TryRead(count * 4, out var ids)) { doc.Truncated = true; break; }
                    record.SelectionTriggerIds = new uint[count];
                    for (int i = 0; i < count; i++)
                        record.SelectionTriggerIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(ids[(i * 4)..]);
                }

                if ((flags & (uint)FrameRecordFlags.HouseStats) != 0)
                {
                    if (!reader.TryRead(4, out var hc)) { doc.Truncated = true; break; }
                    int count = BinaryPrimitives.ReadInt32LittleEndian(hc);
                    if (count <= 0 || count > ReplayFormat.MaxHouseStatsPerFrame)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} claims {count} house statistics samples, " +
                                         $"outside 1..{ReplayFormat.MaxHouseStatsPerFrame}; stopped reading here.");
                        break;
                    }
                    if (!reader.TryRead(count * ReplayFormat.HouseStatsSampleSize, out var hb))
                    { doc.Truncated = true; break; }
                    record.HouseStats = new HouseStatsSample[count];
                    for (int i = 0; i < count; i++)
                        record.HouseStats[i] = ParseHouseStats(hb.Slice(i * ReplayFormat.HouseStatsSampleSize,
                            ReplayFormat.HouseStatsSampleSize));
                }

                if ((flags & (uint)FrameRecordFlags.Extensions) != 0)
                {
                    if (!reader.TryRead(4, out var eb)) { doc.Truncated = true; break; }
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(eb);
                    if (length > ReplayFormat.MaxFrameExtensionBytes)
                    {
                        doc.Warnings.Add($"Frame {frameNumber} has an extension block of {length} " +
                                         "bytes, past the 1 MiB cap; stopped reading here.");
                        break;
                    }
                    if (!reader.TryRead((int)length, out var ext)) { doc.Truncated = true; break; }
                    record.Extension = ext.ToArray();
                    doc.HasExtensionBlocks = true;
                }

                record.EventStart = eventBlob.Count / ReplayFormat.EventSize;
                record.EventCount = eventCount;
                bool eventsOk = true;
                for (int i = 0; i < eventCount; i++)
                {
                    if (!reader.TryRead(ReplayFormat.EventSize, out var ev))
                    { doc.Truncated = true; eventsOk = false; record.EventCount = i; break; }
                    eventBlob.Append(ev);
                }

                frames.Add(record);
                if (!eventsOk) break;

                if (frames.Count % 20000 == 0)
                    progress?.Report($"Read {frames.Count:N0} frame records...");
            }
        }
        catch (InvalidDataException ex)
        {
            // A deflate stream cut short mid-symbol - the shape a crashed recording leaves.
            doc.Truncated = true;
            doc.Warnings.Add($"The compressed frame stream ends early ({ex.Message.Trim()}). " +
                             "Everything decoded up to that point is intact.");
        }

        doc.Frames = frames;
        doc.EventBlob = eventBlob.ToArray();
        doc.InflatedStreamBytes = reader.TotalRead;

        if (doc.Truncated && !doc.Warnings.Any(w => w.Contains("ends early")))
            doc.Warnings.Add("The frame stream ends part-way through a record. The recording was cut " +
                             "short - the game did not close the file down cleanly.");

        if (!doc.SawEndOfStream && !doc.Truncated)
            doc.Warnings.Add("The frame stream ended without an end-of-stream marker.");
    }

    private static HouseStatsSample ParseHouseStats(ReadOnlySpan<byte> r)
    {
        var f = new int[28];
        for (int i = 0; i < f.Length; i++)
            f[i] = BinaryPrimitives.ReadInt32LittleEndian(r[(i * 4)..]);
        return new HouseStatsSample(
            f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10],
            f[11], f[12], f[13], f[14], f[15], f[16], f[17], f[18], f[19],
            f[20], f[21], f[22], f[23], f[24], f[25], f[26],
            (HouseStatsFlags)(uint)f[27]);
    }

    private static SideChannelEvent ParseSideChannel(ReadOnlySpan<byte> r)
    {
        byte rawType = r[4];

        return new SideChannelEvent
        {
            FrameNumber = BinaryPrimitives.ReadInt32LittleEndian(r),
            RawType = rawType,
            Type = (SideChannelEventType)rawType,
            House = BinaryPrimitives.ReadInt32LittleEndian(r[5..]),
            Aux = BinaryPrimitives.ReadInt32LittleEndian(r[9..]),
            Coord = new Coord3D(
                BinaryPrimitives.ReadInt32LittleEndian(r[13..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[17..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[21..])),
            // Replays get shared, so the text arrays off disk are untrusted and need not be
            // terminated. Read to the first NUL, or to the end of the fixed array.
            SenderName = ReadFixedUtf16(r.Slice(25, ReplayFormat.SideChannelNameLength * 2)),
            Text = ReadFixedUtf16(r.Slice(73, ReplayFormat.SideChannelTextLength * 2)),
        };
    }

    private static string ReadFixedUtf16(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
                return Encoding.Unicode.GetString(bytes[..i]);
        }
        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>Exact-size reads over the inflate stream, which hands back short reads freely.</summary>
    private sealed class StreamCursor(Stream stream)
    {
        private readonly byte[] _scratch = new byte[1 << 16];
        public long TotalRead { get; private set; }

        public bool TryRead(int count, out ReadOnlySpan<byte> data)
        {
            byte[] target = count > _scratch.Length ? new byte[count] : _scratch;
            int got = ReadFully(target, count);
            TotalRead += got;
            data = target.AsSpan(0, got);
            return got == count;
        }

        private int ReadFully(byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    private sealed class GrowableBlob
    {
        private byte[] _data = new byte[1 << 16];
        public int Count { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (Count + bytes.Length > _data.Length)
            {
                int size = _data.Length;
                while (size < Count + bytes.Length) size *= 2;
                Array.Resize(ref _data, size);
            }
            bytes.CopyTo(_data.AsSpan(Count));
            Count += bytes.Length;
        }

        public byte[] ToArray() => _data.AsSpan(0, Count).ToArray();
    }
}
