namespace YrrpAnalyser;

public sealed class PlayerActivity
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Commands per minute, one sample per bucket. See <see cref="TotalCommands"/>.</summary>
    public List<Sample> Apm { get; } = [];

    /// <summary>
    /// Every player-issued event. A single click on a group of units produces one MegaMission
    /// per unit, so this counts army size as much as it counts effort.
    /// </summary>
    public int TotalOrders { get; set; }

    /// <summary>
    /// Orders with same-frame group moves collapsed to one, which is much closer to what the
    /// player actually did with the mouse. This is what the per-minute rate is taken from.
    /// </summary>
    public int TotalCommands { get; set; }

    public int TotalEvents { get; set; }
    public Dictionary<EventType, int> ByType { get; } = [];

    /// <summary>Production and placement orders in the order they were issued.</summary>
    public List<BuildOrderEntry> BuildOrder { get; } = [];

    public double AverageApm { get; set; }
    public double PeakApm { get; set; }

    /// <summary>Last frame this house issued a player action - when it effectively stopped playing.</summary>
    public int LastActionFrame { get; set; } = -1;

    public double OrdersPerCommand => TotalCommands > 0 ? TotalOrders / (double)TotalCommands : 0;
}

public sealed class BuildOrderEntry
{
    public int Frame { get; init; }
    public EventType Type { get; init; }
    public string What { get; init; } = "";
    public string Where { get; init; } = "";
}

/// <summary>
/// Per-player activity, and the view-side series the recording player's own screen produced.
/// Everything here is derived from the event stream and the per-frame capture; nothing is guessed.
/// </summary>
public sealed class ActivityAnalysis
{
    public List<PlayerActivity> Players { get; } = [];

    /// <summary>Distance the recording player's camera moved, per bucket, in cells.</summary>
    public List<Sample> CameraMovement { get; } = [];

    /// <summary>How many objects the recording player had selected, sampled per frame record.</summary>
    public List<Sample> SelectionSize { get; } = [];

    /// <summary>Events recorded per bucket, all houses together.</summary>
    public List<Sample> EventDensity { get; } = [];

    public Dictionary<EventType, int> EventTotals { get; } = [];
    public int BucketFrames { get; private set; }

    public static ActivityAnalysis Build(ReplayDocument doc, EventDescriber describer)
    {
        var analysis = new ActivityAnalysis();
        int fps = Math.Max(1, doc.Header.SimulationFps);

        // One bucket per 15 seconds of simulated time: fine enough to show a push, coarse enough
        // that a long game does not turn into noise.
        int bucketFrames = Math.Max(1, fps * 15);
        analysis.BucketFrames = bucketFrames;

        int lastFrame = Math.Max(doc.LastRecordedFrame, 1);
        int bucketCount = lastFrame / bucketFrames + 1;

        var byHouse = new Dictionary<int, PlayerActivity>();
        var actionBuckets = new Dictionary<int, int[]>();
        var densityBuckets = new int[bucketCount];

        // One click on a group of selected units emits one MegaMission per unit, all on the same
        // frame with the same mission and destination. Counting those as one command is what
        // makes the per-minute rate a measure of the player rather than of their army size.
        var commandKeys = new HashSet<(int Frame, int House, EventType Type, int Mission, int DestId, byte DestRtti)>();

        PlayerActivity ActivityFor(int house)
        {
            if (byHouse.TryGetValue(house, out var a)) return a;
            a = new PlayerActivity
            {
                HouseIndex = house,
                Name = doc.Roster.ForHouse(house)?.DisplayName ?? $"House {house}",
            };
            byHouse[house] = a;
            actionBuckets[house] = new int[bucketCount];
            analysis.Players.Add(a);
            return a;
        }

        foreach (var e in doc.EnumerateEvents())
        {
            analysis.EventTotals[e.Type] = analysis.EventTotals.GetValueOrDefault(e.Type) + 1;

            int bucket = Math.Min(bucketCount - 1, e.RecordFrame / bucketFrames);
            densityBuckets[bucket]++;

            int house = e.HouseIndex;
            if (house < 0) continue;

            var activity = ActivityFor(house);
            activity.TotalEvents++;
            activity.ByType[e.Type] = activity.ByType.GetValueOrDefault(e.Type) + 1;

            if (!EventTypes.IsPlayerAction(e.Type)) continue;

            activity.TotalOrders++;
            activity.LastActionFrame = e.RecordFrame;

            bool isGroupOrder = e.Type is EventType.MegaMission or EventType.MegaMissionF;
            var destination = isGroupOrder
                ? e.Type == EventType.MegaMission ? e.Target(12) : e.Target(11)
                : default;
            int mission = isGroupOrder ? e.U8(5) : 0;

            var key = (e.RecordFrame, house, e.Type, mission, destination.Id, destination.Rtti);
            if (!isGroupOrder || commandKeys.Add(key))
            {
                activity.TotalCommands++;
                actionBuckets[house][bucket]++;
            }

            if (e.Type is EventType.Produce or EventType.Place)
            {
                activity.BuildOrder.Add(new BuildOrderEntry
                {
                    Frame = e.RecordFrame,
                    Type = e.Type,
                    What = describer.Describe(e),
                    Where = e.Type == EventType.Place ? e.Cell(12).ToString() : "",
                });
            }
        }

        double bucketMinutes = bucketFrames / (double)fps / 60.0;

        foreach (var activity in analysis.Players)
        {
            var buckets = actionBuckets[activity.HouseIndex];
            for (int i = 0; i < buckets.Length; i++)
            {
                double apm = buckets[i] / bucketMinutes;
                activity.Apm.Add(new Sample(i * bucketFrames, apm));
                if (apm > activity.PeakApm) activity.PeakApm = apm;
            }

            double totalMinutes = lastFrame / (double)fps / 60.0;
            activity.AverageApm = totalMinutes > 0 ? activity.TotalCommands / totalMinutes : 0;
        }

        for (int i = 0; i < densityBuckets.Length; i++)
            analysis.EventDensity.Add(new Sample(i * bucketFrames, densityBuckets[i]));

        analysis.BuildViewSeries(doc, bucketFrames, bucketCount);
        analysis.Players.Sort((a, b) => a.HouseIndex.CompareTo(b.HouseIndex));
        return analysis;
    }

    /// <summary>
    /// Camera and selection describe the recording player's own screen, not the simulation, and
    /// only exist for whoever made the file.
    /// </summary>
    private void BuildViewSeries(ReplayDocument doc, int bucketFrames, int bucketCount)
    {
        var cameraBuckets = new double[bucketCount];
        Point2D? previous = null;

        foreach (var frame in doc.Frames)
        {
            if (frame.SelectionIds is not null)
                SelectionSize.Add(new Sample(frame.FrameNumber, frame.SelectionIds.Length));

            if (frame.TacticalPos is not { } position) continue;

            if (previous is { } last)
            {
                double dx = position.X - last.X;
                double dy = position.Y - last.Y;
                // TacticalCoord is in leptons; 256 to a cell.
                double cells = Math.Sqrt(dx * dx + dy * dy) / 256.0;
                int bucket = Math.Min(bucketCount - 1, frame.FrameNumber / bucketFrames);
                cameraBuckets[bucket] += cells;
            }
            previous = position;
        }

        for (int i = 0; i < cameraBuckets.Length; i++)
            CameraMovement.Add(new Sample(i * bucketFrames, cameraBuckets[i]));
    }
}
