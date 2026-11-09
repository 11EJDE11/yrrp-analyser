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
        result.Sections.Add(Compressed("spawn.ini", header.SpawnIniRawSize, header.SpawnIniSize));
        result.Sections.Add(Compressed("spawnmap.ini (the map)", header.SpawnMapRawSize, header.SpawnMapSize));

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

    private static SizeEntry Compressed(string name, long rawBytes, long storedBytes) =>
        new() { Name = name, RawBytes = rawBytes, StoredBytes = storedBytes };

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
    private const string Framing = "Block tags and lengths";
    private const string Camera = "Camera position";
    private const string Selection = "Selection";
    private const string Chat = "Chat and beacons";
    private const string Crc = "State hash (desync check)";
    private const string Speed = "Game speed changes";
    private const string Triggers = "Selection triggers";
    private const string HouseStats = "House statistics samples";
    private const string MoneyIn = "Income payments";
    private const string Seals = "Canonical seals";
    private const string Dropped = "Players dropped outside the events";
    private const string Unknown = "Blocks this build does not know";
    private const string Unparsed = "Unparsed (after a damaged record)";

    /// <summary>Feeds each part of the stream to its bucket, as <see cref="FrameStream"/> walks it.</summary>
    private sealed class BucketSink(Buckets buckets) : IFramePartSink
    {
        public long Consumed;

        public void Add(FramePart part, ReadOnlySpan<byte> bytes, int count, byte eventType)
        {
            Consumed += bytes.Length;
            var bucket = part switch
            {
                FramePart.Event => buckets.Event(eventType),
                FramePart.RecordHead => buckets[Headers],
                FramePart.Hash => buckets[Crc],
                FramePart.BlockFraming => buckets[Framing],
                FramePart.Camera => buckets[Camera],
                FramePart.Selection => buckets[Selection],
                FramePart.Chat => buckets[Chat],
                FramePart.Speed => buckets[Speed],
                FramePart.Triggers => buckets[Triggers],
                FramePart.HouseStats => buckets[HouseStats],
                FramePart.MoneyIn => buckets[MoneyIn],
                FramePart.Seal => buckets[Seals],
                FramePart.DroppedHouses => buckets[Dropped],
                _ => buckets[Unknown],
            };
            bucket.Add(bytes, count);
        }
    }

    private static SizeEntry MeasureFrameStream(Stream file, long compressedBytes, CancellationToken cancel)
    {
        using var buckets = new Buckets();
        var raw = FrameStream.Inflate(file, out _);
        cancel.ThrowIfCancellationRequested();
        var sink = new BucketSink(buckets);
        FrameStream.Parse(raw, sink);
        // Whatever follows a record the walk could not make sense of still took space.
        if (sink.Consumed < raw.Length)
            buckets[Unparsed].Add(raw.AsSpan((int)sink.Consumed));

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
        };
        foreach (var name in new[] { HouseStats, MoneyIn, Selection, Camera, Chat, Crc, Headers, Framing, Speed, Triggers,
                     Seals, Dropped, Unknown, Unparsed })
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
        var section = Compressed("Statistics section", header.StatisticsRawSize, header.StatisticsSize);
        var stored = new byte[header.StatisticsSize];
        file.Position = (long)header.StatisticsOffset;
        file.ReadExactly(stored);

        var bytes = new byte[Math.Min(header.StatisticsRawSize, ReplayFormat.MaxStatisticsSectionBytes)];
        try
        {
            using var inflate = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
            inflate.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return section;
        }

        // The section is one deflate stream, so its chunks share its compressed bytes out by size.
        double share = header.StatisticsSize / (double)Math.Max(1, bytes.Length);

        int pos = 0;
        while (bytes.Length - pos >= 8)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4));
            if (length > bytes.Length - pos - 8) break;
            section.Children.Add(new SizeEntry
            {
                Name = ChunkName(tag), RawBytes = length + 8,
                StoredBytes = (long)Math.Round((length + 8) * share), StoredIsEstimate = true,
            });
            pos += 8 + (int)length;
        }

        if (pos < bytes.Length)
            section.Children.Add(new SizeEntry
            {
                Name = "Unparsed", RawBytes = bytes.Length - pos,
                StoredBytes = (long)Math.Round((bytes.Length - pos) * share), StoredIsEstimate = true,
            });
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
        if (tag == ReplayFormat.ChunkSeals) return "Canonical seals (SEAL)";
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)((tag >> (i * 8)) & 0xFF);
            chars[i] = char.IsAsciiLetterOrDigit(ch) ? ch : '?';
        }
        return $"Unknown chunk ({new string(chars)})";
    }

    #endregion

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
