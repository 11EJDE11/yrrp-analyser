using System.Buffers.Binary;
using System.IO.Compression;

namespace YrrpAnalyser;

/// <summary>One part of the file: a section, a frame block kind, an event type or a save.</summary>
public sealed class SizeEntry
{
    public required string Name { get; init; }
    /// <summary>How many of it there are - records, events, samples - or 0 where a count means nothing.</summary>
    public long Count { get; set; }
    /// <summary>Bytes before compression.</summary>
    public long RawBytes { get; set; }
    /// <summary>Bytes it takes in the file.</summary>
    public long StoredBytes { get; set; }
    /// <summary>
    /// Whether <see cref="StoredBytes"/> is an estimate. Everything inside a deflate stream shares the
    /// stream's compressed bytes, so no part of it has an exact size on disk.
    /// </summary>
    public bool StoredIsEstimate { get; set; }
    public List<SizeEntry> Children { get; } = [];
}

/// <summary>
/// Where a replay's bytes go. Uncompressed sizes are exact. The frame stream and each checkpoint are
/// single deflate streams, so their parts have no size on disk of their own: each part is compressed
/// on its own, in stream order, and the stream's real compressed size is shared out in proportion.
/// That is close for parts that compress independently of each other, which the stream's parts
/// mostly do - they are different record kinds, and deflate's 32 KB window rarely matches across them.
///
/// It re-reads the file rather than working from <see cref="ReplayDocument"/>, which does not keep
/// the stream's raw bytes, and it is slow enough (a second or two for an hour-long game) that it
/// runs only when asked for.
/// </summary>
public sealed class SizeBreakdown
{
    public long FileSize { get; init; }
    public double GameMinutes { get; init; }
    public List<SizeEntry> Sections { get; } = [];

    // A zlib level close to the recorder's 128-probe miniz setting, so the shares come out as the
    // recorder would have produced them.
    private const CompressionLevel EstimateLevel = CompressionLevel.Optimal;

    public static SizeBreakdown Measure(ReplayDocument doc, CancellationToken cancel = default)
    {
        var header = doc.Header;
        using var file = new FileStream(doc.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 1 << 16);

        var result = new SizeBreakdown
        {
            FileSize = file.Length,
            GameMinutes = doc.Duration.TotalMinutes,
        };

        long streamOffset = header.HeaderSize + (long)header.SpawnIniSize + header.SpawnMapSize;

        result.Sections.Add(Exact("Header", header.HeaderSize));
        result.Sections.Add(Exact("spawn.ini", header.SpawnIniSize));
        result.Sections.Add(Exact("spawnmap.ini (the map)", header.SpawnMapSize));

        file.Position = streamOffset;
        result.Sections.Add(MeasureFrameStream(file, doc.CompressedStreamBytes, cancel));

        if (doc.Checkpoints.Count > 0 && header.HasCheckpointArchive)
            result.Sections.Add(MeasureCheckpoints(file, doc, cancel));

        if (header.HasStatisticsSection && header.StatisticsSize > 0
            && header.StatisticsOffset + header.StatisticsSize <= (ulong)file.Length)
            result.Sections.Add(MeasureStatistics(file, header));

        long accounted = result.Sections.Sum(s => s.StoredBytes);
        if (accounted != result.FileSize)
            result.Sections.Add(Exact("Unaccounted", result.FileSize - accounted));

        return result;
    }

    private static SizeEntry Exact(string name, long bytes, long count = 0) =>
        new() { Name = name, RawBytes = bytes, StoredBytes = bytes, Count = count };

    #region Frame stream

    /// <summary>A category of stream bytes, compressed on its own to estimate its share of the real stream.</summary>
    private sealed class Bucket(string name) : IDisposable
    {
        private readonly CountingStream _sink = new();
        private DeflateStream? _deflate;

        public string Name { get; } = name;
        public long Count;
        public long RawBytes;

        public void Add(ReadOnlySpan<byte> bytes, long count = 0)
        {
            Count += count;
            RawBytes += bytes.Length;
            _deflate ??= new DeflateStream(_sink, EstimateLevel, leaveOpen: true);
            _deflate.Write(bytes);
        }

        public long FinishCompressed()
        {
            _deflate?.Dispose();
            _deflate = null;
            return _sink.Length;
        }

        public void Dispose() => _deflate?.Dispose();
    }

    private sealed class Buckets : IDisposable
    {
        private readonly Dictionary<string, Bucket> _named = [];
        public readonly Bucket?[] EventTypes = new Bucket?[256];

        public Bucket this[string name] =>
            _named.TryGetValue(name, out var b) ? b : _named[name] = new Bucket(name);

        public Bucket Event(byte type) =>
            EventTypes[type] ??= new Bucket(YrrpAnalyser.EventTypes.Name((EventType)type));

