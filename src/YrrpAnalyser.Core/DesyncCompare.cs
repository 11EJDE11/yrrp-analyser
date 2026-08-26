namespace YrrpAnalyser;

public sealed class CrcDivergence
{
    public int Frame { get; init; }
    public uint LeftCrc { get; init; }
    public uint RightCrc { get; init; }
}

public sealed class DesyncCompareResult
{
    public string LeftName { get; init; } = "";
    public string RightName { get; init; } = "";

    public bool SameGame { get; init; }
    public List<string> Mismatches { get; } = [];

    public int ComparedFrames { get; set; }
    public int FirstDivergenceFrame { get; set; } = -1;
    public List<CrcDivergence> Divergences { get; } = [];
    public int TotalDivergentFrames { get; set; }

    /// <summary>Frames present in one recording but not the other.</summary>
    public int LeftOnlyFrames { get; set; }
    public int RightOnlyFrames { get; set; }

    /// <summary>Events either side recorded on the first divergent frame and the ones before it.</summary>
    public List<string> LeftContext { get; } = [];
    public List<string> RightContext { get; } = [];
}

/// <summary>
/// Compares the per-frame Compute_Game_CRC hashes of two recordings of the same game.
///
/// Every peer records the engine's own state hash at the same instant in the frame - after
/// LogicClass::AI and before the frame's events are applied - so two files from the same match
/// hold two independently produced hashes of the same simulation. The first frame they disagree
/// on is the frame the two machines actually came apart, which is otherwise only visible as a
/// desync message with no frame attached.
/// </summary>
public static class DesyncCompare
{
    /// <summary>How many frames of event context to list either side of the divergence.</summary>
    private const int ContextFrames = 3;

    public static DesyncCompareResult Compare(ReplayDocument left, ReplayDocument right,
        EventDescriber describer)
    {
        var result = new DesyncCompareResult
        {
            LeftName = left.FileName,
            RightName = right.FileName,
            SameGame = IsSameGame(left, right, out var mismatches),
        };
        result.Mismatches.AddRange(mismatches);

        var leftCrc = BuildCrcMap(left);
        var rightCrc = BuildCrcMap(right);

        foreach (var (frame, crc) in leftCrc)
        {
            if (!rightCrc.TryGetValue(frame, out var other))
            {
                result.LeftOnlyFrames++;
                continue;
            }

            result.ComparedFrames++;
            if (crc == other) continue;

            result.TotalDivergentFrames++;
            if (result.FirstDivergenceFrame < 0)
                result.FirstDivergenceFrame = frame;

            // The whole point is the first divergence and whether it persists; a full list of a
            // desynced game's frames is tens of thousands of identical rows.
            if (result.Divergences.Count < 200)
                result.Divergences.Add(new CrcDivergence { Frame = frame, LeftCrc = crc, RightCrc = other });
        }

        foreach (var frame in rightCrc.Keys)
            if (!leftCrc.ContainsKey(frame)) result.RightOnlyFrames++;

        if (result.FirstDivergenceFrame >= 0)
        {
            CollectContext(left, result.FirstDivergenceFrame, describer, result.LeftContext);
            CollectContext(right, result.FirstDivergenceFrame, describer, result.RightContext);
        }

        return result;
    }

    private static bool IsSameGame(ReplayDocument a, ReplayDocument b, out List<string> mismatches)
    {
        mismatches = [];

        if (a.Header.Seed != b.Header.Seed)
            mismatches.Add($"Seed differs: {a.Header.Seed} vs {b.Header.Seed}");

        string gameIdA = a.SpawnIni.GetString("Settings", "GameID");
        string gameIdB = b.SpawnIni.GetString("Settings", "GameID");
        if (gameIdA.Length > 0 && gameIdB.Length > 0 && gameIdA != gameIdB)
            mismatches.Add($"GameID differs: {gameIdA} vs {gameIdB}");

        if (!string.Equals(a.Header.MapName, b.Header.MapName, StringComparison.Ordinal))
            mismatches.Add($"Map differs: {a.Header.MapName} vs {b.Header.MapName}");

        string sha1A = a.SpawnIni.GetString("Settings", "MapSHA1");
        string sha1B = b.SpawnIni.GetString("Settings", "MapSHA1");
        if (sha1A.Length > 0 && sha1B.Length > 0 && sha1A != sha1B)
            mismatches.Add($"Map SHA1 differs: {sha1A} vs {sha1B}");

        if (a.Header.RecordedGameSpeed != b.Header.RecordedGameSpeed)
            mismatches.Add($"Recorded game speed differs: {a.Header.RecordedGameSpeed} vs {b.Header.RecordedGameSpeed}");

        // The file hash block is what the client itself gates a replay on; a difference here is
        // the likeliest cause of a divergence that is nobody's netcode fault.
        CompareHashBlocks(a, b, mismatches);

        return mismatches.Count == 0;
    }

    private static void CompareHashBlocks(ReplayDocument a, ReplayDocument b, List<string> mismatches)
    {
        var left = ReadHashes(a);
        var right = ReadHashes(b);
        if (left.Count == 0 || right.Count == 0) return;

        foreach (var (file, hash) in left)
        {
            if (!right.TryGetValue(file, out var other))
                mismatches.Add($"{file}: present in {a.FileName} only");
            else if (hash != other)
                mismatches.Add($"{file}: hash differs ({Short(hash)} vs {Short(other)})");
        }

        foreach (var file in right.Keys)
            if (!left.ContainsKey(file)) mismatches.Add($"{file}: present in {b.FileName} only");
    }

    private static Dictionary<string, string> ReadHashes(ReplayDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = doc.SpawnIni.GetSection("ReplayFileHashes");
        if (section is null) return result;

        foreach (var (_, value) in section.Entries)
        {
            int bar = value.LastIndexOf('|');
            if (bar <= 0) continue;
            result[value[..bar]] = value[(bar + 1)..];
        }
        return result;
    }

    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;

    private static SortedDictionary<int, uint> BuildCrcMap(ReplayDocument doc)
    {
        var map = new SortedDictionary<int, uint>();
        foreach (var frame in doc.Frames)
            if (frame.GameCrc is { } crc) map[frame.FrameNumber] = crc;
        return map;
    }

    private static void CollectContext(ReplayDocument doc, int frame, EventDescriber describer,
        List<string> into)
    {
        foreach (var record in doc.Frames)
        {
            if (record.FrameNumber < frame - ContextFrames) continue;
            if (record.FrameNumber > frame + ContextFrames) break;

            for (int i = 0; i < record.EventCount; i++)
            {
                var e = doc.GetEvent(record.EventStart + i, record.FrameNumber);
                if (EventTypes.IsTiming(e.Type)) continue;
                into.Add($"frame {record.FrameNumber}  {doc.Roster.HouseLabel(e.HouseIndex)}  " +
                         $"{EventTypes.Name(e.Type)}  {describer.Describe(e)}");
            }
        }

        if (into.Count == 0)
            into.Add("(no gameplay events in this window - the divergence is not event-driven)");
    }
}
