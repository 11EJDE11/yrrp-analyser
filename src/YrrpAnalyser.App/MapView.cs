using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using YrrpAnalyser.Map;

namespace YrrpAnalyser.App;

internal enum HeatLayer { None, Fighting, Presence, Orders }

/// <summary>
/// The map at one moment of the game: terrain, buildings, units, where units were ordered to and
/// where they have come from, what died, beacons, superweapons and the recording player's camera,
/// with an optional heatmap underneath. Wheel to zoom, drag to pan, double-click to fit.
/// </summary>
internal sealed class MapView : Control
{
    private MatchAnalysis? _match;
    private MapInfo? _map;
    private Bitmap? _terrain;
    private double _terrainScale = 1;

    private double _zoom = 1;          // screen pixels per map pixel
    private PointF _offset;            // where map pixel (0,0) lands on screen
    private bool _fitted;
    // Until the user pans or zooms, the map keeps fitting itself to the control as it is laid out.
    private bool _userMoved;
    private Point? _dragFrom;
    private PointF _dragOffset;
    private Point? _mouse;

    private Bitmap? _heat;
    private (int Frame, HeatLayer Layer, int Window, int Hidden) _heatKey = (-1, HeatLayer.None, 0, 0);
    private List<ObjectState> _states = [];
    private ObjectState? _hovered;
    private readonly ToolTip _tip = new() { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 30000 };
    private string _tipText = "";

    public int Frame { get; private set; }
    public Func<int, Color> HouseColour { get; set; } = Theme.ForHouse;
    public Func<int, string> HouseName { get; set; } = h => $"House {h}";
    public Func<int, bool> IsPlayer { get; set; } = _ => true;
    public Func<ObjectKind, int, TypeInfo?> TypeOf { get; set; } = (_, _) => null;
    public Func<int, string> TimeLabel { get; set; } = f => f.ToString();
    public Func<int, int> SecondsToFrames { get; set; } = s => s * 60;

    public HashSet<int> HiddenHouses { get; } = [];
    public bool ShowUnits { get; set; } = true;
    public bool ShowBuildings { get; set; } = true;
    public bool ShowOrders { get; set; } = true;
    public bool ShowTrails { get; set; } = true;
    public bool ShowDeaths { get; set; } = true;
    public bool ShowCamera { get; set; } = true;
    public bool ShowMarkers { get; set; } = true;
    public HeatLayer Heat { get; set; } = HeatLayer.None;
    /// <summary>How far back the orders, trails and heatmap look, in seconds of game time.</summary>
    public int WindowSeconds { get; set; } = 30;

