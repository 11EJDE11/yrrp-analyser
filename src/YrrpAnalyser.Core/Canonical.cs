using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace YrrpAnalyser;

/// <summary>
/// The canonical form of a recording - the part every player's copy of the same game shares - and the
/// SHA-256 chain the recorder seals over it. A mirror of yrpp-spawner/src/Replay/ReplayCanonical.cpp:
/// any difference makes every seal disagree, so a change there is a change here.
/// </summary>
public static class Canonical
{
    // MEGAMISSION payload offsets: see ReplayCanonical.cpp.
    private const int MegaMissionWhomEnd = 5;
    private const int MegaMissionSlot = 6;
    private const int MegaMissionFollowBegin = 17;
    private const int MegaMissionFollowEnd = 22;

    public static bool IsCanonicalEventType(byte type) => type switch
    {
        0 or 25 or 26 or 27 or 28 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 => false,
        < 47 => true,
        _ => type == ReplayFormat.EventChatFingerprint,
    };

    private sealed record Player(bool IsHuman, bool IsObserver, int Color, int Country, int Difficulty, string Name);

    /// <summary>The players spawn.ini describes, read the way ReadCanonicalPlayers reads them.</summary>
    private static List<Player> ReadPlayers(string spawnIni)
    {
        var ini = ParseIni(spawnIni);
        string[] sections = ["settings", "other1", "other2", "other3", "other4", "other5", "other6", "other7"];
        var players = new List<Player>();

        for (int i = 0; i < 8; i++)
        {
            string tag = $"multi{i + 1}";
            if (Find(ini, sections[i], "name") is { Length: > 0 } name)
            {
                players.Add(new Player(true, ReadBool(ini, sections[i], "isspectator"),
                    ReadInt(ini, sections[i], "color"), ReadInt(ini, sections[i], "side"), -1, name));
                continue;
            }

            int color = ReadInt(ini, "housecolors", tag);
            int country = ReadInt(ini, "housecountries", tag);
            int difficulty = ReadInt(ini, "househandicaps", tag);
            if (color < 0 && country < 0 && difficulty < 0) continue;
            players.Add(new Player(false, false, color, country, difficulty, ""));
        }

        return players;
    }

