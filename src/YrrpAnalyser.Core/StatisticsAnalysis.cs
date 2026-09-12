namespace YrrpAnalyser;

/// <summary>One house's economy and army over the game, built from its HouseStats samples.</summary>
public sealed class HouseTimeline
{
    public int HouseIndex { get; init; }
    public string Name { get; set; } = "";

    public List<Sample> CreditsOnHand { get; } = [];
    /// <summary>Money gained since the first sample, refunds and starting credits excluded; see <see cref="StatisticsAnalysis"/>.</summary>
    public List<Sample> Income { get; } = [];
    /// <summary>Income per minute of game time, over a trailing window.</summary>
    public List<Sample> IncomeRate { get; } = [];
    /// <summary>Money from harvesting, from the recorded payments.</summary>
    public List<Sample> Harvested { get; } = [];
    public List<Sample> HarvestRate { get; } = [];
    /// <summary>Credits spent less what cancelled production refunded.</summary>
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

    /// <summary>Every recorded payment's total, by what it was for.</summary>
    public Dictionary<IncomeSource, long> IncomeBySource { get; } = [];

    /// <summary>
    /// Income that did not pass through Refund_Money, so has no caller: ore put into silos or onto the
    /// bank by the game's own Harvested_Ore, which Ares normally replaces. Close to zero otherwise.
    /// </summary>
    public double DirectIncome { get; set; }

    /// <summary>The first sampled frame the house was flagged defeated on.</summary>
    public int? DefeatedAtFrame { get; set; }

    public long IncomeFrom(IncomeSource source) => IncomeBySource.GetValueOrDefault(source);

    /// <summary>
    /// Whether the house was only ever watching. Only the first sample says: a player who is defeated and
    /// stays on to watch is flagged an observer too, from then on.
    /// </summary>
    public bool IsSpectator => (First.Flags & HouseStatsFlags.Observer) != 0;
    public double PeakArmyValue => ArmyValue.Count > 0 ? ArmyValue.Max(s => s.Value) : 0;
    public double PeakArmySize => ArmySize.Count > 0 ? ArmySize.Max(s => s.Value) : 0;
    public double TotalIncome => Income.Count > 0 ? Income[^1].Value : 0;
    public double NetSpent => Spent.Count > 0 ? Spent[^1].Value : 0;
    public double PeakIncomeRate => IncomeRate.Count > 0 ? IncomeRate.Max(s => s.Value) : 0;
    public double PeakHarvestRate => HarvestRate.Count > 0 ? HarvestRate.Max(s => s.Value) : 0;
}

/// <summary>One superweapon launch, from the SpecialPlace events.</summary>
public readonly record struct SuperweaponUse(int Frame, int HouseIndex, int TypeIndex, string Name, string Where);

/// <summary>A caller the income table does not know, with everything it paid out.</summary>
public sealed class UnclassifiedCaller
{
    public ResolvedCaller Caller { get; init; }
    public long Amount { get; set; }
    public int Payments { get; set; }
    public SortedSet<int> Houses { get; } = [];
}

/// <summary>
/// The Age of Empires-style end-of-game timeline: one series per house for money, income, army
/// and the rest, from the samples the recorder writes every HouseStatsIntervalFrames.
///
/// Income is what the house gained, worked out as the change in money on hand plus the change in
/// credits spent, less refunds. HouseClass::Spend_Money (0x4F9790) adds everything it takes to
/// CreditsSpent, and every payment arrives through Refund_Money (0x4F9950) - or, for ore the game
/// banks itself, Harvested_Ore - which only add to the balance. A refund of cancelled production is
/// money that was spent and came back, so it is taken out of both income and spent rather than
/// counted as earnings. Starting credits paid to AI houses are left out the same way. Where the
/// income came from is read off the recorded payments' callers.
/// </summary>
public sealed class StatisticsAnalysis
{
    public const double IncomeRateWindowSeconds = 30;

    public List<HouseTimeline> Houses { get; } = [];
    public List<UnclassifiedCaller> Unclassified { get; } = [];

    /// <summary>Houses flagged observer from their first sample on: spectators, not players.</summary>
    public HashSet<int> Spectators { get; } = [];

    public bool HasTimeline => Houses.Count > 0;

    /// <summary>
    /// Whether a house was a spectator rather than a player. The end-of-game flags alone cannot say, since
    /// a defeated player who stays on to watch ends the game flagged an observer; so a house with samples
    /// goes by its first one, and only a house with none falls back to "observer and never defeated".
    /// </summary>
    public bool IsSpectator(int houseIndex, HouseStatsFlags finalFlags) =>
        Spectators.Contains(houseIndex)
        || (Houses.All(h => h.HouseIndex != houseIndex)
            && (finalFlags & HouseStatsFlags.Observer) != 0
            && (finalFlags & HouseStatsFlags.Defeated) == 0);

