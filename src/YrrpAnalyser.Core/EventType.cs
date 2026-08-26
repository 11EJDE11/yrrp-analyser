namespace YrrpAnalyser;

/// <summary>
/// EventClass::Type. 0x00-0x2E are vanilla; Ares claims 0x60-0x61; the CnCNet spawner adds
/// 0x30 (ResponseTime2) for its own latency protocol.
/// </summary>
public enum EventType : byte
{
    Empty = 0x00,
    PowerOn = 0x01,
    PowerOff = 0x02,
    Ally = 0x03,
    MegaMission = 0x04,
    MegaMissionF = 0x05,
    Idle = 0x06,
    Scatter = 0x07,
    Destruct = 0x08,
    Deploy = 0x09,
    Detonate = 0x0A,
    Place = 0x0B,
    Options = 0x0C,
    GameSpeed = 0x0D,
    Produce = 0x0E,
    Suspend = 0x0F,
    Abandon = 0x10,
    Primary = 0x11,
    SpecialPlace = 0x12,
    Exit = 0x13,
    Animation = 0x14,
    Repair = 0x15,
    Sell = 0x16,
    SellCell = 0x17,
    Special = 0x18,
    FrameSync = 0x19,
    Message = 0x1A,
    ResponseTime = 0x1B,
    FrameInfo = 0x1C,
    SaveGame = 0x1D,
    Archive = 0x1E,
    AddPlayer = 0x1F,
    Timing = 0x20,
    ProcessTime = 0x21,
    PageUser = 0x22,
    RemovePlayer = 0x23,
    LatencyFudge = 0x24,
    MegaFrameInfo = 0x25,
    PacketTiming = 0x26,
    AboutToExit = 0x27,
    FallbackHost = 0x28,
    AddressChange = 0x29,
    PlanConnect = 0x2A,
    PlanCommit = 0x2B,
    PlanNodeDelete = 0x2C,
    AllCheer = 0x2D,
    AbandonAll = 0x2E,

    /// <summary>CnCNet spawner, ProtocolZero: per-player response time and latency level.</summary>
    ResponseTime2 = 0x30,
}

public static class EventTypes
{
    /// <summary>
    /// Network and timing plumbing. The spawner's IsTimingEvent classifies exactly this set, and
    /// playback drops all of it rather than re-executing it. It is what the "timing events"
    /// checkbox hides.
    /// </summary>
    public static bool IsTiming(EventType type) => type switch
    {
        EventType.Empty
            or EventType.ResponseTime
            or EventType.FrameInfo
            or EventType.Timing
            or EventType.ProcessTime
            or EventType.PacketTiming
            or EventType.MegaFrameInfo
            or EventType.FrameSync
            or EventType.ResponseTime2 => true,
        _ => false,
    };

    /// <summary>An action a player took, as opposed to plumbing or a session-level control.</summary>
    public static bool IsPlayerAction(EventType type) => type switch
    {
        EventType.MegaMission or EventType.MegaMissionF or EventType.Place
            or EventType.Produce or EventType.Suspend or EventType.Abandon or EventType.AbandonAll
            or EventType.Deploy or EventType.Sell or EventType.SellCell or EventType.Repair
            or EventType.Primary or EventType.Scatter or EventType.Idle or EventType.Detonate
            or EventType.PowerOn or EventType.PowerOff or EventType.SpecialPlace
            or EventType.Ally or EventType.Archive or EventType.Animation
            or EventType.PlanConnect or EventType.PlanCommit or EventType.PlanNodeDelete
            or EventType.AllCheer or EventType.Destruct => true,
        _ => false,
    };

    public static string Name(EventType type) =>
        Enum.IsDefined(type) ? type.ToString() : $"Unknown(0x{(byte)type:X2})";
}

/// <summary>AbstractType, the RTTI tag carried by TargetClass and by the production events.</summary>
public enum AbstractType : uint
{
    None = 0, Unit = 1, Aircraft = 2, AircraftType = 3, Anim = 4, AnimType = 5,
    Building = 6, BuildingType = 7, Bullet = 8, BulletType = 9, Campaign = 10, Cell = 11,
    Factory = 12, House = 13, HouseType = 14, Infantry = 15, InfantryType = 16, Isotile = 17,
    IsotileType = 18, BuildingLight = 19, Overlay = 20, OverlayType = 21, Particle = 22,
    ParticleType = 23, ParticleSystem = 24, ParticleSystemType = 25, Script = 26, ScriptType = 27,
    Side = 28, Smudge = 29, SmudgeType = 30, Special = 31, SuperWeaponType = 32, TaskForce = 33,
    Team = 34, TeamType = 35, Terrain = 36, TerrainType = 37, Trigger = 38, TriggerType = 39,
    UnitType = 40, VoxelAnim = 41, VoxelAnimType = 42, Wave = 43, Tag = 44, TagType = 45,
    Tiberium = 46, Action = 47, Event = 48, WeaponType = 49, WarheadType = 50, Waypoint = 51,
    Abstract = 52, Tube = 53, LightSource = 54, EMPulse = 55, TacticalMap = 56, Super = 57,
    AITrigger = 58,
}

public enum Mission
{
    None = -1, Sleep = 0, Attack = 1, Move = 2, QMove = 3, Retreat = 4, Guard = 5, Sticky = 6,
    Enter = 7, Capture = 8, Eaten = 9, Harvest = 10, AreaGuard = 11, Return = 12, Stop = 13,
    Ambush = 14, Hunt = 15, Unload = 16, Sabotage = 17, Construction = 18, Selling = 19,
    Repair = 20, Rescue = 21, Missile = 22, Harmless = 23, Open = 24, Patrol = 25,
    ParadropApproach = 26, ParadropOverfly = 27, Wait = 28, AttackMove = 29,
    SpyplaneApproach = 30, SpyplaneOverfly = 31,
}
