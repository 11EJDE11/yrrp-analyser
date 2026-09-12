namespace YrrpAnalyser;

/// <summary>One house's economy and army over the game, built from its HouseStats samples.</summary>
public sealed class HouseTimeline
{
    public int HouseIndex { get; init; }
    public string Name { get; set; } = "";

    public List<Sample> CreditsOnHand { get; } = [];
    /// <summary>Money gained from any source since the first sample; see <see cref="StatisticsAnalysis"/>.</summary>
    public List<Sample> Income { get; } = [];
    /// <summary>Income per minute of game time, over a trailing window.</summary>
    public List<Sample> IncomeRate { get; } = [];
    /// <summary>Ore and gems unloaded at refineries, as counted by the recorder at Refund_Money.</summary>
    public List<Sample> Harvested { get; } = [];
    public List<Sample> HarvestRate { get; } = [];
    public List<Sample> Spent { get; } = [];
    public List<Sample> ArmyValue { get; } = [];
    public List<Sample> BuildingValue { get; } = [];
    public List<Sample> ArmySize { get; } = [];
    public List<Sample> BuildingCount { get; } = [];
    public List<Sample> PowerOutput { get; } = [];
    public List<Sample> PowerDrain { get; } = [];
    public List<Sample> Kills { get; } = [];
    public List<Sample> Losses { get; } = [];
    public List<Sample> UnitsBuilt { get; } = [];
    public List<Sample> Score { get; } = [];

    public HouseStatsSample First { get; set; }
    public HouseStatsSample Last { get; set; }

    /// <summary>The first sampled frame the house was flagged defeated on.</summary>
    public int? DefeatedAtFrame { get; set; }

    public bool IsObserver => (Last.Flags & HouseStatsFlags.Observer) != 0;
    public double PeakArmyValue => ArmyValue.Count > 0 ? ArmyValue.Max(s => s.Value) : 0;
    public double PeakArmySize => ArmySize.Count > 0 ? ArmySize.Max(s => s.Value) : 0;
    public double TotalIncome => Income.Count > 0 ? Income[^1].Value : 0;
    public double PeakIncomeRate => IncomeRate.Count > 0 ? IncomeRate.Max(s => s.Value) : 0;
    public double PeakHarvestRate => HarvestRate.Count > 0 ? HarvestRate.Max(s => s.Value) : 0;
}

/// <summary>One superweapon launch, from the SpecialPlace events.</summary>
public readonly record struct SuperweaponUse(int Frame, int HouseIndex, int TypeIndex, string Name, string Where);

/// <summary>
/// The Age of Empires-style end-of-game timeline: one series per house for money, income, army
/// and the rest, from the samples the recorder writes every HouseStatsIntervalFrames.
///
/// Income is what the house gained from any source - harvesting, oil derricks, selling, refunds of
/// cancelled production - worked out as the change in money on hand plus the change in credits
/// spent. That is exact because HouseClass::Spend_Money (0x4F9790) adds everything it takes to
/// CreditsSpent and every other way money arrives goes through Refund_Money (0x4F9950), which only
/// adds to the balance. A spy stealing from a refinery is the one thing that takes money away
/// without spending it, and shows as income going down.
/// </summary>
public sealed class StatisticsAnalysis
{
    public const double IncomeRateWindowSeconds = 30;

    public List<HouseTimeline> Houses { get; } = [];

    public bool HasTimeline => Houses.Count > 0;

    /// <summary>
    /// Whether any house harvested anything. The recorder counts it; HouseClass::HarvestedCredits is
    /// never written after the constructor in YR, which is why the packet's HRV is always 0.
    /// </summary>
    public bool HasHarvestData { get; private set; }

    /// <summary>Whether the recording carries the recorder's income-by-source counts.</summary>
    public bool HasIncomeSources { get; private set; }

