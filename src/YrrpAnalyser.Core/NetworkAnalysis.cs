namespace YrrpAnalyser;

public readonly record struct Sample(int Frame, double Value);

/// <summary>Everything the file says about one house's connection over the game.</summary>
public sealed class PlayerNetworkSeries
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";

    /// <summary>ResponseTime2: measured round-trip time, in milliseconds.</summary>
    public List<Sample> RoundTripMs { get; } = [];

    /// <summary>ResponseTime2: the latency level that round-trip time maps to (1-9).</summary>
    public List<Sample> LatencyLevel { get; } = [];

    /// <summary>FRAMEINFO Delay: the MaxAhead this peer was scheduling its orders at.</summary>
    public List<Sample> MaxAhead { get; } = [];

    /// <summary>PROCESS_TIME: mean simulation cost per frame on this peer, in milliseconds.</summary>
    public List<Sample> ProcessMs { get; } = [];

    /// <summary>TIMING: the frame rate the session master asked everyone to run at.</summary>
    public List<Sample> RequestedFps { get; } = [];

    /// <summary>TIMING: the FrameSendRate the session master imposed.</summary>
    public List<Sample> FrameSendRate { get; } = [];

    /// <summary>
    /// Gaps between consecutive FRAMEINFO packets from this peer, in frames. A live game cannot
    /// advance past a peer's last delivered FRAMEINFO, so a gap here is a stall the recording
    /// machine actually sat through.
    /// </summary>
    public List<Sample> FrameInfoGap { get; } = [];

    public List<int> FrameInfoFrames { get; } = [];

    public int FrameInfoCount => FrameInfoFrames.Count;
    public bool HasFrameInfo => FrameInfoFrames.Count > 0;

    public double MedianRoundTripMs => Median(RoundTripMs);
    public double WorstRoundTripMs => RoundTripMs.Count > 0 ? RoundTripMs.Max(s => s.Value) : 0;
    public double MedianProcessMs => Median(ProcessMs);
    public double WorstProcessMs => ProcessMs.Count > 0 ? ProcessMs.Max(s => s.Value) : 0;
    public double MedianMaxAhead => Median(MaxAhead);
    public double WorstMaxAhead => MaxAhead.Count > 0 ? MaxAhead.Max(s => s.Value) : 0;
    public double WorstFrameInfoGap => FrameInfoGap.Count > 0 ? FrameInfoGap.Max(s => s.Value) : 0;

    private static double Median(List<Sample> samples)
    {
        if (samples.Count == 0) return 0;
        var values = samples.Select(s => s.Value).OrderBy(v => v).ToArray();
        int mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}

/// <summary>A run of frames where a peer's FRAMEINFO stopped arriving for longer than usual.</summary>
public sealed class StallEvent
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public int Frames => EndFrame - StartFrame;
    public double Seconds { get; init; }
}

public sealed class NetworkAnalysis
{
    public List<PlayerNetworkSeries> Series { get; } = [];
    public List<StallEvent> Stalls { get; } = [];

    /// <summary>Houses whose events appear in the file but that spawn.ini does not account for.</summary>
    public List<int> UnknownHouses { get; } = [];

    /// <summary>The protocol the recording ran under, from spawn.ini.</summary>
    public int Protocol { get; private set; }
    public int ConfiguredFrameSendRate { get; private set; }

    /// <summary>60 Hz system ticks to milliseconds. Every engine timing value is in these.</summary>
    public static double TicksToMs(double ticks) => ticks * (1000.0 / 60.0);

    /// <summary>
    /// ProtocolZero's latency ladder, from ProtocolZero.LatencyLevel.cpp. The level a peer reports
    /// is the lowest whose MaxAhead covers its measured round-trip time.
    /// </summary>
    public static readonly int[] LatencyLevelMaxAhead = [1, 4, 6, 12, 16, 20, 24, 28, 32, 36];

    public static string LatencyLevelName(int level) => level switch
    {
        0 => "Initial",
        1 => "Best",
        2 => "Super",
        3 => "Excellent",
        4 => "Very Good",
        5 or 6 => "Good",
        7 or 8 or 9 => "Default",
        _ => $"Level {level}",
    };

