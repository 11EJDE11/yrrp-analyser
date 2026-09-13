namespace YrrpAnalyser;

/// <summary>
/// Every team's likeliness at one sampled frame, the advantage index it was sharpened from, and what
/// went into it.
/// </summary>
public sealed record WinSample(int Frame, double[] Shares, double[] Index, double[][] Features, double[][] FactorShares, bool[] Active);

/// <summary>
/// An estimate of who is ahead, sample by sample: a weighted share of each team's army value, recent
/// income, base value and usable cash, relative to the other teams still playing. It is an explainable
/// index, not a calibrated probability - 60% does not mean a team wins six games in ten like it.
///
/// It only ever looks backwards: every sample uses what had happened by then, so the line never knows
/// the result early. A team is out the moment its last player is defeated; the opening stays even for
/// the first 30 seconds and phases the inputs in until 90; a 15% even share tempers the certainty, and a
/// 12-second smoother keeps it from jittering. That index is the one Replay Insights shipped
/// (advantage.py), carried over unchanged.
///
/// The index itself is timid: its leader went on to win 80% of the time from the halfway point of 20
/// recorded games (September 2026), while it gave that leader 55%. So what is shown is the index
/// sharpened - each team's index to the power <see cref="Sharpness"/>, renormalised - which leaves who
/// leads untouched and makes the stated chances match how often those favourites actually won. Fitted
/// on those 20 games (best fit 9.5; 6 is used, a little on the cautious side), so a rough calibration.
/// </summary>
public sealed class WinLikelihood
{
    public const double Sharpness = 6;
    public static readonly string[] FactorNames = ["Army value", "Income, last minute", "Base value", "Usable cash"];
    public static readonly double[] Weights = [0.55, 0.25, 0.15, 0.05];

    public const double EvenShareBlend = 0.15;
    public const double SmoothingSeconds = 12;
    public const double OpeningStartSeconds = 30;
    public const double OpeningFullSeconds = 90;
    public const double IncomeWindowSeconds = 60;
    public const double CashAllowance = 2000;

    public List<WinSample> Samples { get; } = [];
    public IReadOnlyList<Team> Teams { get; private set; } = [];
    public bool HasData => Samples.Count > 0 && Teams.Count > 1;