    public MapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(18, 21, 26);
    }

    public void SetMatch(MatchAnalysis? match)
    {
        _match = match;
        _map = match?.Map;
        _terrain?.Dispose();
        _terrain = null;
        _heat?.Dispose();
        _heat = null;
        _heatKey = (-1, HeatLayer.None, 0, 0);
        if (_map is { IsUsable: true } map)
        {
            _terrainScale = TerrainRenderer.ScaleFor(map);
            _terrain = TerrainRenderer.Render(map, _terrainScale);
        }
        _fitted = false;
        _userMoved = false;
        Invalidate();
    }

    public void SetFrame(int frame)
    {
        Frame = frame;
        Invalidate();
    }

    public void Refresh(bool heatChanged)
    {
        if (heatChanged) _heatKey = (-1, HeatLayer.None, 0, 0);
        Invalidate();
    }

    public void FitToView()
    {
        if (_map is not { IsUsable: true } map || Width < 10 || Height < 10) return;
        _zoom = Math.Min(Width / map.PixelWidth, Height / map.PixelHeight) * 0.98;
        _offset = new PointF((float)((Width - map.PixelWidth * _zoom) / 2), (float)((Height - map.PixelHeight * _zoom) / 2));
        _fitted = true;
        _userMoved = false;
    }

    /// <summary>Centres the view on a cell, keeping the zoom, e.g. when a feed entry is clicked.</summary>
    public void CentreOn(double x, double y)
    {
        if (_map is null) return;
        var (px, py) = _map.ToPixel(x, y, _map.LevelAt(x, y));
        _offset = new PointF((float)(Width / 2.0 - px * _zoom), (float)(Height / 2.0 - py * _zoom));
        _userMoved = true;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_userMoved) FitToView();
    }

    private PointF Screen(double cellX, double cellY, double level)
    {
        var (px, py) = _map!.ToPixel(cellX, cellY, level);
        return new PointF((float)(_offset.X + px * _zoom), (float)(_offset.Y + py * _zoom));
    }

    private PointF Ground(double cellX, double cellY) => Screen(cellX, cellY, _map!.LevelAt(cellX, cellY));

    /// <summary>Screen position of an object: recorded height where there is one, else the ground.</summary>
    private PointF At(in ObjectState s) =>
        s.Height > 0 ? Screen(s.X, s.Y, s.Height * 16 / 104.0) : Ground(s.X, s.Y);

    private Color ColourOf(int house) => IsPlayer(house) ? HouseColour(house) : Color.FromArgb(150, 150, 150);

    private int WindowFrames => SecondsToFrames(WindowSeconds);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_match is null || _map is not { IsUsable: true })
        {
            TextRenderer.DrawText(g, _match is null ? "No replay open." : "This recording's map has no size to draw.",
                Theme.Ui, ClientRectangle, Color.FromArgb(170, 176, 186),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (!_fitted) FitToView();

        var mapRect = new RectangleF(_offset.X, _offset.Y, (float)(_map.PixelWidth * _zoom), (float)(_map.PixelHeight * _zoom));
        if (_terrain is not null)
        {
            g.InterpolationMode = _zoom / _terrainScale > 2.5 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_terrain, mapRect);
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        // Dimmed a little so what is on it reads first.
        using (var dim = new SolidBrush(Color.FromArgb(Heat == HeatLayer.None ? 60 : 110, 10, 12, 16)))
            g.FillRectangle(dim, mapRect);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        _states = _match.StatesAt(Frame).Where(s => !HiddenHouses.Contains(s.Owner)).ToList();

        if (Heat != HeatLayer.None) DrawHeat(g, mapRect);
        if (ShowMarkers) DrawStartPositions(g);
        if (ShowBuildings) DrawBuildings(g);
        if (ShowTrails && _match.HasRecordedPositions) DrawTrails(g);
        if (ShowOrders) DrawOrders(g);
        if (ShowDeaths && _match.HasRecordedPositions) DrawDeaths(g);
        if (ShowUnits) DrawUnits(g);
        if (ShowMarkers) { DrawBeacons(g); DrawSuperweapons(g); }
        if (ShowCamera) DrawCamera(g);
        DrawHover(g);
        DrawBanner(g);
    }

    // --- layers -------------------------------------------------------------------------------

    private void DrawStartPositions(Graphics g)
    {
        foreach (var (index, cell) in _map!.StartLocations)
        {
            var p = Ground(cell.X + 0.5, cell.Y + 0.5);
            using var ring = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f) { DashStyle = DashStyle.Dot };
            float r = (float)Math.Max(10, 90 * _zoom);
            g.DrawEllipse(ring, p.X - r, p.Y - r / 2, r * 2, r);
            TextRenderer.DrawText(g, (index + 1).ToString(), Theme.MonoSmall, new Point((int)p.X - 4, (int)(p.Y - r / 2) - 14),
                Color.FromArgb(200, 255, 255, 255));
        }
    }

    private void DrawBuildings(Graphics g)
    {
        if (_match!.HasRecordedPositions)
        {
            // The map's own civilian and tech buildings first and faint, so a city map does not bury the bases.
            foreach (var s in _states.Where(s => s.Kind == ObjectKind.Building).OrderBy(s => IsPlayer(s.Owner)))
                DrawFoundation(g, (int)Math.Floor(s.X), (int)Math.Floor(s.Y), s.FoundationWidth, s.FoundationHeight,
                    IsPlayer(s.Owner) ? ColourOf(s.Owner) : Color.FromArgb(90, 150, 150, 150), s.Health, estimated: false,
                    TypeOf(ObjectKind.Building, s.TypeIndex));
            return;
        }

        // Without positions the buildings are where they were placed, until their owner is defeated.
        // The recording cannot say which were destroyed or sold before that.
        foreach (var b in _match.Placements)
        {
            if (b.Frame > Frame || HiddenHouses.Contains(b.House)) continue;
            if (_match.DefeatedAt.TryGetValue(b.House, out int defeated) && defeated <= Frame) continue;
            DrawFoundation(g, b.X, b.Y, 2, 2, ColourOf(b.House), 1, estimated: true, TypeOf(ObjectKind.Building, b.TypeIndex));
        }
    }

    private void DrawFoundation(Graphics g, int x, int y, int w, int h, Color colour, double health, bool estimated, TypeInfo? type)
    {
        int level = _map!.LevelAt(x, y);
        PointF[] corners = [Screen(x, y, level), Screen(x + w, y, level), Screen(x + w, y + h, level), Screen(x, y + h, level)];
        using var fill = new SolidBrush(Color.FromArgb(estimated ? 90 : colour.A < 255 ? colour.A : 165, colour.R, colour.G, colour.B));
        using var edge = new Pen(Theme.Blend(colour, Color.Black, 0.35), estimated ? 1f : 1.4f);
        if (estimated) edge.DashStyle = DashStyle.Dash;
        g.FillPolygon(fill, corners);
        g.DrawPolygon(edge, corners);

        float span = corners[1].X - corners[3].X;
        var centre = new PointF((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2);
        if (type is not null && span >= 46)
        {
            float cw = Math.Min(60, span * 0.55f), ch = cw * 0.8f;
            var bounds = Rectangle.Round(new RectangleF(centre.X - cw / 2, centre.Y - ch / 2, cw, ch));
            CameoAtlas.Draw(g, bounds, type.Id, type.Cameo, type.DisplayName);
        }
        if (!estimated && health < 0.999 && span >= 14)
        {
            float bw = span * 0.5f;
            var bar = new RectangleF(centre.X - bw / 2, corners[2].Y + 2, bw, 3);
            using var back = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            using var hp = new SolidBrush(HealthColour(health));
            g.FillRectangle(back, bar);
            g.FillRectangle(hp, bar.X, bar.Y, (float)(bar.Width * health), bar.Height);
        }
    }

    private static Color HealthColour(double health) =>
        health > 0.5 ? Color.FromArgb(90, 210, 90) : health > 0.25 ? Color.FromArgb(230, 200, 60) : Color.FromArgb(230, 70, 60);

    private void DrawTrails(Graphics g)
    {
        int from = Frame - SecondsToFrames(Math.Min(WindowSeconds, 20));
        foreach (var s in _states)
        {
            if (s.Kind == ObjectKind.Building) continue;
            var trail = _match!.TrailOf(s.Id, from, Frame);
            if (trail.Count < 1) continue;
            var points = trail.Select(p => Ground(p.X, p.Y)).Append(At(s)).ToArray();
            if (points.Length < 2) continue;
            using var pen = new Pen(Color.FromArgb(110, ColourOf(s.Owner)), s.Kind == ObjectKind.Infantry ? 1f : 1.6f)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, points);
        }
    }

    private void DrawOrders(Graphics g)
    {
        int from = Frame - SecondsToFrames(Math.Min(WindowSeconds, 12));
        var current = _states.ToDictionary(s => s.Id, s => s);
        foreach (var o in OrdersBetween(from, Frame))
        {
            if (HiddenHouses.Contains(o.House)) continue;
            double age = (Frame - o.Frame) / (double)Math.Max(1, Frame - from);
            int alpha = (int)(200 * (1 - age)) + 30;
            var colour = o.IsAttack ? Color.FromArgb(235, 80, 60) : ColourOf(o.House);
            var to = Ground(o.ToX, o.ToY);
            PointF? start = current.TryGetValue(o.Id, out var s) ? At(s)
                : o.FromX is { } fx && o.FromY is { } fy ? Ground(fx, fy) : null;

            using var pen = new Pen(Color.FromArgb(alpha, colour), 1.2f) { DashStyle = DashStyle.Dash };
            if (start is { } p && Distance(p, to) > 4) g.DrawLine(pen, p, to);
            using var mark = new Pen(Color.FromArgb(alpha, colour), 1.5f);
            float r = o.IsAttack ? 5 : 3.5f;
            if (o.IsAttack)
            {
                g.DrawLine(mark, to.X - r, to.Y - r, to.X + r, to.Y + r);
                g.DrawLine(mark, to.X - r, to.Y + r, to.X + r, to.Y - r);
            }
            else g.DrawEllipse(mark, to.X - r, to.Y - r / 2, r * 2, r);
        }
    }

    private IEnumerable<OrderEvent> OrdersBetween(int from, int to)
    {
        var orders = _match!.Orders;
        int lo = 0, hi = orders.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (orders[mid].Frame < from) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < orders.Count && orders[i].Frame <= to; i++) yield return orders[i];
    }

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void DrawDeaths(Graphics g)
    {
        int window = SecondsToFrames(15);
        foreach (var d in _match!.Deaths)
        {
            if (d.Frame > Frame) break;
            if (d.Frame < Frame - window || HiddenHouses.Contains(d.Owner)) continue;
            double age = (Frame - d.Frame) / (double)window;
            var p = Ground(d.X, d.Y);
            float r = d.Kind == ObjectKind.Building ? 7 : d.Kind == ObjectKind.Infantry ? 2.5f : 4;
            using var pen = new Pen(Color.FromArgb((int)(230 * (1 - age)), ColourOf(d.Owner)), 1.6f);
            g.DrawLine(pen, p.X - r, p.Y - r, p.X + r, p.Y + r);
            g.DrawLine(pen, p.X - r, p.Y + r, p.X + r, p.Y - r);
            if (age < 0.15)
            {
                float burst = (float)(r * 2 + age * 60);
                using var flash = new Pen(Color.FromArgb((int)(160 * (1 - age / 0.15)), 255, 190, 90), 1.2f);
                g.DrawEllipse(flash, p.X - burst, p.Y - burst / 2, burst * 2, burst);
            }
        }
    }

    private void DrawUnits(Graphics g)
    {
        float scale = (float)Math.Clamp(_zoom * 4, 0.8, 2.2);
        foreach (var s in _states)
        {
            if (s.Kind == ObjectKind.Building) continue;
            var p = At(s);
            var colour = ColourOf(s.Owner);
            int alpha = (int)(255 * s.Opacity);
            using var fill = new SolidBrush(Color.FromArgb(alpha, colour));
            using var edge = new Pen(Color.FromArgb(alpha, 12, 14, 18), 1f);

            if (s.Estimated)
            {
                float r = 3.2f * scale;
                using var ring = new Pen(Color.FromArgb(alpha, colour), 1.6f);
                g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
                continue;
            }

            switch (s.Kind)
            {
                case ObjectKind.Infantry:
                {
                    float r = 1.9f * scale;
                    g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
                    g.DrawEllipse(edge, p.X - r, p.Y - r, r * 2, r * 2);
                    break;
                }
                case ObjectKind.Aircraft:
                {
                    float r = 4.2f * scale;
                    PointF[] tri = [new(p.X, p.Y - r), new(p.X + r * 0.9f, p.Y + r * 0.7f), new(p.X - r * 0.9f, p.Y + r * 0.7f)];
                    g.FillPolygon(fill, tri);
                    g.DrawPolygon(edge, tri);
                    break;
                }
                default:
                {
                    float r = 3.1f * scale;
                    g.FillRectangle(fill, p.X - r, p.Y - r, r * 2, r * 2);
                    g.DrawRectangle(edge, p.X - r, p.Y - r, r * 2, r * 2);
                    break;
                }
            }
            if ((s.Flags & ObjectFlags.Elite) != 0 || (s.Flags & ObjectFlags.Veteran) != 0)
            {
                using var chevron = new Pen(Color.FromArgb(alpha, 250, 220, 90), 1.2f);
                float y = p.Y - 4.5f * scale;
                g.DrawLines(chevron, [new PointF(p.X - 2.5f, y), new PointF(p.X, y - 2), new PointF(p.X + 2.5f, y)]);
            }
        }
    }

    private void DrawBeacons(Graphics g)
    {
        foreach (var b in _match!.Beacons)
        {
            if (b.From > Frame || b.To <= Frame || HiddenHouses.Contains(b.House)) continue;
            var p = Ground(b.X, b.Y);
            var colour = ColourOf(b.House);
            using var fill = new SolidBrush(colour);
            using var edge = new Pen(Color.White, 1.2f);
            PointF[] pin = [new(p.X, p.Y), new(p.X - 5, p.Y - 10), new(p.X + 5, p.Y - 10)];
            g.FillPolygon(fill, pin);
            g.FillEllipse(fill, p.X - 6, p.Y - 17, 12, 12);
            g.DrawEllipse(edge, p.X - 6, p.Y - 17, 12, 12);
            if (b.Text.Length > 0)
                DrawLabel(g, b.Text, new PointF(p.X + 9, p.Y - 20), colour);
        }
    }

    private void DrawSuperweapons(Graphics g)
    {
        int window = SecondsToFrames(20);
        foreach (var sw in _match!.Superweapons)
        {
            if (sw.Frame > Frame) break;
            if (sw.Frame < Frame - window) continue;
            double age = (Frame - sw.Frame) / (double)window;
            var p = Ground(sw.X + 0.5, sw.Y + 0.5);
            var colour = ColourOf(sw.House);
            for (int ring = 0; ring < 3; ring++)
            {
                double phase = (age * 3 + ring / 3.0) % 1;
                float r = (float)(10 + phase * 60 * Math.Max(0.6, _zoom * 3));
                using var pen = new Pen(Color.FromArgb((int)(200 * (1 - phase) * (1 - age)), colour), 2f);
                g.DrawEllipse(pen, p.X - r, p.Y - r / 2, r * 2, r);
            }
            DrawLabel(g, sw.Name, new PointF(p.X + 10, p.Y - 8), colour);
        }
    }

    private void DrawCamera(Graphics g)
    {
        if (_match!.CameraAt(Frame) is not { } camera) return;
        // The view's size depends on the recording player's resolution, which is not recorded; this
        // is a typical 1080p view without the sidebar.
        const double halfWidth = 870, halfHeight = 480;
        var (cx, cy) = _map!.ToPixel(camera.X, camera.Y, 0);
        var rect = new RectangleF((float)(_offset.X + (cx - halfWidth) * _zoom), (float)(_offset.Y + (cy - halfHeight) * _zoom),
            (float)(halfWidth * 2 * _zoom), (float)(halfHeight * 2 * _zoom));
        using var pen = new Pen(Color.FromArgb(170, 255, 255, 255), 1.2f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        TextRenderer.DrawText(g, "recording player's view", Theme.MonoSmall, new Point((int)rect.X + 4, (int)rect.Y + 2),
            Color.FromArgb(200, 255, 255, 255));
    }

    private static void DrawLabel(Graphics g, string text, PointF at, Color colour)
    {
        var size = TextRenderer.MeasureText(text, Theme.MonoSmall);
        var box = new RectangleF(at.X, at.Y, size.Width + 6, size.Height + 2);
        using var back = new SolidBrush(Color.FromArgb(200, 14, 16, 20));
        using var edge = new Pen(Color.FromArgb(200, colour));
        g.FillRectangle(back, box);
        g.DrawRectangle(edge, box.X, box.Y, box.Width, box.Height);
        TextRenderer.DrawText(g, text, Theme.MonoSmall, Point.Round(new PointF(at.X + 3, at.Y + 1)), Color.White);
    }

    private void DrawBanner(Graphics g)
    {
        string text = _match!.HasRecordedPositions
            ? "Recorded positions"
            : "Estimated positions - this recording has no object snapshots, so units are placed from their orders and buildings where they were placed";
        var size = TextRenderer.MeasureText(text, Theme.MonoSmall);
        var box = new Rectangle(8, 8, size.Width + 12, size.Height + 6);
        using var back = new SolidBrush(Color.FromArgb(190, 14, 16, 20));
        g.FillRectangle(back, box);
        TextRenderer.DrawText(g, text, Theme.MonoSmall, new Point(14, 11),
            _match.HasRecordedPositions ? Color.FromArgb(150, 220, 160) : Color.FromArgb(240, 200, 120));
    }

    // --- heatmap ------------------------------------------------------------------------------

    private void DrawHeat(Graphics g, RectangleF mapRect)
    {
        var key = (Frame / 15, Heat, WindowSeconds, HiddenHouses.Aggregate(0, (a, h) => a | 1 << (h & 31)));
        if (_heat is null || key != _heatKey)
        {
            _heat?.Dispose();
            _heat = BuildHeat();
            _heatKey = key;
        }
        if (_heat is null) return;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(_heat, mapRect);
    }

    /// <summary>
    /// Splats every point into a coarse grid over the map image - one heat pixel to 24 map pixels - with
    /// a soft kernel, then colours it from transparent through amber to red. Drawn enlarged with
    /// bilinear filtering, the grid comes out as smooth blobs.
    /// </summary>
    private Bitmap? BuildHeat()
    {
        const int cell = 24;
        int w = Math.Max(1, (int)(_map!.PixelWidth / cell)), h = Math.Max(1, (int)(_map.PixelHeight / cell));
        var grid = new float[w * h];
        int from = Frame - WindowFrames;

        void Splat(double cellX, double cellY, float weight)
        {
            var (px, py) = _map.ToPixel(cellX, cellY, _map.LevelAt(cellX, cellY));
            int gx = (int)(px / cell), gy = (int)(py / cell);
            const int r = 3;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = gx + dx, y = gy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    float fall = MathF.Exp(-(dx * dx + dy * dy) / 4.5f);
                    grid[y * w + x] += weight * fall;
                }
        }

        switch (Heat)
        {
            case HeatLayer.Fighting when _match!.HasRecordedPositions:
                foreach (var d in _match.Deaths)
                {
                    if (d.Frame > Frame) break;
                    if (d.Frame >= from && !HiddenHouses.Contains(d.Owner))
                        Splat(d.X, d.Y, d.Kind == ObjectKind.Building ? 3 : d.Kind == ObjectKind.Infantry ? 0.5f : 1);
                }
                break;
            case HeatLayer.Fighting:
                // No record of where anything died: attack orders are the nearest thing.
                foreach (var o in OrdersBetween(from, Frame))
                    if (o.IsAttack && !HiddenHouses.Contains(o.House)) Splat(o.ToX, o.ToY, 0.5f);
                break;
            case HeatLayer.Presence:
                foreach (var s in _states)
                    if (s.Kind != ObjectKind.Building) Splat(s.X, s.Y, (float)s.Opacity * (s.Kind == ObjectKind.Infantry ? 0.4f : 1));
                break;
            case HeatLayer.Orders:
                foreach (var o in OrdersBetween(from, Frame))
                    if (!HiddenHouses.Contains(o.House)) Splat(o.ToX, o.ToY, 0.3f);
                break;
        }

        float max = grid.Length > 0 ? grid.Max() : 0;
        if (max <= 0) return null;
        // Scale to a fixed reference as well as the peak, so one lone point is not painted red.
        float reference = Math.Max(max, Heat == HeatLayer.Presence ? 6 : 4);

        var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new int[w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float t = Math.Clamp(grid[y * w + x] / reference, 0, 1);
                row[x] = t < 0.02f ? 0 : HeatColour(t).ToArgb();
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
        }
        bitmap.UnlockBits(data);
        return bitmap;
    }

    private static Color HeatColour(float t)
    {
        // Transparent -> yellow -> orange -> red, getting more opaque as it heats.
        int alpha = (int)(40 + 190 * Math.Sqrt(t));
        if (t < 0.5f) return Color.FromArgb(alpha, 255, (int)(230 - t * 120), (int)(90 - t * 120));
        return Color.FromArgb(alpha, 255, (int)(170 - (t - 0.5f) * 300), 30);
    }

    // --- interaction --------------------------------------------------------------------------

    private void DrawHover(Graphics g)
    {
        _hovered = null;
        if (_mouse is not { } m || _dragFrom is not null) { SetTip(""); return; }

        float best = 10;
        foreach (var s in _states)
        {
            var p = s.Kind == ObjectKind.Building
                ? Screen(Math.Floor(s.X) + s.FoundationWidth / 2.0, Math.Floor(s.Y) + s.FoundationHeight / 2.0, _map!.LevelAt(s.X, s.Y))
                : At(s);
            float d = Distance(p, m) - (s.Kind == ObjectKind.Building ? (float)(s.FoundationWidth * 15 * _zoom) : 0);
            if (d < best) { best = d; _hovered = s; }
        }

        if (_hovered is not { } h) { SetTip(""); return; }
        var at = h.Kind == ObjectKind.Building ? Ground(Math.Floor(h.X) + h.FoundationWidth / 2.0, Math.Floor(h.Y) + h.FoundationHeight / 2.0) : At(h);
        using var ring = new Pen(Color.White, 1.5f);
        g.DrawEllipse(ring, at.X - 8, at.Y - 8, 16, 16);
        SetTip(Describe(h));
    }

    private string Describe(ObjectState s)
    {
        if (s.Estimated)
        {
            uint id = s.Id;
            var last = _match!.Orders.LastOrDefault(o => o.Id == id && o.Frame <= Frame);
            return $"{HouseName(s.Owner)} - object #{s.Id} (estimated position)\n" +
                   (last is null ? "" : $"last order {last.Mission} at {TimeLabel(last.Frame)}\n") +
                   "Recordings without object snapshots do not say what a unit is or where it really is.";
        }
        var type = s.Kind is { } kind ? TypeOf(kind, s.TypeIndex) : null;
        string name = type?.DisplayName ?? $"{s.Kind} #{s.TypeIndex}";
        var lines = new List<string> { $"{name} - {HouseName(s.Owner)}", $"health {s.Health:P0}" };
        if (s.Mission >= 0 && Enum.IsDefined((Mission)s.Mission)) lines.Add($"mission {(Mission)s.Mission}");
        if ((s.Flags & ObjectFlags.Elite) != 0) lines.Add("elite");
        else if ((s.Flags & ObjectFlags.Veteran) != 0) lines.Add("veteran");
        if ((s.Flags & ObjectFlags.Cloaked) != 0) lines.Add("cloaked");
        lines.Add($"cell ({s.X:0.#}, {s.Y:0.#})");
        return string.Join("\n", lines);
    }

    private void SetTip(string text)
    {
        if (text == _tipText) return;
        _tipText = text;
        if (text.Length == 0 || _mouse is not { } m) _tip.Hide(this);
        else _tip.Show(text, this, m.X + 14, m.Y + 18);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (_dragFrom is { } from)
        {
            _offset = new PointF(_dragOffset.X + e.X - from.X, _dragOffset.Y + e.Y - from.Y);
            _userMoved = true;
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        SetTip("");
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _dragFrom = e.Location;
        _dragOffset = _offset;
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragFrom = null;
        Capture = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        FitToView();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_map is null) return;
        double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
        double fit = Math.Min(Width / _map.PixelWidth, Height / _map.PixelHeight);
        double zoom = Math.Clamp(_zoom * factor, fit * 0.5, 2.5);
        // Keep the point under the pointer where it is.
        _offset = new PointF((float)(e.X - (e.X - _offset.X) * zoom / _zoom), (float)(e.Y - (e.Y - _offset.Y) * zoom / _zoom));
        _zoom = zoom;
        _userMoved = true;
        Invalidate();
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _terrain?.Dispose();
            _heat?.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
