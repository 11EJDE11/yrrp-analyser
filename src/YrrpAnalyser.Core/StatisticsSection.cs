using System.Buffers.Binary;
using System.Text;

namespace YrrpAnalyser;

/// <summary>One entry of the recording's type table: what the game had at that array position.</summary>
public sealed class TypeInfo
{
    public AbstractType Kind { get; init; }
    public int Index { get; init; }
    public string Id { get; init; } = "";
    /// <summary>The art Cameo= name, e.g. gtnkicon.</summary>
    public string Cameo { get; init; } = "";
    /// <summary>UIName from the string table, as the game showed it.</summary>
    public string Name { get; init; } = "";
    public int Cost { get; init; }
    public uint Flags { get; init; }

    public bool IsNaval => (Flags & 1) != 0;
    public bool DontScore => (Flags & 2) != 0;

    public string DisplayName =>
        Name.Length > 0 && !Name.StartsWith("MISSING:", StringComparison.OrdinalIgnoreCase) ? Name : Id;
}

/// <summary>The BuildingType/InfantryType/UnitType/AircraftType arrays the recorded game ran with.</summary>
public sealed class TypeTable
{
    public static readonly TypeTable Empty = new();

    private readonly Dictionary<AbstractType, List<TypeInfo>> _lists = [];

    public bool HasData => _lists.Values.Any(l => l.Count > 0);

    public IReadOnlyList<TypeInfo> Of(AbstractType kind) =>
        _lists.TryGetValue(Normalise(kind), out var list) ? list : [];

    public TypeInfo? Get(AbstractType kind, int index)
    {
        var list = Of(kind);
        return index >= 0 && index < list.Count ? list[index] : null;
    }

    internal void Set(AbstractType kind, List<TypeInfo> list) => _lists[Normalise(kind)] = list;

    public static AbstractType Normalise(AbstractType type) => type switch
    {
        AbstractType.Building => AbstractType.BuildingType,
        AbstractType.Infantry => AbstractType.InfantryType,
        AbstractType.Unit => AbstractType.UnitType,
        AbstractType.Aircraft => AbstractType.AircraftType,
        _ => type,
    };
}

/// <summary>The per-type count arrays after each house record, in StatisticsHouseArray order.</summary>
public enum StatisticsArray
{
    BuiltAircraft,
    BuiltInfantry,
    BuiltUnits,
    BuiltBuildings,
    KilledAircraft,
    KilledInfantry,
    KilledUnits,
    KilledBuildings,
    CapturedBuildings,
    CollectedCrates,
    LeftAircraft,
    LeftInfantry,
    LeftUnits,
    LeftBuildings,
    // Counted by the recorder: the engine keeps only totals of what a house lost.
    LostAircraft,
    LostInfantry,
    LostUnits,
    LostBuildings,
}

/// <summary>Facts about the game as a whole when the recording closed (StatisticsGameRecord).</summary>
public sealed class GameRecord
{
    public ulong EndUnixTime { get; init; }
    public int EndFrame { get; init; }
    /// <summary>The first frame the game's own OutOfSync flag was seen set, or -1.</summary>
    public int OutOfSyncFrame { get; init; }
    public bool OutOfSync { get; init; }
    /// <summary>The game reached its own end (SawCompletion), rather than being quit or cut off.</summary>
    public bool SawCompletion { get; init; }
}

/// <summary>One house's end-of-game record, mirroring StatisticsHouseRecord plus its count arrays.</summary>
public sealed class HouseSummary
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";
    public string Country { get; init; } = "";
    public int ColorSchemeIndex { get; init; }
    public int SpawnPosition { get; init; }
    public uint Allies { get; init; }
    public HouseStatsFlags Flags { get; init; }
    public int Credits { get; init; }
    public int StoredOreValue { get; init; }
    public int CreditsSpent { get; init; }
    public int HarvestedCredits { get; init; }
    public int PowerOutput { get; init; }
    public int PowerDrain { get; init; }
    public int UnitsLost { get; init; }
    public int BuildingsLost { get; init; }
    public int Score { get; init; }
    /// <summary>Indexed by the victim's house: how many of that house's units this house destroyed.</summary>
    public int[] UnitsKilledOfHouse { get; init; } = [];
    public int[] BuildingsKilledOfHouse { get; init; } = [];
    public int[][] Arrays { get; init; } = [];

    public int[] Array(StatisticsArray which) => (int)which < Arrays.Length ? Arrays[(int)which] : [];
    public int Total(StatisticsArray which) => Array(which).Sum();

    public int UnitsKilled => UnitsKilledOfHouse.Sum();
    public int BuildingsKilled => BuildingsKilledOfHouse.Sum();

    /// <summary>The type array a count array is indexed by; None for crates, which are indexed by crate type.</summary>
    public static AbstractType KindOf(StatisticsArray which) => which switch
    {
        StatisticsArray.BuiltAircraft or StatisticsArray.KilledAircraft or StatisticsArray.LeftAircraft
            or StatisticsArray.LostAircraft => AbstractType.AircraftType,
        StatisticsArray.BuiltInfantry or StatisticsArray.KilledInfantry or StatisticsArray.LeftInfantry
            or StatisticsArray.LostInfantry => AbstractType.InfantryType,
        StatisticsArray.BuiltUnits or StatisticsArray.KilledUnits or StatisticsArray.LeftUnits
            or StatisticsArray.LostUnits => AbstractType.UnitType,
        StatisticsArray.BuiltBuildings or StatisticsArray.KilledBuildings or StatisticsArray.LeftBuildings
            or StatisticsArray.CapturedBuildings or StatisticsArray.LostBuildings => AbstractType.BuildingType,
        _ => AbstractType.None,
    };
}

