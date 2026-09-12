using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>
/// A scrolling panel that stays where the user left it. A plain one scrolls whatever takes focus into
/// view - a click on a list, a chart or a tab strip, or the window being activated again and focus
/// going back to whatever had it last - which jumps a long page to somewhere else entirely.
/// </summary>
internal sealed class StableScrollPanel : Panel
{
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
}