    // The spawner's deliberately small INI reader: [section] and key=value lines, names without case,
    // values trimmed of spaces, tabs and carriage returns, the first occurrence kept.
    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? section = null;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim(' ', '\t', '\r');
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                string name = line[1..^1].Trim(' ', '\t', '\r').ToLowerInvariant();
                if (!sections.TryGetValue(name, out section))
                    sections[name] = section = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            int equals = line.IndexOf('=');
            if (section is null || equals < 0) continue;
            section.TryAdd(line[..equals].Trim(' ', '\t', '\r').ToLowerInvariant(), line[(equals + 1)..].Trim(' ', '\t', '\r'));
        }

        return sections;
    }

    private static string? Find(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    // strtol: optional sign, then digits; whatever follows is ignored.
    private static int ReadInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key)
    {
        if (Find(ini, section, key) is not { } value) return -1;
        int i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        bool negative = i < value.Length && value[i] == '-';
        if (i < value.Length && (value[i] == '-' || value[i] == '+')) i++;
        long result = 0;
        while (i < value.Length && value[i] is >= '0' and <= '9' && result < int.MaxValue)
            result = result * 10 + (value[i++] - '0');
        return (int)Math.Clamp(negative ? -result : result, int.MinValue, int.MaxValue);
    }

    private static bool ReadBool(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        Find(ini, section, key) is { Length: > 0 } value && "1tTyY".Contains(value[0]);

    public static byte[] BuildDescriptor(ReplayDocument doc)
    {
        var h = doc.Header;
        var out_ = new List<byte>();
        out_.AddRange("YRRPSEAL"u8.ToArray());
        AddU32(out_, 1);
        AddU32(out_, h.GameMode);
        AddI32(out_, h.UniqueIDCounter);
        AddI32(out_, h.Seed);
        AddI32(out_, h.RandomNext1);
        AddI32(out_, h.RandomNext2);
        foreach (var v in h.RandomizerTable) AddU32(out_, v);
        AddU32(out_, h.RecordedGameSpeed);
        out_.AddRange(SHA256.HashData(doc.SpawnMapBytes));

        var entries = ReadPlayers(doc.SpawnIniText).Select(p =>
        {
            var e = new List<byte> { (byte)(p.IsHuman ? 1 : 0), (byte)(p.IsObserver ? 1 : 0) };
            AddI32(e, p.Color);
            AddI32(e, p.Country);
            AddI32(e, p.Difficulty);
            var name = Encoding.UTF8.GetBytes(p.Name);
            AddU32(e, (uint)name.Length);
            e.AddRange(name);
            return e.ToArray();
        }).ToList();

        // std::sort over std::vector<uint8_t>: lexicographic, a shorter prefix first.
        entries.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        AddU32(out_, (uint)entries.Count);
        foreach (var e in entries) out_.AddRange(e);
        return [.. out_];
    }

    /// <summary>One frame's canonical bytes, or null when the frame is not part of the chain.</summary>
    public static byte[]? FrameBytes(ReplayDocument doc, FrameRecord frame)
    {
        var events = Enumerable.Range(0, frame.EventCount).Select(i => doc.GetEvent(frame.EventStart + i, frame.FrameNumber))
            .Where(e => IsCanonicalEventType((byte)e.Type)).ToList();
        if (frame.GameCrc is null && events.Count == 0) return null;

        var out_ = new List<byte>();
        AddVarint(out_, (uint)frame.FrameNumber);
        out_.Add((byte)(frame.GameCrc is null ? 0 : 1));
        if (frame.GameCrc is { } crc) { out_.Add((byte)crc); out_.Add((byte)(crc >> 8)); }
        AddVarint(out_, (uint)events.Count);

        byte[]? previous = null;
        byte previousHouse = 0;
        foreach (var e in events)
        {
            byte type = (byte)e.Type, house = (byte)e.HouseIndex;
            int length = ReplayFormat.EventPayloadLength(type);
            var payload = e.Data[..length].ToArray();

            if (e.Type == EventType.MegaMission)
            {
                payload[MegaMissionSlot] = 0;
                bool continuesGroup = previous is not null && previousHouse == house
                    && previous.AsSpan(MegaMissionWhomEnd, MegaMissionFollowBegin - MegaMissionWhomEnd)
                        .SequenceEqual(payload.AsSpan(MegaMissionWhomEnd, MegaMissionFollowBegin - MegaMissionWhomEnd))
                    && previous.AsSpan(MegaMissionFollowEnd).SequenceEqual(payload.AsSpan(MegaMissionFollowEnd));
                previous = (byte[])payload.Clone();
                previousHouse = house;
                if (continuesGroup)
                    Array.Clear(payload, MegaMissionFollowBegin, MegaMissionFollowEnd - MegaMissionFollowBegin);
            }
            else
            {
                previous = null;
            }

            out_.Add(type);
            out_.Add(house);
            out_.AddRange(payload);
        }

        return [.. out_];
    }

    /// <summary>Recomputes the chain over a recording, the way the recorder sealed it.</summary>
    public static List<SealRecord> ComputeSeals(ReplayDocument doc, out SealRecord? finalSeal)
    {
        var seals = new List<SealRecord>();
        byte[] previous = SHA256.HashData(BuildDescriptor(doc));
        var window = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        window.AppendData(previous);
        int intervalEnd = ReplayFormat.SealIntervalFrames;

        void Close(int through)
        {
            previous = window.GetHashAndReset();
            seals.Add(new SealRecord(through, previous));
            window.AppendData(previous);
        }

        foreach (var frame in doc.Frames)
        {
            if (FrameBytes(doc, frame) is not { } bytes) continue;
            while (frame.FrameNumber >= intervalEnd)
            {
                Close(intervalEnd - 1);
                intervalEnd += ReplayFormat.SealIntervalFrames;
            }
            window.AppendData(bytes);
        }

        // The recorder ends the chain at the last frame it wrote a record for.
        finalSeal = null;
        if (doc.SawEndOfStream && doc.Frames.Count > 0)
        {
            Close(doc.Frames[^1].FrameNumber);
            finalSeal = seals[^1];
            seals.RemoveAt(seals.Count - 1);
        }

        return seals;
    }

    public sealed class Verification
    {
        /// <summary>Seals the frame stream carries, and whether each matched the recomputed chain.</summary>
        public List<(SealRecord Stored, bool Matches)> StreamSeals { get; } = [];
        public SealRecord? FinalSeal { get; set; }
        public bool? FinalSealMatches { get; set; }
        public int FirstBadSealFrame { get; set; } = -1;
        public bool HasSeals => StreamSeals.Count > 0 || FinalSeal is not null;
        public bool AllMatch => StreamSeals.All(s => s.Matches) && FinalSealMatches != false;

        public List<ChatCheck> Chat { get; } = [];
    }

    public enum ChatStatus { Verified, NoSalt, NoFingerprint }

    public sealed record ChatCheck(SideChannelEvent Message, ChatStatus Status, int? FingerprintFrame);

    /// <summary>
    /// Checks the file against itself: its seals against a chain recomputed from its contents, and its
    /// chat against the fingerprints in its events. A file edited without redoing either fails here;
    /// one where both were redone still differs from every other copy of the game, which is what
    /// <see cref="CompareSeals"/> is for.
    /// </summary>
    public static Verification Verify(ReplayDocument doc)
    {
        var result = new Verification();
        var computed = ComputeSeals(doc, out var computedFinal);
        var byFrame = computed.ToDictionary(s => s.ThroughFrame);

        foreach (var frame in doc.Frames)
        {
            if (frame.Seals is null) continue;
            foreach (var seal in frame.Seals)
            {
                bool ok = byFrame.TryGetValue(seal.ThroughFrame, out var c) && c.Hash.AsSpan().SequenceEqual(seal.Hash);
                result.StreamSeals.Add((seal, ok));
                if (!ok && result.FirstBadSealFrame < 0) result.FirstBadSealFrame = seal.ThroughFrame;
            }
        }

        if (doc.Statistics?.Seals is { Count: > 0 } all)
        {
            result.FinalSeal = all[^1];
            result.FinalSealMatches = computedFinal is not null
                && computedFinal.ThroughFrame == all[^1].ThroughFrame
                && computedFinal.Hash.AsSpan().SequenceEqual(all[^1].Hash);
        }

        VerifyChat(doc, result);
        return result;
    }

    private static void VerifyChat(ReplayDocument doc, Verification result)
    {
        var fingerprints = new List<(int Frame, int House, byte[] Hash)>();
        foreach (var e in doc.EnumerateEvents())
        {
            if ((byte)e.Type != ReplayFormat.EventChatFingerprint) continue;
            fingerprints.Add((e.RecordFrame, e.HouseIndex, e.Data[..ReplayFormat.ChatFingerprintBytes].ToArray()));
        }

        foreach (var message in doc.EnumerateSideChannel().Where(m => m.Type == SideChannelEventType.ChatMessage))
        {
            if (message.Salt is null)
            {
                result.Chat.Add(new ChatCheck(message, ChatStatus.NoSalt, null));
                continue;
            }

            var hash = ChatFingerprint(message.Salt, message.House, message.Text);
            var match = fingerprints.FirstOrDefault(f => f.House == message.House && f.Hash.AsSpan().SequenceEqual(hash));
            result.Chat.Add(new ChatCheck(message, match.Hash is null ? ChatStatus.NoFingerprint : ChatStatus.Verified,
                match.Hash is null ? null : match.Frame));
        }
    }

    /// <summary>The first 16 bytes of SHA-256("YRCHAT1" || salt || house || UTF-16LE text).</summary>
    public static byte[] ChatFingerprint(byte[] salt, int house, string text)
    {
        var input = new List<byte>();
        input.AddRange("YRCHAT1"u8.ToArray());
        input.AddRange(salt);
        input.Add((byte)house);
        input.AddRange(Encoding.Unicode.GetBytes(text));
        return SHA256.HashData([.. input])[..ReplayFormat.ChatFingerprintBytes];
    }

    public sealed class SealComparison
    {
        public int Compared { get; set; }
        public int LastMatchingFrame { get; set; } = -1;
        public int FirstDifferingFrame { get; set; } = -1;
    }

    /// <summary>
    /// Compares two recordings interval by interval, through chains recomputed from each file's
    /// contents: the seals a file stores can be stale or redone, so on their own they prove nothing.
    /// Two copies of the same game agree on every full interval both of them reached; the first one
    /// that differs is where they part.
    /// </summary>
    public static SealComparison CompareSeals(ReplayDocument a, ReplayDocument b)
    {
        var left = FullIntervalSeals(a);
        var result = new SealComparison();
        foreach (var (through, hash) in FullIntervalSeals(b).OrderBy(s => s.Key))
        {
            if (!left.TryGetValue(through, out var other)) continue;
            result.Compared++;
            if (other.AsSpan().SequenceEqual(hash))
            {
                if (result.FirstDifferingFrame < 0) result.LastMatchingFrame = through;
            }
            else if (result.FirstDifferingFrame < 0)
            {
                result.FirstDifferingFrame = through;
            }
        }
        return result;
    }

    /// <summary>The recomputed seal of every full interval a recording reached, by the interval's last frame.</summary>
    public static Dictionary<int, byte[]> FullIntervalSeals(ReplayDocument doc) =>
        ComputeSeals(doc, out _).ToDictionary(s => s.ThroughFrame, s => s.Hash);

    public enum CopyStatus
    {
        Agrees,  // matches the other copies on every interval it shares with them
        Differs, // parts from the copies that agree with each other
        Split,   // parts from the others with no majority to say which side was edited
    }

    public sealed class CopyComparison
    {
        public required ReplayDocument Doc { get; init; }
        public CopyStatus Status { get; set; } = CopyStatus.Agrees;
        /// <summary>The intervals this copy shares with at least one other copy.</summary>
        public int Compared { get; set; }
        public int LastAgreeingFrame { get; set; } = -1;
        /// <summary>The last frame of the first interval where it parts from the others; -1 when it never does.</summary>
        public int FirstDifferingFrame { get; set; } = -1;
    }

    /// <summary>
    /// Compares any number of copies of one game through recomputed chains. On each interval the copies
    /// that reached it are grouped by hash; a copy outside the largest group differs from the rest from
    /// there. With no single largest group - two copies that disagree, say - the copies involved are
    /// split: something was edited, but not which one.
    /// </summary>
    public static List<CopyComparison> CompareCopies(IReadOnlyList<ReplayDocument> docs)
    {
        var results = docs.Select(d => new CopyComparison { Doc = d }).ToList();
        var seals = docs.Select(FullIntervalSeals).ToList();

        foreach (int through in seals.SelectMany(s => s.Keys).Distinct().Order())
        {
            var present = Enumerable.Range(0, docs.Count).Where(i => seals[i].ContainsKey(through)).ToList();
            if (present.Count < 2) continue;

            var groups = present.GroupBy(i => Convert.ToHexString(seals[i][through])).OrderByDescending(g => g.Count()).ToList();
            bool hasMajority = groups.Count == 1 || groups[0].Count() > groups[1].Count();
            var agreeing = hasMajority ? groups[0].ToHashSet() : [];

            foreach (int i in present)
            {
                var r = results[i];
                r.Compared++;
                if (r.FirstDifferingFrame >= 0) continue;

                if (agreeing.Contains(i))
                {
                    r.LastAgreeingFrame = through;
                    continue;
                }

                r.FirstDifferingFrame = through;
                r.Status = hasMajority ? CopyStatus.Differs : CopyStatus.Split;
            }
        }

        return results;
    }

    /// <summary>A chat line one copy should hold, going by the line's recipients, but does not.</summary>
    public sealed record MissingChat(ReplayDocument Copy, SideChannelEvent Message, ReplayDocument Source);

    /// <summary>
    /// Checks the copies' chat against each other. Every copy carries every message's fingerprint in
    /// its lockstep stream, and each line records who the message went to, so a genuine line - one
    /// whose fingerprint the copy holding it also carries - has to be in the sender's copy and in each
    /// recipient's, unless that copy had already stopped recording. A copy without it had the line
    /// removed, or its text changed, which <see cref="Verify"/> reports on its own side.
    /// </summary>
    public static List<MissingChat> CompareChat(IReadOnlyList<ReplayDocument> docs)
    {
        var lines = new Dictionary<string, (SideChannelEvent Message, ReplayDocument Source, byte Recipients, HashSet<ReplayDocument> Holders)>();
        foreach (var doc in docs)
        {
            foreach (var check in Verify(doc).Chat.Where(c => c.Status == ChatStatus.Verified))
            {
                var m = check.Message;
                string key = $"{m.House}:{Convert.ToHexString(ChatFingerprint(m.Salt!, m.House, m.Text))}";
                if (!lines.TryGetValue(key, out var line))
                    line = (m, doc, 0, []);
                line.Recipients |= m.Recipients;
                line.Holders.Add(doc);
                lines[key] = line;
            }
        }

        var missing = new List<MissingChat>();
        foreach (var (message, source, recipients, holders) in lines.Values.OrderBy(l => l.Message.FrameNumber))
        {
            foreach (var doc in docs)
            {
                if (holders.Contains(doc) || message.FrameNumber > doc.EffectiveFrameCount) continue;
                int house = RecordingHouse(doc);
                bool expected = house >= 0 && (house == message.House || (house < 8 && (recipients & (1 << house)) != 0));
                if (expected)
                    missing.Add(new MissingChat(doc, message, source));
            }
        }
        return missing;
    }

    /// <summary>The house of the player whose copy this is; -1 when the roster does not say.</summary>
    public static int RecordingHouse(ReplayDocument doc) => doc.Roster.RecordingPlayer?.HouseIndex ?? -1;

    /// <summary>The seals a recording carries: the statistics section's full list, or what its frame stream holds.</summary>
    public static IEnumerable<SealRecord> SealsOf(ReplayDocument doc) =>
        doc.Statistics?.Seals is { Count: > 0 } all ? all : doc.Frames.Where(f => f.Seals is not null).SelectMany(f => f.Seals!);

    private static void AddU32(List<byte> o, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); o.AddRange(b.ToArray()); }
    private static void AddI32(List<byte> o, int v) => AddU32(o, (uint)v);

    private static void AddVarint(List<byte> o, uint v)
    {
        while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }
}
