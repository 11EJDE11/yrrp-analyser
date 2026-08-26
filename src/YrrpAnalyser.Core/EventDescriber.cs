namespace YrrpAnalyser;

/// <summary>
/// Turns a recorded EventClass into the two columns the log shows: a short category and a
/// one-line description of the payload.
///
/// The payload union is the one in YRpp/EventClass.h; every offset below is a field of that
/// union, read from the 104-byte DataBuffer at offset 7.
/// </summary>
public sealed class EventDescriber(TypeNameResolver types)
{
    private readonly TypeNameResolver _types = types;

    public string Describe(in GameEvent e)
    {
        switch (e.Type)
        {
            case EventType.MegaMission:
            {
                // TargetClass Whom; u8 Mission; char gap; TargetClass Target, Destination, Follow;
                // bool IsPlanningEvent
                var whom = e.Target(0);
                var mission = (Mission)e.U8(5);
                var target = e.Target(7);
                var dest = e.Target(12);
                var follow = e.Target(17);
                bool planning = e.U8(22) != 0;
                return Join(
                    $"{whom} -> {MissionName(mission)}",
                    target.IsNone ? null : $"target {target}",
                    dest.IsNone ? null : $"dest {DescribeTarget(dest)}",
                    follow.IsNone ? null : $"follow {follow}",
                    planning ? "planning" : null);
            }

            case EventType.MegaMissionF:
            {
                var whom = e.Target(0);
                var mission = (Mission)e.U8(5);
                var target = e.Target(6);
                var dest = e.Target(11);
                int speed = e.I32(16);
                int maxSpeed = e.I32(20);
                return Join(
                    $"{whom} -> {MissionName(mission)}",
                    target.IsNone ? null : $"target {target}",
                    dest.IsNone ? null : $"dest {DescribeTarget(dest)}",
                    $"speed {speed}/{maxSpeed}");
            }

            case EventType.Place:
            {
                // AbstractType RTTIType; int HeapID; int IsNaval; CellStruct Location
                var rtti = (AbstractType)e.U32(0);
                int heapId = e.I32(4);
                bool naval = e.I32(8) != 0;
                var cell = e.Cell(12);

                // A Place with no heap ID and no cell is the engine releasing a finished unit
                // from its factory, not a player putting a building down. The factory is found
                // from the type category alone, which is why there is nothing else to name.
                if (heapId < 0)
                    return Join($"{Categorise(rtti)} leaves the factory", naval ? "naval" : null);

                return Join($"{_types.Describe(rtti, heapId)} at {cell}", naval ? "naval" : null);
            }

            case EventType.Produce:
            case EventType.Suspend:
            case EventType.Abandon:
            {
                var rtti = (AbstractType)e.U32(0);
                int heapId = e.I32(4);
                bool naval = e.I32(8) != 0;
                return Join(_types.Describe(rtti, heapId), naval ? "naval" : null);
            }

            case EventType.SpecialPlace:
            {
                int id = e.I32(0);
                var cell = e.Cell(4);
                return $"superweapon #{id} at {cell}";
            }

            case EventType.SellCell:
                return $"cell {e.Cell(0)}";

            case EventType.PowerOn:
            case EventType.PowerOff:
            case EventType.Idle:
            case EventType.Scatter:
            case EventType.Deploy:
            case EventType.Detonate:
            case EventType.Primary:
            case EventType.Repair:
            case EventType.Sell:
            case EventType.PlanNodeDelete:
                return e.Target(0).ToString();

            case EventType.Archive:
            case EventType.PlanConnect:
                return $"{e.Target(0)} -> {e.Target(5)}";

            case EventType.Ally:
                return $"house {e.I32(0)}";

            case EventType.RemovePlayer:
                return $"house {e.I32(0)}";

            case EventType.FallbackHost:
                return $"host {e.I32(0)}";

            case EventType.GameSpeed:
            {
                int speed = e.I32(0);
                return $"index {speed} ({ReplayFormat.GetFpsFromGameSpeed(speed)} FPS)";
            }

            case EventType.LatencyFudge:
                return $"fudge {e.I32(0)}";

            case EventType.Animation:
            {
                int animId = e.I32(0);
                int houseId = e.I32(4);
                int x = e.I32(8), y = e.I32(12);
                return $"anim #{animId} house {houseId} at ({x},{y})";
            }

            case EventType.Special:
                return $"flags 0x{e.U32(0):X8}";

            // --- network and timing plumbing ---

            case EventType.FrameInfo:
            {
                // u32 CRC; u16 CommandCount; u8 Delay. Delay is the sender's MaxAhead at send
                // time; the whole event only ever comes from a remote peer, because the local
                // one is written straight into the outgoing packet and never enters DoList.
                uint crc = e.U32(0);
                ushort commands = e.U16(4);
                byte delay = e.U8(6);
                return $"CRC {crc:X8}, sent {commands}, MaxAhead {delay}";
            }

            case EventType.Timing:
            {
                // u16 RequestedFPS; u16 MaxAhead; u8 FrameSendRate. Only the session master emits
                // these; every peer adopts what it carries.
                ushort fps = e.U16(0);
                ushort maxAhead = e.U16(2);
                byte sendRate = e.U8(4);
                return $"{fps} FPS, MaxAhead {maxAhead}, FrameSendRate {sendRate}";
            }

            case EventType.ProcessTime:
            {
                // u16 Time = ProcessingTicks / ProcessingFrames over the last 128 frames, in
                // 60 Hz system ticks. Emitted by every peer about itself when (Frame & 0x7F) == 0.
                ushort ticks = e.U16(0);
                return $"{ticks} ticks/frame ({NetworkAnalysis.TicksToMs(ticks):0.0} ms)";
            }

            case EventType.ResponseTime:
                return $"{e.U8(0)}";

            case EventType.ResponseTime2:
            {
                // Spawner ProtocolZero: i8 MaxAhead = IPX response time + 1, in 60 Hz ticks;
                // u8 LatencyLevel = the level that response time maps to.
                sbyte responseTicks = e.I8(0);
                byte level = e.U8(1);
                return $"round trip {responseTicks} ticks " +
                       $"({NetworkAnalysis.TicksToMs(responseTicks):0} ms), latency level {level}" +
                       $" ({NetworkAnalysis.LatencyLevelName(level)})";
            }

            case EventType.AddressChange:
            {
                byte playerId = e.U8(0);
                uint address = e.U32(1);
                return $"player {playerId} -> 0x{address:X8}";
            }

            case EventType.Empty:
            case EventType.FrameSync:
            case EventType.Exit:
            case EventType.AboutToExit:
            case EventType.Destruct:
            case EventType.Options:
            case EventType.SaveGame:
            case EventType.PlanCommit:
            case EventType.PageUser:
            case EventType.Message:
                return "";

            default:
                return HexPreview(e.Data, 16);
        }
    }

