using System.Buffers.Binary;
using System.Text;

namespace YrrpAnalyser;

/// <summary>PacketClass field types, as the game's statistics packet writes them.</summary>
public enum StatsFieldType : ushort
{
    Byte = 1,
    Boolean = 2,
    Short = 3,
    UnsignedShort = 4,
    Long = 5,
    UnsignedLong = 6,
    Char = 7,
    CustomLength = 20,
}

/// <summary>One field of the statistics packet: a four-character tag, a type and a value.</summary>
public sealed class StatsField
{
    public string Tag { get; init; } = "";
    public StatsFieldType Type { get; init; }
    public byte[] Raw { get; init; } = [];

    /// <summary>The player a per-player field belongs to - its last character - or -1 for a game field.</summary>
    public int PlayerIndex => Tag.Length == 4 && char.IsAsciiDigit(Tag[3]) ? Tag[3] - '0' : -1;

    /// <summary>The tag without its player digit: UNB for UNB0..UNB7.</summary>
    public string Key => PlayerIndex >= 0 ? Tag[..3] : Tag;

    /// <summary>Numeric value for the integer and boolean types; null otherwise.</summary>
    public long? Number => Type switch
    {
        StatsFieldType.Byte or StatsFieldType.Boolean when Raw.Length >= 1 => Raw[0],
        StatsFieldType.Short when Raw.Length >= 2 => BinaryPrimitives.ReadInt16BigEndian(Raw),
        StatsFieldType.UnsignedShort when Raw.Length >= 2 => BinaryPrimitives.ReadUInt16BigEndian(Raw),
        StatsFieldType.Long when Raw.Length >= 4 => BinaryPrimitives.ReadInt32BigEndian(Raw),
        StatsFieldType.UnsignedLong when Raw.Length >= 4 => BinaryPrimitives.ReadUInt32BigEndian(Raw),
        _ => null,
    };

    /// <summary>The field as text: ASCII for Char, UTF-16 for the map name the spawner adds.</summary>
    public string? Text => Type switch
    {
        StatsFieldType.Char => Encoding.ASCII.GetString(Raw).TrimEnd('\0'),
        StatsFieldType.CustomLength when Key == "SCEN" => DecodeUtf16(Raw),
        _ => null,
    };

    /// <summary>
    /// A count array - UNB, UNK, BLC and the like - read as the network-order int32s
    /// UnitTrackerClass::To_Network_Format left them as. Index is the type array position.
    /// </summary>
    public int[] Counts
    {
        get
        {
            if (Type != StatsFieldType.CustomLength) return [];
            var counts = new int[Raw.Length / 4];
            for (int i = 0; i < counts.Length; i++)
                counts[i] = BinaryPrimitives.ReadInt32BigEndian(Raw.AsSpan(i * 4));
            return counts;
        }
    }

    public bool IsCountArray => Type == StatsFieldType.CustomLength && StatsDump.CountArrayKeys.Contains(Key);

