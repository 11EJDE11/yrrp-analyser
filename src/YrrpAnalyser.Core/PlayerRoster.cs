namespace YrrpAnalyser;

public sealed class ReplayPlayer
{
    /// <summary>spawn.ini slot: 0 is [Settings] (the recording player), N is [OtherN].</summary>
    public int Slot { get; init; }

    /// <summary>Index into HouseClass::Array, which is what every recorded event carries.</summary>
    public int HouseIndex { get; set; } = -1;

    public string Name { get; init; } = "";
    public int Side { get; init; } = -1;
    public int Color { get; init; } = -1;
    public bool IsSpectator { get; init; }
    public bool IsHuman { get; init; } = true;
    public bool IsRecordingPlayer => Slot == 0;
    public int Difficulty { get; init; } = -1;
    public string Ip { get; init; } = "";
    public int Port { get; init; }
    public int SpawnLocation { get; set; } = -1;
    public int[] Allies { get; set; } = [];

    public string SideName => SideNames.Get(Side);

    public string DisplayName => Name.Length > 0
        ? Name
        : IsHuman ? $"Slot {Slot}" : $"AI {Slot}";

    public string DifficultyName => Difficulty switch
    {
        0 => "Hard", 1 => "Normal", 2 => "Easy", _ => "",
    };
}

/// <summary>
/// The spawn.ini lobby, resolved to the house indices the recorded event stream uses.
///
/// Assign_Houses (0x687F10) walks the player node vector repeatedly, each pass taking the
/// not-yet-assigned node with the *lowest* PlayerColor and giving it the next house, so house
/// order is player colour ascending with ties going to the earlier slot. Nodes are created in
/// spawn.ini slot order - the recording player first, then Other1..OtherN - and spectators are
/// ordinary human nodes that get houses like anyone else. AI houses are created afterwards, in
/// AI slot order, so they occupy the indices above every human.
/// </summary>
public sealed class PlayerRoster
{
    public static readonly PlayerRoster Empty = new();

    public List<ReplayPlayer> Players { get; } = [];

    /// <summary>Houses in HouseClass::Array order; index is what events carry.</summary>
    public List<ReplayPlayer> ByHouseIndex { get; } = [];

    public ReplayPlayer? RecordingPlayer => Players.FirstOrDefault(p => p.Slot == 0);

    public ReplayPlayer? ForHouse(int houseIndex) =>
        houseIndex >= 0 && houseIndex < ByHouseIndex.Count ? ByHouseIndex[houseIndex] : null;

    public string HouseLabel(int houseIndex)
    {
        if (houseIndex < 0) return "-";
        var p = ForHouse(houseIndex);
        return p is null ? $"House {houseIndex}" : p.DisplayName;
    }

    public static PlayerRoster Build(IniDocument ini, ReplayHeaderInfo header)
    {
        var roster = new PlayerRoster();

        var settings = ini.GetSection("Settings");
        if (settings is null) return roster;

        int aiPlayers = settings.GetInt("AIPlayers", 0);

        // Human slots. Slot 0 is [Settings]; the rest are [Other1]..[Other7]. A slot exists only
        // when its section does - the spawner's PlayerConfig::LoadFromINIFile treats the presence
        // of the section as what makes the slot human.
        for (int slot = 0; slot < ReplayFormat.MaxHouses; slot++)
        {
            var section = slot == 0 ? settings : ini.GetSection($"Other{slot}");
            if (section is null) continue;
            if (slot > 0 && !section.Has("Name") && !section.Has("Color")) continue;

            roster.Players.Add(new ReplayPlayer
            {
                Slot = slot,
                Name = section.GetString("Name"),
                Side = section.GetInt("Side", -1),
                Color = section.GetInt("Color", -1),
                IsSpectator = section.GetBool("IsSpectator"),
                IsHuman = true,
                Ip = section.GetString("Ip"),
                Port = section.GetInt("Port"),
            });
        }

        // AI slots. They have no [OtherN] section; the client describes them through the
        // [HouseColors] / [HouseCountries] / [HouseHandicaps] tag sections instead, keyed MultiN.
        var houseColors = ini.GetSection("HouseColors");
        var houseCountries = ini.GetSection("HouseCountries");
        var houseHandicaps = ini.GetSection("HouseHandicaps");
        int aiFound = 0;
        for (int slot = 0; slot < ReplayFormat.MaxHouses && aiFound < aiPlayers; slot++)
        {
            if (roster.Players.Any(p => p.Slot == slot)) continue;
            string tag = $"Multi{slot + 1}";
            if (houseColors?.Has(tag) != true && houseCountries?.Has(tag) != true) continue;

            roster.Players.Add(new ReplayPlayer
            {
                Slot = slot,
                Name = "",
                Side = houseCountries?.GetInt(tag, -1) ?? -1,
                Color = houseColors?.GetInt(tag, -1) ?? -1,
                Difficulty = houseHandicaps?.GetInt(tag, -1) ?? -1,
                IsHuman = false,
            });
            aiFound++;
        }

        AssignHouseIndices(roster);
        ApplyHouseConfig(roster, ini);
        return roster;
    }

