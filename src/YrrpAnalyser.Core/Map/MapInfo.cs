using System.Buffers.Binary;

namespace YrrpAnalyser.Map;

public enum OverlayKind : byte { None, Ore, Gems }

/// <summary>A building the map places at the start: tech buildings, civilian structures, bases.</summary>
public sealed record MapStructure(string Owner, string TypeId, int X, int Y);

/// <summary>
/// The embedded spawnmap.ini, read for drawing: the map's size, the per-cell terrain height from
/// [IsoMapPack5], ore and gems from [OverlayPack], the start waypoints, the map's own buildings,
/// and its lobby preview image.
///
/// Positions go through one projection, the game's own isometric one: a cell is a 60x30 pixel
/// diamond, and each height level lifts it 15 pixels. <see cref="ToPixel"/> returns full-size map
/// pixels measured from the top-left of [Map] LocalSize, which is the rectangle the preview image
/// covers - the CnCNet client's GetIsoTilePixelCoord places start positions on the preview the
/// same way, including the height correction CnCNet/xna-cncnet-client#1023 adds from IsoMapPack5.
/// </summary>
public sealed class MapInfo
{
    public const int GridSize = 512;
    public const int CellWidth = 60;
    public const int CellHeight = 30;
    public const int LevelHeight = 15;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int LocalLeft { get; private set; }
    public int LocalTop { get; private set; }
    public int LocalWidth { get; private set; }
    public int LocalHeight { get; private set; }
    public string Theater { get; private set; } = "";

    public int PreviewWidth { get; private set; }
    public int PreviewHeight { get; private set; }
    /// <summary>24-bit RGB, row by row, or null when the map has no (or a hidden) preview.</summary>
    public byte[]? PreviewRgb { get; private set; }

    private readonly byte[] _levels = new byte[GridSize * GridSize];
    private readonly bool[] _hasTile = new bool[GridSize * GridSize];
    private readonly OverlayKind[] _overlay = new OverlayKind[GridSize * GridSize];

    public bool HasTiles { get; private set; }
    public bool HasOverlay { get; private set; }
    public int TileCount { get; private set; }

    /// <summary>Cells that carry a tile, for drawing.</summary>
    public List<(int X, int Y)> Tiles { get; } = [];

    public Dictionary<int, CellRef> Waypoints { get; } = [];
    public List<MapStructure> Structures { get; } = [];

    public double PixelWidth => LocalWidth * CellWidth;
    public double PixelHeight => LocalHeight * CellHeight;
    public bool IsUsable => Width > 0 && LocalWidth > 0 && LocalHeight > 0;

    /// <summary>The start position for a spawn.ini SpawnLocations value, which is a waypoint number.</summary>
    public CellRef? StartLocation(int spawnLocation) =>
        spawnLocation >= 0 && Waypoints.TryGetValue(spawnLocation, out var cell) ? cell : null;

    public IEnumerable<(int Index, CellRef Cell)> StartLocations =>
        Enumerable.Range(0, 8).Where(Waypoints.ContainsKey).Select(i => (i, Waypoints[i]));

    private static int Index(int x, int y) => y * GridSize + x;
    private static bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < GridSize && y < GridSize;

    public bool HasTile(int x, int y) => InGrid(x, y) && _hasTile[Index(x, y)];
    public int LevelAt(int x, int y) => InGrid(x, y) ? _levels[Index(x, y)] : 0;
    public OverlayKind OverlayAt(int x, int y) => InGrid(x, y) ? _overlay[Index(x, y)] : OverlayKind.None;

    /// <summary>Terrain height under a fractional cell position, for lifting objects onto it.</summary>
    public int LevelAt(double x, double y) => LevelAt((int)Math.Floor(x), (int)Math.Floor(y));

    /// <summary>
    /// Map pixel of a point in cell units (a coordinate in leptons / 256), at a height level. The
    /// integer point (x, y) is a cell's top corner; its centre is (x + 0.5, y + 0.5).
    /// </summary>
    public (double X, double Y) ToPixel(double x, double y, double level)
    {
        double px = (x - y + Width - 1) * (CellWidth / 2.0) - LocalLeft * CellWidth;
        double py = (x + y - Width - 1) * (CellHeight / 2.0) - level * LevelHeight - LocalTop * CellHeight;
        return (px, py);
    }

    /// <summary>The inverse of <see cref="ToPixel"/> on flat ground: which cell a map pixel falls in.</summary>
    public (double X, double Y) FromPixel(double px, double py, double level = 0)
    {
        double a = (px + LocalLeft * CellWidth) / (CellWidth / 2.0) - Width + 1;             // x - y
        double b = (py + level * LevelHeight + LocalTop * CellHeight) / (CellHeight / 2.0) + Width + 1; // x + y
        return ((a + b) / 2, (b - a) / 2);
    }

    /// <summary>A cell's centre on the map, lifted to its own terrain height.</summary>
    public (double X, double Y) CellCentre(int x, int y) => ToPixel(x + 0.5, y + 0.5, LevelAt(x, y));

