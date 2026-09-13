using YrrpAnalyser.Map;

namespace YrrpAnalyser;

/// <summary>An object's position and state at one recorded snapshot. X and Y are in cells.</summary>
public readonly record struct Keyframe(int Frame, float X, float Y, byte Health, byte Mission, ObjectFlags Flags, byte Height);

/// <summary>A stretch of time an object was on the map with one owner and type.</summary>
public sealed record OwnershipSpan(int From, int To, int Owner, int TypeIndex, ObjectKind Kind);

/// <summary>Everything the recording says about one object, by its unique ID.</summary>
public sealed class TrackedObject
{
    public uint Id { get; init; }
    public List<OwnershipSpan> Spans { get; } = [];
    public List<Keyframe> Keys { get; } = [];
    public int FoundationWidth { get; set; } = 1;
    public int FoundationHeight { get; set; } = 1;
    public int FirstFrame => Spans.Count > 0 ? Spans[0].From : int.MaxValue;
    public int LastFrame => Spans.Count > 0 ? Spans[^1].To : int.MinValue;

    internal (int From, int Owner, int Type, ObjectKind Kind)? Open;
}

/// <summary>
/// Where a unit probably was, when the recording has no positions: it is sent from wherever it was last
/// believed to be towards each ordered destination, at a typical speed. Legs are in cells.
/// </summary>
public sealed class EstimatedUnit
{
    public uint Id { get; init; }
    public int Owner { get; init; }
    public List<(int Frame, double FromX, double FromY, double ToX, double ToY, int Arrive)> Legs { get; } = [];
    public int LastOrderFrame => Legs.Count > 0 ? Legs[^1].Frame : 0;
}

/// <summary>What the map shows for one object at one moment.</summary>
public readonly record struct ObjectState(
    uint Id, int Owner, ObjectKind? Kind, int TypeIndex, double X, double Y, double Health, ObjectFlags Flags,
    int Mission, int FoundationWidth, int FoundationHeight, double Height, bool Moving, bool Estimated, double Opacity);

public sealed record DeathEvent(int Frame, uint Id, double X, double Y, int Owner, ObjectKind Kind, int TypeIndex, int? Killer);

/// <summary>A MEGAMISSION: an order given to one object, from where it was (when known) to a cell.</summary>
public sealed record OrderEvent(int Frame, int House, uint Id, Mission Mission, double ToX, double ToY,
    double? FromX, double? FromY, bool IsAttack);

public sealed record PlacedBuilding(int Frame, int House, int TypeIndex, int X, int Y);
public sealed record BeaconMark(int From, int To, int House, double X, double Y, string Text);
public sealed record SuperweaponStrike(int Frame, int House, int TypeIndex, string Name, int X, int Y);

public enum MatchEventKind { Building, Destroyed, Superweapon, Defeat, Chat, Beacon, Fight, Alliance }

public sealed record MatchEvent(int Frame, int House, MatchEventKind Kind, string Text, double? X = null, double? Y = null);

/// <summary>The recording player's view centre, in cells.</summary>
public readonly record struct CameraSample(int Frame, double X, double Y);

/// <summary>
/// Everything the Match page draws: the map, where each object was and what happened to it, orders,
/// buildings, beacons, superweapons, the recording player's camera, and a feed of moments.
///
/// With object snapshots in the recording, positions are the game's own, every
/// <see cref="ReplayFormat.ObjectSnapshotIntervalFrames"/> frames. Without them - every recording made
/// before the recorder wrote them - units are estimated from their orders: nothing in the event stream
/// says where a unit is, only where it was told to go, so an estimated unit is drawn as one, fades when
/// it has not been ordered for a while, and never stands in for a real position.
/// </summary>
public sealed class MatchAnalysis
{
    /// <summary>Cells per second an estimated unit is assumed to cover: a medium tank on open ground.</summary>
    public const double EstimatedCellsPerSecond = 1.6;
    public const double EstimatedFadeStartSeconds = 60;
    public const double EstimatedFadeEndSeconds = 150;