    public static StatisticsAnalysis Build(ReplayDocument doc)
    {
        var analysis = new StatisticsAnalysis();
        var byHouse = new Dictionary<int, HouseTimeline>();

        foreach (var frame in doc.Frames)
        {
            if (frame.HouseStats is null) continue;
            int f = frame.FrameNumber;

            foreach (var s in frame.HouseStats)
            {
                if (!byHouse.TryGetValue(s.HouseIndex, out var t))
                {
                    t = new HouseTimeline { HouseIndex = s.HouseIndex, Name = HouseName(doc, s.HouseIndex), First = s };
                    byHouse[s.HouseIndex] = t;
                }

                t.Last = s;
                t.CreditsOnHand.Add(new Sample(f, s.CreditsOnHand));
                t.Income.Add(new Sample(f, s.CreditsOnHand - t.First.CreditsOnHand + s.CreditsSpent - t.First.CreditsSpent));
                t.Harvested.Add(new Sample(f, s.IncomeHarvested));
                t.Spent.Add(new Sample(f, s.CreditsSpent));
                t.ArmyValue.Add(new Sample(f, s.ArmyValue));
                t.BuildingValue.Add(new Sample(f, s.BuildingValue));
                t.ArmySize.Add(new Sample(f, s.ArmyCount));
                t.BuildingCount.Add(new Sample(f, s.Buildings));
                t.PowerOutput.Add(new Sample(f, s.PowerOutput));
                t.PowerDrain.Add(new Sample(f, s.PowerDrain));
                t.Kills.Add(new Sample(f, s.UnitsKilled + s.BuildingsKilled));
                t.Losses.Add(new Sample(f, s.UnitsLost + s.BuildingsLost));
                t.UnitsBuilt.Add(new Sample(f, s.UnitsBuilt));
                t.Score.Add(new Sample(f, s.Score));

                if (s.IncomeHarvested > 0) analysis.HasHarvestData = true;
                if (Enum.GetValues<IncomeSource>().Any(source => s.Income(source) != 0)) analysis.HasIncomeSources = true;
                if ((s.Flags & HouseStatsFlags.Defeated) != 0 && t.DefeatedAtFrame is null)
                    t.DefeatedAtFrame = f;
            }
        }

        foreach (var t in byHouse.Values)
        {
            ComputeRate(doc, t.Income, t.IncomeRate);
            ComputeRate(doc, t.Harvested, t.HarvestRate);
        }

        // Spectators are houses too, and own nothing; charting them only adds flat lines.
        analysis.Houses.AddRange(byHouse.Values.Where(t => !t.IsObserver).OrderBy(t => t.HouseIndex));
        return analysis;
    }

    /// <summary>A cumulative series turned into a per-minute rate over a trailing window of game time.</summary>
    private static void ComputeRate(ReplayDocument doc, List<Sample> cumulative, List<Sample> rate)
    {
        int j = 0;
        for (int i = 0; i < cumulative.Count; i++)
        {
            double now = doc.GameSpeed.SecondsAt(cumulative[i].Frame);
            // j: the latest sample at least a window before this one.
            while (j < i && now - doc.GameSpeed.SecondsAt(cumulative[j + 1].Frame) >= IncomeRateWindowSeconds)
                j++;

            double elapsed = now - doc.GameSpeed.SecondsAt(cumulative[j].Frame);
            double gained = cumulative[i].Value - cumulative[j].Value;
            rate.Add(new Sample(cumulative[i].Frame, elapsed > 0 ? Math.Max(0, gained) / elapsed * 60 : 0));
        }
    }

    /// <summary>
    /// Every superweapon launch in the event stream. The event is recorded on the frame it executed,
    /// so this is when the weapon went off, not when it was clicked.
    /// </summary>
    public static List<SuperweaponUse> Superweapons(ReplayDocument doc, TypeNameResolver types)
    {
        var uses = new List<SuperweaponUse>();
        foreach (var e in doc.EnumerateEvents())
        {
            if (e.Type != EventType.SpecialPlace) continue;
            int id = e.I32(0);
            uses.Add(new SuperweaponUse(e.RecordFrame, e.HouseIndex, id,
                types.Describe(AbstractType.SuperWeaponType, id), e.Cell(4).ToString()));
        }
        return uses;
    }

    /// <summary>
    /// A house's name: the lobby's player for a multiplayer game, the house's own name otherwise.
    /// A campaign's house order is the map's, so the spawn.ini roster says nothing about it.
    /// </summary>
    public static string HouseName(ReplayDocument doc, int houseIndex)
    {
        bool campaign = doc.Header.GameMode == (uint)ReplayGameMode.Campaign;
        if (!campaign && doc.Roster.ForHouse(houseIndex) is { } player)
            return player.DisplayName;
        if (doc.Statistics?.ForHouse(houseIndex) is { Name.Length: > 0 } summary)
            return summary.Name;
        return $"House {houseIndex}";
    }
}
