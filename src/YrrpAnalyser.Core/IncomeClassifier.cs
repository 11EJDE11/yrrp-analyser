using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YrrpAnalyser;

/// <summary>A loaded module, from the recording's MODS chunk.</summary>
public sealed class ModuleInfo
{
    public string Name { get; init; } = "";
    public uint Base { get; init; }
    public uint Size { get; init; }
    /// <summary>The PE header's TimeDateStamp: which build of the module this was.</summary>
    public uint TimeDateStamp { get; init; }

    public bool Contains(uint address) => address >= Base && address - Base < Size;
}

/// <summary>One known caller of HouseClass::Refund_Money.</summary>
public sealed class CallerRule
{
    /// <summary><c>game</c> for the executable, or a DLL's file name such as <c>Ares.dll</c>.</summary>
    public string Module { get; init; } = IncomeClassifier.GameModule;

    /// <summary>The module build the offset belongs to (its PE TimeDateStamp); 0 matches any build.</summary>
    public uint TimeDateStamp { get; init; }

    /// <summary>The return address: absolute for the game, whose base is fixed; module-relative for a DLL.</summary>
    public uint Offset { get; init; }

    public IncomeSource Source { get; init; }
    public string Description { get; init; } = "";
}

/// <summary>A payment's caller turned into a module and an offset.</summary>
public readonly record struct ResolvedCaller(string Module, uint Offset, uint TimeDateStamp)
{
    public override string ToString() =>
        Module == IncomeClassifier.GameModule ? $"game 0x{Offset:X}" : $"{Module}+0x{Offset:X}";
}

/// <summary>
/// Decides what a Refund_Money payment was for, from the caller the recorder wrote. The recorder
/// deliberately records nothing but the raw caller, so when Ares, Phobos or the spawner move the code
/// that pays out, only this table changes - never the replay format.
///
/// The table is the built-in one below plus, ahead of it, whatever <see cref="OverridesFileName"/>
/// next to the analyser holds. A rule names a module and a return address; a DLL rule can also be
/// pinned to one build by its PE timestamp, because a DLL rebuilt with the same source will have
/// its call sites at different offsets.
/// </summary>
public sealed class IncomeClassifier
{
    public const string GameModule = "game";
    public const uint GameImageBase = 0x400000;
    // gamemd.exe's SizeOfImage: a recording with no module table still resolves game addresses.
    private const uint GameImageEnd = 0x400000 + 0x793000;
    public const string OverridesFileName = "income-callers.json";

    private readonly List<CallerRule> _rules;

    public IncomeClassifier(IEnumerable<CallerRule> rules) => _rules = [.. rules];

    public IReadOnlyList<CallerRule> Rules => _rules;

    public static string OverridesPath => Path.Combine(AppContext.BaseDirectory, OverridesFileName);

    private static IncomeClassifier? _default;

    /// <summary>The built-in table with the overrides file ahead of it, loaded once.</summary>
    public static IncomeClassifier Default => _default ??= Load(OverridesPath);

    /// <summary>Drops the cached table so the overrides file is read again.</summary>
    public static void Reload() => _default = null;

