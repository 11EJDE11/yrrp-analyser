using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal enum SeriesStyle { Line, Step, Bars, Points }

internal sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public Color Color { get; init; } = Theme.Accent;
    public SeriesStyle Style { get; init; } = SeriesStyle.Line;
    public IReadOnlyList<Sample> Points { get; init; } = [];
    public bool Visible { get; set; } = true;
}

/// <summary>
/// Marks a moment worth seeing against the series - a stall, a chat message, the frame a desync
/// started on.
/// </summary>
internal sealed record ChartMarker(int Frame, Color Color, string Label);

/// <summary>
/// A small frame-versus-value chart. X is always the frame number, labelled as elapsed game time,
/// so several charts stacked together read against one clock and can share a zoom.
/// </summary>
internal sealed class TimeSeriesChart : Control
{
    private const int LeftMargin = 62;
    private const int RightMargin = 12;
    private const int TopMargin = 26;
    private const int BottomMargin = 30;
    private const int LegendHeight = 18;

    private readonly List<ChartSeries> _series = [];
    private readonly List<ChartMarker> _markers = [];

    private int _dataMinFrame, _dataMaxFrame;
    private int _viewMinFrame, _viewMaxFrame;
    private double _viewMaxValue = 1;

    private Point? _hover;
    private bool _panning;
    private int _panAnchorFrame;
    private int _panAnchorX;

    public string Title { get; set; } = "";
    public string ValueSuffix { get; set; } = "";
    /// <summary>Only used to choose a sensible gridline spacing; labels come from TimeLabeller.</summary>
    public int SimulationFps { get; set; } = 60;

    /// <summary>
    /// Turns a frame number into the label shown on the axis. Set from the document, because a
    /// recording whose game speed changed part-way runs at more than one rate and cannot be
    /// labelled by dividing by a single one.
    /// </summary>
    public Func<int, string>? TimeLabeller { get; set; }

    /// <summary>Force the Y axis to start at this value even when the data does not reach it.</summary>
    public double MinimumYRange { get; set; } = 1;

    /// <summary>Charts sharing a group scroll and zoom together.</summary>
    public ChartGroup? Group { get; set; }