    private static string DecodeUtf16(byte[] raw)
    {
        int end = 0;
        while (end + 1 < raw.Length && (raw[end] != 0 || raw[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(raw, 0, end);
    }

    public string DisplayValue
    {
        get
        {
            switch (Key)
            {
                case "CMP": return Number is { } cmp ? $"{cmp}  ({StatsDump.DescribeCompletion(cmp)})" : "";
                case "DATE" when Raw.Length == 8:
                    // A FILETIME, high dword first.
                    long fileTime = (long)BinaryPrimitives.ReadUInt32BigEndian(Raw) << 32
                                    | BinaryPrimitives.ReadUInt32BigEndian(Raw.AsSpan(4));
                    try { return DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-dd HH:mm:ss 'UTC'"); }
                    catch (ArgumentOutOfRangeException) { break; }
                case "TIME" when Number is { } unix:
                    return $"{unix}  ({DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC)";
                case "DURA" when Number is { } seconds:
                    return $"{seconds} s  ({ReplayDocument.FormatTime(TimeSpan.FromSeconds(seconds))})";
                case "IPA" when Raw.Length == 4:
                    return $"{Raw[0]}.{Raw[1]}.{Raw[2]}.{Raw[3]}";
                case "ALY" when Number is { } allies:
                    var houses = Enumerable.Range(0, 32).Where(i => ((allies >> i) & 1) != 0).ToList();
                    return houses.Count == 0 ? "none" : "houses " + string.Join(", ", houses);
            }

            if (Text is { } text) return text;
            if (Number is { } number) return Type == StatsFieldType.Boolean ? (number != 0 ? "yes" : "no") : number.ToString();
            if (IsCountArray)
            {
                var counts = Counts;
                var nonZero = counts.Select((c, i) => (c, i)).Where(p => p.c != 0).ToList();
                return nonZero.Count == 0 ? "none" : string.Join(", ", nonZero.Select(p => $"#{p.i}×{p.c}"));
            }
            return Convert.ToHexString(Raw);
        }
    }
}

/// <summary>
/// The game's end-of-game statistics packet - byte for byte what stats.dmp holds and what the CnCNet
/// ladder parses (GameService::processStatsDmp). Big-endian throughout: a two-byte packet length and
/// two bytes of zero, then fields of tag[4], type u16, length u16 and the data padded to four bytes.
/// </summary>
public sealed class StatsDump
{
    public List<StatsField> Fields { get; } = [];

    public int DeclaredLength { get; private set; }

    public StatsField? Get(string tag) => Fields.FirstOrDefault(f => f.Tag == tag);
    public long? Number(string tag) => Get(tag)?.Number;
    public string? Text(string tag) => Get(tag)?.Text;

    public IEnumerable<StatsField> GameFields => Fields.Where(f => f.PlayerIndex < 0);
    public IEnumerable<StatsField> PlayerFields(int player) => Fields.Where(f => f.PlayerIndex == player);

    public IReadOnlyList<int> Players => Fields
        .Where(f => f.Key == "NAM" && f.PlayerIndex >= 0)
        .Select(f => f.PlayerIndex).Distinct().OrderBy(i => i).ToList();

    public static StatsDump Parse(ReadOnlySpan<byte> data)
    {
        var dump = new StatsDump();
        if (data.Length < 4) return dump;

        dump.DeclaredLength = BinaryPrimitives.ReadUInt16BigEndian(data);
        int pos = 4;
        while (pos + 8 <= data.Length)
        {
            string tag = Encoding.ASCII.GetString(data.Slice(pos, 4));
            var type = (StatsFieldType)BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 6)..]);
            pos += 8;
            if (length > data.Length - pos) break;

            dump.Fields.Add(new StatsField { Tag = tag, Type = type, Raw = data.Slice(pos, length).ToArray() });
            pos += length;
            if (length % 4 != 0) pos += 4 - length % 4;
        }
        return dump;
    }

    public static readonly HashSet<string> CountArrayKeys =
    [
        "UNB", "INB", "PLB", "BLB", "UNK", "INK", "PLK", "BLK", "UNL", "INL", "PLL", "BLL", "BLC", "CRA",
    ];

    /// <summary>
    /// What each tag means. UNL/INL/PLL/BLL are what was *left* at the end, not losses:
    /// Send_Statistics_Packet clears the built arrays and refills them from each house's current
    /// objects (0x6C7D98-0x6C7EA8) before packing them under those tags.
    /// </summary>
    public static string Meaning(string key) => key switch
    {
        "IDNO" => "Game ID",
        "GSKU" => "Game SKU",
        "TRNY" => "Tournament",
        "OOSY" => "Went out of sync",
        "FINI" => "Game finished",
        "DURA" => "Duration",
        "AFPS" => "Average frames per second",
        "SPED" => "Game speed",
        "CRED" => "Starting credits",
        "SHRT" => "Short game",
        "SUPR" => "Superweapons",
        "CRAT" => "Crates",
        "BASE" => "Bases",
        "BAMR" => "MCV repacks",
        "UNIT" => "Starting units",
        "AIPL" => "AI players",
        "PLRS" => "Players",
        "MODE" => "Game mode",
        "SCEN" => "Map",
        "HASH" => "Map SHA-1",
        "VERS" => "Game version",
        "TIME" => "Start time",
        "DATE" => "End time",
        "PNGS" => "Pings sent",
        "PNGR" => "Pings received",
        "MYID" => "Reporting player",
        "NAM" => "Name",
        "IPA" => "IP address",
        "ALY" => "Allies",
        "BSP" => "Start position",
        "SID" => "Side",
        "CTY" => "Country",
        "COL" => "Colour",
        "TID" => "Team",
        "CMP" => "Result",
        "LCN" => "Lost connection",
        "CRD" => "Credits at the end",
        "HRV" => "Credits harvested",
        "UNB" => "Vehicles built",
        "INB" => "Infantry built",
        "PLB" => "Aircraft built",
        "BLB" => "Buildings built",
        "UNK" => "Vehicles destroyed",
        "INK" => "Infantry destroyed",
        "PLK" => "Aircraft destroyed",
        "BLK" => "Buildings destroyed",
        "UNL" => "Vehicles left at the end",
        "INL" => "Infantry left at the end",
        "PLL" => "Aircraft left at the end",
        "BLL" => "Buildings left at the end",
        "BLC" => "Buildings captured",
        "CRA" => "Crates collected",
        "CPT" => "CPU type",
        "CPS" => "CPU speed",
        "MEM" => "Memory",
        "VID" => "Video memory",
        _ => "",
    };

    /// <summary>The CMP bits the ladder relies on (GameResult.php).</summary>
    public static string DescribeCompletion(long cmp)
    {
        var parts = new List<string>();
        if ((cmp & 256) != 0) parts.Add("won");
        if ((cmp & 512) != 0) parts.Add("defeated");
        if ((cmp & 64) != 0) parts.Add("draw");
        if ((cmp & 16) != 0) parts.Add("quit");
        if ((cmp & 2) != 0) parts.Add("disconnected");
        if ((cmp & 8) != 0) parts.Add("did not see the end");
        return parts.Count == 0 ? "no result" : string.Join(", ", parts);
    }
}
