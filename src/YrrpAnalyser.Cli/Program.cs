using YrrpAnalyser;

// Headless counterpart to the window: the same parser and the same analysis, printed. Meant for
// batch work - sweeping a Replays folder for truncated recordings, or diffing two peers' files
// for the frame a desync started on - where opening each one by hand is the slow part.

if (args.Length == 0)
{
    Console.WriteLine("""
        yrrp - Red Alert 2 .yrrp replay analyser

          yrrp <replay.yrrp> [--events] [--network] [--chat] [--rules <ini>...]
          yrrp --compare <a.yrrp> <b.yrrp>       compare two peers' recordings of one game
          yrrp --scan <folder>                   one line per replay in a folder
          yrrp --export <replay.yrrp> <outdir>   write every CSV/JSON export
          yrrp --saves <replay.yrrp> [outdir]    list the embedded saves, or write them out as .SAV
          yrrp --size <replay.yrrp>              where the file's bytes go
          yrrp --verify <replay.yrrp>            check the seals and chat against the file's own contents
          yrrp --compare-seals <a.yrrp> <b.yrrp> [more...]
                                                 check copies of one game against each other and name an edited one
        """);
    return 0;
}

try
{
    switch (args[0])
    {
        case "--compare" when args.Length >= 3: return Compare(args[1], args[2]);
        case "--scan" when args.Length >= 2: return Scan(args[1]);
        case "--export" when args.Length >= 3: return Export(args[1], args[2]);
        case "--saves" when args.Length >= 2: return Saves(args[1], args.Length >= 3 ? args[2] : null);
        case "--size" when args.Length >= 2: return Size(args[1]);
        case "--verify" when args.Length >= 2: return Verify(args[1]);
        case "--compare-seals" when args.Length >= 3: return CompareSeals(args[1..]);
        default: return Report(args);
    }
}
catch (ReplayLoadException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex}");
    return 3;
}

// Exit code 0 when everything the file carries checks out, 1 when something does not.
static int Verify(string path)
{
    var doc = ReplayReader.Load(path);
    var result = Canonical.Verify(doc);
    Console.WriteLine(doc.FileName);

    if (!result.HasSeals)
        Console.WriteLine("  no canonical seals - recorded before they existed, or the hash was unavailable");
    else
    {
        int good = result.StreamSeals.Count(s => s.Matches);
        Console.WriteLine($"  seals in the frame stream   {good:N0} of {result.StreamSeals.Count:N0} match its contents" +
                          (result.FirstBadSealFrame >= 0 ? $" - first bad one ends at frame {result.FirstBadSealFrame:N0}" : ""));
        Console.WriteLine(result.FinalSeal is null
            ? "  final seal                  none - the recording did not close cleanly"
            : $"  final seal                  through frame {result.FinalSeal.ThroughFrame:N0}, " +
              (result.FinalSealMatches == true ? "matches" : "DOES NOT MATCH"));
    }

    int verified = result.Chat.Count(c => c.Status == Canonical.ChatStatus.Verified);
    int unsalted = result.Chat.Count(c => c.Status == Canonical.ChatStatus.NoSalt);
    int missing = result.Chat.Count(c => c.Status == Canonical.ChatStatus.NoFingerprint);
    Console.WriteLine($"  chat                        {verified:N0} verified, {missing:N0} with no matching fingerprint, " +
                      $"{unsalted:N0} without a salt");
    foreach (var c in result.Chat.Where(c => c.Status == Canonical.ChatStatus.NoFingerprint))
        Console.WriteLine($"    ! frame {c.Message.FrameNumber:N0} {c.Message.SenderName}: {c.Message.Text}");

    return result.AllMatch && missing == 0 ? 0 : 1;
}

