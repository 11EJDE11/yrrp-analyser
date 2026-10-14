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

    public const int HeaderSize = 1096;             // sizeof(ReplayHeader)
    public const int EventSize = 111;               // sizeof(EventClass): how events are held in memory here
    public const int EventDataOffset = 7;           // offsetof(EventClass, DataBuffer)
    public const int EventDataLength = 104;

    public const int MaxGameSpeedIndex = 6;
    public const int SideChannelTextLength = 128;
    public const int SideChannelNameLength = 24;
    public const int SideChannelMaxEventsPerFrame = 64;
    public const int ChatSaltBytes = 16;
    public const int ChatFingerprintBytes = 16;
    public const int MaxHouses = 8;
    public const int MaxBeaconSlots = 3;
    public const uint MaxFrameBlockBytes = 1u << 20;
    public const int MaxSelectionCount = 4096;
    public const int MaxSelectionTriggersPerFrame = 4096;
    public const int MaxEventsPerFrame = 128 * 128;
    public const uint MaxEmbeddedFileBytes = 32u * 1024u * 1024u;

    // Checkpoint archive bounds: ReplayFormat.h, ReplayRecordedCheckpoint.h and
    // ReplayCheckpointCodec.h (CheckpointCodec::MaxBytes bounds each inflated payload).
    public const uint MaxCheckpointArchiveBytes = 32u * 1024u * 1024u;
    public const uint MaxCheckpointPayloadBytes = 32u * 1024u * 1024u;
    public const int MaxRecordedCheckpoints = 4;

    // Statistics: the per-frame HouseStats block and the statistics section after the frame stream.
    public const int HouseStatsSampleSize = 84;            // sizeof(HouseStatsSample)
    public const int MaxHouseStatsPerFrame = 32;
    public const int HouseStatsIntervalFrames = 60;
    public const uint MaxStatisticsSectionBytes = 8u * 1024u * 1024u;
    public const int StatisticsHouseRecordSize = 282;      // sizeof(StatisticsHouseRecord)
    public const int StatisticsGameRecordSize = 20;        // sizeof(StatisticsGameRecord)
    public const int MoneyInRecordSize = 12;               // sizeof(MoneyInRecord)
    public const int MaxMoneyInPerFrame = 1024;

    // The canonical hash chain: see Canonical.cs and docs/replay-format.md.
    public const int SealIntervalFrames = 900;
    public const int SealHashBytes = 32;
    public const int SealRecordSize = 36;                  // sizeof(SealRecord)

    /// <summary>
    /// EventClass::EventLength (0x8208EC): the payload bytes each vanilla event type carries, and so
    /// the bytes the frame stream stores for it.
    /// </summary>
    public static readonly byte[] VanillaEventLength =
    [
        0, 5, 5, 4, 23, 24, 5, 5, 0, 5, 5, 16, 0, 4, 12, 12,
        12, 5, 8, 0, 16, 5, 5, 4, 4, 0, 0, 1, 7, 0, 10, 4,
        5, 2, 0, 4, 4, 104, 64, 0, 4, 5, 10, 0, 5, 4, 12,
    ];

    /// <summary>The spawner's own event types (src/Ext/Event/Body.h) and their payload lengths.</summary>
    public const byte EventResponseTime2 = 0x30;
    public const byte EventChatFingerprint = 0x31;

    /// <summary>Payload bytes the stream stores for an event type; -1 when it stores a length of its own.</summary>
    public static int EventPayloadLength(byte type) => type switch
    {
        < 47 => VanillaEventLength[type],
        EventResponseTime2 => 2,
        EventChatFingerprint => ChatFingerprintBytes,
        _ => -1,
    };

    /// <summary>FRAMEINFO is stored as CommandCount, Delay and this in place of the CRC.</summary>
    public const int FrameInfoStoredBytes = 4;

    /// <summary>A four-character chunk tag as MakeChunkTag packs it: first character in the low byte.</summary>
    public static uint ChunkTag(string tag) =>
        (uint)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24);

    public static readonly uint ChunkTypes = ChunkTag("TYPE");
    public static readonly uint ChunkHouses = ChunkTag("HOUS");
    public static readonly uint ChunkStatsPacket = ChunkTag("STAT");
    public static readonly uint ChunkGame = ChunkTag("GAME");
    public static readonly uint ChunkModules = ChunkTag("MODS");
    public static readonly uint ChunkSeals = ChunkTag("SEAL");

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
    public const int OffsetCheckpointArchiveOffset = 1060;  // uint64
    public const int OffsetCheckpointArchiveSize = 1068;
    public const int OffsetStatisticsOffset = 1072;         // uint64
    public const int OffsetStatisticsSize = 1080;
    public const int OffsetSpawnIniRawSize = 1084;
    public const int OffsetSpawnMapRawSize = 1088;
    public const int OffsetStatisticsRawSize = 1092;

    /// <summary>
    /// Vanilla Queue_AI_Multiplayer mapping, duplicated in ReplayFormat.h and in the client's
    /// YRReplayGame.GetFramesPerSecond. All three have to agree or durations disagree.
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

/// <summary>A frame record's tagged blocks: varint tag, varint length, then the bytes; tag 0 ends the list.</summary>
public enum FrameBlockTag : uint
{
    End = 0,
    TacticalPos = 1,
    Selection = 2,
    SideChannel = 3,
    GameSpeed = 4,
    SelectionTriggers = 5,
    HouseStats = 6,
    MoneyIn = 7,
    Seal = 8,
}

/// <summary>What the recorder made of a peer's FRAMEINFO CRC, which the file stores in its place.</summary>
public enum FrameInfoCheck : byte
{
    Mismatch = 0,
    Match = 1,
    NotChecked = 2,
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
