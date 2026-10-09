using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private readonly ListView _sizeView = MakeListView(("Part", 360), ("Count", 100), ("Uncompressed", 120),
        ("On disk", 120), ("Of file", 70), ("Per game minute", 120));

    private readonly Label _sizeSummary = new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 6, 0, 4),
    };

    private readonly SizeBar _sizeBar = new() { Dock = DockStyle.Top, Height = 78 };

    private TabPage? _sizePage;
    private ReplayDocument? _sizeDoc;
    private CancellationTokenSource? _sizeCancel;

    private Control BuildSizeTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(12) };

        var note = new Label
        {
            Text = "Where this file's bytes go. Uncompressed sizes are exact. The frame stream and each save are " +
                   "one deflate stream, so their parts have no size on disk of their own: each part is compressed " +
                   "separately and the stream's real size shared out in proportion. Those figures are marked ~ and " +
                   "are good for ranking parts, to within about a quarter.",
            Dock = DockStyle.Top,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Height = 40,
            Padding = new Padding(0, 6, 0, 6),
        };

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Background,
        };
        header.Controls.Add(_sizeSummary);

        foreach (int column in new[] { 1, 2, 3, 4, 5 })
            _sizeView.Columns[column].TextAlign = HorizontalAlignment.Right;

        root.Controls.Add(_sizeView);
        root.Controls.Add(_sizeBar);
        root.Controls.Add(note);
        root.Controls.Add(header);
        return root;
    }

    /// <summary>Measuring re-reads and recompresses the whole stream, so it waits until the tab is opened.</summary>
    private void OnTabSelectedForSize()
    {
        if (_sizePage is not null && _tabs.SelectedTab == _sizePage && _doc is not null)
            PopulateSize(_doc);
    }

    private void ResetSize()
    {
        _sizeCancel?.Cancel();
        _sizeDoc = null;
        _sizeView.Items.Clear();
        _sizeBar.SetSegments([]);
        _sizeSummary.Text = "Open this tab to measure the file.";
        OnTabSelectedForSize();
    }

    private async void PopulateSize(ReplayDocument doc)
    {
        // Analyse runs again when the rules files change; the file has not.
        if (ReferenceEquals(_sizeDoc, doc)) return;

        _sizeCancel?.Cancel();
        var cancel = _sizeCancel = new CancellationTokenSource();
        _sizeDoc = doc;
        _sizeView.Items.Clear();
        _sizeBar.SetSegments([]);
        _sizeSummary.Text = $"Measuring {doc.FileName}...";

        try
        {
            var size = await Task.Run(() => SizeBreakdown.Measure(doc, cancel.Token), cancel.Token);
            if (cancel.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;
            ShowSize(size);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ReferenceEquals(_sizeDoc, doc))
        {
            _sizeDoc = null;
            _sizeSummary.Text = $"Could not measure the file: {ex.Message}";
        }
    }

    private void ShowSize(SizeBreakdown size)
    {
        _sizeSummary.Text = $"{size.FileSize:N0} bytes ({FormatBytes(size.FileSize)})" +
                            (size.GameMinutes > 0
                                ? $"  ·  {FormatBytes((long)(size.FileSize / size.GameMinutes))} per game minute"
                                : "");

        _sizeView.BeginUpdate();
        _sizeView.Items.Clear();

        void Add(SizeEntry entry, int depth)
        {
            _sizeView.Items.Add(new ListViewItem([
                new string(' ', depth * 4) + entry.Name,
                entry.Count > 0 ? entry.Count.ToString("N0") : "",
                FormatBytes(entry.RawBytes),
                (entry.StoredIsEstimate ? "~" : "") + FormatBytes(entry.StoredBytes),
                $"{100.0 * entry.StoredBytes / Math.Max(1, size.FileSize):0.0}%",
                size.GameMinutes > 0 ? FormatBytes((long)(entry.StoredBytes / size.GameMinutes)) : "",
            ])
            {
                Font = depth == 0 ? Theme.UiBold : Theme.Ui,
                ForeColor = depth <= 1 ? Theme.Text : Theme.Muted,
            });
            foreach (var child in entry.Children)
                Add(child, depth + 1);
        }

        foreach (var section in size.Sections)
            Add(section, 0);
        _sizeView.EndUpdate();

        // The bar opens up the frame stream, since that is where the recorder's choices show.
        var segments = new List<(string, long)>();
        foreach (var section in size.Sections)
        {
            if (section.Name == "Frame stream")
                segments.AddRange(section.Children.Select(c => (c.Name, c.StoredBytes)));
            else
                segments.Add((section.Name, section.StoredBytes));
        }
        _sizeBar.SetSegments(segments);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.00} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes:N0} B",
    };

    /// <summary>The file as one bar, a segment per part, with a legend for the parts big enough to matter.</summary>
    private sealed class SizeBar : Control
    {
        private static readonly Color[] Palette =
        [
            Color.FromArgb(0x1F, 0x6F, 0xD8), Color.FromArgb(0xD9, 0x53, 0x1E), Color.FromArgb(0x2E, 0x8B, 0x4F),
            Color.FromArgb(0x8E, 0x44, 0xAD), Color.FromArgb(0xC0, 0x28, 0x28), Color.FromArgb(0x0E, 0x8C, 0x9E),
            Color.FromArgb(0xB0, 0x8D, 0x00), Color.FromArgb(0xD1, 0x4D, 0x9C),
        ];

        // Parts under this share of the file are drawn but left out of the legend and share one grey.
        private const double LegendThreshold = 0.01;

        private List<(string Name, long Bytes)> _segments = [];

        public SizeBar()
        {
            DoubleBuffered = true;
            BackColor = Theme.Background;
            Font = Theme.Ui;
        }

        public void SetSegments(List<(string Name, long Bytes)> segments)
        {
            _segments = segments.Where(s => s.Bytes > 0).OrderByDescending(s => s.Bytes).ToList();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            long total = _segments.Sum(s => s.Bytes);
            if (total <= 0) return;

            var g = e.Graphics;
            const int barTop = 6, barHeight = 26;
            int width = ClientSize.Width - 1;
            double x = 0;
            int legendX = 0, legendY = barTop + barHeight + 10, colour = 0;
            using var textBrush = new SolidBrush(Theme.Text);

            foreach (var (name, bytes) in _segments)
            {
                double share = (double)bytes / total;
                bool listed = share >= LegendThreshold && colour < Palette.Length;
                var fill = listed ? Palette[colour++] : Theme.Border;

                double w = share * width;
                using (var brush = new SolidBrush(fill))
                    g.FillRectangle(brush, (float)x, barTop, (float)Math.Max(1, w), barHeight);
                x += w;

                if (!listed) continue;
                string label = $"{name} {share * 100:0}%";
                var textSize = TextRenderer.MeasureText(g, label, Font);
                if (legendX + 16 + textSize.Width > width)
                {
                    legendX = 0;
                    legendY += textSize.Height + 2;
                }
                using (var brush = new SolidBrush(fill))
                    g.FillRectangle(brush, legendX, legendY + 3, 10, 10);
                TextRenderer.DrawText(g, label, Font, new Point(legendX + 14, legendY), Theme.Text);
                legendX += 14 + textSize.Width + 12;
            }

            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, 0, barTop, width, barHeight);
        }
    }
}