    public MapInfo Map { get; private set; } = new();
    public bool HasRecordedPositions { get; private set; }
    public int SnapshotInterval { get; private set; } = ReplayFormat.ObjectSnapshotIntervalFrames;
    public int LastFrame { get; private set; }

    public Dictionary<uint, TrackedObject> Objects { get; } = [];
    public List<TrackedObject> ObjectList { get; } = [];
    public List<EstimatedUnit> Estimated { get; } = [];
    public List<DeathEvent> Deaths { get; } = [];
    public List<OrderEvent> Orders { get; } = [];
    public List<PlacedBuilding> Placements { get; } = [];
    public List<BeaconMark> Beacons { get; } = [];
    public List<SuperweaponStrike> Superweapons { get; } = [];
    public List<MatchEvent> Events { get; } = [];
    public List<CameraSample> Camera { get; } = [];

    /// <summary>Units and buildings lost, all houses together, per <see cref="IntensityBucketFrames"/>.</summary>
    public List<Sample> Intensity { get; } = [];
    public int IntensityBucketFrames { get; private set; } = 300;

    /// <summary>The frame each house was defeated on.</summary>
    public Dictionary<int, int> DefeatedAt { get; } = [];

    public static MatchAnalysis Build(ReplayDocument doc, StatisticsAnalysis stats, TypeNameResolver types)
    {
        var match = new MatchAnalysis
        {
            Map = MapInfo.Parse(doc.SpawnMapIni),
            HasRecordedPositions = doc.HasObjectSnapshots,
            LastFrame = Math.Max(1, doc.LastRecordedFrame),
        };
        foreach (var h in stats.Houses.Where(h => h.DefeatedAtFrame.HasValue))
            match.DefeatedAt[h.HouseIndex] = h.DefeatedAtFrame!.Value;

        if (match.HasRecordedPositions) match.ReadSnapshots(doc);
        match.ReadEvents(doc, types);
        if (!match.HasRecordedPositions) match.EstimateUnits(doc);
        match.ResolveOrderOrigins();
        match.ReadSideChannel(doc);
        match.ReadCamera(doc);
        match.BuildIntensity(doc, stats);
        match.AddDefeats(doc, stats);
        match.Events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
        return match;
    }

    // --- recorded positions -------------------------------------------------------------------

    private void ReadSnapshots(ReplayDocument doc)
    {
        TrackedObject Get(uint id)
        {
            if (!Objects.TryGetValue(id, out var o)) Objects[id] = o = new TrackedObject { Id = id };
            return o;
        }

        void Close(TrackedObject o, int frame)
        {
            if (o.Open is not { } open) return;
            o.Spans.Add(new OwnershipSpan(open.From, frame, open.Owner, open.Type, open.Kind));
            o.Open = null;
        }

        foreach (var frame in doc.Frames)
        {
            if (frame.Objects is not { } snapshot) continue;
            int f = frame.FrameNumber;

            foreach (var a in snapshot.Appeared)
            {
                var o = Get(a.Id);
                Close(o, f);
                o.Open = (f, a.Owner, a.TypeIndex, a.Kind);
                if (a.Kind == ObjectKind.Building)
                {
                    o.FoundationWidth = Math.Max(1, (int)a.FoundationWidth);
                    o.FoundationHeight = Math.Max(1, (int)a.FoundationHeight);
                }
            }

            foreach (var u in snapshot.Updated)
                Get(u.Id).Keys.Add(new Keyframe(f, (float)u.CellX, (float)u.CellY, u.Health, u.Mission, u.Flags, u.Height));

            foreach (var g in snapshot.Gone)
            {
                if (!Objects.TryGetValue(g.Id, out var o) || o.Open is not { } open) continue;
                Close(o, f);
                if (g.Reason == GoneReason.Destroyed)
                    Deaths.Add(new DeathEvent(f, g.Id, g.CellX, g.CellY, open.Owner, open.Kind, open.Type, g.Killer));
            }
        }

        foreach (var o in Objects.Values)
        {
            Close(o, int.MaxValue);
            if (o.Spans.Count > 0 && o.Keys.Count > 0) ObjectList.Add(o);
        }
        ObjectList.Sort((a, b) => a.FirstFrame.CompareTo(b.FirstFrame));
    }