        public IEnumerable<Bucket> All => _named.Values.Concat(EventTypes.OfType<Bucket>());

        public void Dispose()
        {
            foreach (var b in All) b.Dispose();
        }
    }

    private const string Headers = "Frame record headers";
    private const string Camera = "Camera position";
    private const string Selection = "Selection";
    private const string Chat = "Chat and beacons";
    private const string Crc = "State hash (desync check)";
    private const string Census = "Object census (old recordings)";
    private const string Random = "RNG cursors (old recordings)";
    private const string Speed = "Game speed changes";
    private const string Triggers = "Selection triggers";
    private const string HouseStats = "House statistics samples";
    private const string MoneyIn = "Income payments";
    private const string ObjectHeaders = "Block headers";
    private const string ObjectAppear = "Appeared";
    private const string ObjectUpdate = "Moved or changed";
    private const string ObjectGone = "Left the map";
    private const string Extensions = "Extension blocks";
    private const string Unparsed = "Unparsed (after a damaged record)";

    private static SizeEntry MeasureFrameStream(Stream file, long compressedBytes, CancellationToken cancel)
    {
        using var buckets = new Buckets();
        using var inflate = new DeflateStream(file, CompressionMode.Decompress, leaveOpen: true);
        var cursor = new RawCursor(inflate);

        try
        {
            ReadRecords(cursor, buckets, cancel);
            // Whatever follows a record the walk could not make sense of still took space.
            while (cursor.Read(1 << 16, out var rest) > 0)
                buckets[Unparsed].Add(rest);
        }
        catch (InvalidDataException)
        {
            // A cut-short recording: what was measured stands.
        }

        var stream = new SizeEntry { Name = "Frame stream", StoredBytes = compressedBytes };
        var estimates = new Dictionary<Bucket, long>();
        foreach (var bucket in buckets.All)
            estimates[bucket] = bucket.FinishCompressed();
        long estimateTotal = Math.Max(1, estimates.Values.Sum());

        SizeEntry Leaf(Bucket b) => new()
        {
            Name = b.Name,
            Count = b.Count,
            RawBytes = b.RawBytes,
            StoredBytes = (long)Math.Round(compressedBytes * (double)estimates[b] / estimateTotal),
            StoredIsEstimate = true,
        };

        SizeEntry Group(string name, IEnumerable<Bucket> members)
        {
            var group = new SizeEntry { Name = name, StoredIsEstimate = true };
            foreach (var b in members.Where(b => b.RawBytes > 0))
                group.Children.Add(Leaf(b));
            group.Children.Sort((x, y) => y.StoredBytes.CompareTo(x.StoredBytes));
            group.Count = group.Children.Sum(c => c.Count);
            group.RawBytes = group.Children.Sum(c => c.RawBytes);
            group.StoredBytes = group.Children.Sum(c => c.StoredBytes);
            return group;
        }

        var gameplay = new List<Bucket>();
        var network = new List<Bucket>();
        for (int type = 0; type < 256; type++)
        {
            if (buckets.EventTypes[type] is not { } b) continue;
            (EventTypes.IsTiming((EventType)type) ? network : gameplay).Add(b);
        }

        var parts = new List<SizeEntry>
        {
            Group("Events: player orders and commands", gameplay),
            Group("Events: network and timing", network),
            Group("Object snapshots", [buckets[ObjectHeaders], buckets[ObjectAppear], buckets[ObjectUpdate], buckets[ObjectGone]]),
        };
        foreach (var name in new[] { HouseStats, MoneyIn, Selection, Camera, Chat, Crc, Headers, Speed, Triggers,
                     Census, Random, Extensions, Unparsed })
        {
            var b = buckets[name];
            if (b.RawBytes > 0) parts.Add(Leaf(b));
        }

        parts.RemoveAll(p => p.RawBytes == 0);
        parts.Sort((x, y) => y.StoredBytes.CompareTo(x.StoredBytes));
        stream.Children.AddRange(parts);
        stream.RawBytes = parts.Sum(p => p.RawBytes);

        // Rounding each share can leave the parts a few bytes off the stream; give that to the largest.
        long drift = compressedBytes - parts.Sum(p => p.StoredBytes);
        if (parts.Count > 0 && drift != 0) Nudge(parts[0], drift);
        return stream;
    }

    private static void Nudge(SizeEntry entry, long drift)
    {
        entry.StoredBytes += drift;
        if (entry.Children.Count > 0) Nudge(entry.Children[0], drift);
    }