    /// <summary>Reproduces the ordering Assign_Houses (0x687F10) imposes on HouseClass::Array.</summary>
    private static void AssignHouseIndices(PlayerRoster roster)
    {
        var humans = roster.Players.Where(p => p.IsHuman).ToList();

        // Node vector order is slot order; the selection loop takes the strictly-lowest colour,
        // so an exact colour tie leaves the earlier node ahead. OrderBy is stable, which is the
        // same rule.
        var ordered = humans.OrderBy(p => p.Color).ToList();
        foreach (var p in ordered)
        {
            p.HouseIndex = roster.ByHouseIndex.Count;
            roster.ByHouseIndex.Add(p);
        }

        // AI houses are constructed after every human one, walking the AI slots in order.
        foreach (var ai in roster.Players.Where(p => !p.IsHuman).OrderBy(p => p.Slot))
        {
            ai.HouseIndex = roster.ByHouseIndex.Count;
            roster.ByHouseIndex.Add(ai);
        }
    }

    /// <summary>
    /// [SpawnLocations] and the [Alliances*] sections are keyed MultiN by *house array index*,
    /// not by spawn.ini slot - the spawner reads Houses[indexOfHouseArray] with tag Multi{N+1}.
    /// </summary>
    private static void ApplyHouseConfig(PlayerRoster roster, IniDocument ini)
    {
        var spawnLocations = ini.GetSection("SpawnLocations");

        for (int houseIndex = 0; houseIndex < roster.ByHouseIndex.Count; houseIndex++)
        {
            var player = roster.ByHouseIndex[houseIndex];
            string tag = $"Multi{houseIndex + 1}";

            if (spawnLocations is not null)
                player.SpawnLocation = spawnLocations.GetInt(tag, -1);

            var alliances = ini.GetSection($"Multi{houseIndex + 1}_Alliances");
            if (alliances is not null)
            {
                var allies = new List<int>();
                for (int i = 0; i < ReplayFormat.MaxHouses; i++)
                {
                    int v = alliances.GetInt($"HouseAlly{ToOrdinalWord(i)}", -1);
                    if (v >= 0) allies.Add(v);
                }
                player.Allies = [.. allies];
            }
        }
    }

    private static string ToOrdinalWord(int i) => i switch
    {
        0 => "One", 1 => "Two", 2 => "Three", 3 => "Four",
        4 => "Five", 5 => "Six", 6 => "Seven", 7 => "Eight",
        _ => i.ToString(),
    };
}

public static class SideNames
{
    // spawn.ini Side is a country index. 0..8 are the stock YR countries in rules order; 9 and 10
    // are what the CnCNet client writes for the two "random" choices.
    private static readonly string[] Names =
    [
        "Americans", "Alliance", "French", "Germans", "British",
        "Africans", "Arabs", "Confederation", "Russians",
    ];

    public static string Get(int side)
    {
        if (side < 0) return "";
        if (side < Names.Length) return Names[side];
        return side switch
        {
            9 => "Yuri",
            10 => "Random",
            _ => $"Country {side}",
        };
    }
}
