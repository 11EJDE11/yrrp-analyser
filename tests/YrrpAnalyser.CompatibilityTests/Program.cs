using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using YrrpAnalyser;

// These fixtures deliberately use numeric wire offsets and flags from the C++ format, never
// ReplayFormat constants: changing the parser's layout must not silently change its test input.
int passed = 0, failed = 0;
Run("all 512 frame flag combinations preserve block and event alignment", () =>
{
    for (uint flags = 0; flags < 512; flags++)
    {
        var doc = Load(Fixture(Frames(w =>
        {
            Frame(w, 60, 2, flags);
            if ((flags & 1) != 0) { w.Write(-20); w.Write(800); }
            if ((flags & 2) != 0) { w.Write(2); w.Write(101u); w.Write(0xF1234567u); }
            if ((flags & 4) != 0) { w.Write(1); w.Write(Chat()); }
            if ((flags & 8) != 0) w.Write(0xA1B2C3D4u);
            if ((flags & 32) != 0) { w.Write(345); w.Write(678); }
            if ((flags & 128) != 0) { w.Write(249); w.Write(17); }
            if ((flags & 64) != 0) w.Write(1);
            if ((flags & 256) != 0) { w.Write(2); w.Write(90u); w.Write(0xF1234567u); }
            if ((flags & 16) != 0) { w.Write(3u); w.Write(new byte[] { 0xED, 0xAB, 0xCD }); }
            w.Write(Event(0x04, 3, 64, 0x12345678));
            w.Write(Event(0x1B, -1, 65, 0x87654321));
            Frame(w, 120, 1, 8);
            w.Write(0xCAFEBABEu);
            w.Write(Event(0x02, 1, 121, 0x10203040));
            End(w);
        })));
        Check(doc.SawEndOfStream && !doc.Truncated && doc.Warnings.Count == 0, $"flags 0x{flags:X}: clean parse");
        Equal(2, doc.Frames.Count, "frame count");
        Equal(3, doc.EventCount, "event count");
        var f = doc.Frames[0];
        Equal(flags, f.Flags, "flags");
        Equal((flags & 1) != 0 ? new Point2D(-20, 800) : (Point2D?)null, f.TacticalPos, "camera");
        Sequence((flags & 2) != 0 ? new uint[] { 101, 0xF1234567 } : null, f.SelectionIds, "selection");
        if ((flags & 4) != 0)
        {
            var chat = f.SideChannel!.Single();
            Equal(60, chat.FrameNumber, "chat frame");
            Equal(3, chat.House, "chat house");
            Equal(5, chat.Aux, "chat colour");
            Equal(new Coord3D(11, 22, 33), chat.Coord, "chat coordinates");
            Equal("\u03a9 player", chat.SenderName, "UTF-16 sender");
            Equal("hello \u4e16\u754c", chat.Text, "UTF-16 text");
        }
        else Check(f.SideChannel is null, "absent side channel");
        Equal((flags & 8) != 0 ? 0xA1B2C3D4u : (uint?)null, f.GameCrc, "CRC");
        Equal((flags & 32) != 0 ? new FrameObjectCensus(345, 678) : (FrameObjectCensus?)null, f.Census, "census");
        Equal((flags & 128) != 0 ? new FrameRandomState(249, 17) : (FrameRandomState?)null, f.RandomState, "RNG");
        Equal((flags & 64) != 0 ? 1 : (int?)null, f.GameSpeed, "speed");
        Sequence((flags & 256) != 0 ? new uint[] { 90, 0xF1234567 } : null, f.SelectionTriggerIds, "triggers");
        Sequence((flags & 16) != 0 ? new byte[] { 0xED, 0xAB, 0xCD } : null, f.Extension, "extension");
        Equal((flags & 16) != 0, doc.HasExtensionBlocks, "extension presence");
        var events = doc.EnumerateEvents().ToArray();
        Equal(60, events[0].RecordFrame, "executed frame");
        Equal(64u, events[0].ScheduledFrame, "scheduled frame");
        Equal((sbyte)3, events[0].HouseIndex, "house index");
        Equal(0x12345678u, events[0].U32(0), "event payload");
        Equal((sbyte)-1, events[1].HouseIndex, "signed house index");
        Equal(0x87654321u, events[1].U32(0), "second payload");
        Equal(120, events[2].RecordFrame, "following record");
        Equal(0x10203040u, events[2].U32(0), "following payload");
        Equal(0xCAFEBABEu, doc.Frames[1].GameCrc, "following CRC");
    }
});
Run("1072-byte header fields, embedded UTF-8 metadata, and appended header bytes", () =>
{
    foreach (int extra in new[] { 0, 32 })
    {
        var doc = Load(Fixture(Frames(End), extraHeaderBytes: extra));
        var h = doc.Header;
        Equal(1072u + (uint)extra, h.HeaderSize, "header size");
        Equal(5u, h.GameMode, "game mode");
        Equal(777, h.UniqueIDCounter, "unique ID");
        Equal(-123456, h.Seed, "seed");
        Equal(7, h.RandomNext1, "RNG cursor 1");
        Equal(249, h.RandomNext2, "RNG cursor 2");
        Equal(250, h.RandomizerTable.Length, "RNG table length");
        for (int i = 0; i < 250; i++) Equal(0x10203000u + (uint)i, h.RandomizerTable[i], $"RNG word {i}");
        Equal(2u, h.RecordedGameSpeed, "header game speed");
        Equal(1_800_000_000ul, h.RecordedUnixTime, "timestamp");
        Equal(120u, h.TotalFrames, "total frames");
        Check(h.CleanShutdown, "shutdown flag");
        Check(!h.HasCheckpointArchive && doc.Checkpoints.Count == 0, "no checkpoint archive");
        Equal("Test \u4e16\u754c", doc.MapName, "INI display map");
        Equal("9.8.7", doc.GamePackageVersion, "INI package version");
        Equal("[Basic]\nName=Fallback map\n", doc.SpawnMapText, "embedded map starts at correct offset");
        Equal("198.51.100.20", doc.SpawnIni.GetString("Tunnel", "Ip"), "verbatim INI");
        Check(doc.SawEndOfStream && doc.Warnings.Count == 0, "stream begins at header + INI sizes");
    }
});
Run("empty recording below the old minimum size and campaign metadata fallbacks", () =>
{
    var bytes = Fixture(Frames(End), ini: "", map: "");
    Check(bytes.Length < 1452, "fixture exposes old minimum-size bug");
    var doc = Load(bytes);
    Check(doc.SawEndOfStream && doc.Frames.Count == 0 && !doc.HasEmbeddedMap, "empty complete recording");
    Equal("", doc.MapName, "missing map");
    doc = Load(Fixture(Frames(End), ini: "[Settings]\nScenario=ALL01UMD.MAP\n", map: ""));
    Equal("ALL01UMD.MAP", doc.MapName, "campaign scenario fallback");
    doc = Load(Fixture(Frames(End), ini: "[Settings]\nUIMapName=\n"));
    Equal("Fallback map", doc.MapName, "embedded map fallback");
});
Run("empty optional blocks and game-speed changes", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 0, 0, 2 | 4 | 16);
        w.Write(0); w.Write(0); w.Write(0u);
        Frame(w, 60, 0, 64); w.Write(1);
        Frame(w, 120, 0, 0);
        End(w);
    })));
    Check(doc.HasExtensionBlocks && doc.Frames[0].Extension!.Length == 0, "zero-length extension present");
    Equal(0, doc.Frames[0].SelectionIds!.Length, "empty selection");
    Equal(0, doc.Frames[0].SideChannel!.Length, "empty side channel");
    Check(Math.Abs(doc.GameSpeed.SecondsAt(120) - (2 + 60.0 / 45)) < 1e-9, "piecewise duration");
});
Run("selection-trigger boundaries", () =>
{
    foreach (int count in new[] { 1, 4096 })
    {
        var doc = Load(Fixture(Frames(w =>
        {
            Frame(w, 0, 1, 256);
            w.Write(count);
            for (uint i = 0; i < count; i++) w.Write(i + 1);
            w.Write(Event(2, 0, 0, 123));
            End(w);
        })));
        Equal(count, doc.Frames[0].SelectionTriggerIds!.Length, "trigger count");
        Equal(123u, doc.EnumerateEvents().Single().U32(0), "event after trigger boundary");
        Check(doc.SawEndOfStream && doc.Warnings.Count == 0, "clean boundary parse");
    }
});
Run("invalid header bounds are classified before parsing frames", () =>
{
    foreach (var (offset, value) in new (int, uint)[]
    {
        (8, 1071), (8, uint.MaxValue), (16, uint.MaxValue), (24, uint.MaxValue),
        (24, 250), (28, 250), (1032, 32 * 1024 * 1024 + 1),
        (1036, 32 * 1024 * 1024 + 1), (1040, 7), (1032, 5000),
    })
    {
        var bytes = Fixture(Frames(End));
        Put(bytes, offset, value);
        Reject(bytes, ReplayLoadStatus.CorruptHeader);
    }
    var badMagic = Fixture(Frames(End)); Put(badMagic, 0, 0); Reject(badMagic, ReplayLoadStatus.NotAReplay);
    var version = Fixture(Frames(End)); Put(version, 4, 2); Reject(version, ReplayLoadStatus.UnsupportedVersion);
});
Run("invalid counts, speed, unknown flags, frame sequence, and end markers stop at the valid prefix", () =>
{
    var invalid = new List<Action<BinaryWriter>>();
    foreach (int n in new[] { -1, 0, 4097 })
        invalid.Add(w => { Frame(w, 2, 0, 256); w.Write(n); });
    foreach (int n in new[] { -1, 4097 })
        invalid.Add(w => { Frame(w, 2, 0, 2); w.Write(n); });
    foreach (int n in new[] { -1, 65 })
        invalid.Add(w => { Frame(w, 2, 0, 4); w.Write(n); });
    foreach (int n in new[] { -1, 7 })
        invalid.Add(w => { Frame(w, 2, 0, 64); w.Write(n); });
    invalid.Add(w => Frame(w, 2, 16385, 0));
    invalid.Add(w => Frame(w, 2, -1, 0));
    invalid.Add(w => Frame(w, -2, 0, 0));
    invalid.Add(w => Frame(w, 2, 0, 512));
    invalid.Add(w => Frame(w, 0, 0, 0));
    invalid.Add(w => Frame(w, 1, 0, 0));
    invalid.Add(w => { Frame(w, 2, 0, 16); w.Write(1048577u); });
    invalid.Add(w => Frame(w, -1, 1, 0));
    invalid.Add(w => Frame(w, -1, 0, 8));
    foreach (var write in invalid)
    {
        var doc = Load(Fixture(Frames(w => { Frame(w, 1, 0, 0); write(w); End(w); })));
        Equal(1, doc.Frames.Count, "only valid prefix retained");
        Check(!doc.SawEndOfStream && doc.Warnings.Count > 0, "invalid record diagnosed");
    }
});
Run("truncation at each byte of RNG and selection-trigger records retains the valid prefix", () =>
{
    foreach (var body in new[]
    {
        Frames(w => { Frame(w, 2, 0, 128); w.Write(20); w.Write(40); }),
        Frames(w => { Frame(w, 2, 0, 256); w.Write(2); w.Write(100u); w.Write(200u); }),
    })
    {
        for (int n = 1; n < body.Length; n++)
        {
            var raw = Frames(w => { Frame(w, 1, 0, 0); w.Write(body[..n]); });
            var doc = Load(Fixture(raw));
            Equal(1, doc.Frames.Count, "truncated record excluded");
            Check(doc.Truncated && !doc.SawEndOfStream && doc.Warnings.Count > 0, $"truncation at byte {n}");
        }
    }
});
Run("partial event payloads and a missing end marker remain recoverable", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 10, 2, 0); w.Write(Event(2, 0, 10, 123)); w.Write(new byte[20]);
    })));
    Equal(1, doc.EventCount, "complete event retained");
    Equal(1, doc.Frames.Single().EventCount, "partial event excluded");
    Check(doc.Truncated && !doc.SawEndOfStream, "partial event diagnosed");
    doc = Load(Fixture(Frames(w => Frame(w, 0, 0, 0))));
    Check(!doc.SawEndOfStream && doc.Warnings.Count > 0, "missing terminator diagnosed");
});
Run("raw deflate sync-flush crash recovery", () =>
{
    var raw = Frames(w =>
    {
        Frame(w, 10, 1, 8); w.Write(0xABCDEF12u); w.Write(Event(2, 0, 10, 123));
    });
    var bytes = Fixture(raw, syncFlushOnly: true);
    Put(bytes, 1052, 0); Put(bytes, 1056, 0);
    var doc = Load(bytes);
    Equal(1, doc.EventCount, "flushed event recovered");
    Equal(10, doc.EffectiveFrameCount, "unstamped header uses stream frame");
    Check(!doc.Header.CleanShutdown && !doc.SawEndOfStream, "crash state");
});
Run("exports expose INI metadata, RNG cursors, and selection-trigger IDs", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 0, 0, 128 | 256);
        w.Write(12); w.Write(34);
        w.Write(2); w.Write(456u); w.Write(789u);
        End(w);
    })));
    var path = Path.GetTempFileName();
    try
    {
        Exporters.WriteFrameCrcCsv(path, doc);
        var lines = File.ReadAllLines(path);
        Check(lines[0].EndsWith(",RandomNext1,RandomNext2,SelectionTriggerIDs"), "CSV header");
        Check(lines[1].EndsWith(",12,34,456 789"), "CSV values");
        var network = NetworkAnalysis.Build(doc);
        var activity = ActivityAnalysis.Build(doc, new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni)));
        Exporters.WriteSummaryJson(path, doc, network, activity);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Equal("Test \u4e16\u754c", json.RootElement.GetProperty("metadata").GetProperty("map").GetString(), "JSON map");
        Equal("9.8.7", json.RootElement.GetProperty("metadata").GetProperty("gamePackageVersion").GetString(), "JSON package");
        Check(!json.RootElement.GetProperty("header").TryGetProperty("spawnerVersion", out _), "removed version absent");
        Equal(1, json.RootElement.GetProperty("stream").GetProperty("randomStates").GetInt32(), "JSON RNG count");
        Equal(2, json.RootElement.GetProperty("stream").GetProperty("selectionTriggers").GetInt32(), "JSON triggers");
    }
    finally { File.Delete(path); }
});
Run("checkpoint archive after the frame stream: index, payload split, CRC, and stream size", () =>
{
    Equal(0xCBF43926u, Crc("123456789"u8.ToArray()), "fixture CRC-32 check value");
    var raw = Frames(w => { Frame(w, 10, 0, 0); Frame(w, 120, 0, 0); End(w); });
    long plainStream = Load(Fixture(raw)).CompressedStreamBytes;

    var doc = Load(Fixture(raw, archive: Archive((30, Payload(5000, 300), null), (120, Payload(1, 0), null))));
    Check(doc.SawEndOfStream && !doc.Truncated && doc.Warnings.Count == 0, "archive does not disturb the frame stream");
    Equal(2, doc.Frames.Count, "frames");
    Equal(plainStream, doc.CompressedStreamBytes, "stream size stops at the archive");
    Check(doc.Header.HasCheckpointArchive, "header points at the archive");
    Equal(2, doc.Checkpoints.Count, "checkpoint count");
    Equal(30, doc.Checkpoints[0].Frame, "first frame");
    Equal(120, doc.Checkpoints[1].Frame, "a checkpoint on the last recorded frame is allowed");
    Equal(8u + 5300u, doc.Checkpoints[0].RawSize, "raw size");
    Equal(5000L, doc.Checkpoints[0].SaveBytes, "save split");
    Equal(300L, doc.Checkpoints[0].SidecarBytes, "sidecar split");
    Check(doc.Checkpoints.All(c => c.Usable), "both usable");

    // A payload fault skips that checkpoint only.
    doc = Load(Fixture(raw, archive: Archive(
        (30, Payload(64, 8), 0xDEADBEEF), (60, Payload(0, 8), null),
        (90, Payload(64, 8, trailing: 1), null), (120, Payload(64, 8), null))));
    Equal(4, doc.Checkpoints.Count, "entries kept for display");
    Check(!doc.Checkpoints[0].Usable && doc.Checkpoints[0].Problem!.Contains("CRC"), "CRC mismatch");
    Check(!doc.Checkpoints[1].Usable, "empty save");
    Check(!doc.Checkpoints[2].Usable, "bytes after the sidecar");
    Check(doc.Checkpoints[3].Usable, "later entry still usable");
    Check(doc.SawEndOfStream && doc.Frames.Count == 2 && doc.Warnings.Count > 0, "faults reported, frames intact");

    // An envelope fault makes playback ignore the whole archive.
    foreach (var bad in new Func<byte[]>[]
    {
        () => Fixture(raw, archive: Archive((60, Payload(8, 8), null), (60, Payload(8, 8), null))),
        () => Fixture(raw, archive: Archive((121, Payload(8, 8), null))),
        () => Fixture(raw, archive: Archive(Enumerable.Range(1, 5).Select(i => (i, Payload(8, 8), (uint?)null)).ToArray())),
        () => { var b = Fixture(raw, archive: Archive((10, Payload(8, 8), null))); Put(b, 1068, BitConverter.ToUInt32(b, 1068) - 1); return b; },
        () => [.. Fixture(raw, archive: Archive((10, Payload(8, 8), null))), 0],
    })
    {
        doc = Load(bad());
        Equal(0, doc.Checkpoints.Count, "archive ignored");
        Check(doc.SawEndOfStream && doc.Frames.Count == 2 && doc.Warnings.Count > 0, "frames intact, archive diagnosed");
    }
});


