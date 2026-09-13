using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>A moment marked on the scrubber: a defeat, a superweapon, a heavy fight.</summary>
internal sealed record ScrubMarker(int Frame, Color Color, string Label);

/// <summary>
/// The seek bar under the transport controls: the whole game left to right, how heavy the fighting was
/// as bars along it, the moments worth jumping to as markers above it, and the current position. Click
/// or drag anywhere to move.
/// </summary>
internal sealed class TimelineScrubber : Control
{
    private const int Pad = 12;
    private const int MarkerBand = 14;
    private const int LabelBand = 16;

    private int _last = 1;
    private int _frame;
    private int _fps = 60;
    private IReadOnlyList<Sample> _intensity = [];
    private List<ScrubMarker> _markers = [];
    private Func<int, string> _label = f => f.ToString();
    private bool _dragging;
    private Point? _hover;

    public event Action<int>? SeekRequested;

    public TimelineScrubber()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Height = 64;
        Cursor = Cursors.Hand;
    }

    public void SetData(int lastFrame, IReadOnlyList<Sample> intensity, List<ScrubMarker> markers, Func<int, string> label, int fps)
    {
        _last = Math.Max(1, lastFrame);
        _intensity = intensity;
        _markers = markers;
        _label = label;
        _fps = Math.Max(1, fps);
        Invalidate();
    }

    public int Frame
    {
        get => _frame;
        set { if (_frame == value) return; _frame = value; Invalidate(); }
    }

    private Rectangle Track => new(Pad, MarkerBand + 4, Math.Max(1, Width - Pad * 2), Math.Max(8, Height - MarkerBand - LabelBand - 8));
    private float X(int frame) => Track.X + frame / (float)_last * Track.Width;
    private int FrameAt(int x) => (int)Math.Clamp((x - Track.X) / (double)Track.Width * _last, 0, _last);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = Track;

        using (var back = new SolidBrush(Theme.Grid)) g.FillRectangle(back, track);
        using (var played = new SolidBrush(Color.FromArgb(38, Theme.Accent)))
            g.FillRectangle(played, track.X, track.Y, X(_frame) - track.X, track.Height);

        // Fighting, as bars rising from the bottom of the track.
        double peak = _intensity.Count > 0 ? Math.Max(1, _intensity.Max(s => s.Value)) : 1;
        using (var bar = new SolidBrush(Color.FromArgb(150, Theme.Danger)))
        {
            foreach (var s in _intensity)
            {
                if (s.Value <= 0) continue;
                float barHeight = (float)(s.Value / peak * (track.Height - 2));
                float w = Math.Max(1.5f, track.Width * (_intensity.Count > 1 ? (_intensity[1].Frame - _intensity[0].Frame) : _last) / (float)_last - 0.5f);
                g.FillRectangle(bar, X(s.Frame), track.Bottom - barHeight, w, barHeight);
            }
        }

        foreach (var m in _markers)
        {
            float x = X(m.Frame);
            using var fill = new SolidBrush(m.Color);
            g.FillPolygon(fill, [new PointF(x - 4, 3), new PointF(x + 4, 3), new PointF(x, MarkerBand)]);
            using var line = new Pen(Color.FromArgb(70, m.Color));
            g.DrawLine(line, x, MarkerBand, x, track.Bottom);
        }

        // Time labels at round minutes.
        double seconds = _last / (double)_fps;
        int step = new[] { 30, 60, 120, 300, 600, 900, 1800 }.FirstOrDefault(s => seconds / s <= 10, 3600);
        using var tick = new Pen(Theme.Border);
        for (int s = 0; s <= seconds; s += step)
        {
            float x = X((int)(s * (double)_fps));
            g.DrawLine(tick, x, track.Bottom, x, track.Bottom + 3);
            TextRenderer.DrawText(g, _label((int)(s * (double)_fps)), Theme.MonoSmall, new Point((int)x - 12, track.Bottom + 3), Theme.Muted);
        }

        float cursor = X(_frame);
        using (var pen = new Pen(Theme.Text, 2)) g.DrawLine(pen, cursor, track.Y - 3, cursor, track.Bottom + 3);
        using (var knob = new SolidBrush(Theme.Accent)) g.FillEllipse(knob, cursor - 6, track.Y - 9, 12, 12);
        using (var ring = new Pen(Color.White, 1.5f)) g.DrawEllipse(ring, cursor - 6, track.Y - 9, 12, 12);

        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        if (_hover is { } h && !_dragging) DrawHover(g, h);
    }

    private void DrawHover(Graphics g, Point h)
    {
        int frame = FrameAt(h.X);
        var near = _markers.Where(m => Math.Abs(X(m.Frame) - h.X) <= 5).Select(m => m.Label).Take(3).ToList();
        string text = _label(frame) + (near.Count > 0 ? "  " + string.Join("; ", near) : "");
        var size = TextRenderer.MeasureText(text, Theme.MonoSmall);
        float x = Math.Clamp(h.X + 10, 2, Width - size.Width - 10);
        var box = new RectangleF(x, 2, size.Width + 8, size.Height + 2);
        using var back = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
        using var edge = new Pen(Theme.Border);
        g.FillRectangle(back, box);
        g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
        TextRenderer.DrawText(g, text, Theme.MonoSmall, Point.Round(new PointF(box.X + 4, box.Y + 1)), Theme.Text);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        SeekRequested?.Invoke(FrameAt(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.Location;
        if (_dragging) SeekRequested?.Invoke(FrameAt(e.X));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }
}

/// <summary>
/// The side panel's scoreboard at the current moment: each team with its share, and under it each
/// player's army, cash, income, power and kills. Click a player to hide or show them on the map.
/// </summary>
internal sealed class TeamStatusView : Control
{
    private const int TeamHeader = 28;
    private const int PlayerRow = 44;

    private ReplayDocument? _doc;
    private TeamAnalysis? _teams;
    private StatisticsAnalysis? _stats;
    private WinLikelihood? _win;
    private HashSet<int> _hidden = [];
    private int _frame;
    private readonly List<(Rectangle Bounds, int House)> _rows = [];

    public event Action<int>? HouseToggled;

    public TeamStatusView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    public void SetData(ReplayDocument doc, TeamAnalysis teams, StatisticsAnalysis stats, WinLikelihood win, HashSet<int> hidden)
    {
        _doc = doc; _teams = teams; _stats = stats; _win = win; _hidden = hidden;
        Height = Math.Max(40, teams.Teams.Sum(t => TeamHeader + t.Members.Count * PlayerRow) + 8);
        Invalidate();
    }

    public int Frame
    {
        get => _frame;
        set { if (_frame == value) return; _frame = value; Invalidate(); }
    }

    internal static double ValueAt(List<Sample> samples, int frame)
    {
        int lo = 0, hi = samples.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].Frame <= frame) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found >= 0 ? samples[found].Value : samples.Count > 0 ? samples[0].Value : 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        _rows.Clear();
        if (_teams is null || _stats is null || _doc is null)
            return;

        var shares = _win is { HasData: true } ? _win.SharesAt(_frame) : [];
        int y = 4;
        int w = Width - 8;
        foreach (var team in _teams.Teams)
        {
            var colour = Theme.ForTeam(team);
            using (var strip = new SolidBrush(colour)) g.FillRectangle(strip, 4, y + 4, 4, TeamHeader - 8);
            TextRenderer.DrawText(g, team.Name, Theme.UiBold, new Point(14, y + 6), Theme.Text);
            string right = team.Index < shares.Length ? $"{shares[team.Index]:P0} to win" : "";
            bool eliminated = team.EliminatedAtFrame is { } f && f <= _frame;
            if (eliminated) right = "eliminated";
            var size = TextRenderer.MeasureText(right, Theme.UiBold);
            TextRenderer.DrawText(g, right, Theme.UiBold, new Point(w - size.Width, y + 6), eliminated ? Theme.Danger : colour);
            y += TeamHeader;

            foreach (int house in team.Members)
            {
                var bounds = new Rectangle(4, y, w, PlayerRow - 2);
                _rows.Add((bounds, house));
                DrawPlayer(g, bounds, house);
                y += PlayerRow;
            }
        }
    }

    private void DrawPlayer(Graphics g, Rectangle r, int house)
    {
        bool hidden = _hidden.Contains(house);
        var timeline = _stats!.Houses.FirstOrDefault(h => h.HouseIndex == house);
        var colour = Theme.ForHouse(house);
        using (var back = new SolidBrush(hidden ? Theme.Background : Theme.Blend(Theme.Panel, colour, 0.06))) g.FillRectangle(back, r);
        using (var swatch = new SolidBrush(hidden ? Theme.Border : colour)) g.FillRectangle(swatch, r.X + 6, r.Y + 7, 10, 10);

        string name = StatisticsAnalysis.HouseName(_doc!, house);
        string side = _doc!.Statistics?.ForHouse(house)?.Country is { Length: > 0 } c ? c : _doc.Roster.ForHouse(house)?.SideName ?? "";
        var nameColour = hidden ? Theme.Muted : Theme.Text;
        TextRenderer.DrawText(g, name, Theme.UiBold, new Point(r.X + 22, r.Y + 3), nameColour);
        int nameWidth = TextRenderer.MeasureText(name, Theme.UiBold).Width;
        TextRenderer.DrawText(g, side + (hidden ? "  (hidden on map)" : ""), Theme.MonoSmall, new Point(r.X + 26 + nameWidth, r.Y + 5), Theme.Muted);

        if (timeline is null) return;
        bool defeated = timeline.DefeatedAtFrame is { } d && d <= _frame;
        double army = ValueAt(timeline.ArmyValue, _frame), size = ValueAt(timeline.ArmySize, _frame);
        double cash = ValueAt(timeline.CreditsOnHand, _frame), income = ValueAt(timeline.IncomeRate, _frame);
        double power = ValueAt(timeline.PowerOutput, _frame), drain = ValueAt(timeline.PowerDrain, _frame);
        double kills = ValueAt(timeline.Kills, _frame), losses = ValueAt(timeline.Losses, _frame);

        string armyText = defeated ? "defeated" : $"army ${army:N0} ({size:N0})";
        var armySize = TextRenderer.MeasureText(armyText, Theme.MonoSmall);
        TextRenderer.DrawText(g, armyText, Theme.MonoSmall, new Point(r.Right - armySize.Width - 4, r.Y + 5), defeated ? Theme.Danger : Theme.Text);

        string line = $"${cash:N0}  +{income:N0}/min  K/L {kills:N0}/{losses:N0}";
        TextRenderer.DrawText(g, line, Theme.MonoSmall, new Point(r.X + 22, r.Y + 24), Theme.Muted);

        // Power: the bar fills with what is used against what is produced, red when short.
        var bar = new Rectangle(r.Right - 74, r.Y + 27, 70, 7);
        using (var back = new SolidBrush(Theme.Grid)) g.FillRectangle(back, bar);
        if (power > 0 || drain > 0)
        {
            double used = power > 0 ? Math.Min(1, drain / power) : 1;
            using var fill = new SolidBrush(drain > power ? Theme.Danger : Theme.Good);
            g.FillRectangle(fill, bar.X, bar.Y, (int)(bar.Width * used), bar.Height);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (bounds, house) in _rows)
        {
            if (!bounds.Contains(e.Location)) continue;
            HouseToggled?.Invoke(house);
            Invalidate();
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _rows.Any(r => r.Bounds.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }
}
