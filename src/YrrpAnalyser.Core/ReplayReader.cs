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

        var doc = new ReplayDocument
        {
            FilePath = path,
            FileSize = file.Length,
            Header = header,
            SpawnIniText = DecodeIni(spawnIniBytes),
            SpawnMapText = DecodeIni(spawnMapBytes),
            CompressedStreamBytes = file.Length - streamOffset,
        };

        progress?.Report("Inflating frame stream...");
        file.Position = streamOffset;
        ReadFrameStream(file, doc, progress);

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

        var reserved = new uint[ReplayFormat.ReservedLength];
        for (int i = 0; i < reserved.Length; i++)
            reserved[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                h.AsSpan(ReplayFormat.OffsetReserved + i * 4));

        return new ReplayHeaderInfo
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetMagic)),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetVersion)),
            HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(ReplayFormat.OffsetHeaderSize)),
            MapName = ReadFixedString(h, ReplayFormat.OffsetMapName, ReplayFormat.MapNameLength),
            SpawnerVersionMajor = h[ReplayFormat.OffsetSpawnerVersion + 0],
            SpawnerVersionMinor = h[ReplayFormat.OffsetSpawnerVersion + 1],
            SpawnerVersionRevision = h[ReplayFormat.OffsetSpawnerVersion + 2],
            SpawnerVersionPatch = h[ReplayFormat.OffsetSpawnerVersion + 3],
            GameClientVersion = ReadFixedString(h, ReplayFormat.OffsetGameClientVersion,
                ReplayFormat.GameClientVersionLength),
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
            Reserved = reserved,
        };
    }

    private static string ReadFixedString(byte[] buffer, int offset, int length)
    {
        var span = buffer.AsSpan(offset, length);
        int end = span.IndexOf((byte)0);
        if (end < 0) end = length;
        return Encoding.UTF8.GetString(span[..end]).Trim();
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
                if (!reader.TryRead(ReplayFormat.FrameRecordHeaderSize, out var fh)) break;

                int frameNumber = BinaryPrimitives.ReadInt32LittleEndian(fh);
                int eventCount = BinaryPrimitives.ReadInt32LittleEndian(fh[4..]);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(fh[8..]);

                if (frameNumber == -1)
                {
                    doc.SawEndOfStream = true;
                    break;
                }

                if (frameNumber < 0 || eventCount < 0)
                {
                    doc.Warnings.Add($"Frame record {frames.Count} has a negative frame number or " +
                                     $"event count ({frameNumber}, {eventCount}); stopped reading here.");
                    break;
                }

                if ((flags & ~(uint)FrameRecordFlags.Known) != 0)
                {
                    // Blocks are stored bare and in flag order, so an unknown flag means the end
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

                // Census and game speed are read here, ahead of the extension block, because that
                // is the order the writer puts them in - not the numeric order of their flag bits.
                // The extension block stays physically last on purpose: it is the only block that
                // carries its own length, so anything written after it would be unreachable to a
                // reader that stepped over it.
                if ((flags & (uint)FrameRecordFlags.ObjectCensus) != 0)
                {
                    if (!reader.TryRead(ReplayFormat.FrameObjectCensusSize, out var nb))
                    { doc.Truncated = true; break; }
                    record.Census = new FrameObjectCensus(
                        BinaryPrimitives.ReadInt32LittleEndian(nb),
                        BinaryPrimitives.ReadInt32LittleEndian(nb[4..]));
                }

                if ((flags & (uint)FrameRecordFlags.GameSpeed) != 0)
                {
                    if (!reader.TryRead(4, out var sb)) { doc.Truncated = true; break; }
                    record.GameSpeed = BinaryPrimitives.ReadInt32LittleEndian(sb);
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
                    if (length > 0)
                    {
                        if (!reader.TryRead((int)length, out var ext)) { doc.Truncated = true; break; }
                        record.Extension = ext.ToArray();
                        doc.HasExtensionBlocks = true;
                    }
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
