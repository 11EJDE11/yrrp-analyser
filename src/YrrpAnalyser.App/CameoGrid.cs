using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal readonly record struct CameoItem(string TypeId, string Cameo, string Name, int Count, int Cost);

/// <summary>
/// A captioned grid of cameo tiles with a count under each, the way the cnc ladder shows a game's
/// units. Painted rather than built from child controls, so a player with a hundred types built is
/// still one control; its height follows its width.
/// </summary>
internal sealed class CameoGrid : Control
{
    private const int CaptionHeight = 24;
    private const int CountHeight = 18;
    private const int Gap = 6;
    private const int EmptyHeight = 20;

    private readonly List<CameoItem> _items = [];
    private readonly ToolTip _tip = new();
    private int _hover = -1;

    public string Caption { get; set; } = "";

    public CameoGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Font = Theme.Ui;
        Height = CaptionHeight + EmptyHeight;
    }

    public CameoGrid WithItems(IEnumerable<CameoItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        UpdateHeight();
        Invalidate();
        return this;
    }

    private int Columns => Math.Max(1, (Width + Gap) / (CameoAtlas.TileWidth + Gap));

    private void UpdateHeight()
    {
        int rows = (_items.Count + Columns - 1) / Columns;
        int height = CaptionHeight + (_items.Count == 0 ? EmptyHeight : rows * (CameoAtlas.TileHeight + CountHeight + Gap)) + Gap;
        if (Height != height) Height = height;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateHeight();
    }

    private Rectangle TileBounds(int index)
    {
        int columns = Columns;
        int column = index % columns, row = index / columns;
        return new Rectangle(
            column * (CameoAtlas.TileWidth + Gap),
            CaptionHeight + row * (CameoAtlas.TileHeight + CountHeight + Gap),
            CameoAtlas.TileWidth, CameoAtlas.TileHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        using var text = new SolidBrush(Theme.Text);
        using var muted = new SolidBrush(Theme.Muted);
        g.DrawString(Caption, Theme.UiBold, text, 0, 4);

        if (_items.Count == 0)
        {
            g.DrawString("none", Font, muted, 0, CaptionHeight);
            return;
        }

        using var strip = new SolidBrush(Color.FromArgb(0x0A, 0x0C, 0x10));
        using var number = new SolidBrush(Color.FromArgb(0xE0, 0xE0, 0xE0));
        using var highlight = new Pen(Theme.Accent, 2);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var tile = TileBounds(i);
            if (!tile.IntersectsWith(e.ClipRectangle) && !e.ClipRectangle.IsEmpty
                && tile.Bottom + CountHeight < e.ClipRectangle.Top) continue;

            CameoAtlas.Draw(g, tile, item.TypeId, item.Cameo, item.Name);
            var count = new Rectangle(tile.Left, tile.Bottom, tile.Width, CountHeight);
            g.FillRectangle(strip, count);
            g.DrawString(item.Count.ToString("N0"), Theme.UiBold, number, count, centred);
            if (i == _hover) g.DrawRectangle(highlight, tile.Left, tile.Top, tile.Width - 1, tile.Height + CountHeight - 1);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = -1;
        for (int i = 0; i < _items.Count; i++)
        {
            var tile = TileBounds(i);
            if (new Rectangle(tile.Left, tile.Top, tile.Width, tile.Height + CountHeight).Contains(e.Location)) { hit = i; break; }
        }
        if (hit == _hover) return;

        _hover = hit;
        Invalidate();
        if (hit < 0) { _tip.Hide(this); return; }

        var item = _items[hit];
        string tip = item.Name == item.TypeId ? item.Name : $"{item.Name} [{item.TypeId}]";
        tip += $"\n× {item.Count:N0}";
        if (item.Cost > 0) tip += $"   ({item.Cost:N0} credits each, {(long)item.Cost * item.Count:N0} in all)";
        _tip.Show(tip, this, e.X + 14, e.Y + 14);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.Hide(this);
        Invalidate();
    }
}