/// <summary>Everything the recording's statistics section carried.</summary>
public sealed class ReplayStatistics
{
    public TypeTable Types { get; set; } = TypeTable.Empty;
    public List<HouseSummary> Houses { get; } = [];

    /// <summary>The game's own statistics packet - what stats.dmp holds - when the game built one.</summary>
    public byte[]? StatsPacketBytes { get; set; }
    public StatsDump? StatsPacket { get; set; }

    public GameRecord? Game { get; set; }

    /// <summary>The process's loaded modules, to resolve a payment's absolute caller.</summary>
    public List<ModuleInfo> Modules { get; } = [];

    public HouseSummary? ForHouse(int houseIndex) => Houses.FirstOrDefault(h => h.HouseIndex == houseIndex);
}

/// <summary>
/// Reads the statistics section after the frame stream (docs/replay-format.md). It is a list of
/// chunks - uint32 tag, uint32 length, the bytes - and a reader skips any tag it does not know.
/// </summary>
public static class StatisticsSectionReader
{
    private const int MaxTypeLists = 16;
    private const int MaxTypesPerList = 8192;
    private const int MaxTypeNameLength = 1024;
    private const int MaxHouses = 64;
    private const int MaxArrays = 64;
    private const int MaxArrayLength = 0x1000;

    public static ReplayStatistics? Read(Stream file, ReplayHeaderInfo header, long streamOffset, List<string> warnings)
    {
        if (!header.HasStatisticsSection)
            return null;

        ulong offset = header.StatisticsOffset;
        uint size = header.StatisticsSize;
        ulong fileLength = (ulong)file.Length;
        if (size == 0 || size > ReplayFormat.MaxStatisticsSectionBytes
            || offset <= (ulong)streamOffset || offset > fileLength || size > fileLength - offset)
        {
            warnings.Add($"The header points at a {size:N0}-byte statistics section at offset {offset:N0}, " +
                         "which does not fit in the file after the frame stream; it was not read.");
            return null;
        }

        var bytes = new byte[size];
        file.Position = (long)offset;
        file.ReadExactly(bytes);
        return Parse(bytes, warnings);
    }

    public static ReplayStatistics Parse(byte[] bytes, List<string> warnings)
    {
        var stats = new ReplayStatistics();
        var cursor = new Cursor(bytes, 0, bytes.Length);

        while (cursor.Remaining > 0)
        {
            if (!cursor.TryU32(out uint tag) || !cursor.TryU32(out uint length) || length > cursor.Remaining)
            {
                warnings.Add("The statistics section ends part-way through a chunk; the rest was not read.");
                break;
            }

            var chunk = new Cursor(bytes, cursor.Position, (int)length);
            cursor.Skip((int)length);

            string? problem = null;
            if (tag == ReplayFormat.ChunkTypes)
                problem = ReadTypes(chunk, stats);
            else if (tag == ReplayFormat.ChunkHouses)
                problem = ReadHouses(chunk, stats);
            else if (tag == ReplayFormat.ChunkGame)
                problem = ReadGame(chunk, stats);
            else if (tag == ReplayFormat.ChunkModules)
                problem = ReadModules(chunk, stats);
            else if (tag == ReplayFormat.ChunkStatsPacket)
            {
                stats.StatsPacketBytes = bytes.AsSpan(chunk.Position, (int)length).ToArray();
                stats.StatsPacket = StatsDump.Parse(stats.StatsPacketBytes);
            }

            if (problem is not null)
                warnings.Add($"The statistics section's {TagText(tag)} chunk is malformed ({problem}); what was read before it is kept.");
        }

        return stats;
    }

    private static string TagText(uint tag) =>
        Encoding.ASCII.GetString(BitConverter.GetBytes(tag));

    private static string? ReadTypes(Cursor c, ReplayStatistics stats)
    {
        var table = new TypeTable();
        if (!c.TryU32(out uint lists) || lists > MaxTypeLists) return "list count";

        for (int l = 0; l < lists; l++)
        {
            if (!c.TryU32(out uint kind) || !c.TryU32(out uint count) || count > MaxTypesPerList) return $"list {l} header";
            var list = new List<TypeInfo>((int)count);
            for (int i = 0; i < count; i++)
            {
                if (!c.TryBytes(0x18, out var id) || !c.TryBytes(0x19, out var cameo)
                    || !c.TryU16(out ushort nameLength) || nameLength > MaxTypeNameLength
                    || !c.TryBytes(nameLength * 2, out var name)
                    || !c.TryI32(out int cost) || !c.TryU32(out uint flags))
                    return $"type {i} of list {l}";

                list.Add(new TypeInfo
                {
                    Kind = (AbstractType)kind,
                    Index = i,
                    Id = FixedAscii(id),
                    Cameo = FixedAscii(cameo),
                    Name = Encoding.Unicode.GetString(name),
                    Cost = cost,
                    Flags = flags,
                });
            }
            table.Set((AbstractType)kind, list);
        }

        stats.Types = table;
        return null;
    }