    /// <summary>The last sample at or before a frame.</summary>
    public WinSample? At(int frame)
    {
        if (Samples.Count == 0 || frame < Samples[0].Frame) return Samples.FirstOrDefault();
        int lo = 0, hi = Samples.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Samples[mid].Frame <= frame) lo = mid; else hi = mid - 1;
        }
        return Samples[lo];
    }

    /// <summary>Shares at a frame, eased between the samples either side so playback moves smoothly.</summary>
    public double[] SharesAt(int frame)
    {
        var before = At(frame);
        if (before is null) return [];
        int i = Samples.IndexOf(before);
        if (i + 1 >= Samples.Count || frame <= before.Frame) return before.Shares;
        var after = Samples[i + 1];
        double t = (frame - before.Frame) / (double)Math.Max(1, after.Frame - before.Frame);
        return before.Shares.Select((s, k) => s + (after.Shares[k] - s) * t).ToArray();
    }

    public static WinLikelihood Build(ReplayDocument doc, StatisticsAnalysis stats, TeamAnalysis teams)
    {
        var model = new WinLikelihood { Teams = teams.Teams };
        if (teams.Teams.Count < 2) return model;

        var timelines = teams.Teams.SelectMany(t => t.Members)
            .Select(h => stats.Houses.FirstOrDefault(x => x.HouseIndex == h))
            .Where(t => t is not null && t.Income.Count > 0)
            .ToDictionary(t => t!.HouseIndex, t => t!);
        if (timelines.Count == 0) return model;

        var seconds = timelines.ToDictionary(kv => kv.Key,
            kv => kv.Value.Income.Select(s => doc.GameSpeed.SecondsAt(s.Frame)).ToArray());
        var frames = timelines.Values.SelectMany(t => t.Income.Select(s => s.Frame)).Distinct().Order().ToList();

        double[]? previous = null;
        double lastTime = 0;
        foreach (int frame in frames)
        {
            double now = doc.GameSpeed.SecondsAt(frame);
            int n = teams.Teams.Count;
            var features = new double[n][];
            var active = new bool[n];

            for (int k = 0; k < n; k++)
            {
                double army = 0, income = 0, buildings = 0, cash = 0;
                int living = 0;
                foreach (int house in teams.Teams[k].Members)
                {
                    if (!timelines.TryGetValue(house, out var t)) continue;
                    if (t.DefeatedAtFrame is { } defeated && defeated <= frame) continue;
                    int at = LastAtOrBefore(t.Income, frame);
                    if (at < 0) continue;
                    living++;

                    var times = seconds[house];
                    int old = LastAtOrBeforeTime(times, times[at] - IncomeWindowSeconds);
                    double elapsed = times[at] - times[old];
                    double rate = elapsed > 0 ? Math.Max(0, t.Income[at].Value - t.Income[old].Value) * 60 / elapsed : 0;

                    army += Math.Max(0, t.ArmyValue[at].Value);
                    buildings += Math.Max(0, t.BuildingValue[at].Value);
                    income += rate;
                    cash += Math.Min(Math.Max(0, t.CreditsOnHand[at].Value), CashAllowance + rate);
                }
                features[k] = [army, income, buildings, cash];
                active[k] = living > 0;
            }

            var (index, factorShares) = Evaluate(features, active, previous, now - lastTime, now);
            model.Samples.Add(new WinSample(frame, Sharpen(index), index, features, factorShares, active));
            previous = index;
            lastTime = now;
        }
        return model;
    }

    /// <summary>
    /// One step of the model: features[team][factor] for the teams still playing, smoothed towards
    /// the new target from the previous shares. A factor every team has none of counts as even.
    /// </summary>
    public static (double[] Shares, double[][] FactorShares) Evaluate(double[][] features, bool[] active,
        double[]? previous, double dt, double time)
    {
        int n = features.Length;
        var live = Enumerable.Range(0, n).Where(i => active[i]).ToList();
        var normalised = Enumerable.Range(0, n).Select(_ => new double[Weights.Length]).ToArray();
        if (live.Count == 0) return (new double[n], normalised);

        int count = live.Count;
        var uniform = Enumerable.Range(0, n).Select(i => active[i] ? 1.0 / count : 0).ToArray();
        for (int f = 0; f < Weights.Length; f++)
        {
            double total = live.Sum(i => features[i][f]);
            foreach (int i in live) normalised[i][f] = total > 0 ? features[i][f] / total : 1.0 / count;
        }

        double evidence = Math.Clamp((time - OpeningStartSeconds) / (OpeningFullSeconds - OpeningStartSeconds), 0, 1);
        double weight = evidence * (1 - EvenShareBlend);
        var target = Enumerable.Range(0, n).Select(i =>
        {
            double raw = 0;
            for (int f = 0; f < Weights.Length; f++) raw += normalised[i][f] * Weights[f];
            return weight * raw + (1 - weight) * uniform[i];
        }).ToArray();

        double[] shares;
        if (count == 1) shares = uniform;
        else if (previous is null) shares = target;
        else
        {
            double mass = live.Sum(i => previous[i]);
            double alpha = 1 - Math.Exp(-Math.Max(0, dt) / SmoothingSeconds);
            shares = Enumerable.Range(0, n).Select(i =>
            {
                if (!active[i]) return 0;
                double old = mass > 0 ? previous[i] / mass : uniform[i];
                return (1 - alpha) * old + alpha * target[i];
            }).ToArray();
        }

        double sum = shares.Sum();
        return (sum > 0 ? shares.Select(s => s / sum).ToArray() : shares, normalised);
    }

    public static double[] Sharpen(double[] index)
    {
        var powered = index.Select(v => v > 0 ? Math.Pow(v, Sharpness) : 0).ToArray();
        double sum = powered.Sum();
        return sum > 0 ? powered.Select(v => v / sum).ToArray() : index;
    }

    private static int LastAtOrBefore(List<Sample> samples, int frame)
    {
        int lo = 0, hi = samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    private static int LastAtOrBeforeTime(double[] times, double t)
    {
        int lo = 0, hi = times.Length - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= t) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }
}
