using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>One card per team: result, players, and the totals that matter, side by side.</summary>
internal sealed class TeamCardsView : Control
{
    private const int Gap = 12;
    private const int MemberRow = 22;
    private ReplayDocument? _doc;
    private TeamAnalysis? _teams;
    private StatisticsAnalysis? _stats;

    public TeamCardsView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Height = 200;
    }

    public void SetData(ReplayDocument doc, TeamAnalysis teams, StatisticsAnalysis stats)
    {
        _doc = doc; _teams = teams; _stats = stats;
        Relayout();
        Invalidate();
    }

    private (int Columns, int CardWidth, int CardHeight) CardLayout()
    {
        int n = Math.Max(1, _teams?.Teams.Count ?? 1);
        int columns = Math.Clamp((Width + Gap) / (280 + Gap), 1, Math.Min(n, 4));
        int width = (Width - Gap * (columns - 1)) / columns;
        int members = _teams?.Teams.Select(t => t.Members.Count).DefaultIfEmpty(1).Max() ?? 1;
        return (columns, width, 64 + members * MemberRow + 86);
    }

    private void Relayout()
    {
        if (_teams is null) return;
        var (columns, _, cardHeight) = CardLayout();
        int rows = (_teams.Teams.Count + columns - 1) / columns;
        int height = Math.Max(1, rows * cardHeight + (rows - 1) * Gap);
        if (Height != height) Height = height;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_teams is null || _doc is null || _stats is null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (columns, width, height) = CardLayout();
        for (int i = 0; i < _teams.Teams.Count; i++)
        {
            var bounds = new Rectangle(i % columns * (width + Gap), i / columns * (height + Gap), width - 1, height - 1);
            DrawCard(g, bounds, _teams.Teams[i]);
        }
    }

    private void DrawCard(Graphics g, Rectangle r, Team team)
    {
        var colour = Theme.ForTeam(team);
        using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, r);
        using (var edge = new Pen(Theme.Border)) g.DrawRectangle(edge, r);
        using (var strip = new SolidBrush(colour)) g.FillRectangle(strip, r.X, r.Y, r.Width + 1, 5);

        TextRenderer.DrawText(g, team.Name, Theme.Heading, new Point(r.X + 12, r.Y + 14), Theme.Text);
        string result = team.ResultText(_doc!);
        var badgeColour = team.Result switch { TeamResult.Won => Theme.Good, TeamResult.Lost => Theme.Danger, _ => Theme.Muted };
        var size = TextRenderer.MeasureText(result, Theme.UiBold);
        var badge = new Rectangle(r.Right - size.Width - 22, r.Y + 14, size.Width + 12, size.Height + 4);
        using (var fill = new SolidBrush(Color.FromArgb(28, badgeColour))) g.FillRectangle(fill, badge);
        using (var edge = new Pen(badgeColour)) g.DrawRectangle(edge, badge);
        TextRenderer.DrawText(g, result, Theme.UiBold, new Point(badge.X + 6, badge.Y + 2), badgeColour);

        int y = r.Y + 46;
        foreach (int house in team.Members)
        {
            using (var swatch = new SolidBrush(Theme.ForHouse(house))) g.FillRectangle(swatch, r.X + 14, y + 5, 10, 10);
            string name = StatisticsAnalysis.HouseName(_doc!, house);
            TextRenderer.DrawText(g, name, Theme.UiBold, new Point(r.X + 30, y + 1), Theme.Text);
            string side = _doc!.Statistics?.ForHouse(house)?.Country is { Length: > 0 } c ? c : _doc.Roster.ForHouse(house)?.SideName ?? "";
            int nameWidth = TextRenderer.MeasureText(name, Theme.UiBold).Width;
            TextRenderer.DrawText(g, side, Theme.MonoSmall, new Point(r.X + 34 + nameWidth, y + 3), Theme.Muted);

            string status = TeamAnalysis.DefeatedAt(_stats!, house) is { } f ? $"out at {_doc.TimeLabel(f)}"
                : team.Result == TeamResult.Won ? "won" : "";
            var statusSize = TextRenderer.MeasureText(status, Theme.MonoSmall);
            TextRenderer.DrawText(g, status, Theme.MonoSmall, new Point(r.Right - statusSize.Width - 12, y + 3), Theme.Muted);
            y += MemberRow;
        }

        y = r.Bottom - 80;
        using (var sep = new Pen(Theme.Grid)) g.DrawLine(sep, r.X + 12, y - 6, r.Right - 12, y - 6);
        (string, string)[] facts =
        [
            ("Income", team.TotalIncome.ToString("N0")),
            ("Spent", team.NetSpent.ToString("N0")),
            ("Peak army", team.PeakArmyValue.ToString("N0")),
            ("Money left", team.MoneyLeft.ToString("N0")),
            ("Killed", $"{team.UnitsKilled:N0} / {team.BuildingsKilled:N0}"),
            ("Lost", $"{team.UnitsLost:N0} / {team.BuildingsLost:N0}"),
        ];
        int cellWidth = (r.Width - 24) / 3;
        for (int i = 0; i < facts.Length; i++)
        {
            int cx = r.X + 12 + i % 3 * cellWidth, cy = y + i / 3 * 36;
            TextRenderer.DrawText(g, facts[i].Item1, Theme.MonoSmall, new Point(cx, cy), Theme.Muted);
            TextRenderer.DrawText(g, facts[i].Item2, Theme.UiBold, new Point(cx, cy + 14), Theme.Text);
        }
    }
}

