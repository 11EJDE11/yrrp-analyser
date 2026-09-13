using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private readonly FlowLayoutPanel _teamsFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16, 12, 16, 24),
        BackColor = Theme.Background,
    };

    private readonly ChartGroup _teamCharts = new();

    private void PopulateTeams(ReplayDocument doc)
    {
        _teamsFlow.SuspendLayout();
        _teamsFlow.Controls.Clear();
        _teamCharts.Clear();

        if (_teams is not { Teams.Count: > 0 } teams || _statistics is null)
        {
            _teamsFlow.Controls.Add(SectionHeading("Teams"));
            _teamsFlow.Controls.Add(Note("This recording carries no statistics, so there is nobody to put on a team.", Theme.Warning));
            _teamsFlow.ResumeLayout();
            return;
        }

        _teamsFlow.Controls.Add(SectionHeading(teams.IsFreeForAll ? "Free for all" : $"{teams.Teams.Count} teams"));
        string basis = $"Teams are taken from {teams.Basis}. Spectators are left out." +
                       (teams.AllianceChanges > 0
                           ? $" {teams.AllianceChanges} alliance change(s) happened during the game, which these fixed teams do not follow."
                           : "");
        _teamsFlow.Controls.Add(Note(basis, Theme.Muted));

        var cards = FillWidth(new TeamCardsView { Width = 980, Margin = new Padding(0, 4, 0, 12) });
        cards.SetData(doc, teams, _statistics);
        _teamsFlow.Controls.Add(cards);

        _teamsFlow.Controls.Add(SectionHeading("Side by side"));
        _teamsFlow.Controls.Add(BuildTeamTable(doc, teams));

        if (teams.Teams.Any(t => !t.IsSolo))
        {
            _teamsFlow.Controls.Add(SectionHeading("Who carried what"));
            _teamsFlow.Controls.Add(Note("Each bar is one team's total, split between its players.", Theme.Muted));
            var contribution = FillWidth(new ContributionView { Width = 980, Margin = new Padding(0, 0, 0, 12) });
            contribution.SetData(doc, teams, _statistics);
            _teamsFlow.Controls.Add(contribution);
        }

        if (doc.Statistics is { Houses.Count: > 0 })
        {
            _teamsFlow.Controls.Add(SectionHeading("Destroyed, team against team"));
            _teamsFlow.Controls.Add(Note("Units / buildings of the column's team destroyed by the row's team. " +
                                         "The diagonal is a team destroying its own - friendly fire, mostly.", Theme.Muted));
            _teamsFlow.Controls.Add(BuildTeamKillMatrix(doc, teams));
        }

        AddTeamCharts(doc, teams);

        FitWidths(_teamsFlow);
        _teamsFlow.ResumeLayout();
    }

    private Control BuildTeamTable(ReplayDocument doc, TeamAnalysis teams)
    {
        var view = MakeListView(("Team", 130), ("Players", 230), ("Result", 140), ("Income", 80), ("Spent (net)", 85),
            ("Money left", 80), ("Peak army", 80), ("Units built", 75), ("Units killed", 80), ("Units lost", 70),
            ("Bldgs killed", 80), ("Bldgs lost", 70), ("K/L", 50));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        foreach (var team in teams.Teams)
        {
            int kills = team.UnitsKilled + team.BuildingsKilled, losses = team.UnitsLost + team.BuildingsLost;
            var item = new ListViewItem([
                team.Name,
                string.Join(", ", team.Members.Select(h => StatisticsAnalysis.HouseName(doc, h))),
                team.ResultText(doc),
                team.TotalIncome.ToString("N0"),
                team.NetSpent.ToString("N0"),
                team.MoneyLeft.ToString("N0"),
                team.PeakArmyValue.ToString("N0"),
                team.UnitsBuilt.ToString("N0"),
                team.UnitsKilled.ToString("N0"),
                team.UnitsLost.ToString("N0"),
                team.BuildingsKilled.ToString("N0"),
                team.BuildingsLost.ToString("N0"),
                losses > 0 ? (kills / (double)losses).ToString("0.00") : "—",
            ])
            { UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.ForTeam(team);
            item.SubItems[0].Font = Theme.UiBold;
            item.SubItems[2].ForeColor = team.Result switch { TeamResult.Won => Theme.Good, TeamResult.Lost => Theme.Danger, _ => Theme.Muted };
            view.Items.Add(item);
        }
        view.Height = 30 + Math.Max(1, teams.Teams.Count) * 22;
        return view;
    }

    private static Control BuildTeamKillMatrix(ReplayDocument doc, TeamAnalysis teams)
    {
        var columns = new List<(string, int)> { ("Destroyed by", 150) };
        columns.AddRange(teams.Teams.Select(t => (t.Name, 110)));
        var view = MakeListView([.. columns]);
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Height = 30 + Math.Max(1, teams.Teams.Count) * 22;
        view.Margin = new Padding(0, 0, 0, 10);
        view.Font = Theme.Mono;
        FillWidth(view);

        foreach (var killers in teams.Teams)
        {
            var cells = new List<string> { killers.Name };
            cells.AddRange(teams.Teams.Select(victims =>
            {
                var (units, buildings) = teams.Destroyed(doc, killers, victims);
                return units == 0 && buildings == 0 ? "—" : $"{units} / {buildings}";
            }));
            var item = new ListViewItem([.. cells]) { UseItemStyleForSubItems = false };
            item.SubItems[0].ForeColor = Theme.ForTeam(killers);
            for (int i = 0; i < teams.Teams.Count; i++)
                if (ReferenceEquals(teams.Teams[i], killers)) item.SubItems[i + 1].ForeColor = Theme.Muted;
            view.Items.Add(item);
        }
        return view;
    }

    private void AddTeamCharts(ReplayDocument doc, TeamAnalysis teams)
    {
        if (!_statistics!.HasTimeline) return;
        int fps = doc.Header.SimulationFps;

        _teamsFlow.Controls.Add(SectionHeading("Over the game, team against team"));
        _teamsFlow.Controls.Add(Note("Each team's players added together. Dotted lines mark a team being eliminated.", Theme.Muted));
        _teamsFlow.Controls.Add(ChartHint());

        var markers = teams.Teams.Where(t => t.EliminatedAtFrame.HasValue)
            .Select(t => new ChartMarker(t.EliminatedAtFrame!.Value, Theme.ForTeam(t), $"{t.Name} eliminated")).ToList();

        IEnumerable<ChartSeries> Per(Func<Team, IReadOnlyList<Sample>> pick, SeriesStyle style = SeriesStyle.Line) =>
            teams.Teams.Select(t => new ChartSeries
            {
                Name = t.Name,
                Color = Theme.ForTeam(t),
                Key = $"team:{t.Index}",
                Style = style,
                Points = pick(t),
            });

        void Chart(string title, IEnumerable<ChartSeries> series, double minimumY, string suffix = "")
        {
            var chart = new TimeSeriesChart
            {
                Title = title,
                SimulationFps = fps,
                TimeLabeller = doc.TimeLabel,
                MinimumYRange = minimumY,
                ValueSuffix = suffix,
                Width = 980,
                Height = 220,
                Margin = new Padding(0, 4, 0, 10),
            };
            chart.SetData(series, markers);
            _teamCharts.Add(chart);
            _teamsFlow.Controls.Add(FillWidth(chart));
        }

        if (_win is { HasData: true } win)
            Chart("Likeliness to win (estimate)", Per(t => win.Samples.Select(s => new Sample(s.Frame, s.Shares[t.Index] * 100)).ToList()), 100, "%");
        Chart("Army value", Per(t => t.ArmyValue), 1000);
        Chart($"Income per minute, over the last {StatisticsAnalysis.IncomeRateWindowSeconds:0} seconds", Per(t => t.IncomeRate), 500);
        Chart("Money on hand", Per(t => t.CreditsOnHand), 1000);
        Chart("Base value", Per(t => t.BuildingValue), 1000);
        Chart("Army size - vehicles, infantry and aircraft", Per(t => t.ArmySize, SeriesStyle.Step), 5);
        Chart("Kills - units and buildings destroyed", Per(t => t.Kills, SeriesStyle.Step), 5);
        Chart("Losses - units and buildings lost", Per(t => t.Losses, SeriesStyle.Step), 5);
    }
}
