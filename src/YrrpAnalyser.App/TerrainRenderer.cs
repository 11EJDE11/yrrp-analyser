using System.Drawing;
using System.Drawing.Drawing2D;
using YrrpAnalyser.Map;

namespace YrrpAnalyser.App;

/// <summary>
/// Draws the map the recording was played on. Every cell is a diamond coloured from the map's own
/// lobby preview at that cell, lifted to its height from [IsoMapPack5], with the faces under raised
/// cells, a little hillshade, and the ore and gem fields picked out from [OverlayPack]. The preview
/// alone is a few pixels a cell and blurs when enlarged; sampled per cell it keeps the map's real
/// colours - water, snow, roads - with the crisp cell grid the game itself has.
/// </summary>
internal static class TerrainRenderer
{
    private static readonly Color Ore = Color.FromArgb(232, 184, 48);
    private static readonly Color Gems = Color.FromArgb(96, 196, 236);

    /// <summary>A scale that keeps the image's longer side at no more than about 2,400 pixels.</summary>
    public static double ScaleFor(MapInfo map) =>
        Math.Clamp(2400 / Math.Max(1, Math.Max(map.PixelWidth, map.PixelHeight)), 0.08, 0.5);

    public static Bitmap Render(MapInfo map, double scale)
    {
        int width = Math.Max(1, (int)Math.Ceiling(map.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(map.PixelHeight * scale));
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        var fallback = TheaterColour(map.Theater);
        g.Clear(map.HasTiles ? fallback : Color.FromArgb(24, 27, 32));

        // The preview, enlarged, underneath everything: wherever the cells leave a gap - the edge of
        // the map, a seam, a cell the pack does not cover - it shows the map as the game drew it there
        // rather than a hole.
        using (var preview = PreviewBitmap(map))
        {
            if (preview is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(preview, 0, 0, width, height);
                g.PixelOffsetMode = PixelOffsetMode.Default;
            }
        }
        if (!map.HasTiles) return bitmap;

        PointF P(double px, double py, double lv)
        {
            var (sx, sy) = map.ToPixel(px, py, lv);
            return new PointF((float)(sx * scale), (float)(sy * scale));
        }

        Color Sample(PointF at, Color otherwise) =>
            map.PreviewColourAt(at.X / scale, at.Y / scale) is { } c ? Color.FromArgb(c.R, c.G, c.B) : otherwise;

        g.SmoothingMode = SmoothingMode.None;
        // Back to front, and on a tie the lower cell first, so a raised cell covers what it hides.
        foreach (var (x, y) in map.Tiles.OrderBy(t => t.X + t.Y).ThenBy(t => map.LevelAt(t.X, t.Y)))
        {
            int level = map.LevelAt(x, y);
            var (cx, cy) = map.CellCentre(x, y);
            var colour = map.PreviewColourAt(cx, cy) is { } c ? Color.FromArgb(c.R, c.G, c.B) : fallback;

            switch (map.OverlayAt(x, y))
            {
                case OverlayKind.Ore: colour = Theme.Blend(colour, Ore, 0.5); break;
                case OverlayKind.Gems: colour = Theme.Blend(colour, Gems, 0.5); break;
            }

            // Light from the top: a cell rising above its upper neighbours faces it.
            double slope = level - (map.LevelAt(x - 1, y) + map.LevelAt(x, y - 1)) / 2.0;
            var top = slope > 0 ? Theme.Blend(colour, Color.White, Math.Min(0.22, slope * 0.07))
                    : slope < 0 ? Theme.Blend(colour, Color.Black, Math.Min(0.25, -slope * 0.07))
                    : colour;

            // The two faces under the cell's front edges, each down to the neighbour in front of it.
            // A one-level step is a ramp and is shaded like one; a bigger drop is a cliff, coloured from
            // the preview where the game drew that face, darker on the side turned from the light.
            DrawFace(g, P(x + 1, y, level), P(x + 1, y + 1, level), level - map.LevelAt(x + 1, y), scale, colour, 0.28, Sample);
            DrawFace(g, P(x, y + 1, level), P(x + 1, y + 1, level), level - map.LevelAt(x, y + 1), scale, colour, 0.14, Sample);

            var t = P(x, y, level); var r = P(x + 1, y, level);
            var b = P(x + 1, y + 1, level); var l = P(x, y + 1, level);
            using var brush = new SolidBrush(top);
            // Half a pixel of overlap hides the seams between neighbouring diamonds.
            g.FillPolygon(brush, [new PointF(t.X, t.Y - 0.5f), new PointF(r.X + 0.5f, r.Y),
                new PointF(b.X, b.Y + 0.5f), new PointF(l.X - 0.5f, l.Y)]);
        }
        return bitmap;
    }

    /// <summary>The vertical face hanging from a cell's front edge a-b, down <paramref name="drop"/> levels.</summary>
    private static void DrawFace(Graphics g, PointF a, PointF b, int drop, double scale, Color cell, double shade,
        Func<PointF, Color, Color> sample)
    {
        if (drop <= 0) return;
        float depth = (float)(drop * MapInfo.LevelHeight * scale);
        PointF[] face = [a, b, new PointF(b.X, b.Y + depth + 0.5f), new PointF(a.X, a.Y + depth + 0.5f)];

        var colour = drop == 1
            ? Theme.Blend(cell, Color.Black, shade * 0.6)
            : Theme.Blend(sample(new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2 + depth / 2), cell), Color.Black, shade);
        using var brush = new SolidBrush(colour);
        g.FillPolygon(brush, face);
    }

    public static Bitmap? PreviewBitmap(MapInfo map)
    {
        if (map.PreviewRgb is not { } rgb) return null;
        var bitmap = new Bitmap(map.PreviewWidth, map.PreviewHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, map.PreviewWidth, map.PreviewHeight),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        var row = new byte[data.Stride];
        for (int y = 0; y < map.PreviewHeight; y++)
        {
            for (int x = 0; x < map.PreviewWidth; x++)
            {
                int i = (y * map.PreviewWidth + x) * 3;
                row[x * 3] = rgb[i + 2]; row[x * 3 + 1] = rgb[i + 1]; row[x * 3 + 2] = rgb[i];
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, data.Stride);
        }
        bitmap.UnlockBits(data);
        return bitmap;
    }

    /// <summary>Ground for a map with tiles but no preview to colour them from.</summary>
    private static Color TheaterColour(string theater) => theater.ToUpperInvariant() switch
    {
        "SNOW" => Color.FromArgb(206, 216, 222),
        "URBAN" or "NEWURBAN" => Color.FromArgb(128, 132, 126),
        "DESERT" => Color.FromArgb(196, 172, 128),
        "LUNAR" => Color.FromArgb(150, 148, 146),
        _ => Color.FromArgb(104, 132, 80),
    };
}
