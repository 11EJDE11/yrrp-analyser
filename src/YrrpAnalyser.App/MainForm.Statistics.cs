using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private StatisticsAnalysis? _statistics;

    private void PopulateStatistics(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        _statisticsFlow.SuspendLayout();
        _statisticsFlow.Controls.Clear();
        _statisticsCharts.Clear();

        var stats = doc.Statistics;
        if (!analysis.HasTimeline && stats is null)
        {
            _statisticsFlow.Controls.Add(SectionHeading("Statistics"));
            _statisticsFlow.Controls.Add(Note(
                "This recording carries no statistics. The recorder samples every house every " +
                $"{ReplayFormat.HouseStatsIntervalFrames} frames into the frame stream and appends a statistics " +
                "section when the recording closes, so a recording with neither ended before its first sample.",
                Theme.Warning));
            _statisticsFlow.ResumeLayout();
            return;
        }

        _statisticsFlow.Controls.Add(SectionHeading("Result"));
        _statisticsFlow.Controls.Add(Note(
            "Everything here is the engine's own house counters, read by the recorder - not reconstructed " +
            "from the orders. Income is money gained from any source (harvesting, oil derricks, selling, " +
            "refunds): the change in money on hand plus the change in credits spent." +
            (stats is null ? " This recording has no statistics section - it did not close cleanly - so the " +
                             "last timeline sample stands in for the end of the game." : ""),
            Theme.Muted));
        if (stats?.Game is { } game)
            _statisticsFlow.Controls.Add(FactGrid(GameFacts(doc, game), columns: 3));
        _statisticsFlow.Controls.Add(BuildResultTable(doc, analysis));

        if (analysis.HasIncomeSources || stats?.Houses.Any(h => h.IncomeTotal != 0) == true)
        {
            _statisticsFlow.Controls.Add(SectionHeading("Where the money came from"));
            _statisticsFlow.Controls.Add(Note(
                "Counted by the recorder at HouseClass::Refund_Money, the one function every kind of income " +
                "reaches the balance through, by the call site it came from. Harvested is ore and gems unloaded " +
                "at a refinery; buildings is oil derricks and capture bonuses; stolen is a spy in a refinery or a " +
                "money drain. The game's own harvested counter (the packet's HRV) is never updated in YR.",
                Theme.Muted));
            _statisticsFlow.Controls.Add(BuildIncomeTable(doc, analysis));
        }

        if (analysis.HasTimeline)
            AddTimelineCharts(doc, analysis);

        if (stats is { Houses.Count: > 0 })
        {
            _statisticsFlow.Controls.Add(SectionHeading("Built, destroyed and left standing"));
            _statisticsFlow.Controls.Add(Note(
                "Per type, from the same counters the game's statistics packet reports. \"Built\" is every " +
                "object that joined the house - produced, deployed, captured or mind controlled - which is how " +
                "the game counts it. \"Lost\" is counted by the recorder, on the same condition the game counts " +
                "its lost totals on; the game keeps no per-type count of its own. \"Left at the end\" is what the " +
                "house still owned when the recording closed." +
                (stats.Types.HasData ? "" : " The recording carries no type table, so types show by array position."),
                Theme.Muted));
            _statisticsFlow.Controls.Add(BuildCameoTabs(doc, stats));

            _statisticsFlow.Controls.Add(SectionHeading("Kills by opponent"));
            _statisticsFlow.Controls.Add(Note(
                "Units / buildings of the column's house destroyed by the row's house.", Theme.Muted));
            _statisticsFlow.Controls.Add(BuildKillMatrix(doc, stats));
        }

        var superweapons = StatisticsAnalysis.Superweapons(doc, _types);
        if (superweapons.Count > 0)
        {
            _statisticsFlow.Controls.Add(SectionHeading($"Superweapons ({superweapons.Count})"));
            _statisticsFlow.Controls.Add(BuildSuperweaponView(doc, superweapons));
        }

        if (stats?.StatsPacket is { Fields.Count: > 0 } packet)
        {
            _statisticsFlow.Controls.Add(SectionHeading("The game's own statistics packet"));
            _statisticsFlow.Controls.Add(Note(
                "Byte for byte what stats.dmp holds and what the CnCNet ladder parses. The game only builds it " +
                "for an Internet game or when the spawner is asked to write statistics. UNL/INL/PLL/BLL are what " +
                "was left at the end, not losses: the game refills those arrays from each house's objects before " +
                "sending them. Player digits are the packet's own numbering, not house indices.",
                Theme.Muted));
            _statisticsFlow.Controls.Add(BuildStatsPacketView(packet));
        }

        FitWidths(_statisticsFlow);
        _statisticsFlow.ResumeLayout();
    }

    private static Label Note(string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        AutoSize = true,
        MaximumSize = new Size(960, 0),
        Margin = new Padding(0, 0, 0, 6),
    };

    private static List<int> StatisticsHouses(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        var houses = new SortedSet<int>(analysis.Houses.Select(h => h.HouseIndex));
        if (doc.Statistics is { } stats)
        {
            foreach (var h in stats.Houses)
                if ((h.Flags & HouseStatsFlags.Observer) == 0) houses.Add(h.HouseIndex);
        }
        return [.. houses];
    }

    private static string ResultText(ReplayDocument doc, HouseStatsFlags flags, int? defeatedAt)
    {
        if ((flags & HouseStatsFlags.Winner) != 0) return "Won";
        if ((flags & HouseStatsFlags.Resigned) != 0) return "Resigned";
        if ((flags & HouseStatsFlags.LostConnection) != 0) return "Disconnected";
        if ((flags & (HouseStatsFlags.Defeated | HouseStatsFlags.Loser)) != 0)
            return defeatedAt is { } frame ? $"Defeated at {doc.TimeLabel(frame)}" : "Defeated";
        return "Playing at the end";
    }

    private Control BuildResultTable(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        var view = MakeListView(("Player", 150), ("Country", 100), ("Result", 130), ("Money left", 80),
            ("Income", 80), ("Spent", 80), ("Peak army", 80), ("Units built", 75), ("Units killed", 75),
            ("Units lost", 70), ("Bldgs built", 75), ("Bldgs killed", 75), ("Bldgs lost", 70),
            ("Captured", 65), ("Crates", 55), ("Score", 70));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        var houses = StatisticsHouses(doc, analysis);
        foreach (int house in houses)
        {
            var summary = doc.Statistics?.ForHouse(house);
            var timeline = analysis.Houses.FirstOrDefault(h => h.HouseIndex == house);
            var last = timeline?.Last;
            var flags = summary?.Flags ?? last?.Flags ?? HouseStatsFlags.None;

            string N(double? value) => value is { } v ? v.ToString("N0") : "—";
            int? Built(params StatisticsArray[] arrays) => summary is null ? null : arrays.Sum(a => summary.Total(a));

            var item = new ListViewItem([
                StatisticsAnalysis.HouseName(doc, house),
                summary?.Country ?? doc.Roster.ForHouse(house)?.SideName ?? "",
                ResultText(doc, flags, timeline?.DefeatedAtFrame),
                N(summary is not null ? summary.Credits + summary.StoredOreValue : last?.CreditsOnHand),
                N(timeline?.TotalIncome),
                N(summary?.CreditsSpent ?? last?.CreditsSpent),
                N(timeline?.PeakArmyValue),
                N(Built(StatisticsArray.BuiltUnits, StatisticsArray.BuiltInfantry, StatisticsArray.BuiltAircraft) ?? last?.UnitsBuilt),
                N(summary?.UnitsKilled ?? last?.UnitsKilled),
                N(summary?.UnitsLost ?? last?.UnitsLost),
                N(Built(StatisticsArray.BuiltBuildings) ?? last?.BuildingsBuilt),
                N(summary?.BuildingsKilled ?? last?.BuildingsKilled),
                N(summary?.BuildingsLost ?? last?.BuildingsLost),
                N(summary?.Total(StatisticsArray.CapturedBuildings)),
                N(summary?.Total(StatisticsArray.CollectedCrates)),
                N(summary?.Score ?? last?.Score),
            ])
            {
                UseItemStyleForSubItems = false,
            };
            item.SubItems[0].ForeColor = Theme.ForHouse(house);
            item.SubItems[0].Font = Theme.UiBold;
            item.SubItems[2].ForeColor = (flags & HouseStatsFlags.Winner) != 0 ? Theme.Good
                : (flags & (HouseStatsFlags.Defeated | HouseStatsFlags.Loser)) != 0 ? Theme.Danger : Theme.Text;
            view.Items.Add(item);
        }

        view.Height = 30 + Math.Max(1, houses.Count) * 22;
        return view;
    }

    private static (string, string)[] GameFacts(ReplayDocument doc, GameRecord game)
    {
        long wallSeconds = (long)game.EndUnixTime - (long)doc.Header.RecordedUnixTime;
        string outOfSync = game.OutOfSyncFrame >= 0
            ? $"yes - first seen at frame {game.OutOfSyncFrame:N0} ({doc.TimeLabel(game.OutOfSyncFrame)})"
            : game.OutOfSync ? "yes" : "no";
        return
        [
            ("Recording closed", $"frame {game.EndFrame:N0} ({doc.TimeLabel(game.EndFrame)} of game time)"),
            ("Wall-clock length", wallSeconds > 0 ? ReplayDocument.FormatTime(TimeSpan.FromSeconds(wallSeconds)) : "—"),
            ("Average speed", wallSeconds > 0 ? $"{game.EndFrame / (double)wallSeconds:0.0} frames per second" : "—"),
            ("Out of sync", outOfSync),
            ("Game reached its end", game.SawCompletion ? "yes" : "no - quit, disconnected or cut off"),
        ];
    }

    private static readonly (IncomeSource Source, string Title)[] IncomeColumns =
    [
        (IncomeSource.Harvested, "Harvested"),
        (IncomeSource.Buildings, "Buildings"),
        (IncomeSource.Sold, "Sold"),
        (IncomeSource.Refunded, "Refunded"),
        (IncomeSource.Crates, "Crates"),
        (IncomeSource.Stolen, "Stolen"),
        (IncomeSource.Other, "Other"),
    ];

    private Control BuildIncomeTable(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        var columns = new List<(string, int)> { ("Player", 150) };
        columns.AddRange(IncomeColumns.Select(c => (c.Title, 90)));
        columns.Add(("Total", 100));
        columns.Add(("Harvest peak /min", 120));

        var view = MakeListView([.. columns]);
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        var houses = StatisticsHouses(doc, analysis);
        foreach (int house in houses)
        {
            var summary = doc.Statistics?.ForHouse(house);
            var timeline = analysis.Houses.FirstOrDefault(h => h.HouseIndex == house);
            int Amount(IncomeSource s) => summary?.IncomeFrom(s) ?? timeline?.Last.Income(s) ?? 0;

            var cells = new List<string> { StatisticsAnalysis.HouseName(doc, house) };
            cells.AddRange(IncomeColumns.Select(c => Amount(c.Source).ToString("N0")));
            cells.Add(IncomeColumns.Sum(c => Amount(c.Source)).ToString("N0"));
            cells.Add(timeline is null ? "—" : timeline.PeakHarvestRate.ToString("N0"));

            var item = new ListViewItem([.. cells]) { UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.ForHouse(house);
            view.Items.Add(item);
        }

        view.Height = 30 + Math.Max(1, houses.Count) * 22;
        return view;
    }

    private static Control BuildSuperweaponView(ReplayDocument doc, List<SuperweaponUse> uses)
    {
        var view = MakeListView(("Time", 80), ("Frame", 90), ("Player", 160), ("Superweapon", 300), ("Target", 120));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        FillWidth(view);

        foreach (var use in uses)
        {
            var item = new ListViewItem([
                doc.TimeLabel(use.Frame),
                use.Frame.ToString("N0"),
                StatisticsAnalysis.HouseName(doc, use.HouseIndex),
                use.Name,
                use.Where,
            ])
            {
                UseItemStyleForSubItems = false,
            };
            item.SubItems[2].ForeColor = Theme.ForHouse(use.HouseIndex);
            view.Items.Add(item);
        }

        view.Height = Math.Min(30 + uses.Count * 20, 420);
        return view;
    }

    private void AddTimelineCharts(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        int fps = doc.Header.SimulationFps;

        _statisticsFlow.Controls.Add(SectionHeading("Over the game"));
        _statisticsFlow.Controls.Add(Note(
            $"Every house sampled every {ReplayFormat.HouseStatsIntervalFrames} frames " +
            $"({doc.HouseStatsFrameCount:N0} samples). Dotted lines mark a house being defeated. " +
            "Army value is the summed cost of every vehicle, infantry and aircraft the house has on the field.",
            Theme.Muted));
        _statisticsFlow.Controls.Add(ChartHint());

        var markers = analysis.Houses
            .Where(h => h.DefeatedAtFrame.HasValue)
            .Select(h => new ChartMarker(h.DefeatedAtFrame!.Value, Theme.ForHouse(h.HouseIndex), $"{h.Name} defeated"))
            .ToList();

        IEnumerable<ChartSeries> Per(Func<HouseTimeline, List<Sample>> pick, SeriesStyle style = SeriesStyle.Line) =>
            analysis.Houses.Select(h => new ChartSeries
            {
                Name = h.Name,
                Color = Theme.ForHouse(h.HouseIndex),
                Style = style,
                Points = pick(h),
            });

        void Chart(string title, IEnumerable<ChartSeries> series, double minimumY = 1)
        {
            var chart = new TimeSeriesChart
            {
                Title = title,
                SimulationFps = fps,
                TimeLabeller = doc.TimeLabel,
                MinimumYRange = minimumY,
                Width = 980,
                Height = 220,
                Margin = new Padding(0, 4, 0, 10),
            };
            chart.SetData(series, markers);
            _statisticsCharts.Add(chart);
            _statisticsFlow.Controls.Add(FillWidth(chart));
        }

        Chart("Money on hand - credits plus the ore waiting in refineries and silos", Per(h => h.CreditsOnHand), 1000);
        Chart("Income, total so far", Per(h => h.Income), 1000);
        Chart($"Income per minute, over the last {StatisticsAnalysis.IncomeRateWindowSeconds:0} seconds", Per(h => h.IncomeRate), 500);
        if (analysis.HasHarvestData)
        {
            Chart("Harvested - ore and gems unloaded at refineries", Per(h => h.Harvested), 1000);
            Chart($"Harvest per minute, over the last {StatisticsAnalysis.IncomeRateWindowSeconds:0} seconds", Per(h => h.HarvestRate), 500);
        }
        Chart("Credits spent", Per(h => h.Spent), 1000);
        Chart("Army value", Per(h => h.ArmyValue), 1000);
        Chart("Army size - vehicles, infantry and aircraft", Per(h => h.ArmySize, SeriesStyle.Step), 5);
        Chart("Base value - summed cost of every building", Per(h => h.BuildingValue), 1000);
        Chart("Buildings", Per(h => h.BuildingCount, SeriesStyle.Step), 5);
        Chart("Power produced (solid) and used (step)",
            analysis.Houses.SelectMany(h => new[]
            {
                new ChartSeries { Name = h.Name, Color = Theme.ForHouse(h.HouseIndex), Style = SeriesStyle.Line, Points = h.PowerOutput },
                new ChartSeries { Name = $"{h.Name} used", Color = Theme.Blend(Theme.ForHouse(h.HouseIndex), Color.White, 0.45),
                                  Style = SeriesStyle.Step, Points = h.PowerDrain },
            }), 100);
        Chart("Kills - units and buildings destroyed", Per(h => h.Kills, SeriesStyle.Step), 5);
        Chart("Losses - units and buildings lost", Per(h => h.Losses, SeriesStyle.Step), 5);
        Chart("Units built", Per(h => h.UnitsBuilt, SeriesStyle.Step), 5);
        Chart("Score", Per(h => h.Score, SeriesStyle.Step), 100);
    }

    private static readonly (string Title, StatisticsArray[] Arrays)[] CameoGroups =
    [
        ("Built", [StatisticsArray.BuiltUnits, StatisticsArray.BuiltInfantry, StatisticsArray.BuiltAircraft, StatisticsArray.BuiltBuildings]),
        ("Destroyed", [StatisticsArray.KilledUnits, StatisticsArray.KilledInfantry, StatisticsArray.KilledAircraft, StatisticsArray.KilledBuildings]),
        ("Lost", [StatisticsArray.LostUnits, StatisticsArray.LostInfantry, StatisticsArray.LostAircraft, StatisticsArray.LostBuildings]),
        ("Left at the end", [StatisticsArray.LeftUnits, StatisticsArray.LeftInfantry, StatisticsArray.LeftAircraft, StatisticsArray.LeftBuildings]),
        ("Captured", [StatisticsArray.CapturedBuildings]),
    ];

    private static Control BuildCameoTabs(ReplayDocument doc, ReplayStatistics stats)
    {
        var tabs = new TabControl { Width = 980, Height = 480, Margin = new Padding(0, 0, 0, 10) };
        FillWidth(tabs);

        foreach (var house in stats.Houses.Where(h => (h.Flags & HouseStatsFlags.Observer) == 0).OrderBy(h => h.HouseIndex))
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 6) };
            var grids = new List<Control>();

            foreach (var (title, arrays) in CameoGroups)
            {
                var items = arrays.SelectMany(a => CameoItems(stats.Types, a, house.Array(a)))
                    .OrderByDescending(i => i.Count).ToList();
                grids.Add(new CameoGrid { Caption = $"{title}  ({items.Sum(i => i.Count):N0})", Dock = DockStyle.Top }.WithItems(items));
            }

            var crates = house.Array(StatisticsArray.CollectedCrates);
            var crateItems = crates.Select((count, i) => (count, i)).Where(c => c.count > 0)
                .Select(c => new CameoItem($"crate{c.i}", "", CrateName(c.i), c.count, 0)).ToList();
            grids.Add(new CameoGrid { Caption = $"Crates  ({crateItems.Sum(i => i.Count):N0})", Dock = DockStyle.Top }.WithItems(crateItems));

            // Dock.Top stacks the last one added on top.
            for (int i = grids.Count - 1; i >= 0; i--) panel.Controls.Add(grids[i]);

            tabs.TabPages.Add(new TabPage(StatisticsAnalysis.HouseName(doc, house.HouseIndex))
            {
                Controls = { panel },
                BackColor = Theme.Panel,
            });
        }

        if (tabs.TabPages.Count == 0)
            tabs.TabPages.Add(new TabPage("No players") { BackColor = Theme.Panel });
        return tabs;
    }

    private static IEnumerable<CameoItem> CameoItems(TypeTable types, StatisticsArray which, int[] counts)
    {
        var kind = HouseSummary.KindOf(which);
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0) continue;
            var info = types.Get(kind, i);
            string fallback = $"{kind}#{i}";
            yield return new CameoItem(info?.Id ?? fallback, info?.Cameo ?? "", info?.DisplayName ?? fallback,
                counts[i], info?.Cost ?? 0);
        }
    }

    /// <summary>Crate kinds, by the Powerup enum index the game counts them under.</summary>
    private static string CrateName(int index)
    {
        string[] names =
        [
            "Money", "Unit", "Heal base", "Cloak", "Explosion", "Napalm", "Squad", "Darkness", "Reveal",
            "Armor", "Speed", "Firepower", "ICBM", "Invulnerability", "Veteran", "Ion storm", "Gas",
            "Tiberium", "Pod",
        ];
        return index >= 0 && index < names.Length ? names[index] : $"Crate #{index}";
    }

    private static Control BuildKillMatrix(ReplayDocument doc, ReplayStatistics stats)
    {
        var houses = stats.Houses.Where(h => (h.Flags & HouseStatsFlags.Observer) == 0).OrderBy(h => h.HouseIndex).ToList();

        var columns = new List<(string, int)> { ("Destroyed by", 150) };
        columns.AddRange(houses.Select(h => (Short(StatisticsAnalysis.HouseName(doc, h.HouseIndex)), 100)));
        columns.Add(("Total", 90));

        var view = MakeListView([.. columns]);
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Height = 30 + Math.Max(1, houses.Count) * 22;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        foreach (var killer in houses)
        {
            var cells = new List<string> { StatisticsAnalysis.HouseName(doc, killer.HouseIndex) };
            foreach (var victim in houses)
            {
                int v = victim.HouseIndex;
                int units = v < killer.UnitsKilledOfHouse.Length ? killer.UnitsKilledOfHouse[v] : 0;
                int buildings = v < killer.BuildingsKilledOfHouse.Length ? killer.BuildingsKilledOfHouse[v] : 0;
                cells.Add(victim.HouseIndex == killer.HouseIndex && units == 0 && buildings == 0
                    ? "—"
                    : $"{units} / {buildings}");
            }
            cells.Add($"{killer.UnitsKilled} / {killer.BuildingsKilled}");

            var item = new ListViewItem([.. cells]) { UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.ForHouse(killer.HouseIndex);
            view.Items.Add(item);
        }

        return view;

        static string Short(string name) => name.Length > 14 ? name[..14] : name;
    }

    private static Control BuildStatsPacketView(StatsDump packet)
    {
        var view = MakeListView(("Tag", 60), ("Player", 130), ("Meaning", 210), ("Value", 560));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        FillWidth(view);

        string PlayerName(int index) => index < 0 ? "" : packet.Text($"NAM{index}") ?? $"player {index}";

        foreach (var field in packet.GameFields.Concat(packet.Fields.Where(f => f.PlayerIndex >= 0).OrderBy(f => f.PlayerIndex)))
        {
            view.Items.Add(new ListViewItem([
                field.Tag,
                PlayerName(field.PlayerIndex),
                StatsDump.Meaning(field.Key),
                field.DisplayValue,
            ])
            {
                ForeColor = StatsDump.Meaning(field.Key).Length == 0 ? Theme.Muted : Theme.Text,
            });
        }

        view.Height = Math.Min(30 + packet.Fields.Count * 20, 640);
        return view;
    }

    // --- saves ------------------------------------------------------------------------------

    private readonly ListView _savesView = MakeListView(("Frame", 90), ("Time", 80), ("Compressed", 110),
        ("Save", 110), ("Sidecar", 110), ("Status", 420));

    private readonly Label _savesSummary = new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Margin = new Padding(12, 6, 0, 4),
    };

    private Control BuildSavesTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(12) };

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Background,
        };
        var saveOne = new Button { Text = "Save selected as .SAV...", AutoSize = true, Margin = new Padding(0, 1, 6, 0) };
        var saveAll = new Button { Text = "Save all to a folder...", AutoSize = true, Margin = new Padding(0, 1, 6, 0) };
        saveOne.Click += (_, _) => SaveSelectedCheckpoint();
        saveAll.Click += (_, _) => SaveAllCheckpoints();
        header.Controls.Add(saveOne);
        header.Controls.Add(saveAll);
        header.Controls.Add(_savesSummary);

        var note = new Label
        {
            Text = "Saves made during the recorded game, embedded so playback can seek to them. Each is the " +
                   "game's own .SAV, byte for byte, plus a sidecar of simulation state the savegame loses; " +
                   "saving one out writes just the .SAV. Double-click a row to save it.",
            Dock = DockStyle.Top,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Height = 40,
            Padding = new Padding(0, 6, 0, 6),
        };

        _savesView.DoubleClick += (_, _) => SaveSelectedCheckpoint();

        root.Controls.Add(_savesView);
        root.Controls.Add(note);
        root.Controls.Add(header);
        return root;
    }

    private void PopulateSaves(ReplayDocument doc)
    {
        _savesView.BeginUpdate();
        _savesView.Items.Clear();
        foreach (var c in doc.Checkpoints)
        {
            _savesView.Items.Add(new ListViewItem([
                c.Frame.ToString("N0"),
                doc.TimeLabel(c.Frame),
                c.CompressedSize.ToString("N0"),
                c.SaveBytes.ToString("N0"),
                c.SidecarBytes.ToString("N0"),
                c.Problem ?? "ok",
            ])
            {
                Tag = c,
                ForeColor = c.Usable ? Theme.Text : Theme.Warning,
            });
        }
        _savesView.EndUpdate();
        _savesSummary.Text = doc.CheckpointSummary;
    }

    private string SuggestedSaveName(RecordedCheckpoint c) => $"{Stem} - frame {c.Frame}.SAV";

    private void SaveSelectedCheckpoint()
    {
        if (!RequireDoc(out var doc)) return;
        if (_savesView.SelectedItems.Count == 0 || _savesView.SelectedItems[0].Tag is not RecordedCheckpoint c)
        {
            MessageBox.Show(this, doc.Checkpoints.Count == 0 ? "This recording has no embedded saves." : "Pick a save first.",
                "Nothing to save");
            return;
        }
        if (SaveAs(SuggestedSaveName(c), "Saved games (*.sav)|*.sav|All files (*.*)|*.*") is not { } path) return;
        WriteCheckpoints(doc, [(c, path)], path);
    }

    private void SaveAllCheckpoints()
    {
        if (!RequireDoc(out var doc)) return;
        var usable = doc.Checkpoints.Where(c => c.Usable).ToList();
        if (usable.Count == 0)
        {
            MessageBox.Show(this, $"There are no usable embedded saves: {doc.CheckpointSummary}.", "Nothing to save");
            return;
        }

        using var dialog = new FolderBrowserDialog { Description = "Where should the saves go?" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        WriteCheckpoints(doc, usable.Select(c => (c, Path.Combine(dialog.SelectedPath, SuggestedSaveName(c)))).ToList(),
            $"{usable.Count} save(s) to {dialog.SelectedPath}");
    }

    private void WriteCheckpoints(ReplayDocument doc, List<(RecordedCheckpoint Checkpoint, string Path)> targets, string what)
    {
        try
        {
            foreach (var (checkpoint, path) in targets)
                File.WriteAllBytes(path, CheckpointArchiveReader.ExtractSave(doc.FilePath, checkpoint));
            SetStatus($"Wrote {what}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Could not write the save",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // --- statistics exports -----------------------------------------------------------------

    private void ExportHouseStatsCsv()
    {
        if (!RequireDoc(out var doc)) return;
        if (SaveAs($"{Stem}.house-stats.csv", "CSV (*.csv)|*.csv") is not { } path) return;
        RunExport(() => Exporters.WriteHouseStatsCsv(path, doc), path);
    }

    private void ExportStatsPacket()
    {
        if (!RequireDoc(out var doc)) return;
        if (doc.Statistics?.StatsPacketBytes is not { } bytes)
        {
            MessageBox.Show(this, "The game built no statistics packet for this recording. It only does for an " +
                                  "Internet game, or when the spawner is asked to write statistics.", "Nothing to export");
            return;
        }
        if (SaveAs("stats.dmp", "Statistics dump (*.dmp)|*.dmp|All files (*.*)|*.*") is not { } path) return;
        RunExport(() => File.WriteAllBytes(path, bytes), path);
    }
}
