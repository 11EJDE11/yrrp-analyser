using System.Globalization;
using System.Text;
using System.Text.Json;

namespace YrrpAnalyser;

public static class Exporters
{
    public static void WriteEventsCsv(string path, ReplayDocument doc, EventDescriber describer,
        bool includeTiming)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,ScheduledFrame,HouseIndex,Player,Category,Event,Detail");

        foreach (var e in doc.EnumerateEvents())
        {
            if (!includeTiming && EventTypes.IsTiming(e.Type)) continue;
            writer.WriteLine(string.Join(',',
                e.RecordFrame,
                Csv(doc.TimeLabel(e.RecordFrame)),
                e.ScheduledFrame,
                e.HouseIndex,
                Csv(doc.Roster.HouseLabel(e.HouseIndex)),
                Csv(EventDescriber.Category(e.Type)),
                Csv(EventTypes.Name(e.Type)),
                Csv(describer.Describe(e))));
        }
    }

    public static void WriteChatCsv(string path, ReplayDocument doc)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,Type,HouseIndex,Player,Aux,X,Y,Z,Sender,Text");

        foreach (var e in doc.EnumerateSideChannel())
        {
            writer.WriteLine(string.Join(',',
                e.FrameNumber,
                Csv(doc.TimeLabel(e.FrameNumber)),
                Csv(e.TypeName),
                e.House,
                Csv(doc.Roster.HouseLabel(e.House)),
                e.Aux,
                e.Coord.X, e.Coord.Y, e.Coord.Z,
                Csv(e.SenderName),
                Csv(e.Text)));
        }
    }

    public static void WriteNetworkCsv(string path, ReplayDocument doc, NetworkAnalysis network)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,HouseIndex,Player,Metric,Value");

        void Dump(PlayerNetworkSeries s, string metric, List<Sample> samples)
        {
            foreach (var sample in samples)
            {
                writer.WriteLine(string.Join(',',
                    sample.Frame,
                    Csv(doc.TimeLabel(sample.Frame)),
                    s.HouseIndex,
                    Csv(s.Name),
                    metric,
                    sample.Value.ToString("0.###", CultureInfo.InvariantCulture)));
            }
        }

        foreach (var s in network.Series)
        {
            Dump(s, "RoundTripMs", s.RoundTripMs);
            Dump(s, "LatencyLevel", s.LatencyLevel);
            Dump(s, "MaxAhead", s.MaxAhead);
            Dump(s, "ProcessMs", s.ProcessMs);
            Dump(s, "FrameInfoGapFrames", s.FrameInfoGap);
            Dump(s, "RequestedFps", s.RequestedFps);
            Dump(s, "FrameSendRate", s.FrameSendRate);
        }
    }

    public static void WriteFrameCrcCsv(string path, ReplayDocument doc)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,GameCRC,ObjectCount,NextUniqueID,GameSpeed,Events,Flags,RandomNext1,RandomNext2,SelectionTriggerIDs");
        foreach (var f in doc.Frames)
        {
            writer.WriteLine(string.Join(',',
                f.FrameNumber,
                Csv(doc.TimeLabel(f.FrameNumber)),
                f.GameCrc is { } crc ? crc.ToString("X8") : "",
                f.Census is { } census ? census.AbstractCount.ToString() : "",
                f.Census is { } ids ? ids.ScenarioUniqueId.ToString() : "",
                f.GameSpeed is { } speed ? speed.ToString() : "",
                f.EventCount,
                $"0x{f.Flags:X2}",
                f.RandomState is { } rng1 ? rng1.Next1.ToString() : "",
                f.RandomState is { } rng2 ? rng2.Next2.ToString() : "",
                Csv(f.SelectionTriggerIds is { } triggers ? string.Join(" ", triggers) : "")));
        }
    }

    public static void WriteHouseStatsCsv(string path, ReplayDocument doc)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,HouseIndex,Player,Credits,StoredOreValue,CreditsSpent,HarvestedCredits," +
                         "PowerOutput,PowerDrain,Units,Infantry,Aircraft,Buildings,ArmyValue,BuildingValue," +
                         "UnitsKilled,BuildingsKilled,UnitsLost,BuildingsLost,UnitsBuilt,BuildingsBuilt,Score,Flags");

        foreach (var f in doc.Frames)
        {
            if (f.HouseStats is null) continue;
            foreach (var s in f.HouseStats)
            {
                writer.WriteLine(string.Join(',',
                    f.FrameNumber,
                    Csv(doc.TimeLabel(f.FrameNumber)),
                    s.HouseIndex,
                    Csv(StatisticsAnalysis.HouseName(doc, s.HouseIndex)),
                    s.Credits, s.StoredOreValue, s.CreditsSpent, s.HarvestedCredits,
                    s.PowerOutput, s.PowerDrain, s.Units, s.Infantry, s.Aircraft, s.Buildings,
                    s.ArmyValue, s.BuildingValue, s.UnitsKilled, s.BuildingsKilled, s.UnitsLost, s.BuildingsLost,
                    s.UnitsBuilt, s.BuildingsBuilt, s.Score,
                    Csv(s.Flags.ToString())));
            }
        }
    }

    /// <summary>Every recorded payment with its resolved caller and what the income table makes of it.</summary>
    public static void WriteMoneyInCsv(string path, ReplayDocument doc, IncomeClassifier? classifier = null)
    {
        classifier ??= IncomeClassifier.Default;
        var modules = (IReadOnlyList<ModuleInfo>?)doc.Statistics?.Modules ?? [];

        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,HouseIndex,Player,Caller,Module,Offset,Source,Description,Amount");
        foreach (var f in doc.Frames)
        {
            if (f.MoneyIn is null) continue;
            foreach (var m in f.MoneyIn)
            {
                var (source, caller, rule) = classifier.Classify(m.Caller, modules);
                writer.WriteLine(string.Join(',',
                    f.FrameNumber,
                    Csv(doc.TimeLabel(f.FrameNumber)),
                    m.House,
                    Csv(StatisticsAnalysis.HouseName(doc, m.House)),
                    $"0x{m.Caller:X8}",
                    Csv(caller.Module),
                    $"0x{caller.Offset:X}",
                    source,
                    Csv(rule?.Description ?? ""),
                    m.Amount));
            }
        }
    }

    /// <summary>A count array keyed by type ID, for the JSON summary.</summary>
    private static Dictionary<string, int> NamedCounts(ReplayStatistics stats, HouseSummary house, StatisticsArray which)
    {
        var kind = HouseSummary.KindOf(which);
        var counts = house.Array(which);
        var named = new Dictionary<string, int>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0) continue;
            string key = stats.Types.Get(kind, i)?.Id ?? $"#{i}";
            named[key] = named.GetValueOrDefault(key) + counts[i];
        }
        return named;
    }

    private static object? StatisticsSummary(ReplayDocument doc)
    {
        var analysis = StatisticsAnalysis.Build(doc);
        var stats = doc.Statistics;
        if (!analysis.HasTimeline && stats is null) return null;

        return new
        {
            timelineSamples = doc.HouseStatsFrameCount,
            sampleIntervalFrames = ReplayFormat.HouseStatsIntervalFrames,
            timeline = analysis.Houses.Select(t => new
            {
                houseIndex = t.HouseIndex,
                name = t.Name,
                totalIncome = t.TotalIncome,
                netSpent = t.NetSpent,
                directIncome = Math.Round(t.DirectIncome),
                incomeBySource = t.IncomeBySource.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                peakIncomePerMinute = Math.Round(t.PeakIncomeRate),
                peakArmyValue = t.PeakArmyValue,
                peakArmySize = t.PeakArmySize,
                defeatedAtFrame = t.DefeatedAtFrame,
                last = t.Last,
            }),
            houses = stats?.Houses.Select(h => new
            {
                houseIndex = h.HouseIndex,
                name = h.Name,
                country = h.Country,
                flags = h.Flags.ToString(),
                credits = h.Credits,
                storedOreValue = h.StoredOreValue,
                creditsSpent = h.CreditsSpent,
                harvestedCredits = h.HarvestedCredits,
                unitsKilled = h.UnitsKilled,
                buildingsKilled = h.BuildingsKilled,
                unitsLost = h.UnitsLost,
                buildingsLost = h.BuildingsLost,
                score = h.Score,
                unitsKilledOfHouse = h.UnitsKilledOfHouse,
                buildingsKilledOfHouse = h.BuildingsKilledOfHouse,
                counts = Enum.GetValues<StatisticsArray>().ToDictionary(a => a.ToString(), a => NamedCounts(stats, h, a)),
            }),
            game = stats?.Game,
            modules = stats?.Modules.Select(m => new
            {
                name = m.Name,
                baseAddress = $"0x{m.Base:X8}",
                size = m.Size,
                timestamp = $"0x{m.TimeDateStamp:X8}",
            }),
            unclassifiedCallers = analysis.Unclassified.Select(u => new
            {
                caller = u.Caller.ToString(),
                timestamp = $"0x{u.Caller.TimeDateStamp:X8}",
                amount = u.Amount,
                payments = u.Payments,
                houses = u.Houses,
            }),
            statsPacket = stats?.StatsPacket?.Fields.Select(f => new
            {
                tag = f.Tag,
                meaning = StatsDump.Meaning(f.Key),
                value = f.DisplayValue,
            }),
        };
    }

    public static void WriteSummaryJson(string path, ReplayDocument doc, NetworkAnalysis network,
        ActivityAnalysis activity)
    {
        var summary = new
        {
            file = doc.FileName,
            fileSize = doc.FileSize,
            header = new
            {
                version = doc.Header.Version,
                headerSize = doc.Header.HeaderSize,
                gameMode = doc.Header.GameModeName,
                seed = doc.Header.Seed,
                uniqueIdCounter = doc.Header.UniqueIDCounter,
                recordedGameSpeed = doc.Header.RecordedGameSpeed,
                simulationFps = doc.Header.SimulationFps,
                recordedAtUtc = doc.Header.RecordedAt.UtcDateTime,
                totalFrames = doc.Header.TotalFrames,
                cleanShutdown = doc.Header.CleanShutdown,
                hasEmbeddedMap = doc.HasEmbeddedMap,
                checkpointArchiveOffset = doc.Header.CheckpointArchiveOffset,
                checkpointArchiveSize = doc.Header.CheckpointArchiveSize,
                statisticsOffset = doc.Header.StatisticsOffset,
                statisticsSize = doc.Header.StatisticsSize,
            },
            statistics = StatisticsSummary(doc),
            checkpoints = doc.Checkpoints.Select(c => new
            {
                frame = c.Frame,
                compressedBytes = c.CompressedSize,
                rawBytes = c.RawSize,
                saveBytes = c.SaveBytes,
                sidecarBytes = c.SidecarBytes,
                usable = c.Usable,
                problem = c.Problem,
            }),
            metadata = new
            {
                map = doc.MapName,
                gamePackageVersion = doc.GamePackageVersion,
            },
            gameSpeed = doc.GameSpeed.Segments.Select(seg => new
            {
                startFrame = seg.StartFrame,
                speedIndex = seg.SpeedIndex,
                fps = seg.Fps,
                startSeconds = Math.Round(seg.StartSeconds, 2),
            }),
            stream = new
            {
                frameRecords = doc.Frames.Count,
                lastRecordedFrame = doc.LastRecordedFrame,
                events = doc.EventCount,
                sideChannelRecords = doc.EnumerateSideChannel().Count(),
                compressedBytes = doc.CompressedStreamBytes,
                inflatedBytes = doc.InflatedStreamBytes,
                compressionRatio = Math.Round(doc.CompressionRatio, 2),
                objectCensuses = doc.CensusFrameCount,
                randomStates = doc.Frames.Count(f => f.RandomState.HasValue),
                selectionTriggers = doc.Frames.Sum(f => (long)(f.SelectionTriggerIds?.Length ?? 0)),
                sawEndOfStream = doc.SawEndOfStream,
                truncated = doc.Truncated,
                warnings = doc.Warnings,
            },
            players = doc.Roster.Players.Select(p => new
            {
                slot = p.Slot,
                houseIndex = p.HouseIndex,
                name = p.DisplayName,
                side = p.Side,
                sideName = p.SideName,
                color = p.Color,
                isHuman = p.IsHuman,
                isSpectator = p.IsSpectator,
                isRecordingPlayer = p.IsRecordingPlayer,
                spawnLocation = p.SpawnLocation,
            }),
            network = new
            {
                protocol = network.Protocol,
                frameSendRate = network.ConfiguredFrameSendRate,
                players = network.Series.Select(s => new
                {
                    houseIndex = s.HouseIndex,
                    name = s.Name,
                    medianRoundTripMs = Math.Round(s.MedianRoundTripMs, 1),
                    worstRoundTripMs = Math.Round(s.WorstRoundTripMs, 1),
                    medianProcessMs = Math.Round(s.MedianProcessMs, 1),
                    worstProcessMs = Math.Round(s.WorstProcessMs, 1),
                    medianMaxAhead = s.MedianMaxAhead,
                    worstMaxAhead = s.WorstMaxAhead,
                    worstFrameInfoGapFrames = s.WorstFrameInfoGap,
                    frameInfoEvents = s.FrameInfoCount,
                }),
                largeFrameInfoGaps = network.LargeFrameInfoGaps.Take(50).Select(s => new
                {
                    houseIndex = s.HouseIndex,
                    name = s.Name,
                    startFrame = s.StartFrame,
                    endFrame = s.EndFrame,
                    frames = s.Frames,
                    simulationSeconds = Math.Round(s.SimulationSeconds, 2),
                }),
            },
            activity = activity.Players.Select(p => new
            {
                houseIndex = p.HouseIndex,
                name = p.Name,
                totalOrders = p.TotalOrders,
                totalCommands = p.TotalCommands,
                totalEvents = p.TotalEvents,
                averageApm = Math.Round(p.AverageApm, 1),
                peakApm = Math.Round(p.PeakApm, 1),
                lastActionFrame = p.LastActionFrame,
            }),
            eventTotals = activity.EventTotals
                .OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => EventTypes.Name(kv.Key), kv => kv.Value),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Csv(string value)
    {
        if (value.Length == 0) return "";
        bool needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n')
                           || value.Contains('\r');
        if (!needsQuotes) return value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
