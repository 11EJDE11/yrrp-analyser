using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using YrrpAnalyser;
using YrrpAnalyser.App;

internal static class Program
{
    private static int _passed, _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Run("legend toggles the clicked series, rescales Y and preserves zoom", () =>
        {
            using var chart = Chart();
            chart.SetViewRange(200, 800);
            ClickLegend(chart, 1);
            Check(chart.Series[0].Visible && !chart.Series[1].Visible, "only clicked series hidden, even with identical names");
            Check(Field<int>(chart, "_viewMinFrame") == 200 && Field<int>(chart, "_viewMaxFrame") == 800, "zoom preserved");
            Check(Math.Abs(Field<double>(chart, "_viewMaxValue") - 112) < .01, "Y scales to visible player");
            ClickLegend(chart, 0);
            using var empty = Render(chart);
            Check(chart.Series.All(s => !s.Visible), "all hidden is supported");
            ClickLegend(chart, 2);
            Check(chart.Series.All(s => s.Visible), "Show all restores players");
        });
        Run("wrapped legends remain clickable and preserve plot height on resize", () =>
        {
            using var chart = Chart();
            chart.Width = 1000;
            chart.SetData(Enumerable.Range(0, 8).Select(i => new ChartSeries
            {
                Name = $"Player {i} with a long name", Color = Theme.ForHouse(i), Points = [new(0, 100), new(1000, 200)]
            }));
            int wideHeight = chart.Height, plotHeight = Plot(chart).Height;
            chart.Width = 340;
            Check(chart.Height > wideHeight && Plot(chart).Height == plotHeight, "wrapping grows chart, not shrinks plot");
            foreach (var (_, bounds) in Legend(chart))
                Check(bounds.Left >= 0 && bounds.Right <= chart.Width && bounds.Bottom <= chart.Height, "entry fits control");
            ClickLegend(chart, 7);
            Check(!chart.Series[7].Visible, "last wrapped player is clickable");
            chart.Width = 1000;
            Check(chart.Height == wideHeight, "height returns when widened");
            chart.SetData([new ChartSeries { Name = new string('W', 300), Points = [new(0, 1), new(1000, 1)] }]);
            using var image = Render(chart);
            Check(Legend(chart)[0].Item2.Right <= chart.Width, "very long name is bounded");
        });
        Run("legend and title interactions do not pan or reset the plot", () =>
        {
            using var chart = Chart();
            chart.SetViewRange(200, 800);
            Mouse(chart, "OnMouseDown", new Point(10, 10));
            Check(!Field<bool>(chart, "_panning"), "title does not start drag");
            Mouse(chart, "OnMouseDoubleClick", Legend(chart)[0].Item2.Location);
            Check(Field<int>(chart, "_viewMinFrame") == 200, "legend double click does not reset zoom");
            Mouse(chart, "OnMouseDown", new Point(100, 70));
            Check(Field<bool>(chart, "_panning") && chart.Capture, "plot captures drag");
            chart.Capture = false;
            Check(!Field<bool>(chart, "_panning"), "capture loss ends drag");
            Mouse(chart, "OnMouseDoubleClick", new Point(100, 70));
            Check(Field<int>(chart, "_viewMinFrame") == 0, "plot double click resets zoom");
        });
        foreach (var style in new[] { SeriesStyle.Line, SeriesStyle.Step })
            Run($"{style} remains visible when zoomed between samples", () =>
            {
                using var chart = Chart();
                chart.SetData([new ChartSeries { Name = "Sparse", Color = Color.Red, Style = style,
                    Points = [new(0, 500), new(1000, 1000)] }]);
                chart.SetViewRange(200, 400);
                double expected = style == SeriesStyle.Step ? 500 : 700;
                Check(Math.Abs(Field<double>(chart, "_viewMaxValue") - expected * 1.12) < .01, "segment determines scale");
                using var image = Render(chart);
                var plot = Plot(chart);
                int ink = 0;
                for (int y = plot.Top; y < plot.Bottom; y++)
                    for (int x = plot.Left; x < plot.Right; x++)
                    {
                        var c = image.GetPixel(x, y);
                        if (c.R > 200 && c.G < 100 && c.B < 100) ink++;
                    }
                Check(ink > plot.Width, "segment is drawn across plot");
            });
        Run("line charts leave the area under each series unshaded", () =>
        {
            using var chart = Chart();
            chart.SetData([new ChartSeries { Name = "Player", Color = Color.Blue, Points = [new(0, 800), new(1000, 800)] }]);
            using var image = Render(chart);
            var plot = Plot(chart);
            Check(image.GetPixel(plot.Left + 23, plot.Bottom - 7).ToArgb() == Theme.Panel.ToArgb(), "plot stays white below line");
        });
        Run("title-bar buttons each click once, side by side, without starting a drag", () =>
        {
            using var chart = Chart();
            int pins = 0, hides = 0;
            chart.Buttons = [new ChartButton("Pin to top", () => pins++), new ChartButton("Hide", () => hides++)];
            var bounds = Call<List<Rectangle>>(chart, "ButtonBounds");
            Check(bounds.Count == 2 && bounds[0].Right < bounds[1].Left && bounds[1].Right <= chart.Width
                  && bounds.All(b => b.Bottom <= Plot(chart).Top), "buttons sit side by side in the title bar");
            foreach (var b in bounds)
                Mouse(chart, "OnMouseDown", new Point(b.X + b.Width / 2, b.Y + b.Height / 2));
            Check(pins == 1 && hides == 1 && !Field<bool>(chart, "_panning"), "each button clicked once, no drag");
            using var image = Render(chart);
            chart.Buttons = [];
            Check(Call<List<Rectangle>>(chart, "ButtonBounds").Count == 0, "no buttons, none placed");
        });
        Run("hovering one chart shows the same moment on the rest of its group", () =>
        {
            using var a = Chart();
            using var b = Chart();
            var group = new ChartGroup();
            group.Add(a);
            group.Add(b);
            var plot = Plot(a);
            Mouse(a, "OnMouseMove", new Point(plot.Left + plot.Width / 2, plot.Top + 10));
            Check(Field<int?>(b, "_linkedHoverFrame") is int frame && Math.Abs(frame - 500) <= 2, "linked cursor at the hovered frame");
            using var image = Render(b);
            a.GetType().GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(a, [EventArgs.Empty]);
            Check(Field<int?>(b, "_linkedHoverFrame") is null, "cleared when the pointer leaves");
        });
        Run("hiding a player on one chart hides them on every chart in the group", () =>
        {
            static ChartSeries Keyed(string key, string name) => new() { Name = name, Key = key, Points = [new(0, 10), new(1000, 20)] };
            static ChartSeries Unkeyed() => new() { Name = "Unkeyed", Points = [new(0, 1), new(1000, 1)] };
            using var a = Chart();
            using var b = Chart();
            a.SetData([Keyed("p1", "One"), Keyed("p2", "Two"), Unkeyed()]);
            b.SetData([Keyed("p1", "One"), Keyed("p1", "One used"), Keyed("p2", "Two"), Unkeyed()]);
            var group = new ChartGroup();
            group.Add(a);
            group.Add(b);
            ClickLegend(a, 0);
            Check(!b.Series[0].Visible && !b.Series[1].Visible && b.Series[2].Visible, "both of a player's series follow");
            ClickLegend(b, 2);
            Check(!a.Series[1].Visible && a.Series[2].Visible, "works both ways; unkeyed series are left alone");
            ClickLegend(a, 3);
            Check(b.Series.All(s => s.Visible), "Show all reaches the group");
        });
        foreach (bool stable in new[] { false, true })
            Run($"a {(stable ? "stable" : "plain")} scrolling page {(stable ? "stays put" : "jumps")} when a control far above takes focus", () =>
            {
                using var form = new Form { Width = 400, Height = 300 };
                Panel page = stable ? new StableScrollPanel() : new Panel();
                page.Dock = DockStyle.Fill;
                page.AutoScroll = true;
                var content = new Panel { Dock = DockStyle.Top, Height = 3000 };
                var target = new Button { Top = 100, Left = 10 };
                content.Controls.Add(target);
                page.Controls.Add(content);
                form.Controls.Add(page);
                // Scroll bars only exist once the window is shown: show it off-screen and invisible.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                page.AutoScrollPosition = new Point(0, 1500);
                int before = page.AutoScrollPosition.Y;
                Check(before < 0, "page scrolled down to start with");
                page.ScrollControlIntoView(target);
                Check((page.AutoScrollPosition.Y == before) == stable, stable ? "scroll position kept" : "plain panel scrolls to it");
            });
        Run("the cameo grid wraps a side's types into bands, every player under the same cameos", () =>
        {
            var columns = Enumerable.Range(0, 20)
                .Select(i => new TypeColumn(AbstractType.UnitType, i, $"T{i}", "", $"Type {i}", 100)).ToList();
            using var matrix = new CameoMatrix { Width = 150 + 64 * 8 + 10 };
            _ = matrix.Handle;
            matrix.SetBlocks([new CameoBlock("Soviet", columns,
                [new CameoRow("One", Color.Red, Enumerable.Range(0, 20).ToArray()), new CameoRow("Two", Color.Blue, new int[20])])]);
            Check(Field<System.Collections.ICollection>(matrix, "_bands").Count == 3, "twenty columns at eight a band is three bands");
            int narrow = matrix.Height;
            matrix.Width = 150 + 64 * 20 + 10;
            Check(Field<System.Collections.ICollection>(matrix, "_bands").Count == 1 && matrix.Height < narrow,
                "one band, and shorter, when there is room");
            using var image = Render(matrix);
            matrix.SetBlocks([]);
            Check(Field<System.Collections.ICollection>(matrix, "_bands").Count == 0 && matrix.Height > 0,
                "an empty grid keeps a line for its message");
        });
        Run("column autosizing fits longer headers and longer cells", () =>
        {
            using var list = List();
            list.Columns.Add("A much longer header than its values", 30);
            list.Columns.Add("Short", 30);
            list.Columns.Add("Empty column with a long header", 30);
            list.Items.Add(new ListViewItem(["1", new string('W', 80), ""]));
            _ = list.Handle;
            list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            CheckHeaders(list);
            Check(list.Columns[1].Width >= TextRenderer.MeasureText(new string('W', 80), list.Font).Width, "wide content still fits");
            list.Columns[0].Width = 1;
            CheckHeaders(list);
            list.Columns[0].Width = 900;
            Check(list.Columns[0].Width == 900, "manual wider size preserved");
        });
        Run("empty and virtual lists keep headers readable after font changes", () =>
        {
            using var list = List();
            list.Columns.Add("Header wider than values", 10);
            list.Columns.Add("Tail", 10);
            _ = list.Handle;
            list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            CheckHeaders(list);
            list.VirtualMode = true;
            list.RetrieveVirtualItem += (_, e) => e.Item = new ListViewItem(["1", "2"]);
            list.VirtualListSize = 2;
            using var largeFont = new Font("Segoe UI", 18);
            list.Font = largeFont;
            list.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            CheckHeaders(list);
        });
        if (args.Length > 0)
        {
            using var chart = Chart();
            chart.Title = "Power produced (solid) and used (dashed)";
            chart.Width = 740;
            chart.Height = 280;
            chart.SetData(Enumerable.Range(0, 8).SelectMany(i => new[]
            {
                new ChartSeries { Name = $"Player {i + 1}", Color = Theme.ForHouse(i),
                    Points = Enumerable.Range(0, 21).Select(t => new Sample(t * 50, 200 + i * 60 + Math.Sin(t * .5 + i) * 130)).ToArray() },
                new ChartSeries { Name = $"Player {i + 1} used", Color = Theme.ForHouse(i), Style = SeriesStyle.Step, DashStyle = DashStyle.Dash,
                    Points = Enumerable.Range(0, 21).Select(t => new Sample(t * 50, 100 + i * 40 + t * 6)).ToArray() }
            }));
            ClickLegend(chart, 6);
            using var snapshot = Render(chart);
            snapshot.Save(args[0]);
            Console.WriteLine($"Snapshot: {args[0]}");
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static TimeSeriesChart Chart()
    {
        var chart = new TimeSeriesChart { Width = 700, Height = 240, Title = "Test" };
        chart.SetData([
            new ChartSeries { Name = "Same name", Color = Color.Blue, Points = [new(0, 100), new(1000, 100)] },
            new ChartSeries { Name = "Same name", Color = Color.Red, Points = [new(0, 1000), new(1000, 1000)] }
        ]);
        _ = chart.Handle;
        return chart;
    }

    private static HeaderAwareListView List() => new() { View = View.Details, Width = 1000, Height = 200 };
    private static void CheckHeaders(ListView list)
    {
        foreach (ColumnHeader column in list.Columns)
            Check(column.Width >= TextRenderer.MeasureText(column.Text, list.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + 16, $"header fits: {column.Text}");
    }
    private static Bitmap Render(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Rectangle Plot(TimeSeriesChart chart) =>
        (Rectangle)typeof(TimeSeriesChart).GetProperty("PlotArea", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart)!;
    private static T Call<T>(object target, string name) =>
        (T)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [])!;
    private static List<(ChartSeries?, Rectangle)> Legend(TimeSeriesChart chart) =>
        Field<List<(ChartSeries?, Rectangle)>>(chart, "_legend").Select(entry => (entry.Item1,
            new Rectangle(entry.Item2.X, chart.Height - Field<int>(chart, "_legendHeight") - 4 + entry.Item2.Y,
                entry.Item2.Width, entry.Item2.Height))).ToList();
    private static void ClickLegend(TimeSeriesChart chart, int index)
    {
        var rect = Legend(chart)[index].Item2;
        var point = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        Mouse(chart, "OnMouseDown", point);
        Mouse(chart, "OnMouseUp", point);
    }
    private static void Mouse(Control control, string method, Point point) =>
        control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control,
            [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception e) { _failed++; Console.WriteLine($"FAIL {name}: {e}"); }
    }
}
