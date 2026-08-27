namespace YrrpAnalyser;

/// <summary>Everything a single .yrrp file holds, parsed.</summary>
public sealed class ReplayDocument
{
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public required ReplayHeaderInfo Header { get; init; }

    public string SpawnIniText { get; init; } = "";
    public string SpawnMapText { get; init; } = "";

    public IniDocument SpawnIni { get; set; } = IniDocument.Empty;
    public IniDocument SpawnMapIni { get; set; } = IniDocument.Empty;
    public PlayerRoster Roster { get; set; } = PlayerRoster.Empty;

    public List<FrameRecord> Frames { get; set; } = [];

    /// <summary>All recorded events, back to back, 111 bytes each.</summary>
    public byte[] EventBlob { get; set; } = [];

    public long CompressedStreamBytes { get; init; }
    public long InflatedStreamBytes { get; set; }

    /// <summary>The stream carried its end-of-stream marker, so it is complete on disk.</summary>
    public bool SawEndOfStream { get; set; }

    /// <summary>The stream stopped part-way through a record.</summary>
    public bool Truncated { get; set; }

    public bool HasExtensionBlocks { get; set; }

    public List<string> Warnings { get; } = [];

    /// <summary>Speed the simulation ran at over time; one segment unless someone moved the slider.</summary>
    public GameSpeedTrack GameSpeed { get; set; } = new();

    /// <summary>False for a campaign recording, which names a scenario inside the game's own mixes.</summary>
    public bool HasEmbeddedMap => Header.SpawnMapSize > 0;

    public int CensusFrameCount { get; set; }

    public string FileName => Path.GetFileName(FilePath);

    public int EventCount => EventBlob.Length / ReplayFormat.EventSize;

    /// <summary>Highest frame that carried a record. Header.TotalFrames stays 0 on a crash.</summary>
    public int LastRecordedFrame => Frames.Count > 0 ? Frames[^1].FrameNumber : 0;

    /// <summary>
    /// Frames the header claims, falling back to what the stream actually carries when the header
    /// was never stamped - a game that crashed or was killed leaves TotalFrames at 0.
    /// </summary>
    public int EffectiveFrameCount =>
        Header.TotalFrames > 0 ? (int)Header.TotalFrames : LastRecordedFrame;

    public TimeSpan Duration => TimeSpan.FromSeconds(GameSpeed.SecondsAt(EffectiveFrameCount));

    public double CompressionRatio =>
        CompressedStreamBytes > 0 ? (double)InflatedStreamBytes / CompressedStreamBytes : 0;

    public GameEvent GetEvent(int index, int recordFrame) =>
        new(EventBlob, index * ReplayFormat.EventSize, recordFrame);

    /// <summary>Every event in the file, in frame order.</summary>
    public IEnumerable<GameEvent> EnumerateEvents()
    {
        foreach (var frame in Frames)
        {
            for (int i = 0; i < frame.EventCount; i++)
                yield return GetEvent(frame.EventStart + i, frame.FrameNumber);
        }
    }

    public IEnumerable<SideChannelEvent> EnumerateSideChannel()
    {
        foreach (var frame in Frames)
        {
            if (frame.SideChannel is null) continue;
            foreach (var e in frame.SideChannel) yield return e;
        }
    }

    /// <summary>
    /// Frame number to elapsed game time, across whatever speeds the recording actually ran at.
    /// </summary>
    public TimeSpan FrameToTime(int frame) => TimeSpan.FromSeconds(GameSpeed.SecondsAt(frame));

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";

    public string TimeLabel(int frame) => FormatTime(FrameToTime(frame));
}
