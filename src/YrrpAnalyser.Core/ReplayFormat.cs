namespace YrrpAnalyser;

/// <summary>
/// On-disk constants for the .yrrp format, mirrored by hand from
/// yrpp-spawner/src/Replay/ReplayFormat.h. Any change there must be made here too - a size or
/// offset drift is silent, not an error, and misparses everything past the point of divergence.
/// </summary>
public static class ReplayFormat
{
    public const uint Magic = 0x50525259;          // 'YRRP'
    public const uint Version = 1;
    public const uint MinSupportedVersion = 1;

    public const int HeaderSize = 1124;             // sizeof(ReplayHeader)
    public const int FrameRecordHeaderSize = 12;
    public const int FrameObjectCensusSize = 8;
    public const int FrameRandomStateSize = 8;
    public const int SideChannelRecordSize = 329;
    public const int EventSize = 111;               // sizeof(EventClass)
    public const int EventDataOffset = 7;           // offsetof(EventClass, DataBuffer)

    public const int MaxGameSpeedIndex = 6;
    public const int SideChannelTextLength = 128;
    public const int SideChannelNameLength = 24;
    public const int SideChannelMaxEventsPerFrame = 64;
    public const int MaxHouses = 8;
    public const int MaxBeaconSlots = 3;
    public const uint MaxFrameExtensionBytes = 1u << 20;
    public const int MaxSelectionCount = 4096;
    public const int MaxSelectionTriggersPerFrame = 4096;
    public const int MaxEventsPerFrame = 128 * 128;
    public const uint MaxEmbeddedFileBytes = 32u * 1024u * 1024u;

    /// <summary>The spawner sync-flushes the deflate stream this often, bounding crash loss.</summary>
    public const int SyncFlushFrameInterval = 60;

    // Header field offsets. Pinned individually on the writing side; pinned here so a future
    // layout change surfaces as a diff in one file rather than as garbage in the UI.
    public const int OffsetMagic = 0;
    public const int OffsetVersion = 4;
    public const int OffsetHeaderSize = 8;
    public const int OffsetGameMode = 12;
    public const int OffsetUniqueIDCounter = 16;
    public const int OffsetSeed = 20;
    public const int OffsetRandomNext1 = 24;
    public const int OffsetRandomNext2 = 28;
    public const int OffsetRandomizerTable = 32;
    public const int RandomizerTableLength = 250;
    public const int OffsetSpawnIniSize = 1032;
    public const int OffsetSpawnMapSize = 1036;
    public const int OffsetRecordedGameSpeed = 1040;
    public const int OffsetRecordedUnixTime = 1044;
    public const int OffsetTotalFrames = 1052;
    public const int OffsetFlags = 1056;
    public const int OffsetReserved = 1060;
    public const int ReservedLength = 16;

    /// <summary>
    /// Vanilla Queue_AI_Multiplayer mapping, duplicated in ReplayFormat.h and in the client's
    /// ReplayGame.GetFramesPerSecond. All three have to agree or durations disagree.
    /// </summary>
    public static int GetFpsFromGameSpeed(int gameSpeed)
    {
        gameSpeed = Math.Clamp(gameSpeed, 0, MaxGameSpeedIndex);
        if (gameSpeed <= 0) return 60;
        if (gameSpeed == 1) return 45;
        return Math.Max(1, 60 / gameSpeed);
    }
}

[Flags]
public enum ReplayHeaderFlags : uint
{
    None = 0,
    /// <summary>Recording reached StopReplaySystem rather than dying with the process.</summary>
    CleanShutdown = 1u << 0,
}

[Flags]
public enum FrameRecordFlags : uint
{
    None = 0,
    TacticalPos = 1u << 0,
    Selection = 1u << 1,
    SideChannel = 1u << 2,
    GameCrc = 1u << 3,
    Extensions = 1u << 4,

    /// <summary>A FrameObjectCensus follows: how many objects exist and the next unique ID.</summary>
    ObjectCensus = 1u << 5,

    /// <summary>
    /// An int32 game speed index follows. Written only on the frames the speed changes, which is
    /// almost never - and a single player game changes it with no event at all, so the stream is
    /// the only place a reader can learn about it.
    /// </summary>
    GameSpeed = 1u << 6,

    /// <summary>Two int32 randomizer table cursors, preceding the game speed block.</summary>
    RandomState = 1u << 7,

    /// <summary>A positive int32 count followed by selection-trigger object unique IDs.</summary>
    SelectionTriggers = 1u << 8,

    Known = TacticalPos | Selection | SideChannel | GameCrc | Extensions
            | ObjectCensus | GameSpeed | RandomState | SelectionTriggers,
}

public enum SideChannelEventType : byte
{
    ChatMessage = 1,
    BeaconPlace = 2,
    BeaconDelete = 3,
    BeaconText = 4,
    Taunt = 5,
}

/// <summary>SessionClass::GameMode, as stamped into the header.</summary>
public enum ReplayGameMode : uint
{
    Campaign = 0,
    Lan = 3,
    Internet = 4,
    Skirmish = 5,
}