/// <summary>
/// How each team's totals split between its players: one stacked bar per measure, a segment per player
/// in their colour. Shows who carried the economy, the army and the fighting.
/// </summary>
internal sealed class ContributionView : Control
{
    private const int RowHeight = 24;
    private const int LabelWidth = 130;
    private const int ValueWidth = 90;

    private readonly List<(string Team, Color Colour, List<(string Measure, List<(int House, string Name, double Value)> Parts)> Rows)> _blocks = [];

    public ContributionView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    public void SetData(ReplayDocument doc, TeamAnalysis teams, StatisticsAnalysis stats)
    {
        _blocks.Clear();
        foreach (var team in teams.Teams.Where(t => !t.IsSolo))
        {
            var members = team.Members.Select(h => (House: h, Name: StatisticsAnalysis.HouseName(doc, h),
                Timeline: stats.Houses.FirstOrDefault(t => t.HouseIndex == h), Summary: doc.Statistics?.ForHouse(h))).ToList();

            List<(int, string, double)> Parts(Func<HouseTimeline?, HouseSummary?, double> pick) =>
                members.Select(m => (m.House, m.Name, Math.Max(0, pick(m.Timeline, m.Summary)))).ToList();

            _blocks.Add((team.Name, Theme.ForTeam(team),
            [
                ("Income", Parts((t, _) => t?.TotalIncome ?? 0)),
                ("Spent", Parts((t, s) => t?.NetSpent ?? s?.CreditsSpent ?? 0)),
                ("Peak army value", Parts((t, _) => t?.PeakArmyValue ?? 0)),
                ("Units built", Parts((t, s) => s is null ? t?.Last.UnitsBuilt ?? 0
                    : s.Total(StatisticsArray.BuiltUnits) + s.Total(StatisticsArray.BuiltInfantry) + s.Total(StatisticsArray.BuiltAircraft))),
                ("Units killed", Parts((t, s) => s?.UnitsKilled ?? t?.Last.UnitsKilled ?? 0)),
                ("Buildings killed", Parts((t, s) => s?.BuildingsKilled ?? t?.Last.BuildingsKilled ?? 0)),
                ("Units lost", Parts((t, s) => s?.UnitsLost ?? t?.Last.UnitsLost ?? 0)),
            ]));
        }
        Height = Math.Max(30, _blocks.Sum(b => 30 + b.Rows.Count * RowHeight) + 8);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using (var edge = new Pen(Theme.Border)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        if (_blocks.Count == 0)
        {
            TextRenderer.DrawText(g, "Every player was on their own - nothing to split.", Theme.Ui, new Point(10, 8), Theme.Muted);
            return;
        }

        int y = 6;
        int barX = LabelWidth + 12, barWidth = Math.Max(40, Width - barX - ValueWidth - 16);
        foreach (var (team, colour, rows) in _blocks)
        {
            TextRenderer.DrawText(g, team, Theme.UiBold, new Point(10, y + 4), colour);
            y += 30;
            foreach (var (measure, parts) in rows)
            {
                TextRenderer.DrawText(g, measure, Theme.Ui, new Point(12, y + 3), Theme.Muted);
                double total = parts.Sum(p => p.Value);
                float x = barX;
                using (var back = new SolidBrush(Theme.Grid)) g.FillRectangle(back, barX, y + 3, barWidth, RowHeight - 7);
                if (total > 0)
                {
                    foreach (var (house, name, value) in parts)
                    {
                        float w = (float)(value / total * barWidth);
                        if (w <= 0) continue;
                        var seg = new RectangleF(x, y + 3, w, RowHeight - 7);
                        using (var fill = new SolidBrush(Theme.ForHouse(house))) g.FillRectangle(fill, seg);
                        string label = $"{name} {value / total:P0}";
                        if (TextRenderer.MeasureText(label, Theme.MonoSmall).Width + 6 > w) label = $"{value / total:P0}";
                        if (TextRenderer.MeasureText(label, Theme.MonoSmall).Width + 4 <= w)
                            TextRenderer.DrawText(g, label, Theme.MonoSmall, Rectangle.Round(seg), Color.White,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                        x += w;
                        using var divider = new Pen(Theme.Panel, 1.5f);
                        g.DrawLine(divider, x, seg.Y, x, seg.Bottom);
                    }
                }
                TextRenderer.DrawText(g, total.ToString("N0"), Theme.Mono, new Point(barX + barWidth + 10, y + 3), Theme.Text);
                y += RowHeight;
            }
        }
    }
}
