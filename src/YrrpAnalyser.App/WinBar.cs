using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>
/// Who looks likely to win: a bar split between the teams at the current moment, and under it the same
/// split across the whole game, stacked, with the current moment marked. Click or drag the history to
/// move there; hover a segment for what it is made of.
/// </summary>
internal sealed class WinBar : Control
{
    private const int TitleHeight = 22;
    private const int BarHeight = 30;
    private const int Gap = 6;
    private const int HistoryHeight = 54;

    private WinLikelihood? _model;
    private Color[] _colours = [];
    private string[] _names = [];
    private int _frame;
    private int _lastFrame = 1;
    private bool _dragging;
    private readonly ToolTip _tip = new() { InitialDelay = 150, ReshowDelay = 50, AutoPopDelay = 20000 };
    private int _tipSegment = -2;

    public event Action<int>? SeekRequested;

    public WinBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Height = TitleHeight + BarHeight + Gap + HistoryHeight + 8;
    }

    public void SetData(WinLikelihood? model, IReadOnlyList<Team> teams, int lastFrame)
    {
        _model = model;
        _colours = teams.Select(Theme.ForTeam).ToArray();
        _names = teams.Select(t => t.Name).ToArray();
        _lastFrame = Math.Max(1, lastFrame);
        Invalidate();
    }

    public int Frame
    {
        get => _frame;
        set { if (_frame == value) return; _frame = value; Invalidate(); }
    }

    private Rectangle BarRect => new(8, TitleHeight, Math.Max(1, Width - 16), BarHeight);
    private Rectangle HistoryRect => new(8, TitleHeight + BarHeight + Gap, Math.Max(1, Width - 16), HistoryHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Theme.Border);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        TextRenderer.DrawText(g, "Likeliness to win", Theme.UiBold, new Point(8, 4), Theme.Text);
        int titleWidth = TextRenderer.MeasureText("Likeliness to win", Theme.UiBold).Width;
        TextRenderer.DrawText(g, "estimate from army, income, base and cash; roughly calibrated on 20 recorded games - hover for the breakdown",
            Theme.MonoSmall, new Point(12 + titleWidth, 6), Theme.Muted);

        if (_model is not { HasData: true } model)
        {
            TextRenderer.DrawText(g, "Needs statistics samples and at least two teams.", Font,
                new Point(BarRect.X, BarRect.Y + 6), Theme.Muted);
            return;
        }

        DrawBar(g, model.SharesAt(_frame));
        DrawHistory(g, model);
    }

    private void DrawBar(Graphics g, double[] shares)
    {
        var bar = BarRect;
        using (var back = new SolidBrush(Theme.Grid)) g.FillRectangle(back, bar);
        float x = bar.X;
        for (int i = 0; i < shares.Length; i++)
        {
            float w = (float)(shares[i] * bar.Width);
            if (w <= 0) continue;
            var segment = new RectangleF(x, bar.Y, w, bar.Height);
            using (var fill = new SolidBrush(_colours[i])) g.FillRectangle(fill, segment);

            string label = $"{_names[i]}  {shares[i]:P0}";
            var size = TextRenderer.MeasureText(label, Theme.UiBold);
            if (size.Width + 10 > w) label = $"{shares[i]:P0}";
            size = TextRenderer.MeasureText(label, Theme.UiBold);
            if (size.Width + 6 <= w)
                TextRenderer.DrawText(g, label, Theme.UiBold, Rectangle.Round(segment), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += w;
            if (i < shares.Length - 1)
            {
                using var divider = new Pen(Theme.Panel, 2);
                g.DrawLine(divider, x, bar.Y, x, bar.Bottom);
            }
        }
    }

    private void DrawHistory(Graphics g, WinLikelihood model)
    {
        var area = HistoryRect;
        using (var back = new SolidBrush(Theme.Grid)) g.FillRectangle(back, area);

        float X(int frame) => area.X + frame / (float)_lastFrame * area.Width;
        int n = model.Teams.Count;
        var samples = model.Samples;
        // Thin the samples to about one per pixel column; the shares move slowly anyway.
        int step = Math.Max(1, samples.Count / Math.Max(1, area.Width));
        var picked = samples.Where((_, i) => i % step == 0 || i == samples.Count - 1).ToList();

        var lower = new float[picked.Count];
        for (int k = 0; k < n; k++)
        {
            var points = new List<PointF>(picked.Count * 2);
            var upper = new float[picked.Count];
            for (int i = 0; i < picked.Count; i++)
            {
                upper[i] = lower[i] + (float)picked[i].Shares[k];
                points.Add(new PointF(X(picked[i].Frame), area.Bottom - upper[i] * area.Height));
            }
            for (int i = picked.Count - 1; i >= 0; i--)
                points.Add(new PointF(X(picked[i].Frame), area.Bottom - lower[i] * area.Height));
            if (points.Count >= 3)
            {
                using var fill = new SolidBrush(Color.FromArgb(215, _colours[k]));
                g.FillPolygon(fill, [.. points]);
            }
            lower = upper;
        }

        using (var half = new Pen(Color.FromArgb(120, Color.White)) { DashStyle = DashStyle.Dot })
            g.DrawLine(half, area.X, area.Y + area.Height / 2f, area.Right, area.Y + area.Height / 2f);

        float cursor = X(Math.Clamp(_frame, 0, _lastFrame));
        using var cursorPen = new Pen(Theme.Text, 2);
        g.DrawLine(cursorPen, cursor, area.Y - 2, cursor, area.Bottom + 2);
    }

    private int SegmentAt(Point p)
    {
        if (_model is not { HasData: true } model || !BarRect.Contains(p)) return -1;
        var shares = model.SharesAt(_frame);
        double at = (p.X - BarRect.X) / (double)BarRect.Width, sum = 0;
        for (int i = 0; i < shares.Length; i++)
        {
            sum += shares[i];
            if (at <= sum) return i;
        }
        return shares.Length - 1;
    }

    private string Explain(int team)
    {
        if (_model?.At(_frame) is not { } s) return "";
        var lines = new List<string> { $"{_names[team]}: {s.Shares[team]:P1}" };
        if (!s.Active[team]) lines.Add("Eliminated.");
        for (int f = 0; f < WinLikelihood.FactorNames.Length; f++)
            lines.Add($"{WinLikelihood.FactorNames[f]} ({WinLikelihood.Weights[f]:P0} weight): " +
                      $"{s.Features[team][f]:N0}{(f == 1 ? "/min" : "")} - {s.FactorShares[team][f]:P0} of the total");
        return string.Join("\n", lines);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) { Seek(e.X); return; }
        int segment = SegmentAt(e.Location);
        Cursor = HistoryRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (segment == _tipSegment) return;
        _tipSegment = segment;
        if (segment >= 0) _tip.Show(Explain(segment), this, e.X + 12, e.Y + 16);
        else _tip.Hide(this);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipSegment = -2;
        _tip.Hide(this);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !HistoryRect.Contains(e.Location)) return;
        _dragging = true;
        Capture = true;
        Seek(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    private void Seek(int x)
    {
        var area = HistoryRect;
        int frame = (int)Math.Clamp((x - area.X) / (double)area.Width * _lastFrame, 0, _lastFrame);
        SeekRequested?.Invoke(frame);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
