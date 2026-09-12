using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private StatisticsAnalysis? _statistics;
    private readonly List<TimeSeriesChart> _timelineCharts = [];
    private string? _pinnedTitle;

    private static string HouseKey(int houseIndex) => $"house:{houseIndex}";

    /// <summary>
    /// Puts a copy of a chart above the scrolling page, on the same time axis, cursor and player
    /// visibility as the rest, so it can be read against anything further down.
    /// </summary>
    private void PinChart(TimeSeriesChart source)
    {
        UnpinChart();
        _statisticsPinned.Visible = true;
        var pinned = new TimeSeriesChart
        {
            Title = source.Title,
            ValueSuffix = source.ValueSuffix,
            SimulationFps = source.SimulationFps,
            TimeLabeller = source.TimeLabeller,
            MinimumYRange = source.MinimumYRange,
            Height = 220,
            Dock = DockStyle.Top,
        };
        _statisticsPinned.Controls.Add(pinned);
        pinned.SetData(source.Series.Select(s => new ChartSeries
        {
            Name = s.Name,
            Color = s.Color,
            Style = s.Style,
            DashStyle = s.DashStyle,
            Points = s.Points,
            Key = s.Key,
            Visible = s.Visible,
        }), source.Markers);
        pinned.SetViewRange(source.ViewMinFrame, source.ViewMaxFrame, propagate: false);
        // Deferred: the chart would otherwise be disposed from inside its own mouse handler.
        pinned.Buttons = [new ChartButton("Unpin", () => BeginInvoke(UnpinChart))];
        pinned.SizeChanged += (_, _) => FitPinnedStrip();
        _statisticsCharts.Add(pinned);
        _pinnedTitle = source.Title;
        FitPinnedStrip();
    }

    // The row of charts the user has hidden, each a button that brings it back.
    private readonly FlowLayoutPanel _hiddenChartsBar = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        MaximumSize = new Size(960, 0),
        BackColor = Theme.Background,
        Margin = new Padding(0, 0, 0, 6),
        Visible = false,
    };

    /// <summary>Hides or shows charts, remembers the choice by title, and redraws the hidden-charts row.</summary>
    private void SetChartsHidden(IEnumerable<TimeSeriesChart> charts, bool hidden)
    {
        foreach (var chart in charts)
        {
            chart.Visible = !hidden;
            _settings.HiddenCharts.Remove(chart.Title);
            if (hidden) _settings.HiddenCharts.Add(chart.Title);
        }
        RefreshHiddenChartsBar();
    }

    private void RefreshHiddenChartsBar()
    {
        _hiddenChartsBar.SuspendLayout();
        foreach (var old in _hiddenChartsBar.Controls.Cast<Control>().ToList())
            old.Dispose();

        var hidden = _timelineCharts.Where(c => !c.Visible).ToList();
        if (hidden.Count > 0)
        {
            _hiddenChartsBar.Controls.Add(new Label
            {
                Text = "Hidden charts:",
                AutoSize = true,
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 6, 4, 0),
            });
            foreach (var chart in hidden)
                _hiddenChartsBar.Controls.Add(HiddenChartButton(ShortTitle(chart.Title), [chart]));
            if (hidden.Count > 1)
                _hiddenChartsBar.Controls.Add(HiddenChartButton("Show all", hidden));
        }
        _hiddenChartsBar.Visible = hidden.Count > 0;
        _hiddenChartsBar.ResumeLayout();
    }

    private Button HiddenChartButton(string text, List<TimeSeriesChart> charts)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 4, 4) };
        // Deferred: showing a chart rebuilds this row, which disposes the button being clicked.
        button.Click += (_, _) => BeginInvoke(() => SetChartsHidden(charts, false));
        return button;
    }

    /// <summary>A chart title up to its first explanation: "Army size - vehicles, ..." is "Army size".</summary>
    private static string ShortTitle(string title)
    {
        int cut = title.Length;
        foreach (var separator in new[] { " - ", ", ", " (" })
        {
            int at = title.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0) cut = Math.Min(cut, at);
        }
        return title[..cut];
    }

    private void FitPinnedStrip()
    {
        if (_statisticsPinned.Controls.Count > 0)
            _statisticsPinned.Height = _statisticsPinned.Controls[0].Height + _statisticsPinned.Padding.Vertical;
    }

    private void UnpinChart()
    {
        foreach (var chart in _statisticsPinned.Controls.OfType<TimeSeriesChart>().ToList())
        {
            _statisticsCharts.Remove(chart);
            chart.Dispose();
        }
        _statisticsPinned.Visible = false;
        _pinnedTitle = null;
    }

    private void PopulateStatistics(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        // A pinned chart comes back, by title, from the new analysis once the charts exist again.
        string? pinnedTitle = _pinnedTitle;
        UnpinChart();
        _statisticsFlow.SuspendLayout();
        _statisticsFlow.Controls.Clear();
        _statisticsCharts.Clear();
        _timelineCharts.Clear();

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
            "Money left - cash plus ore still waiting in refineries and silos at the end.\n" +
            "Income - everything earned during the game; the table below splits it up.\n" +
            "Spent (net) - money spent on building and repairs, less refunds for production that was cancelled.\n" +
            "Peak army - the most this player's vehicles, infantry and aircraft were worth at any one time.\n" +
            "Built - everything that joined the player: produced, deployed, captured or mind controlled.\n" +
            "Killed - other players' units or buildings this player destroyed. Lost - this player's own destroyed.\n" +
            "Captured - buildings taken with engineers. Crates - crates picked up. Score - the game's own score." +
            (stats is null ? "\nThis recording has no statistics section - it did not close cleanly - so the " +
                             "last timeline sample stands in for the end of the game." : ""),
            Theme.Muted));
        if (stats?.Game is { } game)
            _statisticsFlow.Controls.Add(FactGrid(GameFacts(doc, game), columns: 3));
        _statisticsFlow.Controls.Add(BuildResultTable(doc, analysis));

        if (analysis.HasIncomeSources)
        {
            _statisticsFlow.Controls.Add(SectionHeading("Where the money came from"));
            _statisticsFlow.Controls.Add(Note(IncomeLegend(analysis), Theme.Muted));
            _statisticsFlow.Controls.Add(BuildIncomeTable(doc, analysis));

            if (analysis.Unclassified.Count > 0)
            {
                _statisticsFlow.Controls.Add(SectionHeading($"Money the analyser does not recognise ({analysis.Unclassified.Count})"));
                _statisticsFlow.Controls.Add(Note(
                    "What makes up the Unclassified column: payments from places in the game or its DLLs the income " +
                    "table has no entry for yet - usually after an Ares or Phobos update. Add them with Edit income " +
                    "table, using the details shown here, then Reload.",
                    Theme.Warning));
                _statisticsFlow.Controls.Add(BuildIncomeTableButtons());
                _statisticsFlow.Controls.Add(BuildUnclassifiedView(doc, analysis));
            }
            else
            {
                _statisticsFlow.Controls.Add(BuildIncomeTableButtons());
            }
        }

        if (stats is { Houses.Count: > 0 })
        {
            _statisticsFlow.Controls.Add(SectionHeading("Built, destroyed and left standing"));
            _statisticsFlow.Controls.Add(Note(
                "Every player sits under the same row of pictures, so each type's counts line up. Types are grouped " +
                "by side - Soviet, then Allied, then Yuri, then anything else - by whichever side owned most of them " +
                "in this game, and players are listed in the same order; a player with none of a side's types is " +
                "left out of that group. Hover a count for what it was worth.\n" +
                "Built - everything that joined the player: produced, deployed, captured or mind controlled.\n" +
                "Destroyed - other players' things this player destroyed. Lost - this player's own, destroyed.\n" +
                "Left at the end - what the player still had when the recording closed. Captured - buildings taken " +
                "with engineers." +
                (stats.Types.HasData ? "" : "\nThe recording carries no type table, so types show by array position."),
                Theme.Muted));
            AddCameoMatrix(doc, stats);
        }

        if (analysis.HasTimeline)
            AddTimelineCharts(doc, analysis);

        if (stats is { Houses.Count: > 0 })
        {
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

        if (_timelineCharts.FirstOrDefault(c => c.Title == pinnedTitle) is { } again)
            PinChart(again);
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
                houses.Add(h.HouseIndex);
        }
        return [.. houses];
    }

    private static string ResultText(ReplayDocument doc, HouseStatsFlags flags, int? defeatedAt, bool spectator)
    {
        if (spectator) return "Spectator";
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
            ("Income", 80), ("Spent (net)", 80), ("Peak army", 80), ("Units built", 75), ("Units killed", 75),
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
                ResultText(doc, flags, timeline?.DefeatedAtFrame, analysis.IsSpectator(house, flags)),
                N(summary is not null ? summary.Credits + summary.StoredOreValue : last?.CreditsOnHand),
                N(timeline?.TotalIncome),
                N(timeline?.NetSpent ?? summary?.CreditsSpent),
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

    private static string IncomeTitle(IncomeSource source) => source switch
    {
        IncomeSource.Refunded => "Refunded*",
        IncomeSource.StartingCredits => "Starting bonus*",
        _ => source.ToString(),
    };

    private static string IncomeMeaning(IncomeSource source) => source switch
    {
        IncomeSource.Harvested => "ore and gems unloaded by harvesters and slave miners.",
        IncomeSource.Buildings => "oil derricks and other buildings that make money, and the bonus for capturing one.",
        IncomeSource.Sold => "buildings and units sold.",
        IncomeSource.Refunded => "money back for production cancelled part-way through. It was never really spent, " +
                                 "so it is not income, and it is taken off Spent.",
        IncomeSource.StartingCredits => "the extra money AI players get at the start, on top of the game's starting " +
                                        "credits, which grows with their difficulty. Not income.",
        IncomeSource.Grinding => "units sent into a Grinder.",
        IncomeSource.Crates => "money crates.",
        IncomeSource.Stolen => "taken from other players: a spy in a refinery, or a money drain.",
        IncomeSource.Bounty => "bounties paid for kills.",
        IncomeSource.Superweapon => "superweapons that give money.",
        IncomeSource.Warhead => "weapons that give money when they hit.",
        IncomeSource.Other => "anything else that pays out, such as a building's upgrades refunded when it is captured.",
        IncomeSource.Unclassified => "money the analyser does not recognise yet - see the list below.",
        _ => "",
    };

    /// <summary>Only the sources anyone actually received, in the enum's order.</summary>
    private static List<IncomeSource> ShownIncomeSources(StatisticsAnalysis analysis) =>
        Enum.GetValues<IncomeSource>().Where(s => analysis.Houses.Any(h => h.IncomeFrom(s) != 0)).ToList();

    private static bool ShowsDirectIncome(StatisticsAnalysis analysis) =>
        analysis.Houses.Any(h => Math.Abs(h.DirectIncome) >= 1);

    /// <summary>What each column of the income table means, for the columns it actually shows.</summary>
    private static string IncomeLegend(StatisticsAnalysis analysis)
    {
        var lines = ShownIncomeSources(analysis).Select(s => $"{IncomeTitle(s)} - {IncomeMeaning(s)}").ToList();
        if (ShowsDirectIncome(analysis))
            lines.Add("Direct - money that arrived without a payment the game records, such as ore stored in silos.");
        lines.Add("Income - the total earned: every column added up, apart from those marked *.");
        lines.Add($"Harvest peak /min - the fastest this player harvested over any {StatisticsAnalysis.IncomeRateWindowSeconds:0} " +
                  "seconds, as a rate per minute.");
        return string.Join("\n", lines);
    }

    private static Control BuildIncomeTable(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        // Refunded and the starting bonus stay visible even though they are not income: refunds are what
        // makes Spent (net) differ from the raw counter, and the bonus is where an AI's money came from.
        var sources = ShownIncomeSources(analysis);
        bool showDirect = ShowsDirectIncome(analysis);

        var columns = new List<(string, int)> { ("Player", 150) };
        columns.AddRange(sources.Select(s => (IncomeTitle(s), 90)));
        if (showDirect) columns.Add(("Direct", 80));
        columns.Add(("Income", 100));
        columns.Add(("Harvest peak /min", 120));

        var view = MakeListView([.. columns]);
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 4);
        view.Font = Theme.Mono;
        FillWidth(view);

        foreach (var t in analysis.Houses)
        {
            var cells = new List<string> { t.Name };
            cells.AddRange(sources.Select(s => t.IncomeFrom(s).ToString("N0")));
            if (showDirect) cells.Add(t.DirectIncome.ToString("N0"));
            cells.Add(t.TotalIncome.ToString("N0"));
            cells.Add(t.PeakHarvestRate.ToString("N0"));

            var item = new ListViewItem([.. cells]) { UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.ForHouse(t.HouseIndex);
            for (int i = 0; i < sources.Count; i++)
                if (!StatisticsAnalysis.IsIncome(sources[i])) item.SubItems[i + 1].ForeColor = Theme.Muted;
            int unclassifiedColumn = sources.IndexOf(IncomeSource.Unclassified);
            if (unclassifiedColumn >= 0) item.SubItems[unclassifiedColumn + 1].ForeColor = Theme.Warning;
            view.Items.Add(item);
        }

        view.Height = 30 + Math.Max(1, analysis.Houses.Count) * 22;
        return view;
    }

    private Control BuildIncomeTableButtons()
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = Theme.Background,
        };
        var edit = new Button { Text = "Edit income table...", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        var reload = new Button { Text = "Reload income table", AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
        edit.Click += (_, _) => EditIncomeTable();
        reload.Click += (_, _) => ReloadIncomeTable();
        row.Controls.Add(edit);
        row.Controls.Add(reload);
        return row;
    }

    private static Control BuildUnclassifiedView(ReplayDocument doc, StatisticsAnalysis analysis)
    {
        var view = MakeListView(("Caller", 220), ("Build", 110), ("Amount", 110), ("Payments", 80), ("Players", 400));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        foreach (var u in analysis.Unclassified)
        {
            view.Items.Add(new ListViewItem([
                u.Caller.ToString(),
                u.Caller.TimeDateStamp == 0 ? "—" : $"0x{u.Caller.TimeDateStamp:X8}",
                u.Amount.ToString("N0"),
                u.Payments.ToString("N0"),
                string.Join(", ", u.Houses.Select(h => StatisticsAnalysis.HouseName(doc, h))),
            ]));
        }

        view.Height = Math.Min(30 + analysis.Unclassified.Count * 20, 300);
        return view;
    }

    /// <summary>Opens the income table overrides file, creating it from the built-in table first.</summary>
    private void EditIncomeTable()
    {
        var path = IncomeClassifier.OverridesPath;
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, IncomeClassifier.ToJson(IncomeClassifier.BuiltIn));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"Editing {path}. Reload the income table when you have saved it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Could not open the income table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReloadIncomeTable()
    {
        IncomeClassifier.Reload();
        if (_doc is not null) Analyse(_doc);
        SetStatus($"Reloaded the income table ({IncomeClassifier.Default.Rules.Count} callers).");
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
            "Army value is the summed cost of every vehicle, infantry and aircraft the house has on the field. " +
            "Spectators are left off, since they own nothing.",
            Theme.Muted));
        _statisticsFlow.Controls.Add(ChartHint());
        _statisticsFlow.Controls.Add(Note(
            "Pin to top keeps a chart above the page while you scroll, to read it against any other chart or table. " +
            "Hide puts a chart away; it waits in the row below until you click it to bring it back. " +
            "Hiding or isolating a player on one chart does it on all of them.",
            Theme.Muted));
        _statisticsFlow.Controls.Add(_hiddenChartsBar);

        var players = analysis.Houses.Where(h => !h.IsSpectator).ToList();
        var markers = players
            .Where(h => h.DefeatedAtFrame.HasValue)
            .Select(h => new ChartMarker(h.DefeatedAtFrame!.Value, Theme.ForHouse(h.HouseIndex), $"{h.Name} defeated"))
            .ToList();

        IEnumerable<ChartSeries> Per(Func<HouseTimeline, List<Sample>> pick, SeriesStyle style = SeriesStyle.Line) =>
            players.Select(h => new ChartSeries
            {
                Name = h.Name,
                Color = Theme.ForHouse(h.HouseIndex),
                Key = HouseKey(h.HouseIndex),
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
                Visible = !_settings.HiddenCharts.Contains(title),
            };
            chart.SetData(series, markers);
            chart.Buttons =
            [
                new ChartButton("Pin to top", () => PinChart(chart)),
                // Deferred, like every button here that changes the layout under the pointer.
                new ChartButton("Hide", () => BeginInvoke(() => SetChartsHidden([chart], true))),
            ];
            _statisticsCharts.Add(chart);
            _timelineCharts.Add(chart);
            _statisticsFlow.Controls.Add(FillWidth(chart));
        }

        // The overall picture first - money, army, power and the fighting - then the economy in detail,
        // the base, and production and score last.
        Chart("Money on hand - credits plus the ore waiting in refineries and silos", Per(h => h.CreditsOnHand), 1000);
        Chart("Army value", Per(h => h.ArmyValue), 1000);
        Chart("Army size - vehicles, infantry and aircraft", Per(h => h.ArmySize, SeriesStyle.Step), 5);
        Chart("Power produced (solid) and used (dashed)",
            players.SelectMany(h => new[]
            {
                new ChartSeries { Name = h.Name, Color = Theme.ForHouse(h.HouseIndex), Key = HouseKey(h.HouseIndex),
                                  Style = SeriesStyle.Line, Points = h.PowerOutput },
                new ChartSeries { Name = $"{h.Name} used", Color = Theme.ForHouse(h.HouseIndex), Key = HouseKey(h.HouseIndex),
                                  Style = SeriesStyle.Step, DashStyle = System.Drawing.Drawing2D.DashStyle.Dash, Points = h.PowerDrain },
            }), 100);
        Chart("Kills - units and buildings destroyed", Per(h => h.Kills, SeriesStyle.Step), 5);
        Chart("Losses - units and buildings lost", Per(h => h.Losses, SeriesStyle.Step), 5);
        Chart($"Income per minute, over the last {StatisticsAnalysis.IncomeRateWindowSeconds:0} seconds", Per(h => h.IncomeRate), 500);
        Chart("Income, total so far", Per(h => h.Income), 1000);
        if (analysis.HasHarvestData)
        {
            Chart($"Harvest per minute, over the last {StatisticsAnalysis.IncomeRateWindowSeconds:0} seconds", Per(h => h.HarvestRate), 500);
            Chart("Harvested - ore and gems unloaded at refineries and slave miners", Per(h => h.Harvested), 1000);
        }
        Chart("Credits spent, less refunds of cancelled production", Per(h => h.Spent), 1000);
        Chart("Base value - summed cost of every building", Per(h => h.BuildingValue), 1000);
        Chart("Buildings", Per(h => h.BuildingCount, SeriesStyle.Step), 5);
        Chart("Units built", Per(h => h.UnitsBuilt, SeriesStyle.Step), 5);
        Chart("Score", Per(h => h.Score, SeriesStyle.Step), 100);
        RefreshHiddenChartsBar();
    }

    private static readonly (string Title, StatisticsArray[] Arrays)[] CameoGroups =
    [
        ("Built", [StatisticsArray.BuiltUnits, StatisticsArray.BuiltInfantry, StatisticsArray.BuiltAircraft, StatisticsArray.BuiltBuildings]),
        ("Destroyed", [StatisticsArray.KilledUnits, StatisticsArray.KilledInfantry, StatisticsArray.KilledAircraft, StatisticsArray.KilledBuildings]),
        ("Lost", [StatisticsArray.LostUnits, StatisticsArray.LostInfantry, StatisticsArray.LostAircraft, StatisticsArray.LostBuildings]),
        ("Left at the end", [StatisticsArray.LeftUnits, StatisticsArray.LeftInfantry, StatisticsArray.LeftAircraft, StatisticsArray.LeftBuildings]),
        ("Captured", [StatisticsArray.CapturedBuildings]),
    ];

    // Which view of the per-type counts was last chosen, kept when another recording is opened.
    private string _cameoView = "Built";

    /// <summary>
    /// The per-type counts, one view at a time - built, destroyed and the rest - picked with a row of
    /// buttons above one grid that stacks every player under the same cameos.
    /// </summary>
    private void AddCameoMatrix(ReplayDocument doc, ReplayStatistics stats)
    {
        Faction FactionOf(HouseSummary house) => Factions.OfHouse(doc, house);
        CameoRow Row(int house, int[] counts) =>
            new(StatisticsAnalysis.HouseName(doc, house), Theme.ForHouse(house), counts);

        var views = CameoGroups
            .Select(group => (group.Title, Blocks: Factions.Blocks(stats.Houses, stats.Types, FactionOf, group.Arrays)
                .Select(b => new CameoBlock(Factions.Title(b.Faction), b.Columns,
                    b.Rows.Select(r => Row(r.HouseIndex, r.Counts)).ToList()))
                .ToList()))
            .Append(("Crates", CrateBlocks(stats, FactionOf, Row)))
            .ToList();

        var matrix = FillWidth(new CameoMatrix { Width = 980, Margin = new Padding(0, 0, 0, 10) });
        var selector = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            MaximumSize = new Size(960, 0),
            BackColor = Theme.Background,
            Margin = new Padding(0, 0, 0, 4),
        };

        RadioButton? initial = null;
        foreach (var (title, blocks) in views)
        {
            int total = blocks.Sum(b => b.Rows.Sum(r => r.Counts.Sum()));
            var button = new RadioButton
            {
                Text = $"{title}  ({total:N0})",
                Appearance = Appearance.Button,
                AutoSize = true,
                Margin = new Padding(0, 0, 4, 0),
            };
            button.CheckedChanged += (_, _) =>
            {
                if (!button.Checked) return;
                _cameoView = title;
                matrix.SetBlocks(blocks);
            };
            selector.Controls.Add(button);
            if (initial is null || title == _cameoView) initial = button;
        }

        _statisticsFlow.Controls.Add(selector);
        _statisticsFlow.Controls.Add(matrix);
        if (initial is not null) initial.Checked = true;
    }

    /// <summary>Crates picked up, as one block: a column per crate kind anyone collected.</summary>
    private static List<CameoBlock> CrateBlocks(ReplayStatistics stats, Func<HouseSummary, Faction> factionOf,
        Func<int, int[], CameoRow> row)
    {
        static int CrateCount(HouseSummary house, int kind)
        {
            var counts = house.Array(StatisticsArray.CollectedCrates);
            return kind < counts.Length ? counts[kind] : 0;
        }

        int length = stats.Houses.Select(h => h.Array(StatisticsArray.CollectedCrates).Length).DefaultIfEmpty(0).Max();
        var kinds = Enumerable.Range(0, length).Where(k => stats.Houses.Any(h => CrateCount(h, k) > 0)).ToList();
        if (kinds.Count == 0) return [];

        var rows = stats.Houses.OrderBy(factionOf).ThenBy(h => h.HouseIndex)
            .Select(h => (House: h, Counts: kinds.Select(k => CrateCount(h, k)).ToArray()))
            .Where(r => r.Counts.Any(n => n > 0))
            .Select(r => row(r.House.HouseIndex, r.Counts))
            .ToList();
        var columns = kinds.Select(k => new TypeColumn(AbstractType.None, k, $"crate{k}", "", CrateName(k), 0)).ToList();
        return [new CameoBlock("Crates", columns, rows)];
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
        var houses = stats.Houses.OrderBy(h => h.HouseIndex).ToList();

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
