namespace YrrpAnalyser;

public enum TeamResult { Undecided, Won, Lost }

/// <summary>One side of the game: an alliance, or a player alone in a free-for-all.</summary>
public sealed class Team
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public List<int> Members { get; } = [];
    public bool IsSolo => Members.Count == 1;

    public TeamResult Result { get; set; }
    /// <summary>The frame the last member was defeated on, when every one of them was.</summary>
    public int? EliminatedAtFrame { get; set; }

    // Every member's timeline added up, sample by sample.
    public List<Sample> ArmyValue { get; } = [];
    public List<Sample> ArmySize { get; } = [];
    public List<Sample> BuildingValue { get; } = [];
    public List<Sample> CreditsOnHand { get; } = [];
    public List<Sample> Income { get; } = [];
    public List<Sample> IncomeRate { get; } = [];
    public List<Sample> Spent { get; } = [];
    public List<Sample> Kills { get; } = [];
    public List<Sample> Losses { get; } = [];

    public double TotalIncome { get; set; }
    public double NetSpent { get; set; }
    public double PeakArmyValue { get; set; }
    public double MoneyLeft { get; set; }
    public int UnitsKilled { get; set; }
    public int BuildingsKilled { get; set; }
    public int UnitsLost { get; set; }
    public int BuildingsLost { get; set; }
    public int UnitsBuilt { get; set; }
    public int BuildingsBuilt { get; set; }

    public string ResultText(ReplayDocument doc) => Result switch
    {
        TeamResult.Won => "Won",
        TeamResult.Lost => EliminatedAtFrame is { } f ? $"Eliminated at {doc.TimeLabel(f)}" : "Lost",
        _ => "Undecided",
    };
}

/// <summary>
/// Who played with whom. The lobby's teams are the spawn.ini [MultiN_Alliances] sections, keyed by
/// house index like everything else the spawner reads for a house; a recording without them falls back to
/// the alliances each house held when the game ended (the statistics record's bitmask), and a recording
/// with neither is a free-for-all. Spectators are nobody's team.
/// </summary>
public sealed class TeamAnalysis
{
    public List<Team> Teams { get; } = [];
    private readonly Dictionary<int, Team> _byHouse = [];

    /// <summary>Where the teams came from, in words, for the page to say.</summary>
    public string Basis { get; private set; } = "";

    /// <summary>Alliances made or broken during the game, which the fixed teams above do not follow.</summary>
    public int AllianceChanges { get; private set; }

    public bool IsFreeForAll => Teams.Count > 1 && Teams.All(t => t.IsSolo);

    public Team? TeamOf(int house) => _byHouse.GetValueOrDefault(house);