    /// <summary>
    /// Walks the records in the order ReplayFrameCodec.cpp writes their blocks. The reader has already
    /// validated the stream, so this only needs to stop, not to explain, when something does not fit.
    /// </summary>
    private static void ReadRecords(RawCursor cursor, Buckets buckets, CancellationToken cancel)
    {
        int records = 0;
        while (true)
        {
            if ((++records & 0x3FFF) == 0) cancel.ThrowIfCancellationRequested();

            if (!cursor.TryRead(ReplayFormat.FrameRecordHeaderSize, out var fh)) return;
            buckets[Headers].Add(fh, 1);
            int frame = BinaryPrimitives.ReadInt32LittleEndian(fh);
            int events = BinaryPrimitives.ReadInt32LittleEndian(fh[4..]);
            var flags = (FrameRecordFlags)BinaryPrimitives.ReadUInt32LittleEndian(fh[8..]);
            if (frame == -1) return;
            if (events < 0 || events > ReplayFormat.MaxEventsPerFrame || (flags & ~FrameRecordFlags.Known) != 0)
                return;

            bool Fixed(FrameRecordFlags flag, string name, int size)
            {
                if ((flags & flag) == 0) return true;
                if (!cursor.TryRead(size, out var b)) return false;
                buckets[name].Add(b, 1);
                return true;
            }

            // A count, then that many fixed-size items; the count's bytes belong to the block.
            bool Counted(FrameRecordFlags flag, string name, int itemSize, int max)
            {
                if ((flags & flag) == 0) return true;
                if (!cursor.TryRead(4, out var cb)) return false;
                int count = BinaryPrimitives.ReadInt32LittleEndian(cb);
                buckets[name].Add(cb);
                if (count < 0 || count > max) return false;
                if (count == 0) return true;
                if (!cursor.TryRead(count * itemSize, out var items)) return false;
                buckets[name].Add(items, count);
                return true;
            }

            if (!Fixed(FrameRecordFlags.TacticalPos, Camera, 8)
                || !Counted(FrameRecordFlags.Selection, Selection, 4, ReplayFormat.MaxSelectionCount)
                || !Counted(FrameRecordFlags.SideChannel, Chat, ReplayFormat.SideChannelRecordSize,
                    ReplayFormat.SideChannelMaxEventsPerFrame)
                || !Fixed(FrameRecordFlags.GameCrc, Crc, 4)
                || !Fixed(FrameRecordFlags.ObjectCensus, Census, ReplayFormat.FrameObjectCensusSize)
                || !Fixed(FrameRecordFlags.RandomState, Random, ReplayFormat.FrameRandomStateSize)
                || !Fixed(FrameRecordFlags.GameSpeed, Speed, 4)
                || !Counted(FrameRecordFlags.SelectionTriggers, Triggers, 4, ReplayFormat.MaxSelectionTriggersPerFrame)
                || !Counted(FrameRecordFlags.HouseStats, HouseStats, ReplayFormat.HouseStatsSampleSize,
                    ReplayFormat.MaxHouseStatsPerFrame)
                || !Counted(FrameRecordFlags.MoneyIn, MoneyIn, ReplayFormat.MoneyInRecordSize, ReplayFormat.MaxMoneyInPerFrame))
                return;

            if ((flags & FrameRecordFlags.Objects) != 0)
            {
                if (!cursor.TryRead(ReplayFormat.ObjectsBlockHeaderSize, out var oh)) return;
                buckets[ObjectHeaders].Add(oh, 1);
                int appeared = BinaryPrimitives.ReadUInt16LittleEndian(oh);
                int updated = BinaryPrimitives.ReadUInt16LittleEndian(oh[2..]);
                int gone = BinaryPrimitives.ReadUInt16LittleEndian(oh[4..]);
                foreach (var (name, count) in new[] { (ObjectAppear, appeared), (ObjectUpdate, updated), (ObjectGone, gone) })
                {
                    if (count == 0) continue;
                    if (!cursor.TryRead(count * ReplayFormat.ObjectRecordSize, out var records2)) return;
                    buckets[name].Add(records2, count);
                }
            }

            if ((flags & FrameRecordFlags.Extensions) != 0)
            {
                if (!cursor.TryRead(4, out var lb)) return;
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(lb);
                buckets[Extensions].Add(lb, 1);
                if (length > ReplayFormat.MaxFrameExtensionBytes) return;
                if (length > 0)
                {
                    if (!cursor.TryRead((int)length, out var ext)) return;
                    buckets[Extensions].Add(ext);
                }
            }

            for (int i = 0; i < events; i++)
            {
                if (!cursor.TryRead(ReplayFormat.EventSize, out var ev)) return;
                buckets.Event(ev[0]).Add(ev, 1);
            }
        }
    }

    #endregion

    #region Trailing sections

