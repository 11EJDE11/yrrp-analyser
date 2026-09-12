using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private void PopulateNetwork(ReplayDocument doc, NetworkAnalysis network)
    {
        _networkFlow.SuspendLayout();
        _networkFlow.Controls.Clear();
        _networkCharts.Clear();

        int fps = doc.Header.SimulationFps;
        var gapMarkers = network.LargeFrameInfoGaps
            .Select(s => new ChartMarker(s.EndFrame, Theme.Danger, s.Name))
            .ToList();

        _networkFlow.Controls.Add(SectionHeading("Connection"));
        _networkFlow.Controls.Add(BuildNetworkSummary(network));
        _networkFlow.Controls.Add(ChartHint());

        _networkFlow.Controls.Add(Chart(
            "Connection response — each peer's worst smoothed connection (ResponseTime2)",
            " ms", fps,
            network.Series.Where(s => s.RoundTripMs.Count > 0).Select(s => new ChartSeries
            {
                Name = s.Name,
                Color = Theme.ForHouse(s.HouseIndex),
                Style = SeriesStyle.Step,
                Points = s.RoundTripMs,
            }),
            gapMarkers));

        _networkFlow.Controls.Add(Chart(
            "Latency level request — each peer's requested ProtocolZero level (1 best, 9 highest)",
            "", fps,
            network.Series.Where(s => s.LatencyLevel.Count > 0).Select(s => new ChartSeries
            {
                Name = s.Name,
                Color = Theme.ForHouse(s.HouseIndex),
                Style = SeriesStyle.Step,
                Points = s.LatencyLevel,
            }),
            gapMarkers,
            minimumY: 9));

        _networkFlow.Controls.Add(Chart(
            "MaxAhead — how far ahead of the current frame each remote peer was scheduling orders",
            " frames", fps,
            network.Series.Where(s => s.MaxAhead.Count > 0).Select(s => new ChartSeries
            {
                Name = s.Name,
                Color = Theme.ForHouse(s.HouseIndex),
                Style = SeriesStyle.Step,
                Points = s.MaxAhead,
            }),
            gapMarkers));

        _networkFlow.Controls.Add(Chart(
            "Process time — mean main-loop work per frame, including input, rendering and logic",
            " ms", fps,
            network.Series.Where(s => s.ProcessMs.Count > 0).Select(s => new ChartSeries
            {
                Name = s.Name,
                Color = Theme.ForHouse(s.HouseIndex),
                Style = SeriesStyle.Step,
                Points = s.ProcessMs,
            }),
            gapMarkers,
            minimumY: 20));

        _networkFlow.Controls.Add(Chart(
            "FRAMEINFO spacing — simulation frames between recorded events",
            " frames", fps,
            network.Series.Where(s => s.FrameInfoGap.Count > 0).Select(s => new ChartSeries
            {
                Name = s.Name,
                Color = Theme.ForHouse(s.HouseIndex),
                Style = SeriesStyle.Points,
                Points = s.FrameInfoGap,
            }),
            gapMarkers));

        var fpsSeries = network.Series.Where(s => s.RequestedFps.Count > 0).ToList();
        if (fpsSeries.Count > 0)
        {
            _networkFlow.Controls.Add(Chart(
                "Requested frame rate — session target, not measured FPS",
                " FPS", fps,
                fpsSeries.Select(s => new ChartSeries
                {
                    Name = $"{s.Name} (master)",
                    Color = Theme.ForHouse(s.HouseIndex),
                    Style = SeriesStyle.Step,
                    Points = s.RequestedFps,
                }),
                gapMarkers,
                minimumY: 60));
        }

        if (network.LargeFrameInfoGaps.Count > 0)
        {
            _networkFlow.Controls.Add(SectionHeading($"Large FRAMEINFO gaps ({network.LargeFrameInfoGaps.Count})"));
            _networkFlow.Controls.Add(BuildFrameInfoGapList(doc, network));
        }

        _networkFlow.Controls.Add(SectionHeading("Where these numbers come from"));
        _networkFlow.Controls.Add(new Label
        {
            Text = NetworkAnalysis.ProvenanceNote,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 960,
            Height = 320,
            Margin = new Padding(0, 0, 0, 12),
        });

        FitWidths(_networkFlow);
        _networkFlow.ResumeLayout();
    }

    /// <summary>Nothing about the wheel or the drag is discoverable, so it is written down.</summary>
    internal static Label ChartHint() => new()
    {
        Text = "Click a legend name to toggle it; Shift+click to isolate it; Show all to restore.\nDrag the plot to pan, Ctrl+scroll to zoom, double-click the plot to reset. Charts share one time axis, and hovering one shows the same moment on all of them.",
        ForeColor = Theme.Muted,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 8),
    };

    private Control Chart(string title, string suffix, int fps, IEnumerable<ChartSeries> series,
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
        _networkCharts.Add(chart);
        return FillWidth(chart);
    }

    private static Control BuildNetworkSummary(NetworkAnalysis network)
    {
        var view = MakeListView(
            ("House", 55), ("Player", 170), ("Response", 110), ("Worst", 80),
            ("Level request", 110), ("Process mean", 130), ("Worst", 80),
            ("MaxAhead", 90), ("Worst gap", 90), ("FRAMEINFO", 100));
        view.Dock = DockStyle.None;
        view.Width = 980;
        view.Height = 28 + Math.Max(1, network.Series.Count) * 20;
        view.Margin = new Padding(0, 0, 0, 10);
        FillWidth(view);

        foreach (var s in network.Series)
        {
            string level = s.LatencyLevel.Count > 0
                ? $"{s.LatencyLevel.Max(x => x.Value):0} ({NetworkAnalysis.LatencyLevelName((int)s.LatencyLevel.Max(x => x.Value))})"
                : "—";

            view.Items.Add(new ListViewItem([
                s.HouseIndex.ToString(),
                s.Name,
                s.RoundTripMs.Count > 0 ? $"{s.MedianRoundTripMs:0} ms median" : "—",
                s.RoundTripMs.Count > 0 ? $"{s.WorstRoundTripMs:0} ms" : "—",
                level,
                s.ProcessMs.Count > 0 ? $"{s.MedianProcessMs:0.0} ms median" : "—",
                s.ProcessMs.Count > 0 ? $"{s.WorstProcessMs:0.0} ms" : "—",
                // The recording machine's own FRAMEINFO never enters the event queue, so there is
                // nothing to report here for it - which is not the same as a MaxAhead of zero.
                s.HasFrameInfo ? $"{s.MedianMaxAhead:0} / {s.WorstMaxAhead:0}" : "not recorded",
                s.HasFrameInfo ? $"{s.WorstFrameInfoGap:0} frames" : "not recorded",
                s.HasFrameInfo ? $"{s.FrameInfoCount:N0}" : "—",
            ])
            { ForeColor = Theme.ForHouse(s.HouseIndex) });
        }

        return view;
    }

    private static Control BuildFrameInfoGapList(ReplayDocument doc, NetworkAnalysis network)
    {
        var view = MakeListView(
            ("Player", 180), ("From", 90), ("To", 90), ("Frames", 80), ("Game seconds", 100), ("At", 80));
        view.Dock = DockStyle.None;
        view.Width = 620;
        view.Height = 26 + Math.Min(12, Math.Max(1, network.LargeFrameInfoGaps.Count)) * 20;
        view.Margin = new Padding(0, 0, 0, 10);

        foreach (var gap in network.LargeFrameInfoGaps.Take(200))
        {
            view.Items.Add(new ListViewItem([
                gap.Name,
                gap.StartFrame.ToString("N0"),
                gap.EndFrame.ToString("N0"),
                gap.Frames.ToString("N0"),
                $"{gap.SimulationSeconds:0.00}",
                doc.TimeLabel(gap.StartFrame),
            ])
            { ForeColor = Theme.ForHouse(gap.HouseIndex) });
        }

        return view;
    }
}