// Exit code 0 when every copy agrees with the others and with itself, 1 when anything does not.
static int CompareSeals(string[] paths)
{
    var docs = paths.Select(p => ReplayReader.Load(p)).ToList();
    bool ok = true;

    // Each copy against itself: a stored seal that no longer matches the contents means an edit that
    // did not redo the chain.
    Console.WriteLine("Copies");
    foreach (var doc in docs)
    {
        var v = Canonical.Verify(doc);
        var player = doc.Roster.ForHouse(Canonical.RecordingHouse(doc));
        string self = !v.HasSeals ? "no stored seals"
            : v.AllMatch ? "stored seals match its contents"
            : $"STORED SEALS DO NOT MATCH ITS CONTENTS (first bad one ends at frame {v.FirstBadSealFrame:N0})";
        Console.WriteLine($"  {doc.FileName}");
        Console.WriteLine($"      recorded by {player?.DisplayName ?? "?"}, {doc.EffectiveFrameCount:N0} frames, {self}");
        ok &= v.AllMatch;
    }

    // The copies against each other, through chains recomputed from what each file holds.
    Console.WriteLine();
    Console.WriteLine("Seals recomputed from each file");
    var copies = Canonical.CompareCopies(docs);
    if (copies.All(c => c.Compared == 0))
    {
        Console.WriteLine("  no full interval reached by two copies - nothing to compare");
        ok = false;
    }
    else if (copies.All(c => c.Status == Canonical.CopyStatus.Agrees))
    {
        int through = copies.Max(c => c.LastAgreeingFrame);
        Console.WriteLine($"  every copy agrees on every interval it shares - the same game, unedited, through frame {through:N0}");
    }
    else
    {
        ok = false;
        foreach (var c in copies)
        {
            string line = c.Status switch
            {
                Canonical.CopyStatus.Agrees => $"agrees with the others through frame {c.LastAgreeingFrame:N0}",
                Canonical.CopyStatus.Differs => $"DIFFERS from the others from the interval ending at frame {c.FirstDifferingFrame:N0} " +
                                                $"({c.Doc.TimeLabel(c.FirstDifferingFrame)}) - this copy was edited",
                _ => $"disagrees from the interval ending at frame {c.FirstDifferingFrame:N0} " +
                     $"({c.Doc.TimeLabel(c.FirstDifferingFrame)}) with no majority - a copy was edited; " +
                     "add another copy to find which",
            };
            Console.WriteLine($"  {c.Doc.FileName}: {line}");
        }
    }

    // Chat: a genuine line belongs in the sender's copy and every recipient's.
    Console.WriteLine();
    Console.WriteLine("Chat across copies");
    var missing = Canonical.CompareChat(docs);
    int lines = docs.Sum(d => Canonical.Verify(d).Chat.Count(c => c.Status == Canonical.ChatStatus.Verified));
    if (missing.Count == 0)
        Console.WriteLine($"  {lines:N0} verified lines across the copies; every copy that should hold a line does");
    foreach (var m in missing)
    {
        var sender = m.Source.Roster.ForHouse(m.Message.House)?.DisplayName ?? m.Message.SenderName;
        Console.WriteLine($"  ! {m.Copy.FileName} is missing frame {m.Message.FrameNumber:N0} " +
                          $"{m.Message.SenderWithLabel(sender)}: {m.Message.Text}");
    }
    ok &= missing.Count == 0;

    return ok ? 0 : 1;
}

static (ReplayDocument Doc, EventDescriber Describer) Load(string path, string[] rulesPaths)
{
    var doc = ReplayReader.Load(path);
    var types = TypeNameResolver.ForDocument(doc, rulesPaths);
    return (doc, new EventDescriber(types));
}

static string[] RulesFrom(string[] args)
{
    int i = Array.IndexOf(args, "--rules");
    if (i < 0) return [];
    return args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).ToArray();
}

