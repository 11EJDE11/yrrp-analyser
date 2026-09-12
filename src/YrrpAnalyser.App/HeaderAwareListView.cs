using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>Keep headers readable when dragging or auto-sizing a column to short cell values.</summary>
internal sealed class HeaderAwareListView : ListView
{
    private bool _adjustingColumns;

    private int HeaderWidth(int index) => TextRenderer.MeasureText(Columns[index].Text, Font,
        System.Drawing.Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width
        + (int)Math.Ceiling(24 * DeviceDpi / 96.0);

    protected override void OnColumnWidthChanging(ColumnWidthChangingEventArgs e)
    {
        base.OnColumnWidthChanging(e);
        e.NewWidth = Math.Max(e.NewWidth, HeaderWidth(e.ColumnIndex));
    }

    protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
    {
        // Native divider double-click auto-sizing bypasses ColumnWidthChanging.
        EnsureHeaderWidths();
        base.OnColumnWidthChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnsureHeaderWidths();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (IsHandleCreated) EnsureHeaderWidths();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        EnsureHeaderWidths();
    }

    private void EnsureHeaderWidths()
    {
        if (_adjustingColumns || !IsHandleCreated) return;
        _adjustingColumns = true;
        try
        {
            for (int i = 0; i < Columns.Count; i++)
            {
                int minimum = HeaderWidth(i);
                if (Columns[i].Width < minimum) Columns[i].Width = minimum;
            }
        }
        finally { _adjustingColumns = false; }
    }
}
