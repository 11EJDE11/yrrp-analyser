using System.Buffers.Binary;

namespace YrrpAnalyser;

public sealed class ReplayHeaderInfo
{
    public uint Magic { get; init; }
    public uint Version { get; init; }
    public uint HeaderSize { get; init; }
    public string MapName { get; init; } = "";
    public byte SpawnerVersionMajor { get; init; }
    public byte SpawnerVersionMinor { get; init; }
    public byte SpawnerVersionRevision { get; init; }
    public byte SpawnerVersionPatch { get; init; }
    public string GameClientVersion { get; init; } = "";
    public uint GameMode { get; init; }
    public int UniqueIDCounter { get; init; }
    public int Seed { get; init; }
    public int RandomNext1 { get; init; }
    public int RandomNext2 { get; init; }
    public uint[] RandomizerTable { get; init; } = [];
    public uint SpawnIniSize { get; init; }
    public uint SpawnMapSize { get; init; }
    public uint RecordedGameSpeed { get; init; }
    public ulong RecordedUnixTime { get; init; }
    public uint TotalFrames { get; init; }
    public uint Flags { get; init; }
    public uint[] Reserved { get; init; } = [];

    public string SpawnerVersion =>
        $"{SpawnerVersionMajor}.{SpawnerVersionMinor}.{SpawnerVersionRevision}.{SpawnerVersionPatch}";

    public bool CleanShutdown => (Flags & (uint)ReplayHeaderFlags.CleanShutdown) != 0;

    public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeSeconds((long)RecordedUnixTime);

    /// <summary>Frames per second the recorded simulation ran at. Pins playback duration.</summary>
    public int SimulationFps => ReplayFormat.GetFpsFromGameSpeed((int)RecordedGameSpeed);

    public string GameModeName => GameMode switch
    {
        0 => "Campaign",
        3 => "LAN",
        4 => "Internet",
        5 => "Skirmish",
        _ => $"Unknown ({GameMode})",
    };

    /// <summary>Any reserved word carrying a value this build does not understand, if any.</summary>
    public bool HasUnknownReservedData => Reserved.Any(w => w != 0);
}

public readonly record struct Point2D(int X, int Y);
public readonly record struct Coord3D(int X, int Y, int Z);

public sealed class SideChannelEvent
{
    public int FrameNumber { get; init; }
    public SideChannelEventType Type { get; init; }
    public byte RawType { get; init; }
    public int House { get; init; }
    /// <summary>Colour scheme index for chat, beacon slot 0-2 for beacons, command byte for taunts.</summary>
    public int Aux { get; init; }
    public Coord3D Coord { get; init; }
    public string SenderName { get; init; } = "";
    public string Text { get; init; } = "";

    public string TypeName => Enum.IsDefined(Type) ? Type.ToString() : $"Unknown({RawType})";
}

/// <summary>
/// One frame's record. Blocks are present only when the matching flag is set; the writer omits
/// a block whose value has not changed since the last written frame.
/// </summary>
public sealed class FrameRecord
{
    public int FrameNumber;
    public uint Flags;
    public Point2D? TacticalPos;
    public uint[]? SelectionIds;
    public SideChannelEvent[]? SideChannel;
    public uint? GameCrc;
    public byte[]? Extension;

    /// <summary>Index of this frame's first event in <see cref="ReplayDocument.Events"/>.</summary>
    public int EventStart;
    public int EventCount;
}

/// <summary>
/// A recorded EventClass. The 111-byte payloads all live in one blob on the document; this is a
/// lightweight cursor over it, so a 100k-event replay costs one allocation rather than 100k.
/// </summary>
public readonly struct GameEvent(byte[] blob, int offset, int recordFrame)
{
    private readonly byte[] _blob = blob;
    private readonly int _offset = offset;

    /// <summary>The frame record this event was written under - i.e. the frame it executed on.</summary>
    public int RecordFrame { get; } = recordFrame;

    public EventType Type => (EventType)_blob[_offset];
    public bool IsExecuted => (_blob[_offset + 1] & 1) != 0;
    /// <summary>Sending house's array index, or -1 when the event carries no house.</summary>
    public sbyte HouseIndex => (sbyte)_blob[_offset + 2];
    /// <summary>The frame the event is scheduled to execute on, as stamped by the sender.</summary>
    public uint ScheduledFrame => BinaryPrimitives.ReadUInt32LittleEndian(_blob.AsSpan(_offset + 3, 4));

    public ReadOnlySpan<byte> Data => _blob.AsSpan(_offset + ReplayFormat.EventDataOffset, 104);

    public byte U8(int i) => _blob[_offset + ReplayFormat.EventDataOffset + i];
    public sbyte I8(int i) => (sbyte)U8(i);
    public ushort U16(int i) => BinaryPrimitives.ReadUInt16LittleEndian(Data[i..]);
    public short I16(int i) => BinaryPrimitives.ReadInt16LittleEndian(Data[i..]);
    public uint U32(int i) => BinaryPrimitives.ReadUInt32LittleEndian(Data[i..]);
    public int I32(int i) => BinaryPrimitives.ReadInt32LittleEndian(Data[i..]);

    /// <summary>TargetClass is { int32 ID; uint8 RTTI } - five bytes, packed.</summary>
    public TargetRef Target(int i) => new(I32(i), U8(i + 4));

    public CellRef Cell(int i) => new(I16(i), I16(i + 2));
}

/// <summary>
/// A TargetClass. RTTI 52 (Abstract) means ID is an object's Fetch_ID; RTTI 11 (Cell) packs the
/// cell as X + 1000*Y; RTTI 0 means no target.
/// </summary>
public readonly record struct TargetRef(int Id, byte Rtti)
{
    public AbstractType Type => (AbstractType)Rtti;
    public bool IsNone => Rtti == 0;
    public bool IsCell => Rtti == (byte)AbstractType.Cell;
    public CellRef AsCell => new((short)(Id % 1000), (short)(Id / 1000));

    public override string ToString()
    {
        if (IsNone) return "-";
        if (IsCell) return AsCell.ToString();
        if (Rtti == (byte)AbstractType.Abstract) return $"#{Id}";
        return $"{Type}#{Id}";
    }
}

public readonly record struct CellRef(short X, short Y)
{
    public override string ToString() => $"({X},{Y})";
}
