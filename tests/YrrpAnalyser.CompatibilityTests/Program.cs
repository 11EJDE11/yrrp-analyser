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
Run("1124-byte header fields, embedded UTF-8 metadata, and appended header bytes", () =>
{
    foreach (int extra in new[] { 0, 32 })
    {
        var doc = Load(Fixture(Frames(End), extraHeaderBytes: extra));
        var h = doc.Header;
        Equal(1124u + (uint)extra, h.HeaderSize, "header size");
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
        for (int i = 0; i < 16; i++) Equal(0xABCDE000u + (uint)i, h.Reserved[i], $"reserved word {i}");
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
        (8, 1123), (8, uint.MaxValue), (16, uint.MaxValue), (24, uint.MaxValue),
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
static byte[] Fixture(byte[] raw, int extraHeaderBytes = 0, string? ini = null,
    string? map = null, bool syncFlushOnly = false)
{
    var spawn = Encoding.UTF8.GetBytes(ini ??
        "\uFEFF[Settings]\nUIMapName=Test \u4e16\u754c\nGamePackageVersion=9.8.7\n" +
        "GameClientVersion=obsolete\nName=Tester\nSide=0\nColor=0\nIsSinglePlayer=yes\n" +
        "[Tunnel]\nIp=198.51.100.20\n");
    var spawnmap = Encoding.UTF8.GetBytes(map ?? "[Basic]\nName=Fallback map\n");
    var h = new byte[1124 + extraHeaderBytes];
    Put(h, 0, 0x50525259); Put(h, 4, 1); Put(h, 8, (uint)h.Length);
    Put(h, 12, 5); Put(h, 16, 777); Put(h, 20, unchecked((uint)-123456));
    Put(h, 24, 7); Put(h, 28, 249);
    for (int i = 0; i < 250; i++) Put(h, 32 + i * 4, 0x10203000u + (uint)i);
    Put(h, 1032, (uint)spawn.Length); Put(h, 1036, (uint)spawnmap.Length); Put(h, 1040, 2);
    BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(1044), 1_800_000_000ul);
    Put(h, 1052, 120); Put(h, 1056, 1);
    for (int i = 0; i < 16; i++) Put(h, 1060 + i * 4, 0xABCDE000u + (uint)i);
    h.AsSpan(1124).Fill(0xFF);
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
    return file.ToArray();
}
