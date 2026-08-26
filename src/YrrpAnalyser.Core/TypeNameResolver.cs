namespace YrrpAnalyser;

/// <summary>
/// Turns the (RTTI, HeapID) pair carried by Place / Produce / Suspend / Abandon into a type name.
///
/// The heap ID is a position in the game's type array, and that array is built by reading the type
/// list sections in order - rules first, then whatever the map appends - so the name is only
/// recoverable with the same rules the recording ran against. Point this at an extracted
/// rulesmd.ini (plus any Ares/Phobos or game-mode INI that adds types, in load order) and the
/// build order becomes readable; without one the events still show, as BuildingType#9.
///
/// This is best-effort by construction: a mod that inserts a type ahead of others shifts every ID
/// after it, and nothing in the replay records which rules were in force beyond the client's
/// [ReplayFileHashes] block.
/// </summary>
public sealed class TypeNameResolver
{
    private readonly Dictionary<AbstractType, List<string>> _lists = [];
    private readonly List<string> _countries = [];

    public bool HasData => _lists.Count > 0;
    public string SourceDescription { get; private set; } = "none loaded";

    public static TypeNameResolver Empty { get; } = new();

    /// <summary>Section that lists each type array, in the order the engine creates them.</summary>
    private static readonly (AbstractType Kind, string Section)[] TypeSections =
    [
        (AbstractType.BuildingType, "BuildingTypes"),
        (AbstractType.InfantryType, "InfantryTypes"),
        (AbstractType.UnitType, "VehicleTypes"),
        (AbstractType.AircraftType, "AircraftTypes"),
        (AbstractType.SuperWeaponType, "SuperWeaponTypes"),
    ];

    public static TypeNameResolver Load(IEnumerable<string> iniPaths, IniDocument? mapIni)
    {
        var resolver = new TypeNameResolver();
        var loaded = new List<string>();

        foreach (var path in iniPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                resolver.Merge(IniDocument.Parse(File.ReadAllText(path)));
                loaded.Add(Path.GetFileName(path));
            }
            catch (IOException)
            {
                // A rules file we cannot read is a missing nicety, never a reason to fail a load.
            }
        }

        // The map is read after rules and appends its own types to the same arrays, which is
        // exactly the order this reproduces.
        if (mapIni is not null)
        {
            resolver.Merge(mapIni);
            loaded.Add("spawnmap.ini");
        }

        resolver.SourceDescription = loaded.Count > 0 ? string.Join(" + ", loaded) : "none loaded";
        return resolver;
    }

    private void Merge(IniDocument ini)
    {
        foreach (var (kind, sectionName) in TypeSections)
        {
            var section = ini.GetSection(sectionName);
            if (section is null) continue;

            if (!_lists.TryGetValue(kind, out var list))
                _lists[kind] = list = [];

            // Entries are index=Name. The engine appends anything it has not already created, so
            // a name already present keeps its original slot.
            foreach (var (_, value) in section.Entries)
            {
                if (value.Length == 0) continue;
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.Add(value);
            }
        }

        var countries = ini.GetSection("Countries");
        if (countries is not null)
        {
            foreach (var (_, value) in countries.Entries)
            {
                if (value.Length > 0 && !_countries.Contains(value, StringComparer.OrdinalIgnoreCase))
                    _countries.Add(value);
            }
        }
    }

    /// <summary>
    /// Object RTTI tags and their type counterparts both appear in events - Place carries Building
    /// where Produce carries BuildingType - so both map to the same list.
    /// </summary>
    private static AbstractType Normalise(AbstractType type) => type switch
    {
        AbstractType.Building => AbstractType.BuildingType,
        AbstractType.Infantry => AbstractType.InfantryType,
        AbstractType.Unit => AbstractType.UnitType,
        AbstractType.Aircraft => AbstractType.AircraftType,
        AbstractType.Super => AbstractType.SuperWeaponType,
        _ => type,
    };

    public string Describe(AbstractType rtti, int heapId)
    {
        var kind = Normalise(rtti);
        if (_lists.TryGetValue(kind, out var list) && heapId >= 0 && heapId < list.Count)
            return list[heapId];

        return $"{kind}#{heapId}";
    }

    public string CountryName(int index)
    {
        if (index >= 0 && index < _countries.Count) return _countries[index];
        return SideNames.Get(index);
    }
}
