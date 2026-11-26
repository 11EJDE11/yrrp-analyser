using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace YrrpAnalyser;

/// <summary>The kinds of bytes a frame stream is made of, for <see cref="SizeBreakdown"/>.</summary>
public enum FramePart
{
    RecordHead,
    Hash,
    BlockFraming,
    Camera,
    Selection,
    Chat,
    Speed,
    Triggers,
    HouseStats,
    MoneyIn,
    Seal,
    DroppedHouses,
    SidebarTab,
    UnknownBlock,
    Event,
}

/// <summary>Receives every byte of the stream once, labelled with what it is.</summary>
public interface IFramePartSink
{
    void Add(FramePart part, ReadOnlySpan<byte> bytes, int count, byte eventType);
}

/// <summary>
/// Decodes the frame stream (ReplayFrameCodec.cpp). Each record is:
///
///   varint  (frameDelta &lt;&lt; 1) | hasHash     0 ends the stream
///   uint16  hash                              low 16 bits of the engine's state hash, when present
///   varint  eventCount
///   blocks  varint tag, varint length, bytes  until tag 0
///   events  eventCount × (uint8 type, uint8 house, zigzag varint frame delta, payload)
///
/// Events come out as 111-byte EventClass slots, zero beyond what the stream carries, so the rest
/// of the analyser reads them the way the game holds them.
/// </summary>
public static class FrameStream
{
    /// <summary>Inflates the stream at the file's position. A recording that died is cut short; what came before stands.</summary>
    public static byte[] Inflate(Stream file, out string? cutShort)
    {
        cutShort = null;
        using var inflate = new DeflateStream(file, CompressionMode.Decompress, leaveOpen: true);
        var output = new MemoryStream();
        var buffer = new byte[1 << 16];
        try
        {
            int n;
            while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, n);
        }
        catch (InvalidDataException ex)
        {
            cutShort = ex.Message.Trim();
        }
        return output.ToArray();
    }

    public sealed class Result
    {
        public List<FrameRecord> Frames { get; } = [];
        public byte[] EventBlob { get; set; } = [];
        public bool SawEndOfStream { get; set; }
        public bool Truncated { get; set; }
        public List<string> Warnings { get; } = [];
    }

    public static Result Parse(byte[] s, IFramePartSink? sink = null)
    {
        var result = new Result();
        var events = new MemoryStream();
        var cursor = new Cursor(s);
        int lastFrame = -1;
        int camX = 0, camY = 0;
        var slot = new byte[ReplayFormat.EventSize];

        while (true)
        {
            if (cursor.AtEnd)
            {
                // The stream ran out without its end marker: a recording the game did not close.
                break;
            }

            int start = cursor.Position;
            if (!cursor.Varint(out uint head)) { result.Truncated = true; break; }

            if (head == 0)
            {
                sink?.Add(FramePart.RecordHead, s.AsSpan(start, cursor.Position - start), 1, 0);
                result.SawEndOfStream = true;
                break;
            }

            uint delta = head >> 1;
            if (delta == 0 || delta > 0x3FFFFFFF)
            {
                result.Warnings.Add($"A frame record after frame {lastFrame} has an invalid frame step; stopped reading here.");
                break;
            }

            var record = new FrameRecord { FrameNumber = lastFrame + (int)delta };
            int headEnd = cursor.Position;
            if ((head & 1) != 0)
            {
                if (!cursor.U16(out ushort crc)) { result.Truncated = true; break; }
                record.GameCrc = crc;
                sink?.Add(FramePart.Hash, s.AsSpan(headEnd, 2), 1, 0);
            }

            int countStart = cursor.Position;
            if (!cursor.Varint(out uint eventCount)) { result.Truncated = true; break; }
            if (eventCount > ReplayFormat.MaxEventsPerFrame)
            {
                result.Warnings.Add($"Frame {record.FrameNumber} claims {eventCount} events; stopped reading here.");
                break;
            }
            sink?.Add(FramePart.RecordHead, s.AsSpan(start, headEnd - start), 1, 0);
            sink?.Add(FramePart.RecordHead, s.AsSpan(countStart, cursor.Position - countStart), 0, 0);

            string? problem = null;
            bool cut = false;
            var seals = new List<SealRecord>();
            var unknown = new List<uint>();

            while (true)
            {
                int tagStart = cursor.Position;
                if (!cursor.Varint(out uint tag)) { cut = true; break; }
                if (tag == (uint)FrameBlockTag.End)
                {
                    sink?.Add(FramePart.BlockFraming, s.AsSpan(tagStart, cursor.Position - tagStart), 0, 0);
                    break;
                }

                if (!cursor.Varint(out uint length)) { cut = true; break; }
                if (length > ReplayFormat.MaxFrameBlockBytes) { problem = $"a {length}-byte block"; break; }
                sink?.Add(FramePart.BlockFraming, s.AsSpan(tagStart, cursor.Position - tagStart), 0, 0);

                if (!cursor.Bytes((int)length, out var block)) { cut = true; break; }
                var b = new Cursor(s, cursor.Position - (int)length, (int)length);

                FramePart part;
                switch ((FrameBlockTag)tag)
                {
                    case FrameBlockTag.TacticalPos:
                        part = FramePart.Camera;
                        if (!b.ZigZag(out int dx) || !b.ZigZag(out int dy)) { problem = "camera"; break; }
                        camX += dx; camY += dy;
                        record.TacticalPos = new Point2D(camX, camY);
                        break;

                    case FrameBlockTag.Selection:
                        part = FramePart.Selection;
                        record.SelectionIds = b.IdList(ReplayFormat.MaxSelectionCount, allowEmpty: true);
                        if (record.SelectionIds is null) problem = "selection";
                        break;

                    case FrameBlockTag.SideChannel:
                        part = FramePart.Chat;
                        record.SideChannel = ReadSideChannel(b, record.FrameNumber);
                        if (record.SideChannel is null) problem = "chat and beacons";
                        break;

                    case FrameBlockTag.GameSpeed:
                        part = FramePart.Speed;
                        if (!b.U8(out byte speed) || speed > ReplayFormat.MaxGameSpeedIndex) { problem = "game speed"; break; }
                        record.GameSpeed = speed;
                        break;

                    case FrameBlockTag.SelectionTriggers:
                        part = FramePart.Triggers;
                        record.SelectionTriggerIds = b.IdList(ReplayFormat.MaxSelectionTriggersPerFrame, allowEmpty: false);
                        if (record.SelectionTriggerIds is null) problem = "selection triggers";
                        break;

                    case FrameBlockTag.HouseStats:
                        part = FramePart.HouseStats;
                        record.HouseStats = ReadHouseStats(b);
                        if (record.HouseStats is null) problem = "house statistics";
                        break;

                    case FrameBlockTag.MoneyIn:
                        part = FramePart.MoneyIn;
                        record.MoneyIn = ReadMoneyIn(b);
                        if (record.MoneyIn is null) problem = "payments";
                        break;

                    case FrameBlockTag.Seal:
                        part = FramePart.Seal;
                        if (length != ReplayFormat.SealRecordSize) { problem = "seal"; break; }
                        seals.Add(new SealRecord(BinaryPrimitives.ReadInt32LittleEndian(block),
                            block.Slice(4, ReplayFormat.SealHashBytes).ToArray()));
                        break;

                    case FrameBlockTag.DroppedHouses:
                        part = FramePart.DroppedHouses;
                        record.DroppedHouses = ReadDroppedHouses(b);
                        if (record.DroppedHouses is null) problem = "dropped houses";
                        break;

                    case FrameBlockTag.SidebarTab:
                        part = FramePart.SidebarTab;
                        if (!b.U8(out byte tab) || tab >= ReplayFormat.SidebarTabCount) { problem = "sidebar tab"; break; }
                        record.SidebarTab = tab;
                        break;

                    default:
                        part = FramePart.UnknownBlock;
                        unknown.Add(tag);
                        break;
                }

                sink?.Add(part, block, 1, 0);
                if (problem is not null) break;
            }

            if (cut) { result.Truncated = true; break; }
            if (problem is not null)
            {
                result.Warnings.Add($"Frame {record.FrameNumber} has a malformed {problem} block; stopped reading here.");
                break;
            }

            if (seals.Count > 0) record.Seals = [.. seals];
            if (unknown.Count > 0) record.UnknownBlocks = [.. unknown];

            record.EventStart = (int)(events.Length / ReplayFormat.EventSize);
            for (int i = 0; i < eventCount; i++)
            {
                int eventStart = cursor.Position;
                if (!ReadEvent(ref cursor, record.FrameNumber, slot)) { cut = true; record.EventCount = i; break; }
                events.Write(slot);
                sink?.Add(FramePart.Event, s.AsSpan(eventStart, cursor.Position - eventStart), 1, slot[0]);
                record.EventCount = i + 1;
            }

            result.Frames.Add(record);
            lastFrame = record.FrameNumber;
            if (cut) { result.Truncated = true; break; }
        }

        result.EventBlob = events.ToArray();
        return result;
    }

    private static bool ReadEvent(ref Cursor c, int recordFrame, byte[] slot)
    {
        Array.Clear(slot);
        if (!c.U8(out byte type) || !c.U8(out byte house) || !c.ZigZag(out int frameDelta))
            return false;

        slot[0] = type;
        slot[2] = house;
        BinaryPrimitives.WriteInt32LittleEndian(slot.AsSpan(3), recordFrame + frameDelta);

        if (type == (byte)EventType.FrameInfo)
        {
            if (!c.Bytes(ReplayFormat.FrameInfoStoredBytes, out var stored)) return false;
            slot[11] = stored[0];
            slot[12] = stored[1];
            slot[13] = stored[2];
            slot[7] = stored[3];   // the FrameInfoCheck, where the CRC was
            return true;
        }

        int length = ReplayFormat.EventPayloadLength(type);
        if (length < 0)
        {
            if (!c.Varint(out uint stored) || stored > ReplayFormat.EventDataLength) return false;
            length = (int)stored;
        }

        if (!c.Bytes(length, out var payload)) return false;
        payload.CopyTo(slot.AsSpan(ReplayFormat.EventDataOffset));
        return true;
    }

    private static SideChannelEvent[]? ReadSideChannel(Cursor c, int frame)
    {
        if (!c.Varint(out uint count) || count > ReplayFormat.SideChannelMaxEventsPerFrame) return null;
        var list = new SideChannelEvent[count];
        for (int i = 0; i < count; i++)
        {
            if (!c.U8(out byte type) || !c.ZigZag(out int house) || !c.ZigZag(out int aux)
                || !c.ZigZag(out int x) || !c.ZigZag(out int y) || !c.ZigZag(out int z)
                || !c.Utf8(out string name) || !c.Utf8(out string text))
                return null;

            byte[]? salt = null;
            byte label = 0, recipients = 0;
            if (type == (byte)SideChannelEventType.ChatMessage)
            {
                // Bit 0 a salt, bit 1 the label and recipients.
                if (!c.U8(out byte flags) || (flags & ~3) != 0) return null;
                if ((flags & 1) != 0)
                {
                    if (!c.Bytes(ReplayFormat.ChatSaltBytes, out var s)) return null;
                    salt = s.ToArray();
                }
                if ((flags & 2) != 0 && (!c.U8(out label) || !c.U8(out recipients))) return null;
            }

            list[i] = new SideChannelEvent
            {
                FrameNumber = frame,
                RawType = type,
                Type = (SideChannelEventType)type,
                House = house,
                Aux = aux,
                Coord = new Coord3D(x, y, z),
                SenderName = name,
                Text = text,
                Salt = salt,
                Label = label <= (byte)ChatLabel.Private ? (ChatLabel)label : ChatLabel.Unknown,
                Recipients = recipients,
            };
        }
        return list;
    }

    private static HouseStatsSample[]? ReadHouseStats(Cursor c)
    {
        if (!c.Varint(out uint count) || count == 0 || count > ReplayFormat.MaxHouseStatsPerFrame) return null;
        var samples = new HouseStatsSample[count];
        for (int i = 0; i < count; i++)
        {
            if (!c.Bytes(ReplayFormat.HouseStatsSampleSize, out var r)) return null;
            var f = new int[21];
            for (int k = 0; k < f.Length; k++)
                f[k] = BinaryPrimitives.ReadInt32LittleEndian(r[(k * 4)..]);
            samples[i] = new HouseStatsSample(
                f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10],
                f[11], f[12], f[13], f[14], f[15], f[16], f[17], f[18], f[19],
                (HouseStatsFlags)(uint)f[20]);
        }
        return samples;
    }

    private static MoneyIn[]? ReadMoneyIn(Cursor c)
    {
        if (!c.Varint(out uint count) || count == 0 || count > ReplayFormat.MaxMoneyInPerFrame) return null;
        var payments = new MoneyIn[count];
        for (int i = 0; i < count; i++)
        {
            if (!c.Bytes(ReplayFormat.MoneyInRecordSize, out var m)) return null;
            payments[i] = new MoneyIn(m[0], BinaryPrimitives.ReadUInt32LittleEndian(m[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(m[8..]));
        }
        return payments;
    }

    private static DroppedHouse[]? ReadDroppedHouses(Cursor c)
    {
        if (!c.Varint(out uint count) || count == 0 || count > ReplayFormat.MaxDroppedHousesPerFrame) return null;
        var drops = new DroppedHouse[count];
        for (int i = 0; i < count; i++)
        {
            if (!c.Varint(out uint house) || !c.Varint(out uint frame)
                || house > int.MaxValue || frame > int.MaxValue) return null;
            drops[i] = new DroppedHouse((int)house, (int)frame);
        }
        return drops;
    }

    /// <summary>Bounds-checked reads over a span of the stream.</summary>
    private struct Cursor(byte[] data, int start, int length)
    {
        private readonly byte[] _data = data;
        private readonly int _end = start + length;
        public int Position { get; private set; } = start;

        public Cursor(byte[] data) : this(data, 0, data.Length) { }

        public readonly bool AtEnd => Position >= _end;

        public bool Bytes(int count, out ReadOnlySpan<byte> bytes)
        {
            if (count < 0 || count > _end - Position) { bytes = default; return false; }
            bytes = _data.AsSpan(Position, count);
            Position += count;
            return true;
        }

        public bool U8(out byte value)
        {
            if (Position >= _end) { value = 0; return false; }
            value = _data[Position++];
            return true;
        }

        public bool U16(out ushort value)
        {
            if (!Bytes(2, out var b)) { value = 0; return false; }
            value = BinaryPrimitives.ReadUInt16LittleEndian(b);
            return true;
        }

        public bool Varint(out uint value)
        {
            value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (!U8(out byte b)) return false;
                value |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        public bool ZigZag(out int value)
        {
            if (!Varint(out uint raw)) { value = 0; return false; }
            value = (int)(raw >> 1) ^ -(int)(raw & 1);
            return true;
        }

        public bool Utf8(out string value)
        {
            value = "";
            if (!Varint(out uint length) || length > ReplayFormat.SideChannelTextLength * 3) return false;
            if (!Bytes((int)length, out var b)) return false;
            value = Encoding.UTF8.GetString(b);
            return true;
        }

        public uint[]? IdList(int max, bool allowEmpty)
        {
            if (!Varint(out uint count) || count > max || (!allowEmpty && count == 0)) return null;
            var ids = new uint[count];
            for (int i = 0; i < count; i++)
                if (!Varint(out ids[i])) return null;
            return ids;
        }
    }
}