    public static IncomeClassifier Load(string? overridesPath)
    {
        var rules = new List<CallerRule>();
        if (overridesPath is not null && File.Exists(overridesPath))
        {
            try { rules.AddRange(ParseRules(File.ReadAllText(overridesPath))); }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or ArgumentException)
            {
                // A broken overrides file must not take the built-in table down with it.
            }
        }
        rules.AddRange(BuiltIn);
        return new IncomeClassifier(rules);
    }

    public ResolvedCaller Resolve(uint caller, IReadOnlyList<ModuleInfo> modules)
    {
        foreach (var module in modules)
        {
            if (!module.Contains(caller)) continue;
            return module.Base == GameImageBase
                ? new ResolvedCaller(GameModule, caller, module.TimeDateStamp)
                : new ResolvedCaller(module.Name, caller - module.Base, module.TimeDateStamp);
        }

        // No module table - the recording did not close cleanly - so only the fixed game image resolves.
        if (modules.Count == 0 && caller >= GameImageBase && caller < GameImageEnd)
            return new ResolvedCaller(GameModule, caller, 0);

        return new ResolvedCaller("?", caller, 0);
    }

    public (IncomeSource Source, ResolvedCaller Caller, CallerRule? Rule) Classify(uint caller, IReadOnlyList<ModuleInfo> modules)
    {
        var resolved = Resolve(caller, modules);
        foreach (var rule in _rules)
        {
            if (rule.Offset != resolved.Offset) continue;
            if (!string.Equals(rule.Module, resolved.Module, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.TimeDateStamp != 0 && resolved.TimeDateStamp != 0 && rule.TimeDateStamp != resolved.TimeDateStamp) continue;
            return (rule.Source, resolved, rule);
        }
        return (IncomeSource.Unclassified, resolved, null);
    }

    /// <summary>
    /// The overrides file format: a JSON array of
    /// <c>{ "module": "Ares.dll", "timestamp": "0x61DAA114", "offset": "0x44DCE", "source": "Harvested", "description": "..." }</c>.
    /// Numbers may be written as hex strings or plain integers; timestamp may be omitted.
    /// </summary>
    public static List<CallerRule> ParseRules(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var rules = new List<CallerRule>();
        foreach (var e in document.RootElement.EnumerateArray())
        {
            rules.Add(new CallerRule
            {
                Module = e.TryGetProperty("module", out var module) ? module.GetString() ?? GameModule : GameModule,
                TimeDateStamp = e.TryGetProperty("timestamp", out var stamp) ? Number(stamp) : 0,
                Offset = Number(e.GetProperty("offset")),
                Source = Enum.Parse<IncomeSource>(e.GetProperty("source").GetString() ?? "", ignoreCase: true),
                Description = e.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            });
        }
        return rules;
    }

    private static uint Number(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetUInt32();
        string text = e.GetString() ?? "0";
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>The built-in table as an overrides file, for someone to start editing from.</summary>
    public static string ToJson(IEnumerable<CallerRule> rules)
    {
        var sb = new StringBuilder("[\n");
        var list = rules.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            sb.Append($"  {{ \"module\": \"{r.Module}\", ");
            if (r.TimeDateStamp != 0) sb.Append($"\"timestamp\": \"0x{r.TimeDateStamp:X8}\", ");
            sb.Append($"\"offset\": \"0x{r.Offset:X}\", \"source\": \"{r.Source}\", ");
            sb.Append($"\"description\": {JsonSerializer.Serialize(r.Description)} }}");
            sb.Append(i + 1 < list.Count ? ",\n" : "\n");
        }
        return sb.Append("]\n").ToString();
    }

    private const uint Ares30p1 = 0x61DAA114;   // Ares.dll 3.0p1 as shipped with CnCNet YR
    private const uint PhobosBuild = 0x697A04F6; // Phobos.dll as shipped with CnCNet YR at the time of writing

    private static CallerRule Game(uint returnAddress, IncomeSource source, string description) =>
        new() { Module = GameModule, Offset = returnAddress, Source = source, Description = description };

    private static CallerRule Dll(string module, uint stamp, uint offset, IncomeSource source, string description) =>
        new() { Module = module, TimeDateStamp = stamp, Offset = offset, Source = source, Description = description };

    /// <summary>
    /// Every Refund_Money caller in gamemd.exe (a return address is the call site + 5), and every
    /// caller of the GiveMoney stub in the Ares and Phobos builds the table was made against - each one
    /// traced up to the Syringe hook it runs under.
    /// </summary>
    public static readonly IReadOnlyList<CallerRule> BuiltIn =
    [
        Game(0x4CA04B, IncomeSource.Refunded, "FactoryClass::Abandon - production cancelled"),
        Game(0x446E94, IncomeSource.Refunded, "BuildingClass::Grand_Opening - free unit could not be placed"),
        Game(0x446EE2, IncomeSource.Refunded, "BuildingClass::Grand_Opening - free unit could not be placed"),
        Game(0x44A17B, IncomeSource.Sold, "BuildingClass::Mission_Deconstruction"),
        Game(0x44A1B5, IncomeSource.Sold, "BuildingClass::Mission_Deconstruction"),
        Game(0x44A227, IncomeSource.Sold, "BuildingClass::Mission_Deconstruction"),
        Game(0x44AAF4, IncomeSource.Sold, "BuildingClass::Mission_Deconstruction"),
        Game(0x4575E9, IncomeSource.Sold, "BuildingClass_Sell_Upgrade"),
        Game(0x4D9FCB, IncomeSource.Sold, "FootClass::Sell_Back"),
        Game(0x73A0BC, IncomeSource.Grinding, "UnitClass::Per_Cell_Process - entered a grinder"),
        Game(0x73A100, IncomeSource.Grinding, "UnitClass::Per_Cell_Process - entered a grinder"),
        Game(0x73A12A, IncomeSource.Grinding, "UnitClass::Per_Cell_Process - entered a grinder"),
        Game(0x73A15C, IncomeSource.Grinding, "UnitClass::Per_Cell_Process - entered a grinder"),
        Game(0x519880, IncomeSource.Grinding, "InfantryClass::Per_Cell_Process - entered a grinder"),
        Game(0x4587B6, IncomeSource.Buildings, "BuildingClass::Produce_Cash - oil derrick"),
        Game(0x43FDC6, IncomeSource.Buildings, "BuildingClass::AI - cash-producing building"),
        Game(0x4482D5, IncomeSource.Buildings, "BuildingClass::Captured - capture bonus"),
        Game(0x4824D9, IncomeSource.Crates, "CellClass::Goodie_Check - money crate"),
        Game(0x457460, IncomeSource.Stolen, "BuildingClass_Infiltrate - spy in a refinery"),
        Game(0x6FA1C5, IncomeSource.Stolen, "TechnoClass::AI - money drain"),
        Game(0x449337, IncomeSource.Other, "BuildingClass::Captured - upgrade refund"),
        Game(0x4C6278, IncomeSource.Other, "EvadeClass::Do"),
        Game(0x5D6FA1, IncomeSource.Other, "MultiplayerGameMode Generate_Units - unplaceable starting unit"),
        Game(0x686A78, IncomeSource.StartingCredits, "Read_Scenario_INI - AI bonus: starting credits x MultiplayerAICM[difficulty]%"),

        Dll("Ares.dll", Ares30p1, 0x44DCE, IncomeSource.Harvested, "UnitClass_Mi_Unload_Storage - harvester unloading"),
        Dll("Ares.dll", Ares30p1, 0x46E3E, IncomeSource.Harvested, "InfantryClass_Slave_UnloadAt_Storage - slave unloading"),
        Dll("Ares.dll", Ares30p1, 0x14D75, IncomeSource.Buildings, "BuildingClass_Update_ProduceCash - oil derrick"),
        Dll("Ares.dll", Ares30p1, 0x1438F, IncomeSource.Buildings, "BuildingClass_ChangeOwnership_ProduceCash - capture bonus"),
        Dll("Ares.dll", Ares30p1, 0x12A8D, IncomeSource.Stolen, "BuildingClass_Infiltrate - spy"),
        Dll("Ares.dll", Ares30p1, 0x43E1F, IncomeSource.Bounty, "TechnoClass_RegisterDestruction_Bounty"),
        Dll("Ares.dll", Ares30p1, 0x33A11, IncomeSource.Superweapon, "SuperClass_Launch - Money.Amount"),
        Dll("Ares.dll", Ares30p1, 0x36404, IncomeSource.Superweapon, "SuperClass_Update_DrainMoney"),
        Dll("Ares.dll", Ares30p1, 0x4D185, IncomeSource.Grinding, "UnitClass_UpdatePosition_EnteredGrinder"),

        Dll("Phobos.dll", PhobosBuild, 0x63A5E, IncomeSource.Sold, "FootClass_Sell"),
        Dll("Phobos.dll", PhobosBuild, 0x7D71D, IncomeSource.Warhead, "BulletClass_Detonate / MapClass_DamageArea - TransactMoney"),
        Dll("Phobos.dll", PhobosBuild, 0x4E929, IncomeSource.Other, "TechnoClass_AI - PassengerDeletion.Soylent refund"),
    ];
}