    /// <summary>
    /// Every object on the map at a frame. A recorded object between two snapshots is eased towards
    /// the next one only over the last snapshot interval before it: the recorder writes an object only
    /// when it has moved, so a gap longer than that means it stood still first.
    /// </summary>
    public IEnumerable<ObjectState> StatesAt(int frame)
    {
        if (HasRecordedPositions)
        {
            foreach (var o in ObjectList)
            {
                if (o.FirstFrame > frame) break;
                if (o.LastFrame <= frame) continue;
                var span = SpanAt(o, frame);
                if (span is null) continue;

                int k = LastKeyAtOrBefore(o.Keys, frame);
                if (k < 0) continue;
                var key = o.Keys[k];
                double x = key.X, y = key.Y, height = key.Height;
                bool moving = false;
                if (k + 1 < o.Keys.Count && o.Keys[k + 1].Frame < span.To)
                {
                    var next = o.Keys[k + 1];
                    int start = Math.Max(key.Frame, next.Frame - SnapshotInterval);
                    if (frame >= start && next.Frame > start)
                    {
                        double t = (frame - start) / (double)(next.Frame - start);
                        x += (next.X - key.X) * t;
                        y += (next.Y - key.Y) * t;
                        height += (next.Height - key.Height) * t;
                        moving = next.X != key.X || next.Y != key.Y;
                    }
                }

                yield return new ObjectState(o.Id, span.Owner, span.Kind, span.TypeIndex, x, y, key.Health / 255.0,
                    key.Flags, key.Mission, o.FoundationWidth, o.FoundationHeight, height, moving, false, 1);
            }
            yield break;
        }

        foreach (var unit in Estimated)
        {
            if (unit.Legs.Count == 0 || unit.Legs[0].Frame > frame) continue;
            if (DefeatedAt.TryGetValue(unit.Owner, out int defeated) && defeated <= frame) continue;
            var (x, y, moving) = EstimatedPosition(unit, frame);
            double idle = SecondsBetween(unit.LastOrderFrame, frame);
            double opacity = idle <= EstimatedFadeStartSeconds ? 1
                : 1 - (idle - EstimatedFadeStartSeconds) / (EstimatedFadeEndSeconds - EstimatedFadeStartSeconds);
            if (opacity <= 0.02) continue;
            yield return new ObjectState(unit.Id, unit.Owner, null, -1, x, y, 1, ObjectFlags.None, -1, 1, 1, 0,
                moving, true, Math.Clamp(opacity, 0, 1));
        }
    }

    /// <summary>An object's recorded path over a window, for drawing where it has been.</summary>
    public List<(double X, double Y)> TrailOf(uint id, int from, int to)
    {
        var points = new List<(double X, double Y)>();
        if (!Objects.TryGetValue(id, out var o)) return points;
        int k = Math.Max(0, LastKeyAtOrBefore(o.Keys, from));
        for (; k < o.Keys.Count && o.Keys[k].Frame <= to; k++)
            points.Add((o.Keys[k].X, o.Keys[k].Y));
        return points;
    }

    public TrackedObject? Find(uint id) => Objects.GetValueOrDefault(id);

    private static OwnershipSpan? SpanAt(TrackedObject o, int frame)
    {
        foreach (var span in o.Spans)
            if (span.From <= frame && frame < span.To) return span;
        return null;
    }