Run("PROCESS_TIME is an integer millisecond window mean, independent of game speed", () =>
{
    foreach (uint speed in new uint[] { 0, 1, 2, 6 })
    {
        var bytes = Fixture(Frames(w =>
        {
            Frame(w, 150, 1, 0); w.Write(Event(0x21, 0, 144, 12));
            Frame(w, 280, 1, 0); w.Write(Event(0x21, 0, 270, 200));
            End(w);
        }));
        Put(bytes, 1040, speed);
        var doc = Load(bytes);
        var network = NetworkAnalysis.Build(doc);
        var s = network.Series.Single();
        Equal(new Sample(150, 12), s.ProcessMs[0], "12 ms is not scaled to 200 ms");
        Equal(new Sample(280, 200), s.ProcessMs[1], "a genuine 200 ms mean remains 200 ms");
        var describer = new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni));
        Check(describer.Describe(doc.EnumerateEvents().First()).StartsWith("12 ms/frame mean main-loop work"),
            "event description uses milliseconds and window mean");
        var path = Path.GetTempFileName();
        try
        {
            Exporters.WriteNetworkCsv(path, doc, network);
            Check(File.ReadAllLines(path).Any(l => l.EndsWith(",ProcessMs,12")), "CSV uses corrected units");
            Exporters.WriteSummaryJson(path, doc, network, ActivityAnalysis.Build(doc, describer));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var player = json.RootElement.GetProperty("network").GetProperty("players")[0];
            Equal(200.0, player.GetProperty("worstProcessMs").GetDouble(), "JSON maximum uses milliseconds");
        }
        finally { File.Delete(path); }
    }
});
Run("ResponseTime2 removes the extra tick and uses the binary's 16 ms clock", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 100, 6, 0);
        foreach (byte encoded in new byte[] { 1, 13, 127, 0, 128, 255 })
            w.Write(Event(0x30, 0, 90, (uint)(encoded | (3 << 8))));
        End(w);
    })));
    var s = NetworkAnalysis.Build(doc).Series.Single();
    Sequence(new[] { 0.0, 192.0, 2016.0 }, s.RoundTripMs.Select(p => p.Value).ToArray(),
        "response +1 removed; zero and signed overflow excluded");
    Equal(6, s.LatencyLevel.Count, "latency requests remain available even without a usable response");
    var describer = new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni));
    var events = doc.EnumerateEvents().ToArray();
    Check(describer.Describe(events[1]).Contains("192 ms"), "description matches response chart");
    Check(describer.Describe(events[4]).Contains("unavailable"), "signed overflow is not a negative RTT");
});
Run("legacy RESPONSE_TIME reads payload byte six as a MaxAhead command, never RTT", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 10, 1, 0);
        var e = Event(0x1B, 0, 10, 99);
        e[13] = 24;
        w.Write(e); End(w);
    })));
    var network = NetworkAnalysis.Build(doc);
    Check(network.Series.All(s => s.RoundTripMs.Count == 0), "legacy command excluded from RTT");
    var describer = new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni));
    Equal("set MaxAhead to 24 frames", describer.Describe(doc.EnumerateEvents().Single()),
        "read EventClass+0x0D, not the first payload byte");
});
Run("TIMING and FRAMEINFO retain scheduling units and distinguish record frames from scheduled frames", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        Frame(w, 100, 2, 0);
        var timing = Event(0x20, 0, 90, 45u | (24u << 16));
        timing[11] = 6;
        w.Write(timing);
        var info = Event(0x1C, 1, 96, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(11), 1234);
        info[13] = 24;
        w.Write(info);
        End(w);
    })));
    var network = NetworkAnalysis.Build(doc);
    var master = network.Series.Single(s => s.HouseIndex == 0);
    var peer = network.Series.Single(s => s.HouseIndex == 1);
    Equal(new Sample(100, 45), master.RequestedFps.Single(), "requested FPS in frames/second");
    Equal(new Sample(100, 6), master.FrameSendRate.Single(), "send interval in simulation frames");
    Equal(new Sample(100, 24), peer.MaxAhead.Single(), "FRAMEINFO delay remains frames");
    var describer = new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni));
    Check(describer.Describe(doc.EnumerateEvents().First()).Contains("45 FPS, MaxAhead 24, FrameSendRate 6"),
        "TIMING payload offsets");
    Check(describer.Describe(doc.EnumerateEvents().Last()).Contains("1234"), "FRAMEINFO command-count offset");
});
Run("large FRAMEINFO gaps report nominal game time across speed changes, without claiming stalls", () =>
{
    var doc = Load(Fixture(Frames(w =>
    {
        for (int frame = 0; frame <= 24; frame += 3)
        {
            Frame(w, frame, 1, 0); w.Write(Event(0x1C, 1, (uint)frame, 0));
        }
        Frame(w, 60, 0, 64); w.Write(1);
        Frame(w, 144, 1, 0); w.Write(Event(0x1C, 1, 140, 0));
        End(w);
    })));
    var network = NetworkAnalysis.Build(doc);
    var gap = network.LargeFrameInfoGaps.Single();
    Equal(24, gap.StartFrame, "gap starts at previous consumption frame");
    Equal(144, gap.EndFrame, "gap ends at next consumption frame");
    Equal(120, gap.Frames, "simulation-frame gap");
    Check(Math.Abs(gap.SimulationSeconds - (36.0 / 30 + 84.0 / 45)) < 1e-9, "game-time span integrates speeds");
    var path = Path.GetTempFileName();
    try
    {
        var describer = new EventDescriber(TypeNameResolver.Load([], doc.SpawnMapIni));
        Exporters.WriteSummaryJson(path, doc, network, ActivityAnalysis.Build(doc, describer));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var net = json.RootElement.GetProperty("network");
        Check(!net.TryGetProperty("stalls", out _), "JSON no longer claims measured stalls");
        Check(net.GetProperty("largeFrameInfoGaps")[0].TryGetProperty("simulationSeconds", out _),
            "gap seconds explicitly describe simulation time");
        Exporters.WriteNetworkCsv(path, doc, network);
        Check(File.ReadAllText(path).Contains(",FrameInfoGapFrames,120"), "CSV labels frame spacing");
    }
    finally { File.Delete(path); }
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Equal<T>(T expected, T actual, string message) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");
static void Sequence<T>(T[]? expected, T[]? actual, string message) =>
    Check(expected is null ? actual is null : actual is not null && expected.SequenceEqual(actual), message);
static void Put(byte[] b, int offset, uint value) =>
    BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
static ReplayDocument Load(byte[] bytes)
{
    string path = Path.GetTempFileName();
    try { File.WriteAllBytes(path, bytes); return ReplayReader.Load(path); }
    finally { File.Delete(path); }
}
static void Reject(byte[] bytes, ReplayLoadStatus expected)
{
    try { Load(bytes); }
    catch (ReplayLoadException ex) { Equal(expected, ex.Status, "load status"); return; }
    throw new Exception($"Expected {expected}");
}
static byte[] Frames(Action<BinaryWriter> write)
{
    using var buffer = new MemoryStream();
    using var writer = new BinaryWriter(buffer);
    write(writer);
    writer.Flush();
    return buffer.ToArray();
}
static void Frame(BinaryWriter w, int number, int events, uint flags)
{
    w.Write(number); w.Write(events); w.Write(flags);
}
static void End(BinaryWriter w) => Frame(w, -1, 0, 0);
static byte[] Event(byte type, sbyte house, uint scheduledFrame, uint payload)
{
    var b = new byte[111];
    b[0] = type; b[1] = 0; b[2] = unchecked((byte)house);
    Put(b, 3, scheduledFrame); Put(b, 7, payload);
    return b;
}
static byte[] Chat()
{
    var b = new byte[329];
    Put(b, 0, 60); b[4] = 1; Put(b, 5, 3); Put(b, 9, 5);
    Put(b, 13, 11); Put(b, 17, 22); Put(b, 21, 33);
    Encoding.Unicode.GetBytes("\u03a9 player").CopyTo(b, 25);
    Encoding.Unicode.GetBytes("hello \u4e16\u754c").CopyTo(b, 73);
    return b;
}
static byte[] Payload(int saveBytes, int sidecarBytes, int trailing = 0)
{
    using var buffer = new MemoryStream();
    using var w = new BinaryWriter(buffer);
    w.Write((uint)saveBytes);
    for (int i = 0; i < saveBytes; i++) w.Write((byte)(i * 7));
    w.Write((uint)sidecarBytes);
    for (int i = 0; i < sidecarBytes; i++) w.Write((byte)(i * 13));
    w.Write(new byte[trailing]);
    w.Flush();
    return buffer.ToArray();
}
static byte[] Archive(params (int Frame, byte[] Payload, uint? Crc)[] entries)
{
    using var buffer = new MemoryStream();
    using var w = new BinaryWriter(buffer);
    w.Write((uint)entries.Length);
    foreach (var (frame, payload, crc) in entries)
    {
        using var deflated = new MemoryStream();
        using (var d = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true)) d.Write(payload);
        w.Write(frame); w.Write((uint)payload.Length); w.Write(crc ?? Crc(payload));
        w.Write((uint)deflated.Length); w.Write(deflated.ToArray());
    }
    w.Flush();
    return buffer.ToArray();
}
// Bitwise CRC-32, independent of the parser's table-driven one.
static uint Crc(byte[] data)
{
    uint c = 0xFFFFFFFFu;
    foreach (byte b in data)
    {
        c ^= b;
        for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
    }
    return ~c;
}
static byte[] Fixture(byte[] raw, int extraHeaderBytes = 0, string? ini = null,
    string? map = null, bool syncFlushOnly = false, byte[]? archive = null)
{
    var spawn = Encoding.UTF8.GetBytes(ini ??
        "\uFEFF[Settings]\nUIMapName=Test \u4e16\u754c\nGamePackageVersion=9.8.7\n" +
        "GameClientVersion=obsolete\nName=Tester\nSide=0\nColor=0\nIsSinglePlayer=yes\n" +
        "[Tunnel]\nIp=198.51.100.20\n");
    var spawnmap = Encoding.UTF8.GetBytes(map ?? "[Basic]\nName=Fallback map\n");
    var h = new byte[1072 + extraHeaderBytes];
    Put(h, 0, 0x50525259); Put(h, 4, 1); Put(h, 8, (uint)h.Length);
    Put(h, 12, 5); Put(h, 16, 777); Put(h, 20, unchecked((uint)-123456));
    Put(h, 24, 7); Put(h, 28, 249);
    for (int i = 0; i < 250; i++) Put(h, 32 + i * 4, 0x10203000u + (uint)i);
    Put(h, 1032, (uint)spawn.Length); Put(h, 1036, (uint)spawnmap.Length); Put(h, 1040, 2);
    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(1044), 1_800_000_000ul);
    Put(h, 1052, 120); Put(h, 1056, 1);
    h.AsSpan(1072).Fill(0xFF);
    using var file = new MemoryStream();
    file.Write(h); file.Write(spawn); file.Write(spawnmap);
    using (var deflate = new DeflateStream(file, CompressionLevel.Optimal, leaveOpen: true))
    {
        deflate.Write(raw);
        if (syncFlushOnly)
        {
            deflate.Flush();
            return file.ToArray(); // Snapshot before disposal writes the final deflate block.
        }
    }
    if (archive is null) return file.ToArray();
    // Appended after the finished stream, then stamped into the header, as FinishRecordingCheckpoints does.
    long archiveOffset = file.Length;
    file.Write(archive);
    var bytes = file.ToArray();
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(1060), (ulong)archiveOffset);
    Put(bytes, 1068, (uint)archive.Length);
    return bytes;
}
