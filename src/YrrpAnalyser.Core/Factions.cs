namespace YrrpAnalyser;

/// <summary>The three YR sides, in the order the statistics show them, and everything else.</summary>
public enum Faction { Soviet, Allied, Yuri, Other }

/// <summary>One type's column in a <see cref="CountBlock"/>.</summary>
public sealed record TypeColumn(AbstractType Kind, int Index, string Id, string Cameo, string Name, int Cost);

/// <summary>One house's counts, one for every column of its block.</summary>
public sealed record CountRow(int HouseIndex, int[] Counts);

/// <summary>A side's types as columns, and a row for every house with a count of any of them.</summary>
public sealed record CountBlock(Faction Faction, IReadOnlyList<TypeColumn> Columns, IReadOnlyList<CountRow> Rows);

public static class Factions
{
    public static string Title(Faction faction) => faction switch
    {
        Faction.Soviet => "Soviet",
        Faction.Allied => "Allied",
        Faction.Yuri => "Yuri",
        _ => "Other",
    };

    /// <summary>The side a country belongs to, by its rules ID, or by the "Yuri" the roster names it.</summary>
    public static Faction OfCountry(string country) => country.ToLowerInvariant() switch
    {
        "americans" or "alliance" or "french" or "germans" or "british" => Faction.Allied,
        "africans" or "arabs" or "confederation" or "russians" => Faction.Soviet,
        "yuricountry" or "yuri" => Faction.Yuri,
        _ => Faction.Other,
    };

    /// <summary>A house's side, by the country in its statistics record, falling back to spawn.ini's.</summary>
    public static Faction OfHouse(ReplayDocument doc, HouseSummary house)
    {
        var faction = OfCountry(house.Country);
        return faction != Faction.Other ? faction : OfCountry(doc.Roster.ForHouse(house.HouseIndex)?.SideName ?? "");
    }

    // The arrays that say a house owned a type: built, lost, or still had at the end.
    private static readonly StatisticsArray[] Owned =
    [
        StatisticsArray.BuiltAircraft, StatisticsArray.BuiltInfantry, StatisticsArray.BuiltUnits, StatisticsArray.BuiltBuildings,
        StatisticsArray.LostAircraft, StatisticsArray.LostInfantry, StatisticsArray.LostUnits, StatisticsArray.LostBuildings,
        StatisticsArray.LeftAircraft, StatisticsArray.LeftInfantry, StatisticsArray.LeftUnits, StatisticsArray.LeftBuildings,
    ];

    /// <summary>
    /// Which side each type belongs to, going by who owned it in this game: the side whose houses built,
    /// lost or kept the most of it. That needs no rules file and follows a mod's types as well as the stock
    /// ones, and a few mind-controlled or captured enemy units do not outweigh the side that built them.
    /// A type nobody on a side owned - a tech building only ever captured or destroyed - is not listed,
    /// which makes it Other.
    /// </summary>
    public static Dictionary<(AbstractType Kind, int Index), Faction> OfTypes(
        IEnumerable<HouseSummary> houses, Func<HouseSummary, Faction> factionOf)
    {
        var tally = new Dictionary<(AbstractType Kind, int Index), int[]>();
        foreach (var house in houses)
        {
            var faction = factionOf(house);
            if (faction == Faction.Other) continue;
            foreach (var array in Owned)
            {
                var kind = HouseSummary.KindOf(array);
                var counts = house.Array(array);
                for (int i = 0; i < counts.Length; i++)
                {
                    if (counts[i] <= 0) continue;
                    if (!tally.TryGetValue((kind, i), out var bySide))
                        tally[(kind, i)] = bySide = new int[Enum.GetValues<Faction>().Length];
                    bySide[(int)faction] += counts[i];
                }
            }
        }
        // Ties go to the side listed first.
        return tally.ToDictionary(kv => kv.Key, kv => (Faction)System.Array.IndexOf(kv.Value, kv.Value.Max()));
    }

    /// <summary>
    /// One array per kind - everything built, say - as a block per side: that side's types as columns,
    /// infantry, vehicles, aircraft and then buildings, cheapest first; and a row for every house with a
    /// count of any of them, Soviet houses first, then Allied, then Yuri. Every row has a count for every
    /// column of its block, zero where the house had none, so a type lines up for every house.
    /// </summary>
    public static List<CountBlock> Blocks(IReadOnlyList<HouseSummary> houses, TypeTable types,
        Func<HouseSummary, Faction> factionOf, IReadOnlyList<StatisticsArray> arrays)
    {
        var typeSides = OfTypes(houses, factionOf);
        var arrayOf = new Dictionary<AbstractType, StatisticsArray>();
        foreach (var array in arrays) arrayOf[HouseSummary.KindOf(array)] = array;

        var columns = new List<TypeColumn>();
        foreach (var (kind, array) in arrayOf)
        {
            int length = houses.Select(h => h.Array(array).Length).DefaultIfEmpty(0).Max();
            for (int i = 0; i < length; i++)
            {
                if (!houses.Any(h => Count(h, array, i) > 0)) continue;
                var info = types.Get(kind, i);
                string fallback = $"{kind}#{i}";
                columns.Add(new TypeColumn(kind, i, info?.Id ?? fallback, info?.Cameo ?? "",
                    info?.DisplayName ?? fallback, info?.Cost ?? 0));
            }
        }

        var ordered = houses.OrderBy(factionOf).ThenBy(h => h.HouseIndex).ToList();
        var blocks = new List<CountBlock>();
        foreach (var faction in Enum.GetValues<Faction>())
        {
            var own = columns
                .Where(c => typeSides.GetValueOrDefault((c.Kind, c.Index), Faction.Other) == faction)
                .OrderBy(c => KindOrder(c.Kind)).ThenBy(c => c.Cost).ThenBy(c => c.Index)
                .ToList();
            if (own.Count == 0) continue;

            var rows = new List<CountRow>();
            foreach (var house in ordered)
            {
                var counts = own.Select(c => Count(house, arrayOf[c.Kind], c.Index)).ToArray();
                if (counts.Any(n => n > 0)) rows.Add(new CountRow(house.HouseIndex, counts));
            }
            blocks.Add(new CountBlock(faction, own, rows));
        }
        return blocks;
    }

    private static int Count(HouseSummary house, StatisticsArray array, int index)
    {
        var counts = house.Array(array);
        return index < counts.Length ? counts[index] : 0;
    }

    private static int KindOrder(AbstractType kind) => kind switch
    {
        AbstractType.InfantryType => 0,
        AbstractType.UnitType => 1,
        AbstractType.AircraftType => 2,
        _ => 3,
    };
}
