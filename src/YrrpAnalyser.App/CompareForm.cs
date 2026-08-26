using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>
/// Two peers' recordings of one game, held against each other frame by frame.
///
/// Each machine wrote down the engine's own state hash at the same instant in every frame, so the
/// first frame the two disagree on is the frame the simulations actually came apart. That is the
/// one number a desync report never has, and it is exactly what this recovers.
/// </summary>
internal sealed class CompareForm : Form
{
    public CompareForm(ReplayDocument left, ReplayDocument right, DesyncCompareResult result)
    {
        Text = "Compare recordings";
        Size = new Size(1120, 760);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        Font = Theme.Ui;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            BackColor = Theme.Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeadline(left, right, result), 0, 0);
        root.Controls.Add(BuildCrcChart(left, result), 0, 1);
        root.Controls.Add(BuildDetail(left, right, result), 0, 2);

        Controls.Add(root);
    }

    private static Control BuildHeadline(ReplayDocument left, ReplayDocument right,
        DesyncCompareResult result)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Theme.Background,
        };

        string verdict;
        Color verdictColor;

        if (result.FirstDivergenceFrame < 0 && result.ComparedFrames > 0)
        {
            verdict = $"In step. All {result.ComparedFrames:N0} shared frames hash identically.";
            verdictColor = Theme.Good;
        }
        else if (result.ComparedFrames == 0)
        {
            verdict = "No overlapping frames carry a state hash — nothing to compare.";
            verdictColor = Theme.Warning;
        }
        else
        {
            verdict = $"Diverged at frame {result.FirstDivergenceFrame:N0} " +
                      $"({left.TimeLabel(result.FirstDivergenceFrame)}). " +
                      $"{result.TotalDivergentFrames:N0} of {result.ComparedFrames:N0} shared frames differ.";
            verdictColor = Theme.Danger;
        }

        panel.Controls.Add(new Label
        {
            Text = verdict,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = verdictColor,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        });

        panel.Controls.Add(new Label
        {
            Text = $"A: {left.FileName}   ({left.Roster.RecordingPlayer?.DisplayName ?? "?"})\n" +
                   $"B: {right.FileName}   ({right.Roster.RecordingPlayer?.DisplayName ?? "?"})\n" +
                   $"Frames present on only one side: {result.LeftOnlyFrames:N0} in A, " +
                   $"{result.RightOnlyFrames:N0} in B.",
            ForeColor = Theme.Muted,
            AutoSize = true,
            Font = Theme.Mono,
            Margin = new Padding(0, 0, 0, 8),
        });

        if (result.Mismatches.Count > 0)
        {
            var warning = new Panel
            {
                BackColor = Color.FromArgb(0xFF, 0xF6, 0xE5),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 8, 12, 8),
                Width = 1040,
                Margin = new Padding(0, 0, 0, 8),
            };
            warning.Controls.Add(new Label
            {
                Text = "The two machines did not agree on their inputs, so a divergence here need " +
                       "not be a netcode fault at all:\n  · " +
                       string.Join("\n  · ", result.Mismatches.Take(12)) +
                       (result.Mismatches.Count > 12 ? $"\n  · ... and {result.Mismatches.Count - 12} more" : ""),
                ForeColor = Theme.Warning,
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
            });
            panel.Controls.Add(warning);
        }

        return panel;
    }

    private static Control BuildCrcChart(ReplayDocument left, DesyncCompareResult result)
    {
        var chart = new TimeSeriesChart
        {
            Title = "Frames where the two recordings hash differently",
            SimulationFps = left.Header.SimulationFps,
            Dock = DockStyle.Fill,
            MinimumYRange = 1,
            Margin = new Padding(0, 0, 0, 10),
        };

        var points = result.Divergences.Select(d => new Sample(d.Frame, 1)).ToList();
        chart.SetData(
            [new ChartSeries
            {
                Name = "divergent frame",
                Color = Theme.Danger,
                Style = SeriesStyle.Points,
                Points = points,
            }],
            result.FirstDivergenceFrame >= 0
                ? [new ChartMarker(result.FirstDivergenceFrame, Theme.Danger, "first divergence")]
                : []);

        return chart;
    }

    private static Control BuildDetail(ReplayDocument left, ReplayDocument right,
        DesyncCompareResult result)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var crcView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = Theme.Panel,
            Font = Theme.Mono,
        };
        crcView.Columns.Add("Frame", 100, HorizontalAlignment.Right);
        crcView.Columns.Add("Time", 80, HorizontalAlignment.Right);
        crcView.Columns.Add($"A  {left.FileName}", 320);
        crcView.Columns.Add($"B  {right.FileName}", 320);

        foreach (var d in result.Divergences)
        {
            crcView.Items.Add(new ListViewItem([
                d.Frame.ToString("N0"),
                left.TimeLabel(d.Frame),
                d.LeftCrc.ToString("X8"),
                d.RightCrc.ToString("X8"),
            ]));
        }

        if (result.Divergences.Count == 0)
            crcView.Items.Add(new ListViewItem(["—", "", "every shared frame matched", ""])
            { ForeColor = Theme.Good });
        else if (result.TotalDivergentFrames > result.Divergences.Count)
            crcView.Items.Add(new ListViewItem([
                "...", "",
                $"{result.TotalDivergentFrames - result.Divergences.Count:N0} further divergent frames not listed",
                "",
            ])
            { ForeColor = Theme.Muted });

        tabs.TabPages.Add(new TabPage($"Divergent frames ({result.TotalDivergentFrames:N0})")
        { Controls = { crcView }, BackColor = Theme.Panel });

        tabs.TabPages.Add(BuildContextPage($"Events around it — A", result.LeftContext));
        tabs.TabPages.Add(BuildContextPage($"Events around it — B", result.RightContext));

        return tabs;
    }

    private static TabPage BuildContextPage(string title, List<string> lines)
    {
        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Theme.Mono,
            BackColor = Theme.Panel,
            Text = lines.Count > 0
                ? string.Join("\r\n", lines)
                : "Nothing to show — the two recordings never diverged.",
        };
        return new TabPage(title) { Controls = { box }, BackColor = Theme.Panel };
    }

    /// <summary>Plain-text form of the whole comparison, for pasting into a bug report.</summary>
    public static string ToText(ReplayDocument left, ReplayDocument right, DesyncCompareResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"A: {left.FileName}");
        sb.AppendLine($"B: {right.FileName}");
        sb.AppendLine();
        foreach (var m in result.Mismatches) sb.AppendLine($"! {m}");
        sb.AppendLine($"compared {result.ComparedFrames:N0} frames");
        sb.AppendLine(result.FirstDivergenceFrame < 0
            ? "no divergence"
            : $"first divergence at frame {result.FirstDivergenceFrame:N0}");
        foreach (var d in result.Divergences.Take(20))
            sb.AppendLine($"  {d.Frame,8:N0}  {d.LeftCrc:X8}  {d.RightCrc:X8}");
        return sb.ToString();
    }
}
