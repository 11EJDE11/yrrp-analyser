namespace YrrpAnalyser;

public readonly record struct GameSpeedSegment(int StartFrame, int SpeedIndex, int Fps)
{
    /// <summary>Seconds of play elapsed before this segment began.</summary>
    public double StartSeconds { get; init; }
}

/// <summary>
/// The speed the simulation ran at over the course of a recording.
///
/// The header only carries the speed the game started at. A speed change is written into the frame
/// stream on the frame it happens, so a recording where someone moved the slider runs at more than
/// one rate and a single frames-divided-by-one-rate duration is wrong for it. This turns the
/// recorded changes into segments and integrates across them.
///
/// Old recordings carry no speed blocks at all, which leaves exactly one segment at the header's
/// speed - the same answer the old arithmetic gave.
/// </summary>
public sealed class GameSpeedTrack
{
    private readonly List<GameSpeedSegment> _segments = [];

    public IReadOnlyList<GameSpeedSegment> Segments => _segments;

    /// <summary>Speed changes after the first segment - empty for a game run at one speed.</summary>
    public IEnumerable<GameSpeedSegment> Changes => _segments.Skip(1);

    public bool SpeedChanged => _segments.Count > 1;

    public int StartingSpeedIndex => _segments.Count > 0 ? _segments[0].SpeedIndex : 0;

    public static GameSpeedTrack Build(ReplayHeaderInfo header, IEnumerable<FrameRecord> frames)
    {
        var track = new GameSpeedTrack();

        int speed = (int)Math.Clamp(header.RecordedGameSpeed, 0u, (uint)ReplayFormat.MaxGameSpeedIndex);
        track._segments.Add(new GameSpeedSegment(0, speed, ReplayFormat.GetFpsFromGameSpeed(speed))
        { StartSeconds = 0 });

        foreach (var frame in frames)
        {
            if (frame.GameSpeed is not { } recorded) continue;

            int next = Math.Clamp(recorded, 0, ReplayFormat.MaxGameSpeedIndex);
            var current = track._segments[^1];
            if (next == current.SpeedIndex) continue;

            // The frame the change lands on is the first one that runs at the new rate, so the
            // frames before it still belong to the segment that is ending.
            double seconds = current.StartSeconds
                             + (frame.FrameNumber - current.StartFrame) / (double)current.Fps;

            track._segments.Add(
                new GameSpeedSegment(frame.FrameNumber, next, ReplayFormat.GetFpsFromGameSpeed(next))
                { StartSeconds = seconds });
        }

        return track;
    }

    public double SecondsAt(int frame)
    {
        if (_segments.Count == 0) return 0;
        if (frame <= 0) return 0;

        var segment = _segments[0];
        for (int i = 1; i < _segments.Count; i++)
        {
            if (_segments[i].StartFrame > frame) break;
            segment = _segments[i];
        }

        return segment.StartSeconds + (frame - segment.StartFrame) / (double)segment.Fps;
    }

    public int FpsAt(int frame)
    {
        if (_segments.Count == 0) return 60;

        var segment = _segments[0];
        for (int i = 1; i < _segments.Count; i++)
        {
            if (_segments[i].StartFrame > frame) break;
            segment = _segments[i];
        }
        return segment.Fps;
    }
}