    private static SizeEntry MeasureCheckpoints(Stream file, ReplayDocument doc, CancellationToken cancel)
    {
        var header = doc.Header;
        var archive = new SizeEntry
        {
            Name = "Embedded saves (checkpoints)",
            Count = doc.Checkpoints.Count,
            StoredBytes = header.CheckpointArchiveSize,
        };

        foreach (var c in doc.Checkpoints)
        {
            cancel.ThrowIfCancellationRequested();

            var entry = new SizeEntry
            {
                Name = $"Save at {doc.TimeLabel(c.Frame)} (frame {c.Frame:N0})",
                Count = 1,
                RawBytes = c.RawSize,
                StoredBytes = c.CompressedSize,
            };
            archive.Children.Add(entry);

            if (!c.Usable || c.SaveBytes <= 0) continue;

            file.Position = c.CompressedOffset;
            var compressed = new byte[c.CompressedSize];
            file.ReadExactly(compressed);
            var raw = new byte[c.RawSize];
            try
            {
                using var inflate = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress);
                inflate.ReadExactly(raw);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            // uint32 saveBytes, save, uint32 sidecarBytes, sidecar.
            var save = raw.AsSpan(4, (int)c.SaveBytes);
            var sidecar = raw.AsSpan(8 + (int)c.SaveBytes, (int)c.SidecarBytes);
            long saveAlone = CompressedLength(save), sidecarAlone = CompressedLength(sidecar);
            long saveShare = (long)Math.Round(c.CompressedSize * (double)saveAlone / Math.Max(1, saveAlone + sidecarAlone));

            entry.Children.Add(new SizeEntry
            {
                Name = "Savegame (.SAV)", Count = 1, RawBytes = save.Length + 4,
                StoredBytes = saveShare, StoredIsEstimate = true,
            });
            entry.Children.Add(new SizeEntry
            {
                Name = "Sidecar (state the save loses)", Count = 1, RawBytes = sidecar.Length + 4,
                StoredBytes = c.CompressedSize - saveShare, StoredIsEstimate = true,
            });
        }

        long payloads = archive.Children.Sum(c => c.StoredBytes);
        archive.RawBytes = archive.Children.Sum(c => c.RawBytes) + (header.CheckpointArchiveSize - payloads);
        if (header.CheckpointArchiveSize > payloads)
            archive.Children.Add(Exact("Archive index", header.CheckpointArchiveSize - payloads));
        return archive;
    }

    private static long CompressedLength(ReadOnlySpan<byte> bytes)
    {
        var sink = new CountingStream();
        using (var deflate = new DeflateStream(sink, EstimateLevel, leaveOpen: true))
            deflate.Write(bytes);
        return sink.Length;
    }

    private static SizeEntry MeasureStatistics(Stream file, ReplayHeaderInfo header)
    {
        var section = Exact("Statistics section", header.StatisticsSize);
        var bytes = new byte[header.StatisticsSize];
        file.Position = (long)header.StatisticsOffset;
        file.ReadExactly(bytes);

        int pos = 0;
        while (bytes.Length - pos >= 8)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
            if (length > bytes.Length - pos - 8) break;
            section.Children.Add(Exact(ChunkName(tag), length + 8));
            pos += 8 + (int)length;
        }

        if (pos < bytes.Length)
            section.Children.Add(Exact("Unparsed", bytes.Length - pos));
        section.Children.Sort((x, y) => y.StoredBytes.CompareTo(x.StoredBytes));
        return section;
    }

    private static string ChunkName(uint tag)
    {
        if (tag == ReplayFormat.ChunkTypes) return "Type table (TYPE)";
        if (tag == ReplayFormat.ChunkHouses) return "End-of-game house records (HOUS)";
        if (tag == ReplayFormat.ChunkStatsPacket) return "Game's statistics packet (STAT)";
        if (tag == ReplayFormat.ChunkGame) return "Game record (GAME)";
        if (tag == ReplayFormat.ChunkModules) return "Loaded modules (MODS)";
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)((tag >> (i * 8)) & 0xFF);
            chars[i] = char.IsAsciiLetterOrDigit(ch) ? ch : '?';
        }
        return $"Unknown chunk ({new string(chars)})";
    }

    #endregion

    /// <summary>Exact-size reads that hand back the bytes, over a stream that returns short reads freely.</summary>
    private sealed class RawCursor(Stream stream)
    {
        private byte[] _buffer = new byte[1 << 16];

        public bool TryRead(int count, out ReadOnlySpan<byte> data) => Read(count, out data) == count;

        public int Read(int count, out ReadOnlySpan<byte> data)
        {
            if (count > _buffer.Length) _buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(_buffer, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            data = _buffer.AsSpan(0, total);
            return total;
        }
    }

    /// <summary>A write-only stream that only counts, so an estimate never holds its compressed bytes.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