    /// <summary>Whether any house harvested anything, by the recorded payments.</summary>
    public bool HasHarvestData { get; private set; }

    /// <summary>Whether the recording carries any payments at all.</summary>
    public bool HasIncomeSources { get; private set; }

    public static StatisticsAnalysis Build(ReplayDocument doc, IncomeClassifier? classifier = null)
    {
        classifier ??= IncomeClassifier.Default;
        var modules = (IReadOnlyList<ModuleInfo>?)doc.Statistics?.Modules ?? [];

        var analysis = new StatisticsAnalysis();
        var byHouse = new Dictionary<int, HouseTimeline>();
        var running = new Dictionary<int, Dictionary<IncomeSource, long>>();
        var atFirst = new Dictionary<int, (long Refunded, long Starting, long Paid)>();
        var unclassified = new Dictionary<ResolvedCaller, UnclassifiedCaller>();

        Dictionary<IncomeSource, long> RunningFor(int house)
        {
            if (!running.TryGetValue(house, out var totals))
                running[house] = totals = [];
            return totals;
        }

        static long PaidAsIncome(Dictionary<IncomeSource, long> totals) =>
            totals.Where(kv => IsIncome(kv.Key)).Sum(kv => kv.Value);

        foreach (var frame in doc.Frames)
        {
            int f = frame.FrameNumber;

            if (frame.MoneyIn is not null)
            {
                foreach (var payment in frame.MoneyIn)
                {
                    var (source, caller, _) = classifier.Classify(payment.Caller, modules);
                    var totals = RunningFor(payment.House);
                    totals[source] = totals.GetValueOrDefault(source) + payment.Amount;
                    analysis.HasIncomeSources = true;

                    if (source == IncomeSource.Unclassified)
                    {
                        if (!unclassified.TryGetValue(caller, out var entry))
                            unclassified[caller] = entry = new UnclassifiedCaller { Caller = caller };
                        entry.Amount += payment.Amount;
                        entry.Payments++;
                        entry.Houses.Add(payment.House);
                    }
                }
            }

            if (frame.HouseStats is null) continue;

            foreach (var s in frame.HouseStats)
            {
                var totals = RunningFor(s.HouseIndex);
                long refunded = totals.GetValueOrDefault(IncomeSource.Refunded);
                long starting = totals.GetValueOrDefault(IncomeSource.StartingCredits);

                if (!byHouse.TryGetValue(s.HouseIndex, out var t))
                {
                    t = new HouseTimeline { HouseIndex = s.HouseIndex, Name = HouseName(doc, s.HouseIndex), First = s };
                    byHouse[s.HouseIndex] = t;
                    atFirst[s.HouseIndex] = (refunded, starting, PaidAsIncome(totals));
                }

                var first = atFirst[s.HouseIndex];
                double income = s.CreditsOnHand - t.First.CreditsOnHand
                                + s.CreditsSpent - t.First.CreditsSpent
                                - (refunded - first.Refunded)
                                - (starting - first.Starting);

                t.Last = s;
                t.CreditsOnHand.Add(new Sample(f, s.CreditsOnHand));
                t.Income.Add(new Sample(f, income));
                t.Harvested.Add(new Sample(f, totals.GetValueOrDefault(IncomeSource.Harvested)));
                t.Spent.Add(new Sample(f, s.CreditsSpent - refunded));
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

                // Whatever the payments do not account for arrived some other way - see DirectIncome.
                t.DirectIncome = income - (PaidAsIncome(totals) - first.Paid);

                if ((s.Flags & HouseStatsFlags.Defeated) != 0 && t.DefeatedAtFrame is null)
                    t.DefeatedAtFrame = f;
            }
        }

        foreach (var t in byHouse.Values)
        {
            foreach (var (source, amount) in RunningFor(t.HouseIndex))
                t.IncomeBySource[source] = amount;

            if (t.IncomeFrom(IncomeSource.Harvested) > 0) analysis.HasHarvestData = true;
            ComputeRate(doc, t.Income, t.IncomeRate);
            ComputeRate(doc, t.Harvested, t.HarvestRate);
        }

        // Spectators are houses too and stay in; they simply own nothing.
        foreach (var t in byHouse.Values)
            if (t.IsSpectator) analysis.Spectators.Add(t.HouseIndex);
        analysis.Houses.AddRange(byHouse.Values.OrderBy(t => t.HouseIndex));
        analysis.Unclassified.AddRange(unclassified.Values.OrderByDescending(u => u.Amount));
        return analysis;
    }

    /// <summary>
    /// Whether a payment is money earned. A refund is money spent coming back, and starting credits are
    /// handed out before anything happens, so neither is counted as income.
    /// </summary>
    public static bool IsIncome(IncomeSource source) =>
        source is not (IncomeSource.Refunded or IncomeSource.StartingCredits);

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