    /// <summary>Grouping used by the log's category column and by the summary counts.</summary>
    public static string Category(EventType type)
    {
        if (EventTypes.IsTiming(type)) return "Network";
        return type switch
        {
            EventType.MegaMission or EventType.MegaMissionF or EventType.Idle
                or EventType.Scatter or EventType.Deploy or EventType.Detonate => "Orders",
            EventType.Place or EventType.Produce or EventType.Suspend or EventType.Abandon
                or EventType.AbandonAll or EventType.Primary => "Production",
            EventType.Sell or EventType.SellCell or EventType.Repair or EventType.PowerOn
                or EventType.PowerOff or EventType.Destruct => "Base",
            EventType.SpecialPlace => "Superweapon",
            EventType.Ally or EventType.AllCheer or EventType.PageUser => "Diplomacy",
            EventType.PlanConnect or EventType.PlanCommit or EventType.PlanNodeDelete => "Planning",
            EventType.Exit or EventType.AboutToExit or EventType.RemovePlayer
                or EventType.AddPlayer or EventType.SaveGame or EventType.Options
                or EventType.GameSpeed or EventType.Special or EventType.FallbackHost
                or EventType.AddressChange or EventType.LatencyFudge => "Session",
            _ => "Other",
        };
    }

    private string DescribeTarget(TargetRef t) => t.IsCell ? t.AsCell.ToString() : t.ToString();

    private static string Categorise(AbstractType rtti) => rtti switch
    {
        AbstractType.Building or AbstractType.BuildingType => "Building",
        AbstractType.Infantry or AbstractType.InfantryType => "Infantry",
        AbstractType.Unit or AbstractType.UnitType => "Vehicle",
        AbstractType.Aircraft or AbstractType.AircraftType => "Aircraft",
        _ => rtti.ToString(),
    };

    private static string MissionName(Mission m) =>
        Enum.IsDefined(m) ? m.ToString() : $"Mission {(int)m}";

    private static string Join(params string?[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public static string HexPreview(ReadOnlySpan<byte> data, int count)
    {
        count = Math.Min(count, data.Length);
        var chars = new char[count * 3];
        for (int i = 0; i < count; i++)
        {
            chars[i * 3] = HexDigit(data[i] >> 4);
            chars[i * 3 + 1] = HexDigit(data[i] & 0xF);
            chars[i * 3 + 2] = ' ';
        }
        return new string(chars).TrimEnd();
    }

    private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);
}