    public TimeSeriesChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Font = Theme.Ui;
        Height = 150;
    }

    public IReadOnlyList<ChartSeries> Series => _series;

    public void SetData(IEnumerable<ChartSeries> series, IEnumerable<ChartMarker>? markers = null)
    {
        _series.Clear();
        _series.AddRange(series);
        _markers.Clear();
        if (markers is not null) _markers.AddRange(markers);
        Rescale();
        Invalidate();
    }

    public void Clear() => SetData([]);

    private void Rescale()
    {
        _dataMinFrame = int.MaxValue;
        _dataMaxFrame = int.MinValue;

        foreach (var s in _series)
        {
            foreach (var p in s.Points)
            {
                if (p.Frame < _dataMinFrame) _dataMinFrame = p.Frame;
                if (p.Frame > _dataMaxFrame) _dataMaxFrame = p.Frame;
            }
        }

        if (_dataMinFrame > _dataMaxFrame) { _dataMinFrame = 0; _dataMaxFrame = 1; }
        if (_dataMaxFrame == _dataMinFrame) _dataMaxFrame = _dataMinFrame + 1;

        _viewMinFrame = _dataMinFrame;
        _viewMaxFrame = _dataMaxFrame;
        RecomputeValueRange();
    }

    private void RecomputeValueRange()
    {
        double max = MinimumYRange;
        foreach (var s in _series)
        {
            if (!s.Visible) continue;
            foreach (var p in s.Points)
            {
                if (p.Frame < _viewMinFrame || p.Frame > _viewMaxFrame) continue;
                if (p.Value > max) max = p.Value;
            }
        }
        _viewMaxValue = max * 1.12;
    }

    public void SetViewRange(int minFrame, int maxFrame, bool propagate = true)
    {
        int span = Math.Max(2, maxFrame - minFrame);
        minFrame = Math.Max(_dataMinFrame, minFrame);
        maxFrame = Math.Min(_dataMaxFrame, minFrame + span);
        if (maxFrame - minFrame < 2) return;

        _viewMinFrame = minFrame;
        _viewMaxFrame = maxFrame;
        RecomputeValueRange();
        Invalidate();

        if (propagate) Group?.Broadcast(this, minFrame, maxFrame);
    }

    public void ResetView() => SetViewRange(_dataMinFrame, _dataMaxFrame);

    private Rectangle PlotArea => new(
        LeftMargin, TopMargin,
        Math.Max(1, Width - LeftMargin - RightMargin),
        Math.Max(1, Height - TopMargin - BottomMargin - LegendHeight));

    private float FrameToX(double frame)
    {
        var plot = PlotArea;
        double t = (frame - _viewMinFrame) / (double)(_viewMaxFrame - _viewMinFrame);
        return (float)(plot.Left + t * plot.Width);
    }

    private int XToFrame(int x)
    {
        var plot = PlotArea;
        double t = (x - plot.Left) / (double)plot.Width;
        return (int)Math.Round(_viewMinFrame + t * (_viewMaxFrame - _viewMinFrame));
    }

    private float ValueToY(double value)
    {
        var plot = PlotArea;
        double t = value / _viewMaxValue;
        return (float)(plot.Bottom - t * plot.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);

        var plot = PlotArea;
        using var borderPen = new Pen(Theme.Border);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        if (Title.Length > 0)
        {
            using var titleBrush = new SolidBrush(Theme.Text);
            g.DrawString(Title, Theme.UiBold, titleBrush, 8, 5);
        }

        if (_series.Count == 0 || _series.All(s => s.Points.Count == 0))
        {
            using var mutedBrush = new SolidBrush(Theme.Muted);
            g.DrawString("No data in this recording.", Font, mutedBrush,
                plot.Left + 4, plot.Top + plot.Height / 2f - 8);
            return;
        }

        DrawGrid(g, plot);

        // Series are clipped to the plot: a bar at frame zero is half a bar wide to the left of
        // the axis, and would otherwise paint over the value labels.
        var previousClip = g.Clip;
        g.SetClip(plot);
        DrawMarkers(g, plot);
        foreach (var s in _series)
        {
            if (!s.Visible || s.Points.Count == 0) continue;
            DrawSeries(g, plot, s);
        }
        g.Clip = previousClip;

        DrawLegend(g);
        DrawHover(g, plot);
    }

    private void DrawGrid(Graphics g, Rectangle plot)
    {
        using var gridPen = new Pen(Theme.Grid);
        using var axisPen = new Pen(Theme.Border);
        using var labelBrush = new SolidBrush(Theme.Muted);

        // Y axis: four gridlines at a round step.
        double step = NiceStep(_viewMaxValue / 4);
        for (double v = 0; v <= _viewMaxValue; v += step)
        {
            float y = ValueToY(v);
            if (y < plot.Top - 1 || y > plot.Bottom + 1) continue;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            string label = FormatValue(v) + ValueSuffix;
            var size = g.MeasureString(label, Theme.MonoSmall);
            g.DrawString(label, Theme.MonoSmall, labelBrush, plot.Left - size.Width - 5, y - size.Height / 2);
        }

        g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
        g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);

        // X axis: about six time labels across whatever range is in view.
        int frameSpan = _viewMaxFrame - _viewMinFrame;
        int frameStep = NiceFrameStep(frameSpan / 6, SimulationFps);
        int first = (_viewMinFrame / frameStep) * frameStep;
        for (int frame = first; frame <= _viewMaxFrame; frame += frameStep)
        {
            if (frame < _viewMinFrame) continue;
            float x = FrameToX(frame);
            g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            string label = LabelForFrame(frame);
            var size = g.MeasureString(label, Theme.MonoSmall);
            g.DrawString(label, Theme.MonoSmall, labelBrush, x - size.Width / 2, plot.Bottom + 4);
        }
    }

    private void DrawMarkers(Graphics g, Rectangle plot)
    {
        foreach (var marker in _markers)
        {
            if (marker.Frame < _viewMinFrame || marker.Frame > _viewMaxFrame) continue;
            float x = FrameToX(marker.Frame);
            using var pen = new Pen(Color.FromArgb(90, marker.Color)) { DashStyle = DashStyle.Dot };
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        }
    }

    private void DrawSeries(Graphics g, Rectangle plot, ChartSeries s)
    {
        using var pen = new Pen(s.Color, 1.6f) { LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(Color.FromArgb(38, s.Color));
        using var dot = new SolidBrush(s.Color);

        var points = new List<PointF>(Math.Min(s.Points.Count, plot.Width * 2));
        int previousX = int.MinValue;

        foreach (var p in s.Points)
        {
            if (p.Frame < _viewMinFrame || p.Frame > _viewMaxFrame) continue;
            float x = FrameToX(p.Frame);
            float y = ValueToY(p.Value);

            if (s.Style == SeriesStyle.Bars)
            {
                float barWidth = Math.Max(1.5f, plot.Width / (float)Math.Max(1, s.Points.Count) - 1);
                g.FillRectangle(dot, x - barWidth / 2, y, barWidth, plot.Bottom - y);
                continue;
            }

            if (s.Style == SeriesStyle.Points)
            {
                g.FillEllipse(dot, x - 2f, y - 2f, 4f, 4f);
                continue;
            }

            // Several samples inside one pixel column cannot be told apart on screen; keeping the
            // first of each column holds a 20,000-point series to a few hundred draw points.
            int column = (int)x;
            if (column == previousX && points.Count > 0) continue;
            previousX = column;

            if (s.Style == SeriesStyle.Step && points.Count > 0)
                points.Add(new PointF(x, points[^1].Y));

            points.Add(new PointF(x, y));
        }

        if (points.Count < 2)
        {
            if (points.Count == 1) g.FillEllipse(dot, points[0].X - 2.5f, points[0].Y - 2.5f, 5f, 5f);
            return;
        }

        // A step series usually sits high in its own range, so filling under it turns the whole
        // plot into one block and hides the second player's line behind it.
        if (s.Style != SeriesStyle.Step)
        {
            var area = new List<PointF>(points.Count + 2) { new(points[0].X, plot.Bottom) };
            area.AddRange(points);
            area.Add(new PointF(points[^1].X, plot.Bottom));
            g.FillPolygon(fill, [.. area]);
        }

        g.DrawLines(pen, [.. points]);
    }

    private void DrawLegend(Graphics g)
    {
        float x = LeftMargin;
        float y = Height - LegendHeight - 4;
        using var brush = new SolidBrush(Theme.Muted);

        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            using var swatch = new SolidBrush(s.Visible ? s.Color : Theme.Border);
            g.FillRectangle(swatch, x, y + 4, 9, 9);
            var size = g.MeasureString(s.Name, Theme.MonoSmall);
            g.DrawString(s.Name, Theme.MonoSmall, brush, x + 12, y + 1);
            x += 12 + size.Width + 12;
            if (x > Width - 40) break;
        }
    }

    private void DrawHover(Graphics g, Rectangle plot)
    {
        if (_hover is not { } h || !plot.Contains(h)) return;

        int frame = XToFrame(h.X);
        using var crosshair = new Pen(Theme.Muted) { DashStyle = DashStyle.Dot };
        float x = FrameToX(frame);
        g.DrawLine(crosshair, x, plot.Top, x, plot.Bottom);

        var lines = new List<(string Text, Color Color)>
        {
            ($"frame {frame:N0}  ({LabelForFrame(frame)})", Theme.Text),
        };

        foreach (var s in _series)
        {
            if (!s.Visible || s.Points.Count == 0) continue;
            var nearest = NearestSample(s, frame);
            if (nearest is null) continue;
            lines.Add(($"{s.Name}: {FormatValue(nearest.Value.Value)}{ValueSuffix}", s.Color));
        }

        float width = 0, height = 4;
        foreach (var (text, _) in lines)
        {
            var size = g.MeasureString(text, Theme.MonoSmall);
            width = Math.Max(width, size.Width);
            height += size.Height;
        }
        width += 12;

        float boxX = x + 10;
        if (boxX + width > plot.Right) boxX = x - width - 10;
        float boxY = Math.Min(h.Y, plot.Bottom - height - 4);

        using var back = new SolidBrush(Color.FromArgb(244, 255, 255, 255));
        using var border = new Pen(Theme.Border);
        g.FillRectangle(back, boxX, boxY, width, height);
        g.DrawRectangle(border, boxX, boxY, width, height);

        float ty = boxY + 2;
        foreach (var (text, color) in lines)
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, Theme.MonoSmall, brush, boxX + 6, ty);
            ty += g.MeasureString(text, Theme.MonoSmall).Height;
        }
    }

    private string LabelForFrame(int frame) => TimeLabeller is { } label
        ? label(frame)
        : ReplayDocument.FormatTime(TimeSpan.FromSeconds(frame / (double)Math.Max(1, SimulationFps)));

    private static Sample? NearestSample(ChartSeries s, int frame)
    {
        Sample? best = null;
        int bestDistance = int.MaxValue;
        foreach (var p in s.Points)
        {
            int distance = Math.Abs(p.Frame - frame);
            if (distance < bestDistance) { bestDistance = distance; best = p; }
            else if (p.Frame > frame) break;
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_panning)
        {
            int span = _viewMaxFrame - _viewMinFrame;
            double framesPerPixel = span / (double)Math.Max(1, PlotArea.Width);
            int shift = (int)((_panAnchorX - e.X) * framesPerPixel);
            int min = Math.Clamp(_panAnchorFrame + shift, _dataMinFrame, Math.Max(_dataMinFrame, _dataMaxFrame - span));
            SetViewRange(min, min + span);
            return;
        }

        _hover = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _panning = true;
        _panAnchorX = e.X;
        _panAnchorFrame = _viewMinFrame;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _panning = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        ResetView();
        Group?.Broadcast(this, _dataMinFrame, _dataMaxFrame);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // These charts sit in a tall scrolling column, so a plain wheel belongs to the page. A
        // chart that zoomed on it would trap the scroll the moment the pointer crossed one, and
        // leave no way back out once the column had nothing left to scroll. Ctrl is the modifier
        // everything else uses for zoom, so it is the one used here.
        //
        // Leaving a plain wheel unhandled is what scrolls the page: WinForms passes an unhandled
        // wheel on to the parent, which reaches the scrolling column and moves it by the system's
        // own step. Doing that by hand here would scroll it twice over.
        if ((ModifierKeys & Keys.Control) == 0)
            return;

        var plot = PlotArea;

        // Zoom about the pointer when it is over the plot, and about the middle when it is over
        // the title or the legend, so a wheel anywhere on the chart still does something sensible.
        int anchor = XToFrame(plot.Contains(e.Location) ? e.X : plot.Left + plot.Width / 2);
        double factor = e.Delta > 0 ? 0.75 : 1 / 0.75;
        int span = (int)Math.Max(SimulationFps, (_viewMaxFrame - _viewMinFrame) * factor);
        double leftShare = (anchor - _viewMinFrame) / (double)Math.Max(1, _viewMaxFrame - _viewMinFrame);

        int min = (int)(anchor - span * leftShare);
        SetViewRange(min, min + span);

        // And stop it here. An unhandled wheel is passed on to the parent, so without this the
        // zoom would scroll the column at the same time.
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    private static string FormatValue(double v)
    {
        if (Math.Abs(v) >= 1000) return v.ToString("N0");
        if (Math.Abs(v) >= 10) return v.ToString("0.#");
        return v.ToString("0.##");
    }

    private static double NiceStep(double rough)
    {
        if (rough <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalised = rough / magnitude;
        double nice = normalised switch { <= 1 => 1, <= 2 => 2, <= 5 => 5, _ => 10 };
        return nice * magnitude;
    }

    private static int NiceFrameStep(int rough, int fps)
    {
        // Time labels read best on whole seconds and minutes rather than on round frame counts.
        int[] seconds = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        foreach (int s in seconds)
            if (s * fps >= rough) return s * fps;
        return Math.Max(1, rough);
    }
}

/// <summary>Keeps a stack of charts on one X range, so panning any of them moves all of them.</summary>
internal sealed class ChartGroup
{
    private readonly List<TimeSeriesChart> _members = [];
    private bool _broadcasting;

    public void Add(TimeSeriesChart chart)
    {
        chart.Group = this;
        _members.Add(chart);
    }

    public void Clear() => _members.Clear();

    public void Broadcast(TimeSeriesChart origin, int minFrame, int maxFrame)
    {
        if (_broadcasting) return;
        _broadcasting = true;
        try
        {
            foreach (var member in _members)
                if (!ReferenceEquals(member, origin))
                    member.SetViewRange(minFrame, maxFrame, propagate: false);
        }
        finally { _broadcasting = false; }
    }
}
