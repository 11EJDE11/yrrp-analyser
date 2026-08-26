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

    /// <summary>
    /// Type ID to the English display name off its own section's Name key - SENGINEER is
    /// "Soviet Engineer". UIName is deliberately not used: it is a CSF label reference
    /// (Name:ENGINEER), which needs the string table to mean anything, and resolves to the
    /// shared "Engineer" rather than to the side-specific name.
    /// </summary>
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);

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

        // Every type's own section carries its display name. Walking the sections once is far
        // cheaper than searching for each ID, and picking up a Name from a section that is not a
        // type is harmless: nothing is ever looked up unless it appears in a type list above.
        foreach (var section in ini.Sections)
        {
            var displayName = section.GetString("Name");
            if (displayName.Length > 0)
                _displayNames[section.Name] = displayName;
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

    /// <summary>
    /// The name for a type array position: "Soviet Engineer [SENGINEER]" where rules are loaded,
    /// the bare ID where the type has no Name of its own, and the array position itself where
    /// there are no rules to resolve against.
    /// </summary>
    public string Describe(AbstractType rtti, int heapId)
    {
        var kind = Normalise(rtti);
        if (!_lists.TryGetValue(kind, out var list) || heapId < 0 || heapId >= list.Count)
            return $"{kind}#{heapId}";

        var id = list[heapId];
        if (_displayNames.TryGetValue(id, out var displayName)
            && !string.Equals(displayName, id, StringComparison.OrdinalIgnoreCase))
        {
            return $"{displayName} [{id}]";
        }

        return id;
    }

    public string CountryName(int index)
    {
        if (index >= 0 && index < _countries.Count) return _countries[index];
        return SideNames.Get(index);
    }
}