static int Report(string[] args)
{
    var (doc, describer) = Load(args[0], RulesFrom(args));
    var network = NetworkAnalysis.Build(doc);
    var activity = ActivityAnalysis.Build(doc, describer);

    Console.WriteLine($"File            {doc.FileName}  ({doc.FileSize:N0} bytes)");
    Console.WriteLine($"Map             {doc.MapName}");
    Console.WriteLine($"Recorded        {doc.Header.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Game package    {doc.GamePackageVersion}");
    Console.WriteLine($"Mode            {doc.Header.GameModeName}   seed {doc.Header.Seed}   " +
                      $"speed index {doc.Header.RecordedGameSpeed} ({doc.Header.SimulationFps} FPS)");
    Console.WriteLine($"Length          {doc.EffectiveFrameCount:N0} frames, " +
                      $"{ReplayDocument.FormatTime(doc.Duration)}" +
                      (doc.GameSpeed.SpeedChanged
                          ? $"   (speed changed {doc.GameSpeed.Changes.Count()}x)"
                          : ""));
    Console.WriteLine($"Shutdown        {(doc.Header.CleanShutdown ? "clean" : "NOT CLEAN - recording cut short")}");
    Console.WriteLine($"Stream          {doc.Frames.Count:N0} frame records, {doc.EventCount:N0} events, " +
                      $"{Canonical.SealsOf(doc).Count():N0} seals, {doc.CompressionRatio:0.0}x compression");
    Console.WriteLine($"Checkpoints     {doc.CheckpointSummary}");
    if (!doc.HasEmbeddedMap)
        Console.WriteLine("Map             not embedded - the scenario lives in the game's own mixes");

    foreach (var warning in doc.Warnings)
        Console.WriteLine($"  ! {warning}");

    Console.WriteLine();
    Console.WriteLine("Players");
    foreach (var p in doc.Roster.Players)
    {
        Console.WriteLine($"  house {p.HouseIndex}  slot {p.Slot}  {p.DisplayName,-20} " +
                          $"{p.SideName,-14} colour {p.Color,-3} " +
                          $"{(p.IsHuman ? "human" : "AI   ")} " +
                          $"{(p.IsSpectator ? "spectator " : "")}" +
                          $"{(p.IsRecordingPlayer ? "<- recorded this" : "")}");
    }

    Console.WriteLine();
    Console.WriteLine($"Network  (protocol {network.Protocol}, FrameSendRate {network.ConfiguredFrameSendRate})");
    Console.WriteLine("  house  player                 response med/max  process mean med/max   maxahead med/max  worst gap");
    foreach (var s in network.Series)
    {
        Console.WriteLine($"  {s.HouseIndex,-6} {s.Name,-22} " +
                          $"{s.MedianRoundTripMs,5:0} /{s.WorstRoundTripMs,5:0} ms  " +
                          $"{s.MedianProcessMs,5:0.0}/{s.WorstProcessMs,5:0.0} ms   " +
                          $"{s.MedianMaxAhead,5:0} /{s.WorstMaxAhead,5:0}     " +
                          $"{s.WorstFrameInfoGap,5:0} frames");
    }

    if (network.LargeFrameInfoGaps.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Large FRAMEINFO gaps  ({network.LargeFrameInfoGaps.Count} total, largest first; game time only)");
        foreach (var gap in network.LargeFrameInfoGaps.Take(10))
            Console.WriteLine($"  {gap.Name,-22} frame {gap.StartFrame,7:N0} -> {gap.EndFrame,7:N0}  " +
                              $"{gap.Frames,4} frames  {gap.SimulationSeconds,5:0.00} game seconds");
    }

    Console.WriteLine();
    Console.WriteLine("Activity");
    foreach (var a in activity.Players)
        Console.WriteLine($"  {a.Name,-22} {a.TotalOrders,7:N0} orders  {a.TotalCommands,6:N0} commands  " +
                          $"avg {a.AverageApm,5:0.0} APM  peak {a.PeakApm,5:0.0}  " +
                          $"last action frame {a.LastActionFrame:N0}");

    var statistics = StatisticsAnalysis.Build(doc);
    if (statistics.HasTimeline || doc.Statistics is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"Statistics  ({doc.HouseStatsFrameCount:N0} timeline samples" +
                          (doc.Statistics?.StatsPacket is not null ? ", plus the game's own packet" : "") + ")");
        Console.WriteLine("  player                  money left     income      spent  peak army  units b/k/l          bldgs b/k/l      score");
        foreach (var t in statistics.Houses)
        {
            var s = t.Last;
            var summary = doc.Statistics?.ForHouse(t.HouseIndex);
            string result = (s.Flags & HouseStatsFlags.Winner) != 0 ? "won"
                : t.DefeatedAtFrame is { } f ? $"defeated {doc.TimeLabel(f)}" : "";
            Console.WriteLine($"  {t.Name,-22} {s.CreditsOnHand,11:N0} {t.TotalIncome,10:N0} {t.NetSpent,10:N0} " +
                              $"{t.PeakArmyValue,10:N0}  {s.UnitsBuilt,5}/{s.UnitsKilled,5}/{s.UnitsLost,-5}   " +
                              $"{s.BuildingsBuilt,4}/{s.BuildingsKilled,4}/{s.BuildingsLost,-4} {s.Score,8:N0}  {result}" +
                              (summary is null ? "" : $"  {summary.Country}"));
            if (t.IncomeBySource.Count > 0)
                Console.WriteLine("      income: " + string.Join("  ", t.IncomeBySource.OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key} {kv.Value:N0}")) +
                    (Math.Abs(t.DirectIncome) >= 1 ? $"  Direct {t.DirectIncome:N0}" : ""));
        }
        foreach (var u in statistics.Unclassified)
            Console.WriteLine($"  unclassified caller {u.Caller} (build 0x{u.Caller.TimeDateStamp:X8}): " +
                              $"{u.Amount:N0} in {u.Payments:N0} payments");
    }

    Console.WriteLine();
    Console.WriteLine("Event totals");
    foreach (var (type, count) in activity.EventTotals.OrderByDescending(kv => kv.Value))
        Console.WriteLine($"  {EventTypes.Name(type),-16} {count,7:N0}" +
                          (EventTypes.IsTiming(type) ? "   (network)" : ""));

    var chat = doc.EnumerateSideChannel().ToList();
    if (chat.Count > 0 && (args.Contains("--chat") || chat.Count <= 30))
    {
        Console.WriteLine();
        Console.WriteLine($"Chat and beacons ({chat.Count})");
        foreach (var e in chat)
            Console.WriteLine($"  {doc.TimeLabel(e.FrameNumber),8}  {e.TypeName,-12} " +
                              $"{e.SenderWithLabel(e.SenderName.Length > 0 ? e.SenderName : doc.Roster.HouseLabel(e.House)),-24} {e.Text}");
    }

    if (args.Contains("--events"))
    {
        Console.WriteLine();
        Console.WriteLine("Events");
        foreach (var e in doc.EnumerateEvents())
        {
            if (!args.Contains("--network") && EventTypes.IsTiming(e.Type)) continue;
            Console.WriteLine($"  {e.RecordFrame,7:N0} {doc.TimeLabel(e.RecordFrame),8}  " +
                              $"{doc.Roster.HouseLabel(e.HouseIndex),-18} {EventTypes.Name(e.Type),-14} " +
                              $"{describer.Describe(e)}");
        }
    }

    return 0;
}

