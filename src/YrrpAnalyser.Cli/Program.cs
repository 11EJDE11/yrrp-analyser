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

static (ReplayDocument Doc, EventDescriber Describer) Load(string path, string[] rulesPaths)
{
    var doc = ReplayReader.Load(path);
    var types = TypeNameResolver.Load(rulesPaths, doc.SpawnMapIni);
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
    Console.WriteLine($"Map             {doc.Header.MapName}");
    Console.WriteLine($"Recorded        {doc.Header.RecordedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Spawner         {doc.Header.SpawnerVersion}   client {doc.Header.GameClientVersion}");
    Console.WriteLine($"Mode            {doc.Header.GameModeName}   seed {doc.Header.Seed}   " +
                      $"speed index {doc.Header.RecordedGameSpeed} ({doc.Header.SimulationFps} FPS)");
    Console.WriteLine($"Length          {doc.EffectiveFrameCount:N0} frames, " +
                      $"{ReplayDocument.FormatTime(doc.Duration)}");
    Console.WriteLine($"Shutdown        {(doc.Header.CleanShutdown ? "clean" : "NOT CLEAN - recording cut short")}");
    Console.WriteLine($"Stream          {doc.Frames.Count:N0} frame records, {doc.EventCount:N0} events, " +
                      $"{doc.CompressionRatio:0.0}x compression");

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
    Console.WriteLine("  house  player                 rtt med/max     process med/max   maxahead med/max  worst gap");
    foreach (var s in network.Series)
    {
        Console.WriteLine($"  {s.HouseIndex,-6} {s.Name,-22} " +
                          $"{s.MedianRoundTripMs,5:0} /{s.WorstRoundTripMs,5:0} ms  " +
                          $"{s.MedianProcessMs,5:0.0}/{s.WorstProcessMs,5:0.0} ms   " +
                          $"{s.MedianMaxAhead,5:0} /{s.WorstMaxAhead,5:0}     " +
                          $"{s.WorstFrameInfoGap,5:0} frames");
    }

    if (network.Stalls.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Stalls  ({network.Stalls.Count} total, worst first)");
        foreach (var stall in network.Stalls.Take(10))
            Console.WriteLine($"  {stall.Name,-22} frame {stall.StartFrame,7:N0} -> {stall.EndFrame,7:N0}  " +
                              $"{stall.Frames,4} frames  {stall.Seconds,5:0.00}s");
    }

    Console.WriteLine();
    Console.WriteLine("Activity");
    foreach (var a in activity.Players)
        Console.WriteLine($"  {a.Name,-22} {a.TotalOrders,7:N0} orders  {a.TotalCommands,6:N0} commands  " +
                          $"avg {a.AverageApm,5:0.0} APM  peak {a.PeakApm,5:0.0}  " +
                          $"last action frame {a.LastActionFrame:N0}");

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
                              $"{(e.SenderName.Length > 0 ? e.SenderName : doc.Roster.HouseLabel(e.House)),-16} {e.Text}");
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

    if (result.FirstDivergenceFrame < 0)
    {
        Console.WriteLine("Every compared frame hashes identically - the two simulations stayed in step.");
        return 0;
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
    File.WriteAllText($"{stem}.spawn.ini", doc.SpawnIniText);
    File.WriteAllText($"{stem}.spawnmap.ini", doc.SpawnMapText);

    Console.WriteLine($"Wrote 7 files to {outDir}");
    return 0;
}
