namespace YrrpAnalyser;

/// <summary>
/// On-disk constants for the .yrrp format, mirrored by hand from
/// yrpp-spawner/src/Replay/ReplayFormat.h. Any change there must be made here too - a size or
/// offset drift is silent, not an error, and misparses everything past the point of divergence.
/// </summary>
public static class ReplayFormat
{
    public const uint Magic = 0x4A455259u;          // 'YREJ'
    public const uint Version = 1;
    public const uint MinSupportedVersion = 1;

    public const int HeaderSize = 1452;             // sizeof(ReplayHeader)
    public const int FrameRecordHeaderSize = 12;
    public const int FrameObjectCensusSize = 8;
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

    /// <summary>The spawner sync-flushes the deflate stream this often, bounding crash loss.</summary>
    public const int SyncFlushFrameInterval = 60;

    // Header field offsets. Pinned individually on the writing side; pinned here so a future
    // layout change surfaces as a diff in one file rather than as garbage in the UI.
    public const int OffsetMagic = 0;
    public const int OffsetVersion = 4;
    public const int OffsetHeaderSize = 8;
    public const int OffsetMapName = 12;
    public const int OffsetSpawnerVersion = 272;
    public const int OffsetGameClientVersion = 276;
    public const int OffsetGameMode = 340;
    public const int OffsetUniqueIDCounter = 344;
    public const int OffsetSeed = 348;
    public const int OffsetRandomNext1 = 352;
    public const int OffsetRandomNext2 = 356;
    public const int OffsetRandomizerTable = 360;
    public const int RandomizerTableLength = 250;
    public const int OffsetSpawnIniSize = 1360;
    public const int OffsetSpawnMapSize = 1364;
    public const int OffsetRecordedGameSpeed = 1368;
    public const int OffsetRecordedUnixTime = 1372;
    public const int OffsetTotalFrames = 1380;
    public const int OffsetFlags = 1384;
    public const int OffsetReserved = 1388;
    public const int ReservedLength = 16;

    public const int MapNameLength = 260;
    public const int GameClientVersionLength = 64;

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

    Known = TacticalPos | Selection | SideChannel | GameCrc | Extensions
            | ObjectCensus | GameSpeed,
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