static int Compare(string leftPath, string rightPath)
{
    var (left, describer) = Load(leftPath, []);
    var right = ReplayReader.Load(rightPath);
    var result = DesyncCompare.Compare(left, right, describer);

    Console.WriteLine($"{result.LeftName}\n{result.RightName}\n");

    if (result.Mismatches.Count > 0)
    {
        Console.WriteLine("These are not two recordings of the same game, or the two machines did " +
                          "not have the same files:");
        foreach (var m in result.Mismatches) Console.WriteLine($"  - {m}");
        Console.WriteLine();
    }

    Console.WriteLine($"Compared {result.ComparedFrames:N0} frames " +
                      $"({result.LeftOnlyFrames:N0} / {result.RightOnlyFrames:N0} present on one side only)");

    if (result.Seals.Compared > 0)
    {
        Console.WriteLine(result.Seals.FirstDifferingFrame < 0
            ? $"Canonical seals agree on all {result.Seals.Compared:N0} shared intervals"
            : $"Canonical seals agree through frame {result.Seals.LastMatchingFrame:N0} and differ from the " +
              $"interval ending at frame {result.Seals.FirstDifferingFrame:N0} ({left.TimeLabel(result.Seals.FirstDifferingFrame)})");
    }

    if (result.FirstDivergenceFrame < 0)
    {
        Console.WriteLine(result.Seals.FirstDifferingFrame < 0
            ? "Every compared frame hashes identically - the two simulations stayed in step."
            : "Frame hashes all match, but the canonical seals do not - the recordings carry different "
              + "orders, or one of them was edited.");
        return result.Seals.FirstDifferingFrame < 0 ? 0 : 1;
    }

    Console.WriteLine($"First divergence at frame {result.FirstDivergenceFrame:N0} " +
                      $"({left.TimeLabel(result.FirstDivergenceFrame)}); " +
                      $"{result.TotalDivergentFrames:N0} frames differ in total.");
    Console.WriteLine();

    foreach (var d in result.Divergences.Take(10))
        Console.WriteLine($"  frame {d.Frame,7:N0}   {d.LeftCrc:X8}  vs  {d.RightCrc:X8}");

    Console.WriteLine();
    Console.WriteLine($"Events around the divergence, {result.LeftName}:");
    foreach (var line in result.LeftContext) Console.WriteLine($"  {line}");
    Console.WriteLine($"Events around the divergence, {result.RightName}:");
    foreach (var line in result.RightContext) Console.WriteLine($"  {line}");

    return 1;
}