    private static string? ReadHouses(Cursor c, ReplayStatistics stats)
    {
        if (!c.TryU32(out uint houses) || houses > MaxHouses) return "house count";

        for (int h = 0; h < houses; h++)
        {
            if (!c.TryBytes(ReplayFormat.StatisticsHouseRecordSize, out var recordBytes)) return $"house {h} record";
            var r = recordBytes.ToArray();
            if (!c.TryU32(out uint arrayCount) || arrayCount > MaxArrays) return $"house {h} array count";

            var arrays = new int[arrayCount][];
            for (int a = 0; a < arrayCount; a++)
            {
                if (!c.TryU32(out uint n) || n > MaxArrayLength || !c.TryBytes((int)n * 4, out var values))
                    return $"house {h} array {a}";
                arrays[a] = new int[n];
                for (int i = 0; i < n; i++)
                    arrays[a][i] = BinaryPrimitives.ReadInt32LittleEndian(values[(i * 4)..]);
            }

            int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(offset));
            int[] Twenty(int offset) => Enumerable.Range(0, 20).Select(i => I(offset + i * 4)).ToArray();

            stats.Houses.Add(new HouseSummary
            {
                HouseIndex = I(0),
                Name = FixedUtf16(r.AsSpan(4, 42)),
                Country = FixedAscii(r.AsSpan(46, 24)),
                ColorSchemeIndex = I(70),
                SpawnPosition = I(74),
                Allies = (uint)I(78),
                Flags = (HouseStatsFlags)(uint)I(82),
                Credits = I(86),
                StoredOreValue = I(90),
                CreditsSpent = I(94),
                HarvestedCredits = I(98),
                PowerOutput = I(102),
                PowerDrain = I(106),
                UnitsLost = I(110),
                BuildingsLost = I(114),
                Score = I(118),
                UnitsKilledOfHouse = Twenty(122),
                BuildingsKilledOfHouse = Twenty(202),
                Arrays = arrays,
            });
        }
        return null;
    }

    private static string? ReadModules(Cursor c, ReplayStatistics stats)
    {
        if (!c.TryU32(out uint count) || count > 4096) return "module count";
        for (int i = 0; i < count; i++)
        {
            if (!c.TryU32(out uint moduleBase) || !c.TryU32(out uint size) || !c.TryU32(out uint stamp)
                || !c.TryU16(out ushort nameLength) || nameLength > 260 || !c.TryBytes(nameLength * 2, out var name))
                return $"module {i}";
            stats.Modules.Add(new ModuleInfo
            {
                Name = Encoding.Unicode.GetString(name),
                Base = moduleBase,
                Size = size,
                TimeDateStamp = stamp,
            });
        }
        return null;
    }

    private static string? ReadGame(Cursor c, ReplayStatistics stats)
    {
        if (!c.TryBytes(ReplayFormat.StatisticsGameRecordSize, out var r)) return "record";
        stats.Game = new GameRecord
        {
            EndUnixTime = BinaryPrimitives.ReadUInt64LittleEndian(r),
            EndFrame = BinaryPrimitives.ReadInt32LittleEndian(r[8..]),
            OutOfSyncFrame = BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
            OutOfSync = r[16] != 0,
            SawCompletion = r[17] != 0,
        };
        return null;
    }

    private static string FixedAscii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? bytes[..end] : bytes);
    }

    private static string FixedUtf16(ReadOnlySpan<byte> bytes)
    {
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0)
                return Encoding.Unicode.GetString(bytes[..i]);
        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>Bounds-checked little-endian reads over one chunk of the section.</summary>
    private sealed class Cursor(byte[] bytes, int start, int length)
    {
        private readonly int _end = start + length;
        public int Position { get; private set; } = start;
        public int Remaining => _end - Position;

        public void Skip(int count) => Position += count;

        public bool TryBytes(int count, out ReadOnlySpan<byte> data)
        {
            if (count < 0 || count > Remaining) { data = default; return false; }
            data = bytes.AsSpan(Position, count);
            Position += count;
            return true;
        }

        public bool TryU16(out ushort value)
        {
            value = 0;
            if (!TryBytes(2, out var b)) return false;
            value = BinaryPrimitives.ReadUInt16LittleEndian(b);
            return true;
        }

        public bool TryU32(out uint value)
        {
            value = 0;
            if (!TryBytes(4, out var b)) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(b);
            return true;
        }

        public bool TryI32(out int value)
        {
            value = 0;
            if (!TryBytes(4, out var b)) return false;
            value = BinaryPrimitives.ReadInt32LittleEndian(b);
            return true;
        }
    }
}
