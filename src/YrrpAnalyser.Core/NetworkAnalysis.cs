namespace YrrpAnalyser;

public readonly record struct Sample(int Frame, double Value);

/// <summary>Everything the file says about one house's connection over the game.</summary>
public sealed class PlayerNetworkSeries
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";

    /// <summary>ResponseTime2: the peer's worst smoothed connection response, with the +1 tick removed.</summary>
    public List<Sample> RoundTripMs { get; } = [];

    /// <summary>ResponseTime2: the peer's requested latency level (1-9), before session-wide limits.</summary>
    public List<Sample> LatencyLevel { get; } = [];

    /// <summary>FRAMEINFO Delay: the MaxAhead this peer was scheduling its orders at.</summary>
    public List<Sample> MaxAhead { get; } = [];

    /// <summary>PROCESS_TIME: mean elapsed main-loop work per frame, already in milliseconds.</summary>
    public List<Sample> ProcessMs { get; } = [];

    /// <summary>TIMING: the frame rate the session master asked everyone to run at.</summary>
    public List<Sample> RequestedFps { get; } = [];

    /// <summary>TIMING: the FrameSendRate the session master imposed.</summary>
    public List<Sample> FrameSendRate { get; } = [];

    /// <summary>
    /// Simulation-frame gaps between consecutive recorded FRAMEINFO events from this peer.
    /// The replay records consumption frames, not packet arrival timestamps or time spent waiting.
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

/// <summary>An unusually large gap in recorded FRAMEINFO consumption frames; not a measured stall.</summary>
public sealed class FrameInfoGapInterval
{
    public int HouseIndex { get; init; }
    public string Name { get; init; } = "";
    public int StartFrame { get; init; }
    public int EndFrame { get; init; }
    public int Frames => EndFrame - StartFrame;
    public double SimulationSeconds { get; init; }
}

public sealed class NetworkAnalysis
{
    public List<PlayerNetworkSeries> Series { get; } = [];
    public List<FrameInfoGapInterval> LargeFrameInfoGaps { get; } = [];

    /// <summary>Houses whose events appear in the file but that spawn.ini does not account for.</summary>
    public List<int> UnknownHouses { get; } = [];

    /// <summary>The protocol the recording ran under, from spawn.ini.</summary>
    public int Protocol { get; private set; }
    public int ConfiguredFrameSendRate { get; private set; }

    // gamemd SystemTimerClass::operator() at 0x6C8C40 returns timeGetTime() >> 4.
    // PROCESS_TIME uses unshifted timeGetTime() and must never use this conversion.
    public const int SystemTickMilliseconds = 16;

    /// <summary>
    /// ProtocolZero stores Response_Time() + 1 in a signed byte. Zero is ignored by playback;
    /// negative values have overflowed and cannot be treated as a meaningful RTT.
    /// </summary>
    public static double? ResponseTime2Milliseconds(sbyte encodedTicks) =>
        encodedTicks > 0 ? (encodedTicks - 1) * (double)SystemTickMilliseconds : null;

    /// <summary>
    /// ProtocolZero's latency ladder, from ProtocolZero.LatencyLevel.cpp. The level a peer reports
    /// is the lowest whose threshold covers its response-tick count. The session applies the
    /// highest recent request, capped by MaxLatencyLevel, and does not lower an applied level.
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
                    if (ResponseTime2Milliseconds(ticks) is { } milliseconds)
                        s.RoundTripMs.Add(new Sample(frame, milliseconds));
                    s.LatencyLevel.Add(new Sample(frame, level));
                    break;
                }

                case EventType.ProcessTime:
                {
                    var s = SeriesFor(house);
                    s.ProcessMs.Add(new Sample(frame, e.U16(0)));
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

                // Legacy RESPONSE_TIME sets global MaxAhead from payload byte 6.
                // It is a scheduling command, not a connection response measurement.
            }
        }

        analysis.Series.Sort((a, b) => a.HouseIndex.CompareTo(b.HouseIndex));
        analysis.FindLargeFrameInfoGaps(doc);
        return analysis;
    }

    /// <summary>
    /// Highlight spacing outliers using the peer's median recorded gap. These are simulation
    /// intervals only: packet arrival times and wall-clock stall durations are absent.
    /// </summary>
    private void FindLargeFrameInfoGaps(ReplayDocument doc)
    {

        foreach (var s in Series)
        {
            if (s.FrameInfoGap.Count < 8) continue;

            var sorted = s.FrameInfoGap.Select(g => g.Value).OrderBy(v => v).ToArray();
            double median = sorted[sorted.Length / 2];
            // A display heuristic: three times the usual spacing, with a floor of one third
            // of a nominal game second at the speed recorded at the end of the interval.
            foreach (var gap in s.FrameInfoGap)
            {
                double threshold = Math.Max(median * 3, doc.GameSpeed.FpsAt(gap.Frame) / 3.0);
                if (gap.Value >= threshold)
                {
                    LargeFrameInfoGaps.Add(new FrameInfoGapInterval
                    {
                        HouseIndex = s.HouseIndex,
                        Name = s.Name,
                        StartFrame = (int)(gap.Frame - gap.Value),
                        EndFrame = gap.Frame,
                        SimulationSeconds = doc.GameSpeed.SecondsAt(gap.Frame)
                                            - doc.GameSpeed.SecondsAt((int)(gap.Frame - gap.Value)),
                    });
                }
            }
        }

        LargeFrameInfoGaps.Sort((a, b) => b.Frames.CompareTo(a.Frames));
    }

    /// <summary>
    /// What the format does and does not carry about the connection, shown next to the charts so
    /// nobody reads a missing number as a healthy one.
    /// </summary>
    public const string ProvenanceNote =
        "Samples are placed at the simulation frame where the replay recorded the event, which " +
        "can be later than its measurement. The time axis is nominal game time.\n\n" +
        "\u2022 Process time is mean elapsed main-loop work, already in milliseconds, normally " +
        "reported every 128 frames. It includes input, rendering and game logic; Queue_AI and " +
        "Sync_Delay run after the measurement. It is not an individual frame or pure CPU time.\n" +
        "\u2022 ResponseTime2 reports each peer's worst smoothed connection response. The chart " +
        "removes its +1 tick margin and uses the binary's 16 ms ticks. ACK servicing delays and " +
        "retries can contribute, so this is not a pure network ping. Zero/negative encoded values " +
        "are omitted from the response chart.\n" +
        "\u2022 Latency level is each peer's request. The session applies the highest recent request, " +
        "subject to its configured cap, and does not lower an already applied level.\n" +
        "\u2022 MaxAhead is the sender's FRAMEINFO scheduling delay, in simulation frames. TIMING " +
        "carries the requested session FPS and send interval; it does not measure achieved FPS.\n" +
        "\u2022 FRAMEINFO spacing and highlighted gaps measure simulation-frame distances between " +
        "recorded events. They cannot establish real-time stalls, packet loss or retransmission counts.\n\n" +
        "The recorder's own FRAMEINFO is written directly into outgoing packets, so it has no " +
        "FRAMEINFO MaxAhead or spacing samples. Legacy ResponseTime sets MaxAhead and is not an RTT sample.";
}