static int Scan(string folder)
{
    var files = Directory.EnumerateFiles(folder, "*.yrrp", SearchOption.AllDirectories)
        .OrderBy(f => f).ToList();

    Console.WriteLine($"{"file",-52} {"frames",8} {"len",8} {"events",8} {"shutdown",-9} note");

    foreach (var path in files)
    {
        try
        {
            var doc = ReplayReader.Load(path);
            Console.WriteLine($"{Trim(doc.FileName, 52),-52} {doc.EffectiveFrameCount,8:N0} " +
                              $"{ReplayDocument.FormatTime(doc.Duration),8} {doc.EventCount,8:N0} " +
                              $"{(doc.Header.CleanShutdown ? "clean" : "CUT SHORT"),-9} " +
                              $"{string.Join("; ", doc.Warnings.Take(1))}");
        }
        catch (ReplayLoadException ex)
        {
            Console.WriteLine($"{Trim(Path.GetFileName(path), 52),-52} {"-",8} {"-",8} {"-",8} " +
                              $"{"-",-9} {ex.Status}: {ex.Message}");
        }
    }
    return 0;
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static int Export(string path, string outDir)
{
    Directory.CreateDirectory(outDir);
    var (doc, describer) = Load(path, []);
    var network = NetworkAnalysis.Build(doc);
    var activity = ActivityAnalysis.Build(doc, describer);
    string stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path));

    Exporters.WriteEventsCsv($"{stem}.events.csv", doc, describer, includeTiming: true);
    Exporters.WriteChatCsv($"{stem}.chat.csv", doc);
    Exporters.WriteNetworkCsv($"{stem}.network.csv", doc, network);
    Exporters.WriteFrameCrcCsv($"{stem}.frames.csv", doc);
    Exporters.WriteSummaryJson($"{stem}.summary.json", doc, network, activity);
    Exporters.WriteHouseStatsCsv($"{stem}.house-stats.csv", doc);
    Exporters.WriteMoneyInCsv($"{stem}.money-in.csv", doc);
    File.WriteAllText($"{stem}.spawn.ini", doc.SpawnIniText);
    File.WriteAllText($"{stem}.spawnmap.ini", doc.SpawnMapText);
    if (doc.Statistics?.StatsPacketBytes is { } packet)
        File.WriteAllBytes($"{stem}.stats.dmp", packet);

    Console.WriteLine($"Wrote the exports to {outDir}");
    return 0;
}

static int Saves(string path, string? outDir)
{
    var doc = ReplayReader.Load(path);
    Console.WriteLine($"{doc.FileName}: {doc.CheckpointSummary}");
    foreach (var c in doc.Checkpoints)
        Console.WriteLine($"  frame {c.Frame,8:N0}  {doc.TimeLabel(c.Frame),8}  save {c.SaveBytes,11:N0} bytes  " +
                          $"sidecar {c.SidecarBytes,9:N0} bytes  {c.Problem ?? "ok"}");

    if (outDir is null) return 0;

    Directory.CreateDirectory(outDir);
    int written = 0;
    foreach (var c in doc.Checkpoints.Where(c => c.Usable))
    {
        string target = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(path)} - frame {c.Frame}.SAV");
        File.WriteAllBytes(target, CheckpointArchiveReader.ExtractSave(path, c));
        written++;
    }
    Console.WriteLine($"Wrote {written} save(s) to {outDir}");
    return 0;
}

static int Size(string path)
{
    var doc = ReplayReader.Load(path);
    var size = SizeBreakdown.Measure(doc);

    Console.WriteLine($"{doc.FileName}: {size.FileSize:N0} bytes, {ReplayDocument.FormatTime(doc.Duration)} of game" +
                      (size.GameMinutes > 0 ? $", {size.FileSize / size.GameMinutes / 1024:N0} KB per game minute" : ""));
    Console.WriteLine("On disk marked ~ is an estimate: a deflate stream's parts share its compressed bytes.");
    Console.WriteLine();
    Console.WriteLine($"  {"part",-52} {"count",11} {"uncompressed",14} {"on disk",14} {"of file",8}");

    void Print(SizeEntry entry, int depth)
    {
        string name = new string(' ', depth * 2) + entry.Name;
        Console.WriteLine($"  {Trim(name, 52),-52} {(entry.Count > 0 ? entry.Count.ToString("N0") : ""),11} " +
                          $"{entry.RawBytes,14:N0} {(entry.StoredIsEstimate ? "~" : "") + entry.StoredBytes.ToString("N0"),14} " +
                          $"{100.0 * entry.StoredBytes / Math.Max(1, size.FileSize),7:0.0}%");
        foreach (var child in entry.Children)
            Print(child, depth + 1);
    }

    foreach (var section in size.Sections)
        Print(section, 0);
    return 0;
}