    /// <summary>The preview's colour at a map pixel, or null without a preview.</summary>
    public (byte R, byte G, byte B)? PreviewColourAt(double px, double py)
    {
        if (PreviewRgb is null || PixelWidth <= 0 || PixelHeight <= 0) return null;
        int x = (int)(px / PixelWidth * PreviewWidth);
        int y = (int)(py / PixelHeight * PreviewHeight);
        if (x < 0 || y < 0 || x >= PreviewWidth || y >= PreviewHeight) return null;
        int i = (y * PreviewWidth + x) * 3;
        return (PreviewRgb[i], PreviewRgb[i + 1], PreviewRgb[i + 2]);
    }

    public static MapInfo Parse(IniDocument ini)
    {
        var map = new MapInfo();

        var size = Numbers(ini.GetString("Map", "Size"));
        var local = Numbers(ini.GetString("Map", "LocalSize"));
        if (size.Length >= 4) { map.Width = size[2]; map.Height = size[3]; }
        if (local.Length >= 4)
        {
            map.LocalLeft = local[0]; map.LocalTop = local[1];
            map.LocalWidth = local[2]; map.LocalHeight = local[3];
        }
        map.Theater = ini.GetString("Map", "Theater");

        map.ReadTiles(ini);
        map.ReadOverlay(ini);
        map.ReadPreview(ini);
        map.ReadWaypoints(ini);
        map.ReadStructures(ini);
        return map;
    }

    private static int[] Numbers(string csv) =>
        csv.Split(',').Select(s => int.TryParse(s.Trim(), out int n) ? n : 0).ToArray();

    private static string Joined(IniDocument ini, string section) =>
        ini.GetSection(section) is { } s ? string.Concat(s.Entries.Select(e => e.Value)) : "";

    // [IsoMapPack5]: 11-byte records, x int16, y int16, tile int32, subtile, level, ice growth.
    private void ReadTiles(IniDocument ini)
    {
        var data = MapCompression.DecodePack(Joined(ini, "IsoMapPack5"), MapCompression.Codec.Lzo);
        if (data is null) return;

        for (int p = 0; p + 11 <= data.Length; p += 11)
        {
            int x = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p));
            int y = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p + 2));
            if (!InGrid(x, y) || (x == 0 && y == 0)) continue;
            int i = Index(x, y);
            if (!_hasTile[i]) Tiles.Add((x, y));
            _hasTile[i] = true;
            _levels[i] = Math.Min(data[p + 9], (byte)14);
        }
        TileCount = Tiles.Count;
        HasTiles = TileCount > 0;
    }

    // [OverlayPack]: one overlay type index per cell of the 512x512 grid, 0xFF for none. In YR's
    // [OverlayTypes] the ore is TIB01-TIB20 (102-121) and TIB2..TIB4 (127-166), gems GEM01-GEM12 (27-38).
    private void ReadOverlay(IniDocument ini)
    {
        var data = MapCompression.DecodePack(Joined(ini, "OverlayPack"), MapCompression.Codec.Lcw);
        if (data is null || data.Length < GridSize * GridSize) return;

        for (int i = 0; i < GridSize * GridSize; i++)
        {
            int type = data[i];
            var kind = type switch
            {
                >= 102 and <= 121 or >= 127 and <= 166 => OverlayKind.Ore,
                >= 27 and <= 38 => OverlayKind.Gems,
                _ => OverlayKind.None,
            };
            _overlay[i] = kind;
            if (kind != OverlayKind.None) HasOverlay = true;
        }
    }

    private void ReadPreview(IniDocument ini)
    {
        var size = Numbers(ini.GetString("Preview", "Size"));
        if (size.Length < 4 || size[2] <= 0 || size[3] <= 0 || size[2] > 4096 || size[3] > 4096) return;

        string packed = Joined(ini, "PreviewPack");
        // The map editor's "hidden preview" is a fixed tiny image; the client refuses it too.
        if (packed.StartsWith("yAsAIAXQ5PDQ5PDQ6JQATAEE6PDQ4PDI4JgBTAFEAkgAJyAATAG0AydEAEABpAJIA0wBVA", StringComparison.Ordinal))
            return;

        var data = MapCompression.DecodePack(packed, MapCompression.Codec.Lzo);
        if (data is null || data.Length < size[2] * size[3] * 3) return;
        PreviewWidth = size[2];
        PreviewHeight = size[3];
        PreviewRgb = data;
    }

    // Waypoints are stored as Y * 1000 + X.
    private void ReadWaypoints(IniDocument ini)
    {
        if (ini.GetSection("Waypoints") is not { } section) return;
        foreach (var (key, value) in section.Entries)
        {
            if (!int.TryParse(key, out int index) || !int.TryParse(value.Split(',')[0], out int packed)) continue;
            Waypoints[index] = new CellRef((short)(packed % 1000), (short)(packed / 1000));
        }
    }

    // [Structures] n=Owner,TypeID,Strength,X,Y,Facing,...
    private void ReadStructures(IniDocument ini)
    {
        if (ini.GetSection("Structures") is not { } section) return;
        foreach (var (_, value) in section.Entries)
        {
            var parts = value.Split(',');
            if (parts.Length < 5 || !int.TryParse(parts[3], out int x) || !int.TryParse(parts[4], out int y)) continue;
            Structures.Add(new MapStructure(parts[0].Trim(), parts[1].Trim(), x, y));
        }
    }
}