    private static int LastKeyAtOrBefore(List<Keyframe> keys, int frame)
    {
        int lo = 0, hi = keys.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    private Func<int, double> _seconds = f => f / 60.0;
    private double SecondsBetween(int from, int to) => _seconds(to) - _seconds(from);

    // --- the event stream ---------------------------------------------------------------------

    private void ReadEvents(ReplayDocument doc, TypeNameResolver types)
    {
        _seconds = f => doc.GameSpeed.SecondsAt(f);
        var buildingIds = new HashSet<uint>();

        foreach (var e in doc.EnumerateEvents())
        {
            int house = e.HouseIndex;
            switch (e.Type)
            {
                case EventType.MegaMission or EventType.MegaMissionF:
                {
                    bool standard = e.Type == EventType.MegaMission;
                    if (standard && e.U8(22) != 0) break; // a planning-mode order, not given yet
                    var whom = e.Target(0);
                    var target = e.Target(standard ? 7 : 6);
                    var destination = e.Target(standard ? 12 : 11);
                    var cell = destination.IsCell ? destination : target.IsCell ? target : default;
                    if (!cell.IsCell || whom.IsNone || whom.IsCell) break;
                    var mission = (Mission)e.U8(5);
                    Orders.Add(new OrderEvent(e.RecordFrame, house, (uint)whom.Id, mission,
                        cell.AsCell.X + 0.5, cell.AsCell.Y + 0.5, null, null,
                        mission is Mission.Attack or Mission.AttackMove || target.IsCell && !destination.IsCell));
                    break;
                }

                case EventType.Place when e.I32(4) >= 0:
                {
                    var cell = e.Cell(12);
                    int type = e.I32(4);
                    Placements.Add(new PlacedBuilding(e.RecordFrame, house, type, cell.X, cell.Y));
                    Events.Add(new MatchEvent(e.RecordFrame, house, MatchEventKind.Building,
                        $"placed {Short(types.Describe(AbstractType.BuildingType, type))}", cell.X + 1, cell.Y + 1));
                    break;
                }

                case EventType.SpecialPlace:
                {
                    int id = e.I32(0);
                    var cell = e.Cell(4);
                    string name = Short(types.Describe(AbstractType.SuperWeaponType, id));
                    Superweapons.Add(new SuperweaponStrike(e.RecordFrame, house, id, name, cell.X, cell.Y));
                    Events.Add(new MatchEvent(e.RecordFrame, house, MatchEventKind.Superweapon, $"launched {name}",
                        cell.X + 0.5, cell.Y + 0.5));
                    break;
                }

                case EventType.Ally:
                    Events.Add(new MatchEvent(e.RecordFrame, house, MatchEventKind.Alliance,
                        $"changed alliance with {doc.Roster.HouseLabel(e.I32(0))}"));
                    break;

                // Only buildings are sold, repaired, powered or made primary - these IDs are not units.
                case EventType.Sell or EventType.Repair or EventType.PowerOn or EventType.PowerOff or EventType.Primary:
                    if (!e.Target(0).IsCell) buildingIds.Add((uint)e.Target(0).Id);
                    break;
            }
        }

        if (buildingIds.Count > 0) Orders.RemoveAll(o => buildingIds.Contains(o.Id));

        if (HasRecordedPositions)
        {
            foreach (var d in Deaths.Where(d => d.Kind == ObjectKind.Building))
            {
                var info = doc.Statistics?.Types.Get(AbstractType.BuildingType, d.TypeIndex);
                if (info is { Cost: < 100 }) continue; // walls and the like
                string killer = d.Killer is { } k ? $" by {doc.Roster.HouseLabel(k)}" : "";
                Events.Add(new MatchEvent(d.Frame, d.Owner, MatchEventKind.Destroyed,
                    $"lost {info?.DisplayName ?? $"building #{d.TypeIndex}"}{killer}", d.X, d.Y));
            }
        }
    }

    /// <summary>"Allied Construction Yard [GACNST]" is "Allied Construction Yard".</summary>
    private static string Short(string described)
    {
        int bracket = described.IndexOf(" [", StringComparison.Ordinal);
        return bracket > 0 ? described[..bracket] : described;
    }

    private void EstimateUnits(ReplayDocument doc)
    {
        // Units come out of the base, so a unit's first order starts it from its owner's start position.
        var home = new Dictionary<int, (double X, double Y)>();
        foreach (var p in doc.Roster.ByHouseIndex)
            if (Map.StartLocation(p.SpawnLocation) is { } cell)
                home[p.HouseIndex] = (cell.X + 0.5, cell.Y + 0.5);

        foreach (var group in Orders.GroupBy(o => o.Id))
        {
            var orders = group.OrderBy(o => o.Frame).ToList();
            var unit = new EstimatedUnit { Id = group.Key, Owner = orders[0].House };
            (double X, double Y) at = home.TryGetValue(unit.Owner, out var h) ? h : (orders[0].ToX, orders[0].ToY);
            foreach (var order in orders)
            {
                if (unit.Legs.Count > 0)
                {
                    var (x, y, _) = EstimatedPosition(unit, order.Frame);
                    at = (x, y);
                }
                double distance = Math.Sqrt(Math.Pow(order.ToX - at.X, 2) + Math.Pow(order.ToY - at.Y, 2));
                int fps = doc.GameSpeed.FpsAt(order.Frame);
                int travel = (int)(distance / EstimatedCellsPerSecond * fps);
                unit.Legs.Add((order.Frame, at.X, at.Y, order.ToX, order.ToY, order.Frame + Math.Max(1, travel)));
            }
            Estimated.Add(unit);
        }
    }

    private static (double X, double Y, bool Moving) EstimatedPosition(EstimatedUnit unit, int frame)
    {
        var leg = unit.Legs[0];
        foreach (var l in unit.Legs)
        {
            if (l.Frame > frame) break;
            leg = l;
        }
        if (frame >= leg.Arrive) return (leg.ToX, leg.ToY, false);
        double t = Math.Clamp((frame - leg.Frame) / (double)(leg.Arrive - leg.Frame), 0, 1);
        return (leg.FromX + (leg.ToX - leg.FromX) * t, leg.FromY + (leg.ToY - leg.FromY) * t, true);
    }

    /// <summary>Where each ordered object was when it got the order: recorded, estimated, or unknown.</summary>
    private void ResolveOrderOrigins()
    {
        var estimated = Estimated.ToDictionary(u => u.Id);
        for (int i = 0; i < Orders.Count; i++)
        {
            var o = Orders[i];
            (double X, double Y)? from = null;
            if (HasRecordedPositions && Objects.TryGetValue(o.Id, out var tracked))
            {
                int k = LastKeyAtOrBefore(tracked.Keys, o.Frame);
                if (k >= 0) from = (tracked.Keys[k].X, tracked.Keys[k].Y);
            }
            else if (estimated.TryGetValue(o.Id, out var unit))
            {
                int leg = unit.Legs.FindIndex(l => l.Frame == o.Frame);
                if (leg >= 0) from = (unit.Legs[leg].FromX, unit.Legs[leg].FromY);
            }
            if (from is { } f) Orders[i] = o with { FromX = f.X, FromY = f.Y };
        }
    }

    // --- beacons, chat, camera ----------------------------------------------------------------

    private void ReadSideChannel(ReplayDocument doc)
    {
        var open = new Dictionary<(int House, int Slot), int>();
        foreach (var e in doc.EnumerateSideChannel())
        {
            switch (e.Type)
            {
                case SideChannelEventType.BeaconPlace:
                {
                    var key = (e.House, e.Aux);
                    if (open.TryGetValue(key, out int previous))
                        Beacons[previous] = Beacons[previous] with { To = e.FrameNumber };
                    open[key] = Beacons.Count;
                    double x = e.Coord.X / 256.0, y = e.Coord.Y / 256.0;
                    Beacons.Add(new BeaconMark(e.FrameNumber, int.MaxValue, e.House, x, y, ""));
                    Events.Add(new MatchEvent(e.FrameNumber, e.House, MatchEventKind.Beacon, "placed a beacon", x, y));
                    break;
                }
                case SideChannelEventType.BeaconText when open.TryGetValue((e.House, e.Aux), out int index):
                    Beacons[index] = Beacons[index] with { Text = e.Text };
                    break;
                case SideChannelEventType.BeaconDelete when open.Remove((e.House, e.Aux), out int index):
                    Beacons[index] = Beacons[index] with { To = e.FrameNumber };
                    break;
                case SideChannelEventType.ChatMessage when e.Text.Length > 0:
                    Events.Add(new MatchEvent(e.FrameNumber, e.House, MatchEventKind.Chat,
                        $"{(e.SenderName.Length > 0 ? e.SenderName : doc.Roster.HouseLabel(e.House))}: {e.Text}"));
                    break;
            }
        }
    }

    /// <summary>
    /// TacticalClass's view position is the centre of the recording player's view in world pixels, where
    /// a cell is 60x30: X is (x - y) * 30 and Y is (x + y) * 15, so it turns back into cells directly.
    /// </summary>
    private void ReadCamera(ReplayDocument doc)
    {
        foreach (var frame in doc.Frames)
        {
            if (frame.TacticalPos is not { } p) continue;
            double a = p.X / 30.0, b = p.Y / 15.0;
            Camera.Add(new CameraSample(frame.FrameNumber, (a + b) / 2, (b - a) / 2));
        }
    }

    public CameraSample? CameraAt(int frame)
    {
        int lo = 0, hi = Camera.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Camera[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found >= 0 ? Camera[found] : null;
    }

    // --- how hard the fighting was, and when --------------------------------------------------

    private void BuildIntensity(ReplayDocument doc, StatisticsAnalysis stats)
    {
        int fps = Math.Max(1, doc.Header.SimulationFps);
        IntensityBucketFrames = fps * 5;
        var buckets = new double[LastFrame / IntensityBucketFrames + 1];

        if (HasRecordedPositions)
        {
            foreach (var d in Deaths)
                buckets[Math.Min(buckets.Length - 1, d.Frame / IntensityBucketFrames)]++;
        }
        else
        {
            foreach (var h in stats.Houses.Where(h => !h.IsSpectator))
                for (int i = 1; i < h.Losses.Count; i++)
                {
                    double lost = h.Losses[i].Value - h.Losses[i - 1].Value;
                    if (lost > 0) buckets[Math.Min(buckets.Length - 1, h.Losses[i].Frame / IntensityBucketFrames)] += lost;
                }
        }

        for (int i = 0; i < buckets.Length; i++)
            Intensity.Add(new Sample(i * IntensityBucketFrames, buckets[i]));

        // The feed's fights: the heaviest 20-second windows, well apart, with where they happened.
        const int windowBuckets = 4;
        var windows = new List<(int Start, double Lost)>();
        for (int i = 0; i + windowBuckets <= buckets.Length; i++)
            windows.Add((i, buckets.Skip(i).Take(windowBuckets).Sum()));

        double threshold = Math.Max(8, windows.Count > 0 ? windows.Max(w => w.Lost) * 0.35 : 0);
        var chosen = new List<int>();
        foreach (var (start, lost) in windows.OrderByDescending(w => w.Lost))
        {
            if (lost < threshold || chosen.Count >= 12) break;
            if (chosen.Any(c => Math.Abs(c - start) < windowBuckets * 2)) continue;
            chosen.Add(start);

            int from = start * IntensityBucketFrames, to = from + windowBuckets * IntensityBucketFrames;
            var where = Deaths.Where(d => d.Frame >= from && d.Frame < to).ToList();
            double? x = where.Count > 0 ? where.Average(d => d.X) : null;
            double? y = where.Count > 0 ? where.Average(d => d.Y) : null;
            Events.Add(new MatchEvent(from, -1, MatchEventKind.Fight, $"heavy fighting: {lost:0} lost in 20 seconds", x, y));
        }
    }

    private void AddDefeats(ReplayDocument doc, StatisticsAnalysis stats)
    {
        foreach (var h in stats.Houses.Where(h => h.DefeatedAtFrame.HasValue && !h.IsSpectator))
            Events.Add(new MatchEvent(h.DefeatedAtFrame!.Value, h.HouseIndex, MatchEventKind.Defeat, "was defeated"));
    }
}
