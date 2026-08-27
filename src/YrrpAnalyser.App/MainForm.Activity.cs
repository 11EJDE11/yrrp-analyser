using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private void PopulateActivity(ReplayDocument doc, ActivityAnalysis activity)
    {
        _activityFlow.SuspendLayout();
        _activityFlow.Controls.Clear();
        _activityCharts.Clear();

        int fps = doc.Header.SimulationFps;
        double bucketSeconds = activity.BucketFrames / (double)Math.Max(1, fps);

        var chatMarkers = doc.EnumerateSideChannel()
            .Where(e => e.Type is SideChannelEventType.ChatMessage or SideChannelEventType.BeaconPlace)
            .Select(e => new ChartMarker(e.FrameNumber, Theme.Accent, e.Text))
            .ToList();

        _activityFlow.Controls.Add(SectionHeading("Activity"));
        _activityFlow.Controls.Add(new Label
        {
            Text = "One click on a group of units emits one MegaMission per unit, so a raw event " +
                   "count measures army size as much as effort. Same-frame group orders are " +
                   "collapsed to one command here, which is much closer to what the player did " +
                   "with the mouse. The roster shows both.",
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 960,
            Height = 34,
            Margin = new Padding(0, 0, 0, 4),
        });
        _activityFlow.Controls.Add(ChartHint());

        _activityFlow.Controls.Add(ActivityChart(
            $"Commands per minute, in {bucketSeconds:0}-second buckets",
            "", fps,
            activity.Players.Select(p => new ChartSeries
            {
                Name = p.Name,
                Color = Theme.ForHouse(p.HouseIndex),
                Style = SeriesStyle.Line,
                Points = p.Apm,
            }),
            chatMarkers,
            minimumY: 30));

        _activityFlow.Controls.Add(ActivityChart(
            "Events recorded per bucket, every house together",
            "", fps,
            [new ChartSeries
            {
                Name = "all events",
                Color = Theme.Accent,
                Style = SeriesStyle.Bars,
                Points = activity.EventDensity,
            }],
            chatMarkers));

        _activityFlow.Controls.Add(SectionHeading("The recording player's own screen"));
        _activityFlow.Controls.Add(new Label
        {
            Text = "Camera position and unit selection describe what the person making the recording " +
                   "was looking at and had selected. They are view state, not simulation state, so " +
                   "they exist only for that one player.",
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 960,
            Height = 34,
            Margin = new Padding(0, 0, 0, 4),
        });

        _activityFlow.Controls.Add(ActivityChart(
            "Camera travel per bucket, in cells",
            " cells", fps,
            [new ChartSeries
            {
                Name = doc.Roster.RecordingPlayer?.DisplayName ?? "recorder",
                Color = Theme.ForHouse(doc.Roster.RecordingPlayer?.HouseIndex ?? 0),
                Style = SeriesStyle.Line,
                Points = activity.CameraMovement,
            }],
            chatMarkers));

        _activityFlow.Controls.Add(ActivityChart(
            "Objects selected",
            "", fps,
            [new ChartSeries
            {
                Name = "selection size",
                Color = Theme.ForHouse(doc.Roster.RecordingPlayer?.HouseIndex ?? 0),
                Style = SeriesStyle.Step,
                Points = activity.SelectionSize,
            }],
            chatMarkers,
            minimumY: 5));

        _activityFlow.Controls.Add(SectionHeading("Build order"));
        _activityFlow.Controls.Add(new Label
        {
            Text = _types.HasData
                ? $"Type names resolved from {_types.SourceDescription}. A heap ID is a position in " +
                  "the game's type array, so names are only right for the rules the game actually ran."
                : "Produce and Place events carry a type array index, not a name. Point the analyser " +
                  "at an extracted rulesmd.ini under Tools > Set rules INIs to turn these into names.",
            ForeColor = _types.HasData ? Theme.Muted : Theme.Warning,
            AutoSize = false,
            Width = 960,
            Height = 34,
            Margin = new Padding(0, 0, 0, 4),
        });

        _activityFlow.Controls.Add(BuildBuildOrderView(doc, activity));

        _activityFlow.Controls.Add(SectionHeading("Event breakdown"));
        _activityFlow.Controls.Add(BuildEventBreakdown(doc, activity));

        FitWidths(_activityFlow);
        _activityFlow.ResumeLayout();
    }

    private Control ActivityChart(string title, string suffix, int fps, IEnumerable<ChartSeries> series,
        IEnumerable<ChartMarker>? markers = null, double minimumY = 1)
    {
        var chart = new TimeSeriesChart
        {
            Title = title,
            ValueSuffix = suffix,
            SimulationFps = fps,
            TimeLabeller = _doc is { } doc ? doc.TimeLabel : null,
            MinimumYRange = minimumY,
            Width = 980,
            Height = 210,
            Margin = new Padding(0, 4, 0, 10),
        };
        chart.SetData(series, markers);
        _activityCharts.Add(chart);
        return FillWidth(chart);
    }

    private static Control BuildBuildOrderView(ReplayDocument doc, ActivityAnalysis activity)
    {
        var tabs = new TabControl
        {
            Width = 980,
            Height = 320,
            Margin = new Padding(0, 0, 0, 10),
        };
        FillWidth(tabs);

        foreach (var player in activity.Players)
        {
            var view = MakeListView(("Time", 70), ("Frame", 80), ("Order", 90), ("What", 260), ("Where", 100));
            view.Dock = DockStyle.Fill;

            foreach (var entry in player.BuildOrder)
            {
                view.Items.Add(new ListViewItem([
                    doc.TimeLabel(entry.Frame),
                    entry.Frame.ToString("N0"),
                    entry.Type.ToString(),
                    entry.What,
                    entry.Where,
                ]));
            }

            if (player.BuildOrder.Count == 0)
                view.Items.Add(new ListViewItem(["—", "", "", "no production orders recorded", ""])
                { ForeColor = Theme.Muted });

            tabs.TabPages.Add(new TabPage($"{player.Name} ({player.BuildOrder.Count})")
            {
                Controls = { view },
                BackColor = Theme.Panel,
            });
        }

        if (tabs.TabPages.Count == 0)
            tabs.TabPages.Add(new TabPage("No players") { BackColor = Theme.Panel });

        return tabs;
    }

    private static Control BuildEventBreakdown(ReplayDocument doc, ActivityAnalysis activity)
    {
        var houses = activity.Players.OrderBy(p => p.HouseIndex).ToList();

        var columns = new List<(string, int)> { ("Event", 150), ("Category", 100), ("Total", 80) };
        columns.AddRange(houses.Select(p => (p.Name.Length > 14 ? p.Name[..14] : p.Name, 100)));

        var view = MakeListView([.. columns]);
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Height = 28 + Math.Max(1, activity.EventTotals.Count) * 20;
        view.Margin = new Padding(0, 0, 0, 10);
        FillWidth(view);

        foreach (var (type, total) in activity.EventTotals.OrderByDescending(kv => kv.Value))
        {
            var cells = new List<string>
            {
                EventTypes.Name(type),
                EventDescriber.Category(type),
                total.ToString("N0"),
            };
            cells.AddRange(houses.Select(p =>
            {
                int n = p.ByType.GetValueOrDefault(type);
                return n > 0 ? n.ToString("N0") : "";
            }));

            view.Items.Add(new ListViewItem([.. cells])
            {
                ForeColor = EventTypes.IsTiming(type) ? Theme.Muted : Theme.Text,
            });
        }

        return view;
    }
}