    public static NetworkAnalysis Build(ReplayDocument doc)
    {
        var analysis = new NetworkAnalysis
        {
            Protocol = doc.SpawnIni.GetInt("Settings", "Protocol", -1),
            ConfiguredFrameSendRate = doc.SpawnIni.GetInt("Settings", "FrameSendRate", 0),
        };

        var byHouse = new Dictionary<int, PlayerNetworkSeries>();
        var lastFrameInfoFrame = new Dictionary<int, int>();

        PlayerNetworkSeries SeriesFor(int house)
        {
            if (byHouse.TryGetValue(house, out var s)) return s;
            var player = doc.Roster.ForHouse(house);
            if (player is null && !analysis.UnknownHouses.Contains(house))
                analysis.UnknownHouses.Add(house);

            s = new PlayerNetworkSeries
            {
                HouseIndex = house,
                Name = player?.DisplayName ?? $"House {house}",
            };
            byHouse[house] = s;
            analysis.Series.Add(s);
            return s;
        }

        foreach (var e in doc.EnumerateEvents())
        {
            int house = e.HouseIndex;
            if (house < 0) continue;
            int frame = e.RecordFrame;

            switch (e.Type)
            {
                case EventType.ResponseTime2:
                {
                    var s = SeriesFor(house);
                    sbyte ticks = e.I8(0);
                    byte level = e.U8(1);
                    s.RoundTripMs.Add(new Sample(frame, TicksToMs(ticks)));
                    s.LatencyLevel.Add(new Sample(frame, level));
                    break;
                }

                case EventType.ProcessTime:
                {
                    var s = SeriesFor(house);
                    s.ProcessMs.Add(new Sample(frame, TicksToMs(e.U16(0))));
                    break;
                }

                case EventType.Timing:
                {
                    var s = SeriesFor(house);
                    s.RequestedFps.Add(new Sample(frame, e.U16(0)));
                    s.FrameSendRate.Add(new Sample(frame, e.U8(4)));
                    break;
                }

                case EventType.FrameInfo:
                {
                    var s = SeriesFor(house);
                    s.MaxAhead.Add(new Sample(frame, e.U8(6)));
                    s.FrameInfoFrames.Add(frame);
                    if (lastFrameInfoFrame.TryGetValue(house, out int previous))
                        s.FrameInfoGap.Add(new Sample(frame, frame - previous));
                    lastFrameInfoFrame[house] = frame;
                    break;
                }

                case EventType.ResponseTime:
                {
                    var s = SeriesFor(house);
                    s.RoundTripMs.Add(new Sample(frame, TicksToMs(e.U8(0))));
                    break;
                }
            }
        }

        analysis.Series.Sort((a, b) => a.HouseIndex.CompareTo(b.HouseIndex));
        analysis.DetectStalls(doc);
        return analysis;
    }

    /// <summary>
    /// A stall is a FRAMEINFO gap well above what this peer normally runs at. The expected
    /// spacing is FrameSendRate frames, but it moves with the latency level, so the threshold is
    /// taken from the peer's own median gap rather than from the configured rate.
    /// </summary>
    private void DetectStalls(ReplayDocument doc)
    {
        int fps = Math.Max(1, doc.Header.SimulationFps);

        foreach (var s in Series)
        {
            if (s.FrameInfoGap.Count < 8) continue;

            var sorted = s.FrameInfoGap.Select(g => g.Value).OrderBy(v => v).ToArray();
            double median = sorted[sorted.Length / 2];
            // Three times the normal spacing, and never less than a third of a second, so a peer
            // sending every frame does not produce a stall list thousands of rows long.
            double threshold = Math.Max(median * 3, fps / 3.0);

            int previousFrame = s.FrameInfoFrames[0];
            foreach (var gap in s.FrameInfoGap)
            {
                if (gap.Value >= threshold)
                {
                    Stalls.Add(new StallEvent
                    {
                        HouseIndex = s.HouseIndex,
                        Name = s.Name,
                        StartFrame = (int)(gap.Frame - gap.Value),
                        EndFrame = gap.Frame,
                        Seconds = gap.Value / fps,
                    });
                }
                previousFrame = gap.Frame;
            }
            _ = previousFrame;
        }

        Stalls.Sort((a, b) => b.Frames.CompareTo(a.Frames));
    }

    /// <summary>
    /// What the format does and does not carry about the connection, shown next to the charts so
    /// nobody reads a missing number as a healthy one.
    /// </summary>
    public const string ProvenanceNote =
        "Every figure here is read out of the game's own network events as they were recorded.\n\n" +
        "• Round trip and latency level come from the spawner's ResponseTime2 event, which each " +
        "peer emits about itself, so both sides of the connection are covered.\n" +
        "• Process time is PROCESS_TIME: the mean cost of simulating a frame on that peer's " +
        "machine, averaged over the preceding 128 frames. High values are a slow computer, not a slow link.\n" +
        "• MaxAhead is the Delay field of each remote peer's FRAMEINFO - how far ahead of the " +
        "current frame it was scheduling its orders. It rises as its connection degrades.\n" +
        "• Order gap is the spacing between a peer's FRAMEINFO packets. The simulation cannot " +
        "advance past the last one delivered, so a gap is a stall the recording machine sat through.\n\n" +
        "FRAMEINFO only ever arrives from remote peers: the recording machine writes its own " +
        "straight into the outgoing packet, so it never reaches the event queue and never reaches " +
        "the file. The recording player therefore has no MaxAhead or order-gap line.\n\n" +
        "Dropped packets and retransmissions are not in a replay. Both are counted below the event " +
        "queue, inside ConnectionClass, and nothing carries them into an event - so they cannot be " +
        "recovered from a recording made by this version. Order gap is the closest proxy the file " +
        "does contain.";
}
