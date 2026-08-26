using System.Drawing;

namespace YrrpAnalyser.App;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0xFA, 0xFA, 0xFB);
    public static readonly Color Panel = Color.White;
    public static readonly Color Border = Color.FromArgb(0xDD, 0xDD, 0xE2);
    public static readonly Color Text = Color.FromArgb(0x1C, 0x1C, 0x22);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x6B, 0x78);
    public static readonly Color Grid = Color.FromArgb(0xEC, 0xEC, 0xF0);
    public static readonly Color Accent = Color.FromArgb(0x1F, 0x6F, 0xD8);
    public static readonly Color Warning = Color.FromArgb(0xC0, 0x62, 0x00);
    public static readonly Color Danger = Color.FromArgb(0xC0, 0x28, 0x28);
    public static readonly Color Good = Color.FromArgb(0x1E, 0x7A, 0x46);

    public static readonly Font Ui = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font Heading = new("Segoe UI Semibold", 11f);
    public static readonly Font Mono = new("Consolas", 9f);
    public static readonly Font MonoSmall = new("Consolas", 8f);

    /// <summary>
    /// Chart and roster colours, one per house index. Deliberately not the in-game player colours:
    /// spawn.ini stores a colour index whose meaning lives in the client, not in the replay, and
    /// guessing at it would put a wrong swatch next to a right name. These are picked to stay
    /// distinguishable from one another instead.
    /// </summary>
    private static readonly Color[] PlayerPalette =
    [
        Color.FromArgb(0x1F, 0x6F, 0xD8), // blue
        Color.FromArgb(0xD9, 0x53, 0x1E), // orange
        Color.FromArgb(0x2E, 0x8B, 0x4F), // green
        Color.FromArgb(0x8E, 0x44, 0xAD), // purple
        Color.FromArgb(0xC0, 0x28, 0x28), // red
        Color.FromArgb(0x0E, 0x8C, 0x9E), // teal
        Color.FromArgb(0xB0, 0x8D, 0x00), // gold
        Color.FromArgb(0xD1, 0x4D, 0x9C), // magenta
    ];

    public static Color ForHouse(int houseIndex) =>
        houseIndex < 0 ? Muted : PlayerPalette[houseIndex % PlayerPalette.Length];

    public static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));
}
