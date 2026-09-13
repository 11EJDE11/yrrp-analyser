using System.Drawing;
using System.Drawing.Drawing2D;
using YrrpAnalyser.Map;

namespace YrrpAnalyser.App;

/// <summary>
/// Draws the map the recording was played on. Every cell is a diamond coloured from the map's own
/// lobby preview at that cell, lifted to its height from [IsoMapPack5], with cliff faces under
/// sharp drops, a little hillshade, and the ore and gem fields picked out from [OverlayPack]. The
/// preview alone is a few pixels a cell and blurs when enlarged; sampled per cell it keeps the map's
/// real colours - water, snow, roads - with the crisp cell grid the game itself has.
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
        g.Clear(Color.FromArgb(24, 27, 32));

        if (!map.HasTiles)
        {
            // Nothing to lay cells out from: the preview as it is, if there is one.
            using var preview = PreviewBitmap(map);
            if (preview is not null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(preview, 0, 0, width, height);
            }
            return bitmap;
        }

        g.SmoothingMode = SmoothingMode.None;
        var fallback = TheaterColour(map.Theater);
        // Back to front, and on a tie the lower cell first, so a raised cell covers what it hides.
        foreach (var (x, y) in map.Tiles.OrderBy(t => t.X + t.Y).ThenBy(t => map.LevelAt(t.X, t.Y)))
        {
            int level = map.LevelAt(x, y);
            var (cx, cy) = map.CellCentre(x, y);
            var sampled = map.PreviewColourAt(cx, cy);
            var colour = sampled is { } c ? Color.FromArgb(c.R, c.G, c.B) : fallback;

            switch (map.OverlayAt(x, y))
            {
                case OverlayKind.Ore: colour = Theme.Blend(colour, Ore, 0.5); break;
                case OverlayKind.Gems: colour = Theme.Blend(colour, Gems, 0.5); break;
            }

            // Light from the top: a cell rising above its upper neighbours faces it.
            double slope = level - (map.LevelAt(x - 1, y) + map.LevelAt(x, y - 1)) / 2.0;
            colour = slope > 0 ? Theme.Blend(colour, Color.White, Math.Min(0.25, slope * 0.08))
                   : slope < 0 ? Theme.Blend(colour, Color.Black, Math.Min(0.3, -slope * 0.08))
                   : colour;

            PointF P(double px, double py, double lv)
            {
                var (sx, sy) = map.ToPixel(px, py, lv);
                return new PointF((float)(sx * scale), (float)(sy * scale));
            }

            // A cliff face under a drop of two levels or more; a one-level step is a ramp and reads as shading.
            int below = Math.Min(map.LevelAt(x + 1, y), map.LevelAt(x, y + 1));
            if (level - below >= 2)
            {
                using var wall = new SolidBrush(Theme.Blend(colour, Color.FromArgb(20, 18, 16), 0.6));
                g.FillPolygon(wall,
                [
                    P(x + 1, y, level), P(x + 1, y + 1, level), P(x, y + 1, level),
                    P(x, y + 1, below), P(x + 1, y + 1, below), P(x + 1, y, below),
                ]);
            }

            using var brush = new SolidBrush(colour);
            // Half a pixel of overlap hides the seams between neighbouring diamonds.
            var top = P(x, y, level); var right = P(x + 1, y, level);
            var bottom = P(x + 1, y + 1, level); var left = P(x, y + 1, level);
            g.FillPolygon(brush, [new PointF(top.X, top.Y - 0.5f), new PointF(right.X + 0.5f, right.Y),
                new PointF(bottom.X, bottom.Y + 0.5f), new PointF(left.X - 0.5f, left.Y)]);
        }
        return bitmap;
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
