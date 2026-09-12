using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed record CameoRow(string Name, Color Color, int[] Counts);

/// <summary>A titled group of columns - one side's types - with the players who have any of them.</summary>
internal sealed record CameoBlock(string Title, IReadOnlyList<TypeColumn> Columns, IReadOnlyList<CameoRow> Rows);

/// <summary>
/// Every player stacked under one row of cameos, so each type's counts line up in its column. Each
/// block - a side's types - wraps into bands as wide as the control, with the player names repeated
/// down the left of every band. Painted rather than built from child controls; its height follows its
/// width.
/// </summary>
internal sealed class CameoMatrix : Control
{
    private const int NameWidth = 150;
    private const int CellWidth = CameoAtlas.TileWidth + 4;
    private const int HeaderHeight = CameoAtlas.TileHeight + 6;
    private const int RowHeight = 20;
    private const int BandGap = 14;
    private const int EmptyHeight = 24;

    private sealed record Band(CameoBlock Block, int FirstColumn, int ColumnCount, int Top, bool FirstOfBlock)
    {
        public int Height => HeaderHeight + Block.Rows.Count * RowHeight;
    }

    private readonly List<CameoBlock> _blocks = [];
    private readonly List<Band> _bands = [];
    private readonly ToolTip _tip = new();
    private (int Band, int Row, int Column)? _hover;

    public CameoMatrix()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Panel;
        Font = Theme.Ui;
        Height = EmptyHeight;
    }

    public void SetBlocks(IEnumerable<CameoBlock> blocks)
    {
        _blocks.Clear();
        _blocks.AddRange(blocks.Where(b => b.Columns.Count > 0 && b.Rows.Count > 0));
        _hover = null;
        _tip.Hide(this);
        Relayout();
        Invalidate();
    }

    private void Relayout()
    {
        _bands.Clear();
        int perBand = Math.Max(1, (Width - NameWidth) / CellWidth);
        int top = 0;
        foreach (var block in _blocks)
        {
            for (int first = 0; first < block.Columns.Count; first += perBand)
            {
                var band = new Band(block, first, Math.Min(perBand, block.Columns.Count - first), top, first == 0);
                _bands.Add(band);
                top += band.Height + BandGap;
            }
        }
        int height = _bands.Count == 0 ? EmptyHeight : top - BandGap + 2;
        if (Height != height) Height = height;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    private static Rectangle HeaderCell(Band band, int column) =>
        new(NameWidth + column * CellWidth, band.Top + 2, CameoAtlas.TileWidth, CameoAtlas.TileHeight);

    private static Rectangle CountCell(Band band, int row, int column) =>
        new(NameWidth + column * CellWidth, band.Top + HeaderHeight + row * RowHeight + 1, CameoAtlas.TileWidth, RowHeight - 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_bands.Count == 0)
        {
            TextRenderer.DrawText(g, "None in this recording.", Font, new Point(0, 4), Theme.Muted);
            return;
        }

        using var zebra = new SolidBrush(Color.FromArgb(70, Theme.Grid));
        using var divider = new Pen(Theme.Border);
        using var highlight = new Pen(Theme.Accent, 2);
        const TextFormatFlags nameFlags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        const TextFormatFlags countFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

        for (int b = 0; b < _bands.Count; b++)
        {
            var band = _bands[b];
            if (band.Top > e.ClipRectangle.Bottom || band.Top + band.Height < e.ClipRectangle.Top) continue;
            var block = band.Block;
            int right = NameWidth + band.ColumnCount * CellWidth;

            if (band.FirstOfBlock)
            {
                if (b > 0) g.DrawLine(divider, 0, band.Top - BandGap / 2, Width, band.Top - BandGap / 2);
                TextRenderer.DrawText(g, block.Title, Theme.UiBold,
                    new Rectangle(4, band.Top, NameWidth - 8, HeaderHeight), Theme.Text, nameFlags);
            }

            for (int c = 0; c < band.ColumnCount; c++)
            {
                var column = block.Columns[band.FirstColumn + c];
                CameoAtlas.Draw(g, HeaderCell(band, c), column.Id, column.Cameo, column.Name);
            }

            // Shaded by how many, against the block's largest count, so the big numbers stand out.
            int max = Math.Max(1, block.Rows.Max(r => r.Counts.Max()));
            for (int r = 0; r < block.Rows.Count; r++)
            {
                var row = block.Rows[r];
                int y = band.Top + HeaderHeight + r * RowHeight;
                if (r % 2 == 1) g.FillRectangle(zebra, 0, y, right, RowHeight);
                TextRenderer.DrawText(g, row.Name, Theme.UiBold, new Rectangle(4, y, NameWidth - 8, RowHeight), row.Color, nameFlags);

                for (int c = 0; c < band.ColumnCount; c++)
                {
                    int count = row.Counts[band.FirstColumn + c];
                    if (count <= 0) continue;
                    var cell = CountCell(band, r, c);
                    using var tint = new SolidBrush(Color.FromArgb(24 + 90 * count / max, Theme.Accent));
                    g.FillRectangle(tint, cell);
                    TextRenderer.DrawText(g, count.ToString("N0"), Theme.MonoSmall, cell, Theme.Text, countFlags);
                }
            }

            if (_hover is { } hover && hover.Band == b)
                g.DrawRectangle(highlight, NameWidth + hover.Column * CellWidth, band.Top + 1,
                    CameoAtlas.TileWidth - 1, band.Height - 2);
        }
    }

    /// <summary>The band, row (-1 for the cameo) and column under a point, or null between cells.</summary>
    private (int Band, int Row, int Column)? HitTest(Point point)
    {
        for (int b = 0; b < _bands.Count; b++)
        {
            var band = _bands[b];
            if (point.Y < band.Top || point.Y >= band.Top + band.Height) continue;
            int x = point.X - NameWidth;
            if (x < 0 || x % CellWidth >= CameoAtlas.TileWidth) return null;
            int column = x / CellWidth;
            if (column >= band.ColumnCount) return null;
            int y = point.Y - band.Top;
            return (b, y < HeaderHeight ? -1 : (y - HeaderHeight) / RowHeight, column);
        }
        return null;
    }

    private string TipFor((int Band, int Row, int Column) hit)
    {
        var band = _bands[hit.Band];
        int index = band.FirstColumn + hit.Column;
        var column = band.Block.Columns[index];
        string name = column.Name == column.Id ? column.Name : $"{column.Name} [{column.Id}]";
        string Value(long count) =>
            column.Cost > 0 && count > 0 ? $"   ({column.Cost:N0} credits each, {column.Cost * count:N0} in all)" : "";

        if (hit.Row < 0)
        {
            long total = band.Block.Rows.Sum(r => (long)r.Counts[index]);
            return $"{name}\n{total:N0} in all{Value(total)}";
        }
        var row = band.Block.Rows[hit.Row];
        int count = row.Counts[index];
        return $"{row.Name}: {name}\n× {count:N0}{Value(count)}";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit == _hover) return;

        _hover = hit;
        Invalidate();
        if (hit is not { } h)
        {
            _tip.Hide(this);
            return;
        }
        _tip.Show(TipFor(h), this, e.X + 14, e.Y + 14);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
