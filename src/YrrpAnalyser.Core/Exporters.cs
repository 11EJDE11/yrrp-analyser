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
            Dump(s, "OrderGapFrames", s.FrameInfoGap);
            Dump(s, "RequestedFps", s.RequestedFps);
            Dump(s, "FrameSendRate", s.FrameSendRate);
        }
    }

    public static void WriteFrameCrcCsv(string path, ReplayDocument doc)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Frame,Time,GameCRC,Events,Flags");
        foreach (var f in doc.Frames)
        {
            writer.WriteLine(string.Join(',',
                f.FrameNumber,
                Csv(doc.TimeLabel(f.FrameNumber)),
                f.GameCrc is { } crc ? crc.ToString("X8") : "",
                f.EventCount,
                $"0x{f.Flags:X2}"));
        }
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
                map = doc.Header.MapName,
                spawnerVersion = doc.Header.SpawnerVersion,
                gameClientVersion = doc.Header.GameClientVersion,
                gameMode = doc.Header.GameModeName,
                seed = doc.Header.Seed,
                uniqueIdCounter = doc.Header.UniqueIDCounter,
                recordedGameSpeed = doc.Header.RecordedGameSpeed,
                simulationFps = doc.Header.SimulationFps,
                recordedAtUtc = doc.Header.RecordedAt.UtcDateTime,
                totalFrames = doc.Header.TotalFrames,
                cleanShutdown = doc.Header.CleanShutdown,
            },
            stream = new
            {
                frameRecords = doc.Frames.Count,
                lastRecordedFrame = doc.LastRecordedFrame,
                events = doc.EventCount,
                sideChannelRecords = doc.EnumerateSideChannel().Count(),
                compressedBytes = doc.CompressedStreamBytes,
                inflatedBytes = doc.InflatedStreamBytes,
                compressionRatio = Math.Round(doc.CompressionRatio, 2),
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
                    worstOrderGapFrames = s.WorstFrameInfoGap,
                    frameInfoPackets = s.FrameInfoCount,
                }),
                stalls = network.Stalls.Take(50).Select(s => new
                {
                    houseIndex = s.HouseIndex,
                    name = s.Name,
                    startFrame = s.StartFrame,
                    endFrame = s.EndFrame,
                    frames = s.Frames,
                    seconds = Math.Round(s.Seconds, 2),
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