    public static TeamAnalysis Build(ReplayDocument doc, StatisticsAnalysis stats)
    {
        var analysis = new TeamAnalysis();
        var players = Players(doc, stats);
        if (players.Count == 0) return analysis;

        // Union-find over the players, joined by alliance.
        var parent = players.ToDictionary(p => p, p => p);
        int Find(int h) { while (parent[h] != h) h = parent[h] = parent[parent[h]]; return h; }
        void Join(int a, int b)
        {
            if (!parent.ContainsKey(a) || !parent.ContainsKey(b)) return;
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        bool lobby = players.Any(p => doc.Roster.ForHouse(p)?.Allies.Length > 0);
        if (lobby)
        {
            foreach (int house in players)
                foreach (int ally in doc.Roster.ForHouse(house)?.Allies ?? [])
                    Join(house, ally);
            analysis.Basis = "the lobby's teams (spawn.ini alliances)";
        }
        else if (doc.Statistics is { Houses.Count: > 0 } summary && summary.Houses.Any(h => h.Allies != 0))
        {
            // Only mutual alliances: a one-sided ally flag is not a team.
            foreach (var a in summary.Houses)
                foreach (var b in summary.Houses)
                    if (a.HouseIndex < b.HouseIndex && a.HouseIndex < 32 && b.HouseIndex < 32
                        && (a.Allies & (1u << b.HouseIndex)) != 0 && (b.Allies & (1u << a.HouseIndex)) != 0)
                        Join(a.HouseIndex, b.HouseIndex);
            analysis.Basis = "the alliances held at the end of the game";
        }
        else
        {
            analysis.Basis = "no alliances in the recording, so every player is on their own";
        }

        analysis.AllianceChanges = doc.EnumerateEvents().Count(e => e.Type == EventType.Ally);

        foreach (var group in players.GroupBy(Find).OrderBy(g => g.Min()))
        {
            var team = new Team { Index = analysis.Teams.Count };
            team.Members.AddRange(group.OrderBy(h => h));
            analysis.Teams.Add(team);
            foreach (int h in team.Members) analysis._byHouse[h] = team;
        }

        foreach (var team in analysis.Teams)
            team.Name = team.IsSolo ? StatisticsAnalysis.HouseName(doc, team.Members[0]) : $"Team {team.Index + 1}";

        analysis.DecideResults(doc, stats);
        foreach (var team in analysis.Teams) Aggregate(doc, stats, team);
        return analysis;
    }

    /// <summary>Everyone who played: every house with statistics that was not only ever watching.</summary>
    private static List<int> Players(ReplayDocument doc, StatisticsAnalysis stats)
    {
        var houses = new SortedSet<int>(stats.Houses.Select(h => h.HouseIndex));
        if (doc.Statistics is { } s)
            foreach (var h in s.Houses) houses.Add(h.HouseIndex);
        if (houses.Count == 0)
            foreach (var p in doc.Roster.ByHouseIndex.Where(p => !p.IsSpectator)) houses.Add(p.HouseIndex);

        // HouseClass::Array holds at most a few dozen houses; anything else is a misread record.
        return houses.Where(h => h is >= 0 and < 64
                                 && !stats.IsSpectator(h, FinalFlags(doc, stats, h))
                                 && doc.Roster.ForHouse(h)?.IsSpectator != true).ToList();
    }

    public static HouseStatsFlags FinalFlags(ReplayDocument doc, StatisticsAnalysis stats, int house) =>
        doc.Statistics?.ForHouse(house)?.Flags
        ?? stats.Houses.FirstOrDefault(h => h.HouseIndex == house)?.Last.Flags
        ?? HouseStatsFlags.None;

    public static int? DefeatedAt(StatisticsAnalysis stats, int house) =>
        stats.Houses.FirstOrDefault(h => h.HouseIndex == house)?.DefeatedAtFrame;

    private static bool Defeated(ReplayDocument doc, StatisticsAnalysis stats, int house) =>
        (FinalFlags(doc, stats, house) & (HouseStatsFlags.Defeated | HouseStatsFlags.Loser)) != 0
        || DefeatedAt(stats, house).HasValue;

    /// <summary>
    /// A team won if the game says one of its players did; failing that, if it is the only one left
    /// standing. A team whose every player was defeated lost. Anything else - a recording that stopped
    /// before the end - is undecided.
    /// </summary>
    private void DecideResults(ReplayDocument doc, StatisticsAnalysis stats)
    {
        foreach (var team in Teams)
        {
            if (team.Members.All(h => Defeated(doc, stats, h)))
            {
                var frames = team.Members.Select(h => DefeatedAt(stats, h)).ToList();
                team.EliminatedAtFrame = frames.All(f => f.HasValue) ? frames.Max() : null;
                team.Result = TeamResult.Lost;
            }
        }

        var flaggedWinner = Teams.FirstOrDefault(t =>
            t.Members.Any(h => (FinalFlags(doc, stats, h) & HouseStatsFlags.Winner) != 0));
        var standing = Teams.Where(t => t.Result != TeamResult.Lost).ToList();
        var winner = flaggedWinner ?? (standing.Count == 1 && Teams.Count > 1 ? standing[0] : null);
        if (winner is null) return;

        foreach (var team in Teams)
            team.Result = ReferenceEquals(team, winner) ? TeamResult.Won : TeamResult.Lost;
    }

    private static void Aggregate(ReplayDocument doc, StatisticsAnalysis stats, Team team)
    {
        var timelines = stats.Houses.Where(h => team.Members.Contains(h.HouseIndex)).ToList();

        static void Sum(List<Sample> into, IEnumerable<List<Sample>> series)
        {
            var totals = new SortedDictionary<int, double>();
            foreach (var s in series)
                foreach (var p in s)
                    totals[p.Frame] = totals.GetValueOrDefault(p.Frame) + p.Value;
            into.AddRange(totals.Select(kv => new Sample(kv.Key, kv.Value)));
        }

        Sum(team.ArmyValue, timelines.Select(t => t.ArmyValue));
        Sum(team.ArmySize, timelines.Select(t => t.ArmySize));
        Sum(team.BuildingValue, timelines.Select(t => t.BuildingValue));
        Sum(team.CreditsOnHand, timelines.Select(t => t.CreditsOnHand));
        Sum(team.Income, timelines.Select(t => t.Income));
        Sum(team.IncomeRate, timelines.Select(t => t.IncomeRate));
        Sum(team.Spent, timelines.Select(t => t.Spent));
        Sum(team.Kills, timelines.Select(t => t.Kills));
        Sum(team.Losses, timelines.Select(t => t.Losses));

        team.TotalIncome = timelines.Sum(t => t.TotalIncome);
        team.NetSpent = timelines.Sum(t => t.NetSpent);
        team.PeakArmyValue = team.ArmyValue.Count > 0 ? team.ArmyValue.Max(s => s.Value) : 0;

        foreach (int house in team.Members)
        {
            var summary = doc.Statistics?.ForHouse(house);
            var last = timelines.FirstOrDefault(t => t.HouseIndex == house)?.Last;
            team.MoneyLeft += summary is not null ? summary.Credits + summary.StoredOreValue : last?.CreditsOnHand ?? 0;
            team.UnitsKilled += summary?.UnitsKilled ?? last?.UnitsKilled ?? 0;
            team.BuildingsKilled += summary?.BuildingsKilled ?? last?.BuildingsKilled ?? 0;
            team.UnitsLost += summary?.UnitsLost ?? last?.UnitsLost ?? 0;
            team.BuildingsLost += summary?.BuildingsLost ?? last?.BuildingsLost ?? 0;
            team.UnitsBuilt += summary is not null
                ? summary.Total(StatisticsArray.BuiltUnits) + summary.Total(StatisticsArray.BuiltInfantry) + summary.Total(StatisticsArray.BuiltAircraft)
                : last?.UnitsBuilt ?? 0;
            team.BuildingsBuilt += summary?.Total(StatisticsArray.BuiltBuildings) ?? last?.BuildingsBuilt ?? 0;
        }
    }

    /// <summary>Units and buildings of one team destroyed by another, from the end-of-game records.</summary>
    public (int Units, int Buildings) Destroyed(ReplayDocument doc, Team killers, Team victims)
    {
        int units = 0, buildings = 0;
        foreach (int k in killers.Members)
        {
            if (doc.Statistics?.ForHouse(k) is not { } summary) continue;
            foreach (int v in victims.Members)
            {
                if (v < summary.UnitsKilledOfHouse.Length) units += summary.UnitsKilledOfHouse[v];
                if (v < summary.BuildingsKilledOfHouse.Length) buildings += summary.BuildingsKilledOfHouse[v];
            }
        }
        return (units, buildings);
    }
}
