using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>
/// Sends the mouse wheel to the control under the pointer instead of the focused one.
///
/// Windows delivers WM_MOUSEWHEEL to whatever has focus, which is why a WinForms scrolling column
/// only responds to the wheel once something in it has been clicked - and why a chart that had
/// been clicked would keep swallowing the wheel afterwards, wherever the pointer was. Routing by
/// position is what everything else on the desktop does, and it is what makes "plain wheel scrolls
/// the page, Ctrl+wheel zooms the chart under the pointer" behave the same before and after a
/// click.
/// </summary>
internal sealed class WheelRouter : IMessageFilter
{
    private const int WmMouseWheel = 0x020A;

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel) return false;

        IntPtr hovered = WindowFromPoint(Cursor.Position);
        if (hovered == IntPtr.Zero || hovered == m.HWnd) return false;

        // Null for any window this process does not own as a WinForms control, which is the
        // guard against redirecting into someone else's window.
        var control = Control.FromHandle(hovered);
        if (control is null) return false;

        // A stray wheel over a dropped-down list changes the selection without a click, which is
        // a real edit rather than a scroll. Leave those to the default routing.
        if (control is ComboBox) return false;

        // SendMessage goes straight to the window procedure and never back through the message
        // pump, so this cannot re-enter the filter.
        SendMessage(hovered, WmMouseWheel, m.WParam, m.LParam);
        return true;
    }
}
