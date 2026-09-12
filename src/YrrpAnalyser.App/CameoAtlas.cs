using System.Drawing;
using System.Drawing.Drawing2D;

namespace YrrpAnalyser.App;

/// <summary>
/// The cnc ladder's YR cameo sprite sheet: one 60x48 tile per cameo, laid out left to right, with
/// the offsets taken from the ladder's stylesheet (see CameoAtlas.Data.cs). A type the sheet has
/// no picture for - anything a mod added - is drawn as a labelled tile instead of left out.
/// </summary>
internal static partial class CameoAtlas
{
    public const int TileWidth = 60;
    public const int TileHeight = 48;

    private static readonly Lazy<Image?> Sheet = new(LoadSheet);

    private static Image? LoadSheet()
    {
        using var stream = typeof(CameoAtlas).Assembly.GetManifestResourceStream("YrrpAnalyser.App.Resources.yr-cameo.png");
        return stream is null ? null : Image.FromStream(stream);
    }

    /// <summary>Sprite X offset for a type, by its art cameo name first and its ID second.</summary>
    public static int? Find(string typeId, string cameoName)
    {
        if (cameoName.Length > 0 && SpriteOffsets.TryGetValue(StripExtension(cameoName), out int x))
            return x;
        if (CameoByTypeId.TryGetValue(typeId, out var mapped) && SpriteOffsets.TryGetValue(mapped, out x))
            return x;
        return null;
    }

    private static string StripExtension(string name)
    {
        int dot = name.IndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>Draws the type's cameo into <paramref name="bounds"/>, or a labelled placeholder.</summary>
    public static void Draw(Graphics g, Rectangle bounds, string typeId, string cameoName, string label)
    {
        var sheet = Sheet.Value;
        if (sheet is not null && Find(typeId, cameoName) is { } x)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(sheet, bounds, new Rectangle(x, 0, TileWidth, TileHeight), GraphicsUnit.Pixel);
            return;
        }

        using var back = new SolidBrush(Color.FromArgb(0x2A, 0x2E, 0x36));
        using var text = new SolidBrush(Color.FromArgb(0xD8, 0xDA, 0xE0));
        g.FillRectangle(back, bounds);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(label.Length > 0 ? label : typeId, Theme.MonoSmall, text, bounds, format);
    }
}
