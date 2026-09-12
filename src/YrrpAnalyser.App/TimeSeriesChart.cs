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
    public DashStyle DashStyle { get; init; } = DashStyle.Solid;
    public IReadOnlyList<Sample> Points { get; init; } = [];
    public bool Visible { get; set; } = true;

    /// <summary>
    /// What the series is about - a player - the same on every chart, so a <see cref="ChartGroup"/> can
    /// show and hide it everywhere at once. Null leaves the series to its own chart's legend.
    /// </summary>
    public string? Key { get; init; }
}

/// <summary>
/// Marks a moment worth seeing against the series - a stall, a chat message, the frame a desync
/// started on.
/// </summary>
internal sealed record ChartMarker(int Frame, Color Color, string Label);

/// <summary>A button in a chart's title bar.</summary>
internal sealed record ChartButton(string Caption, Action Click);

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
    private int _legendHeight = 22;
    private readonly List<(ChartSeries? Series, Rectangle Bounds)> _legend = [];
    private bool _layingOutLegend;

    private readonly List<ChartSeries> _series = [];
    private readonly List<ChartMarker> _markers = [];

    private int _dataMinFrame, _dataMaxFrame;
    private int _viewMinFrame, _viewMaxFrame;
    private double _viewMaxValue = 1;

    private Point? _hover;
    private bool _panning;
    private int _panAnchorFrame;
    private int _panAnchorX;
    // The frame another chart in the group is hovered at, shown here as well.
    private int? _linkedHoverFrame;

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

    /// <summary>Buttons at the right of the title bar, left to right.</summary>
    public IReadOnlyList<ChartButton> Buttons { get; set; } = [];

    public IReadOnlyList<ChartMarker> Markers => _markers;
    public int ViewMinFrame => _viewMinFrame;
    public int ViewMaxFrame => _viewMaxFrame;

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
        LayoutLegend();
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
            // A zoom can sit entirely between samples. Include the values of the segments
            // crossing its edges, without scaling to peaks outside the visible range.
            if (s.Style is SeriesStyle.Line or SeriesStyle.Step)
            {
                max = Math.Max(max, ValueAtFrame(s, _viewMinFrame) ?? 0);
                max = Math.Max(max, ValueAtFrame(s, _viewMaxFrame) ?? 0);
            }
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
        Math.Max(1, Height - TopMargin - BottomMargin - _legendHeight));

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        LayoutLegend();
    }

    private void LayoutLegend()
    {
        if (_layingOutLegend) return;
        _layingOutLegend = true;
        try
        {
            _legend.Clear();
            int rowHeight = Math.Max(22, TextRenderer.MeasureText("Ag", Theme.MonoSmall).Height + 6);
            int available = Math.Max(1, Width - LeftMargin - RightMargin);
            int x = LeftMargin, y = 0;
            var entries = _series.Where(s => s.Points.Count > 0).Cast<ChartSeries?>().ToList();
            if (entries.Count > 0) entries.Add(null); // Show all remains available after hiding every series.
            foreach (var s in entries)
            {
                int width = Math.Min(available, TextRenderer.MeasureText(s?.Name ?? "Show all", Theme.MonoSmall).Width + 32);
                if (x > LeftMargin && x + width > Width - RightMargin)
                {
                    x = LeftMargin;
                    y += rowHeight;
                }
                _legend.Add((s, new Rectangle(x, y, width, rowHeight)));
                x += width;
            }
            int height = y + rowHeight;
            int change = height - _legendHeight;
            _legendHeight = height;
            // Preserve plot height when a narrow window needs more legend rows.
            Height += change;
            Invalidate();
        }
        finally { _layingOutLegend = false; }
    }

    private Rectangle LegendBounds(Rectangle bounds) =>
        new(bounds.X, Height - _legendHeight - 4 + bounds.Y, bounds.Width, bounds.Height);

    private int LegendAt(Point point) => _legend.FindIndex(entry => LegendBounds(entry.Bounds).Contains(point));

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

        var buttons = ButtonBounds();
        if (Title.Length > 0)
        {
            using var titleBrush = new SolidBrush(Theme.Text);
            using var titleFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            float right = buttons.Count == 0 ? Width - RightMargin : buttons[0].Left - 8;
            g.DrawString(Title, Theme.UiBold, titleBrush, new RectangleF(8, 5, Math.Max(1, right - 8), TopMargin), titleFormat);
        }
        for (int i = 0; i < buttons.Count; i++)
            DrawTitleButton(g, Buttons[i].Caption, buttons[i]);

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
        using var previousClip = g.Clip;
        g.SetClip(plot);
        DrawMarkers(g, plot);
        foreach (var s in _series)
        {
            if (!s.Visible || s.Points.Count == 0) continue;
            DrawSeries(g, plot, s);
        }
        g.Clip = previousClip;

        DrawLegend(g);
        if (!_series.Any(s => s.Visible && s.Points.Count > 0))
        {
            using var muted = new SolidBrush(Theme.Muted);
            g.DrawString("All series hidden. Click a legend name or Show all.", Font, muted, plot.Left + 4, plot.Top + 8);
        }
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
        using var pen = new Pen(s.Color, 2f) { LineJoin = LineJoin.Round, DashStyle = s.DashStyle };
        using var dot = new SolidBrush(s.Color);

        var points = new List<PointF>(Math.Min(s.Points.Count, plot.Width * 2));
        int previousX = int.MinValue;

        for (int i = 0; i < s.Points.Count; i++)
        {
            var p = s.Points[i];
            // Keep neighbouring samples so lines cross the visible zoom boundaries.
            if (s.Style is SeriesStyle.Line or SeriesStyle.Step)
            {
                if (p.Frame < _viewMinFrame && (i + 1 == s.Points.Count || s.Points[i + 1].Frame < _viewMinFrame)) continue;
                if (p.Frame > _viewMaxFrame && (i == 0 || s.Points[i - 1].Frame > _viewMaxFrame)) break;
            }
            else if (p.Frame < _viewMinFrame || p.Frame > _viewMaxFrame) continue;
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

        g.DrawLines(pen, [.. points]);
    }

    private void DrawLegend(Graphics g)
    {
        foreach (var (s, bounds) in _legend)
        {
            var rect = LegendBounds(bounds);
            if (_hover is { } h && rect.Contains(h))
            {
                using var highlight = new SolidBrush(Theme.Grid);
                g.FillRectangle(highlight, rect);
            }
            if (s is not null)
            {
                using var swatch = new Pen(s.Visible ? s.Color : Theme.Border, 2f) { DashStyle = s.DashStyle };
                g.DrawLine(swatch, rect.X + 4, rect.Y + rect.Height / 2, rect.X + 19, rect.Y + rect.Height / 2);
            }
            var label = new Rectangle(rect.X + 24, rect.Y, Math.Max(1, rect.Width - 28), rect.Height);
            TextRenderer.DrawText(g, s?.Name ?? "Show all", Theme.MonoSmall, label,
                s is null ? Theme.Accent : s.Visible ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (s is { Visible: false })
            {
                using var strike = new Pen(Theme.Muted);
                g.DrawLine(strike, label.Left, label.Top + label.Height / 2, label.Right, label.Top + label.Height / 2);
            }
        }
    }

    private void DrawHover(Graphics g, Rectangle plot)
    {
        int frame;
        float anchorY;
        if (_hover is { } h && plot.Contains(h))
        {
            frame = XToFrame(h.X);
            anchorY = h.Y;
        }
        else if (_linkedHoverFrame is { } linked && linked >= _viewMinFrame && linked <= _viewMaxFrame)
        {
            // Another chart in the group is hovered: show the same moment here.
            frame = linked;
            anchorY = plot.Top + 4;
        }
        else return;

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
            double? value = s.Style is SeriesStyle.Line or SeriesStyle.Step
                ? ValueAtFrame(s, frame) : NearestSample(s, frame)?.Value;
            if (value is null) continue;
            lines.Add(($"{s.Name}: {FormatValue(value.Value)}{ValueSuffix}", s.Color));
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
        boxX = Math.Max(4, Math.Min(boxX, Width - width - 4));
        float boxY = Math.Max(TopMargin, Math.Min(anchorY, Height - _legendHeight - height - 4));

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

    /// <summary>Where each title-bar button sits, in the order of <see cref="Buttons"/>, packed to the right.</summary>
    private List<Rectangle> ButtonBounds()
    {
        var bounds = new List<Rectangle>(Buttons.Count);
        int right = Width - RightMargin;
        for (int i = Buttons.Count - 1; i >= 0; i--)
        {
            var size = TextRenderer.MeasureText(Buttons[i].Caption, Theme.MonoSmall);
            int width = size.Width + 14, height = Math.Min(TopMargin - 6, size.Height + 4);
            bounds.Insert(0, new Rectangle(right - width, 3, width, height));
            right -= width + 4;
        }
        return bounds;
    }

    private int ButtonAt(Point point) => ButtonBounds().FindIndex(b => b.Contains(point));

    private void DrawTitleButton(Graphics g, string caption, Rectangle bounds)
    {
        bool hot = _hover is { } h && bounds.Contains(h);
        using (var fill = new SolidBrush(hot ? Theme.Grid : Theme.Panel)) g.FillRectangle(fill, bounds);
        using (var border = new Pen(Theme.Border)) g.DrawRectangle(border, bounds);
        TextRenderer.DrawText(g, caption, Theme.MonoSmall, bounds, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private bool IsClickable(Point point) => LegendAt(point) >= 0 || ButtonAt(point) >= 0;

    internal void ShowLinkedHover(int? frame)
    {
        if (_linkedHoverFrame == frame) return;
        _linkedHoverFrame = frame;
        Invalidate();
    }

    /// <summary>Shows or hides every keyed series the map names; see <see cref="ChartGroup.BroadcastVisibility"/>.</summary>
    internal void ApplyVisibility(IReadOnlyDictionary<string, bool> shownByKey)
    {
        bool changed = false;
        foreach (var s in _series)
        {
            if (s.Key is { } key && shownByKey.TryGetValue(key, out bool shown) && s.Visible != shown)
            {
                s.Visible = shown;
                changed = true;
            }
        }
        if (!changed) return;
        RecomputeValueRange();
        Invalidate();
    }

    private string LabelForFrame(int frame) => TimeLabeller is { } label
        ? label(frame)
        : ReplayDocument.FormatTime(TimeSpan.FromSeconds(frame / (double)Math.Max(1, SimulationFps)));

    private static double? ValueAtFrame(ChartSeries s, int frame)
    {
        if (s.Points.Count == 0 || frame < s.Points[0].Frame || frame > s.Points[^1].Frame) return null;
        int low = 0, high = s.Points.Count - 1;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (s.Points[mid].Frame <= frame) low = mid;
            else high = mid - 1;
        }
        var before = s.Points[low];
        if (s.Style == SeriesStyle.Step || before.Frame == frame || low == s.Points.Count - 1)
            return before.Value;
        var after = s.Points[low + 1];
        return before.Value + (after.Value - before.Value) * (frame - before.Frame) / (after.Frame - before.Frame);
    }

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
        Cursor = IsClickable(e.Location) ? Cursors.Hand : Cursors.Default;
        Invalidate();
        Group?.BroadcastHover(this, PlotArea.Contains(e.Location) ? XToFrame(e.X) : null);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        if (!_panning) Cursor = Cursors.Default;
        Invalidate();
        Group?.BroadcastHover(this, null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int button = ButtonAt(e.Location);
        if (button >= 0)
        {
            Buttons[button].Click();
            return;
        }
        int legendIndex = LegendAt(e.Location);
        if (legendIndex >= 0)
        {
            var selected = _legend[legendIndex].Series;
            if (selected is null)
                foreach (var s in _series) s.Visible = true;
            else if ((ModifierKeys & Keys.Shift) != 0)
                foreach (var s in _series) s.Visible = ReferenceEquals(s, selected);
            else
                selected.Visible = !selected.Visible;
            RecomputeValueRange();
            Invalidate();
            Group?.BroadcastVisibility(this);
            return;
        }
        if (!PlotArea.Contains(e.Location)) return;
        _panning = true;
        Capture = true;
        _panAnchorX = e.X;
        _panAnchorFrame = _viewMinFrame;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _panning = false;
        Capture = false;
        Cursor = IsClickable(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) { _panning = false; Cursor = Cursors.Default; }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || !PlotArea.Contains(e.Location)) return;
        ResetView();
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

    public void Remove(TimeSeriesChart chart)
    {
        _members.Remove(chart);
        if (ReferenceEquals(chart.Group, this)) chart.Group = null;
    }

    /// <summary>Shows the frame one chart is hovered at on every other chart, or clears it with null.</summary>
    public void BroadcastHover(TimeSeriesChart origin, int? frame)
    {
        foreach (var member in _members)
            if (!ReferenceEquals(member, origin))
                member.ShowLinkedHover(frame);
    }

    /// <summary>
    /// Matches every other chart's keyed series to one chart's legend, so hiding or isolating a player
    /// on one chart does it on all of them. A key counts as shown when any of its series is.
    /// </summary>
    public void BroadcastVisibility(TimeSeriesChart origin)
    {
        var shown = new Dictionary<string, bool>();
        foreach (var s in origin.Series)
            if (s.Key is { } key)
                shown[key] = shown.GetValueOrDefault(key) || s.Visible;
        if (shown.Count == 0) return;

        foreach (var member in _members)
            if (!ReferenceEquals(member, origin))
                member.ApplyVisibility(shown);
    }

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
