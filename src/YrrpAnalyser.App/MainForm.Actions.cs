using System.Text;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private void PopulateIniTabs(ReplayDocument doc)
    {
        _spawnIniBox.Text = Normalise(doc.SpawnIniText);
        // A campaign recording names a scenario inside the game's own mixes rather than shipping
        // a map, so there is deliberately nothing here to show.
        _spawnMapBox.Text = doc.HasEmbeddedMap
            ? Normalise(doc.SpawnMapText)
            : Normalise("""
                This recording does not embed a map.

                The scenario it names lives inside the game's own mixes, so there is no
                spawnmap.ini to carry. That is the normal shape of a campaign recording.
                """);
    }

    /// <summary>A multiline TextBox only breaks on CRLF, and these files may be LF-only.</summary>
    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private void PopulateDiagnostics(ReplayDocument doc)
    {
        var sb = new StringBuilder();

        sb.AppendLine("PARSE");
        sb.AppendLine($"  file                {doc.FilePath}");
        sb.AppendLine($"  size                {doc.FileSize:N0} bytes");
        sb.AppendLine($"  header size         {doc.Header.HeaderSize} " +
                      $"(this build compiles {ReplayFormat.HeaderSize})");
        sb.AppendLine($"  spawn.ini           {doc.Header.SpawnIniSize:N0} bytes at offset {doc.Header.HeaderSize}");
        sb.AppendLine($"  spawnmap.ini        {doc.Header.SpawnMapSize:N0} bytes");
        sb.AppendLine($"  deflate stream      {doc.CompressedStreamBytes:N0} -> {doc.InflatedStreamBytes:N0} bytes " +
                      $"({doc.CompressionRatio:0.00}x)");
        sb.AppendLine($"  checkpoint archive  " + (doc.Header.HasCheckpointArchive
            ? $"{doc.Header.CheckpointArchiveSize:N0} bytes at offset {doc.Header.CheckpointArchiveOffset:N0}"
            : "none"));
        sb.AppendLine($"  statistics section  " + (doc.Header.HasStatisticsSection
            ? $"{doc.Header.StatisticsSize:N0} bytes at offset {doc.Header.StatisticsOffset:N0}"
            : "none"));
        sb.AppendLine($"  end-of-stream mark  {(doc.SawEndOfStream ? "present" : "MISSING")}");
        sb.AppendLine($"  truncated           {(doc.Truncated ? "yes" : "no")}");
        sb.AppendLine($"  clean shutdown flag {(doc.Header.CleanShutdown ? "set" : "CLEAR")}");
        sb.AppendLine();

        if (doc.Warnings.Count > 0)
        {
            sb.AppendLine("WARNINGS");
            foreach (var w in doc.Warnings) sb.AppendLine($"  ! {w}");
            sb.AppendLine();
        }

        sb.AppendLine("FRAME RECORDS");
        sb.AppendLine($"  records             {doc.Frames.Count:N0}");
        sb.AppendLine($"  first / last frame  {(doc.Frames.Count > 0 ? doc.Frames[0].FrameNumber : 0):N0}" +
                      $" / {doc.LastRecordedFrame:N0}");
        sb.AppendLine($"  header TotalFrames  {doc.Header.TotalFrames:N0}");
        sb.AppendLine($"  events              {doc.EventCount:N0}");
        sb.AppendLine($"  object censuses     {doc.CensusFrameCount:N0}");
        sb.AppendLine($"  RNG cursor snapshots {doc.Frames.Count(f => f.RandomState.HasValue):N0}");
        sb.AppendLine($"  selection triggers  {doc.Frames.Sum(f => (long)(f.SelectionTriggerIds?.Length ?? 0)):N0}");
        sb.AppendLine($"  house stat samples  {doc.HouseStatsFrameCount:N0} frames");
        sb.AppendLine($"  game speed changes  {doc.GameSpeed.Changes.Count():N0}");
        sb.AppendLine($"  bytes per frame     {(doc.Frames.Count > 0 ? doc.InflatedStreamBytes / (double)doc.Frames.Count : 0):0.0} " +
                      "uncompressed");
        sb.AppendLine();

        var flagCounts = doc.Frames
            .GroupBy(f => f.Flags)
            .OrderByDescending(g => g.Count())
            .ToList();

        sb.AppendLine("RECORD FLAG COMBINATIONS");
        sb.AppendLine("  Blocks are stored bare and in write order: TacticalPos, Selection, SideChannel,");
        sb.AppendLine("  GameCRC, ObjectCensus, RandomState, GameSpeed, SelectionTriggers, HouseStats, Extensions.");
        sb.AppendLine("  Gameplay events follow those blocks; this is not numeric flag-bit order.");
        foreach (var group in flagCounts)
            sb.AppendLine($"  0x{group.Key:X2}  {DescribeFlags(group.Key),-52} {group.Count(),8:N0}");
        sb.AppendLine();

        // A monotonic-but-not-contiguous frame sequence is legal; the format only promises
        // monotonicity, so a reader must never index frames by position.
        int gaps = 0, backwards = 0;
        for (int i = 1; i < doc.Frames.Count; i++)
        {
            int delta = doc.Frames[i].FrameNumber - doc.Frames[i - 1].FrameNumber;
            if (delta > 1) gaps++;
            if (delta <= 0) backwards++;
        }
        sb.AppendLine("FRAME SEQUENCE");
        sb.AppendLine($"  gaps (skipped)      {gaps:N0}");
        sb.AppendLine($"  non-monotonic       {backwards:N0}{(backwards > 0 ? "   <- the format guarantees this is 0" : "")}");
        sb.AppendLine();

        sb.AppendLine("HEADER FIELDS");
        sb.AppendLine($"  Magic               0x{doc.Header.Magic:X8}");
        sb.AppendLine($"  Version             {doc.Header.Version}");
        sb.AppendLine($"  GameMode            {doc.Header.GameMode} ({doc.Header.GameModeName})");
        sb.AppendLine($"  UniqueIDCounter     {doc.Header.UniqueIDCounter}");
        sb.AppendLine($"  Seed                {doc.Header.Seed}");
        sb.AppendLine($"  RandomNext1/2       {doc.Header.RandomNext1} / {doc.Header.RandomNext2}");
        sb.AppendLine($"  RecordedGameSpeed   {doc.Header.RecordedGameSpeed} ({doc.Header.SimulationFps} FPS)");
        sb.AppendLine($"  RecordedUnixTime    {doc.Header.RecordedUnixTime} " +
                      $"({doc.Header.RecordedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)");
        sb.AppendLine($"  Flags               0x{doc.Header.Flags:X8}");
        sb.AppendLine($"  CheckpointArchive   offset {doc.Header.CheckpointArchiveOffset}, size {doc.Header.CheckpointArchiveSize}");
        sb.AppendLine($"  Statistics          offset {doc.Header.StatisticsOffset}, size {doc.Header.StatisticsSize}");
        sb.AppendLine();

        if (doc.Checkpoints.Count > 0)
        {
            sb.AppendLine("CHECKPOINTS");
            sb.AppendLine("  Saves made during the game, embedded so playback can seek to them without");
            sb.AppendLine("  simulating the frames before. Each is a deflated .SAV plus a simulation sidecar.");
            sb.AppendLine("     frame      time   compressed         save      sidecar  status");
            foreach (var c in doc.Checkpoints)
                sb.AppendLine($"  {c.Frame,8:N0} {doc.TimeLabel(c.Frame),9} {c.CompressedSize,12:N0} " +
                              $"{c.SaveBytes,12:N0} {c.SidecarBytes,12:N0}  {c.Problem ?? "ok"}");
            sb.AppendLine();
        }

        sb.AppendLine("EMBEDDED METADATA");
        sb.AppendLine($"  Map name            {doc.MapName}");
        sb.AppendLine($"  GamePackageVersion  {doc.GamePackageVersion}");
        sb.AppendLine();

        sb.AppendLine("RNG SNAPSHOT (first 16 of 250)");
        sb.AppendLine("  " + string.Join(" ", doc.Header.RandomizerTable.Take(16).Select(v => v.ToString("X8"))));
        sb.AppendLine();

        sb.AppendLine("GAME SPEED");
        sb.AppendLine("  The header carries the speed the game started at; a change is written into the");
        sb.AppendLine("  stream on the frame it happens. Durations integrate across the segments.");
        foreach (var segment in doc.GameSpeed.Segments)
        {
            sb.AppendLine($"  from frame {segment.StartFrame,8:N0}  index {segment.SpeedIndex}  " +
                          $"{segment.Fps,3} FPS  (at {ReplayDocument.FormatTime(TimeSpan.FromSeconds(segment.StartSeconds))})");
        }
        sb.AppendLine();

        sb.AppendLine("HOUSE INDEX MAP");
        sb.AppendLine("  Derived by reproducing Assign_Houses: human nodes sorted by player colour,");
        sb.AppendLine("  ties to the earlier spawn.ini slot, then AI houses in slot order.");
        foreach (var p in doc.Roster.ByHouseIndex)
            sb.AppendLine($"  house {p.HouseIndex}  <-  slot {p.Slot}  colour {p.Color,-3}  " +
                          $"{(p.IsHuman ? "human" : "AI")}  {p.DisplayName}");

        var seen = doc.EnumerateEvents().Select(e => (int)e.HouseIndex).Distinct().OrderBy(h => h).ToList();
        sb.AppendLine($"  house indices seen in the event stream: " +
                      string.Join(", ", seen.Select(h => h < 0 ? "none" : h.ToString())));

        var unknown = seen.Where(h => h >= 0 && doc.Roster.ForHouse(h) is null).ToList();
        if (unknown.Count > 0)
            sb.AppendLine($"  ! events carry house {string.Join(", ", unknown)}, which spawn.ini does not account for");

        _diagnosticsBox.Text = Normalise(sb.ToString());
    }

    private static string DescribeFlags(uint flags)
    {
        if (flags == 0) return "(none)";
        var parts = new List<string>();
        if ((flags & (uint)FrameRecordFlags.TacticalPos) != 0) parts.Add("TacticalPos");
        if ((flags & (uint)FrameRecordFlags.Selection) != 0) parts.Add("Selection");
        if ((flags & (uint)FrameRecordFlags.SideChannel) != 0) parts.Add("SideChannel");
        if ((flags & (uint)FrameRecordFlags.GameCrc) != 0) parts.Add("GameCRC");
        if ((flags & (uint)FrameRecordFlags.ObjectCensus) != 0) parts.Add("ObjectCensus");
        if ((flags & (uint)FrameRecordFlags.RandomState) != 0) parts.Add("RandomState");
        if ((flags & (uint)FrameRecordFlags.GameSpeed) != 0) parts.Add("GameSpeed");
        if ((flags & (uint)FrameRecordFlags.SelectionTriggers) != 0) parts.Add("SelectionTriggers");
        if ((flags & (uint)FrameRecordFlags.HouseStats) != 0) parts.Add("HouseStats");
        if ((flags & (uint)FrameRecordFlags.Extensions) != 0) parts.Add("Extensions");
        uint unknown = flags & ~(uint)FrameRecordFlags.Known;
        if (unknown != 0) parts.Add($"unknown 0x{unknown:X}");
        return string.Join(" | ", parts);
    }

    // --- comparison -------------------------------------------------------------------------

    private void PromptCompare()
    {
        if (_doc is null)
        {
            MessageBox.Show(this, "Open a replay first, then pick the other peer's recording of the " +
                                  "same game to compare it against.", "Nothing to compare");
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "Pick the other peer's recording of this game",
            Filter = "Red Alert 2 replays (*.yrrp)|*.yrrp|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var other = ReplayReader.Load(dialog.FileName);
            var result = DesyncCompare.Compare(_doc, other, _describer);
            using var form = new CompareForm(_doc, other, result);
            form.ShowDialog(this);
        }
        catch (ReplayLoadException ex)
        {
            MessageBox.Show(this, ex.Message, "That file cannot be read",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // --- export -----------------------------------------------------------------------------

    private bool RequireDoc(out ReplayDocument doc)
    {
        doc = _doc!;
        if (_doc is not null) return true;
        MessageBox.Show(this, "Open a replay first.", "Nothing to export");
        return false;
    }

    private string? SaveAs(string suggestedName, string filter)
    {
        using var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : "",
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private void RunExport(Action action, string what)
    {
        try
        {
            action();
            SetStatus($"Wrote {what}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Could not write the file",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string Stem => Path.GetFileNameWithoutExtension(_doc?.FilePath ?? "replay");

    private void ExportEventsCsv()
    {
        if (!RequireDoc(out var doc)) return;
        if (SaveAs($"{Stem}.events.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        RunExport(() => Exporters.WriteEventsCsv(path, doc, _describer, _showTiming.Checked), path);
    }

    private void ExportChatCsv()
    {
        if (!RequireDoc(out var doc)) return;
        if (SaveAs($"{Stem}.chat.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        RunExport(() => Exporters.WriteChatCsv(path, doc), path);
    }

    private void ExportNetworkCsv()
    {
        if (!RequireDoc(out var doc) || _network is null) return;
        if (SaveAs($"{Stem}.network.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        RunExport(() => Exporters.WriteNetworkCsv(path, doc, _network), path);
    }

    private void ExportCrcCsv()
    {
        if (!RequireDoc(out var doc)) return;
        if (SaveAs($"{Stem}.frames.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        RunExport(() => Exporters.WriteFrameCrcCsv(path, doc), path);
    }

    private void ExportSummaryJson()
    {
        if (!RequireDoc(out var doc) || _network is null || _activity is null) return;
        if (SaveAs($"{Stem}.summary.json", "JSON (*.json)|*.json") is not { } path) return;
        RunExport(() => Exporters.WriteSummaryJson(path, doc, _network, _activity), path);
    }

    private void ExportIni(bool map)
    {
        if (!RequireDoc(out var doc)) return;
        string name = map ? "spawnmap.ini" : "spawn.ini";
        if (SaveAs(name, "INI (*.ini)|*.ini") is not { } path) return;
        RunExport(() => File.WriteAllText(path, map ? doc.SpawnMapText : doc.SpawnIniText), path);
    }

    private void ExportAll()
    {
        if (!RequireDoc(out var doc) || _network is null || _activity is null) return;

        using var dialog = new FolderBrowserDialog { Description = "Where should the exports go?" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string dir = dialog.SelectedPath;
        string stem = Path.Combine(dir, Stem);

        RunExport(() =>
        {
            Exporters.WriteEventsCsv($"{stem}.events.csv", doc, _describer, includeTiming: true);
            Exporters.WriteChatCsv($"{stem}.chat.csv", doc);
            Exporters.WriteNetworkCsv($"{stem}.network.csv", doc, _network);
            Exporters.WriteFrameCrcCsv($"{stem}.frames.csv", doc);
            Exporters.WriteSummaryJson($"{stem}.summary.json", doc, _network, _activity);
            Exporters.WriteHouseStatsCsv($"{stem}.house-stats.csv", doc);
            File.WriteAllText($"{stem}.spawn.ini", doc.SpawnIniText);
            File.WriteAllText($"{stem}.spawnmap.ini", doc.SpawnMapText);
            if (doc.Statistics?.StatsPacketBytes is { } packet)
                File.WriteAllBytes($"{stem}.stats.dmp", packet);
        }, $"the exports to {dir}");
    }
}
