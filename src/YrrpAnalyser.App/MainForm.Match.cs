using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private MatchAnalysis? _match;
    private TeamAnalysis? _teams;
    private WinLikelihood? _win;

    private readonly MapView _mapView = new() { Dock = DockStyle.Fill };
    private readonly WinBar _winBar = new() { Dock = DockStyle.Top };
    private readonly TimelineScrubber _scrubber = new() { Dock = DockStyle.Top };
    private readonly TeamStatusView _teamStatus = new() { Dock = DockStyle.Top };
    private readonly HeaderAwareListView _feed = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        BackColor = Theme.Panel,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Button _playButton = new() { Text = "Play", Width = 72, Height = 28, Margin = new Padding(0, 4, 6, 4) };
    private readonly ComboBox _speedBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Margin = new Padding(0, 6, 10, 4) };
    private readonly Label _clock = new() { AutoSize = true, Font = new Font("Consolas", 13f, FontStyle.Bold), ForeColor = Theme.Text, Margin = new Padding(6, 6, 14, 0) };
    private readonly Label _positionsNote = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 10, 0, 0) };
    private readonly System.Windows.Forms.Timer _playTimer = new() { Interval = 33 };

    private static readonly double[] Speeds = [0.5, 1, 2, 4, 8, 16, 32];
    private int _matchFrame;
    private double _frameCarry;
    private long _lastTick;
    private List<MatchEvent> _feedEvents = [];
    private int _feedCount;

    private Control BuildMatchTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };

        var transport = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, WrapContents = false, BackColor = Theme.Background,
            Padding = new Padding(12, 2, 12, 0),
        };
        var back = new Button { Text = "-10 s", Width = 56, Height = 28, Margin = new Padding(0, 4, 4, 4) };
        var forward = new Button { Text = "+10 s", Width = 56, Height = 28, Margin = new Padding(0, 4, 10, 4) };
        foreach (double s in Speeds) _speedBox.Items.Add($"{s:0.#}x");
        _speedBox.SelectedIndex = 3;
        _playButton.Click += (_, _) => TogglePlay();
        back.Click += (_, _) => StepSeconds(-10);
        forward.Click += (_, _) => StepSeconds(10);
        transport.Controls.AddRange([_playButton, back, forward, new Label { Text = "Speed", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 10, 4, 0) },
            _speedBox, _clock, _positionsNote]);

        _scrubber.SeekRequested += SeekWhileDragging;
        _winBar.SeekRequested += SeekWhileDragging;
        _playTimer.Tick += (_, _) => PlaybackTick();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            BackColor = Theme.Border,
        };
        split.Panel1.BackColor = Theme.Background;
        split.Panel2.BackColor = Theme.Panel;
        split.HandleCreated += (_, _) =>
        {
            split.Panel2MinSize = 300;
            split.SplitterDistance = Math.Max(400, split.Width - 380);
        };

        split.Panel1.Controls.Add(_mapView);
        split.Panel1.Controls.Add(BuildLayerBar());

        _feed.Columns.Add("Time", 56);
        _feed.Columns.Add("Who", 110);
        _feed.Columns.Add("What", 400);
        _feed.RetrieveVirtualItem += (_, e) => e.Item = FeedItem(e.ItemIndex);
        _feed.SizeChanged += (_, _) => _feed.Columns[2].Width = Math.Max(120, _feed.ClientSize.Width - 170);
        _feed.ItemActivate += (_, _) => JumpToFeedItem();
        _feed.MouseClick += (_, _) => JumpToFeedItem();

        var feedHeader = new Label
        {
            Text = "What happened, latest first - click to jump there",
            Dock = DockStyle.Top, Height = 26, ForeColor = Theme.Muted, Padding = new Padding(8, 6, 0, 0),
            BackColor = Theme.Background,
        };
        var statusHost = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Panel, Padding = new Padding(0, 4, 0, 4) };
        statusHost.Controls.Add(_teamStatus);
        split.Panel2.Controls.Add(_feed);
        split.Panel2.Controls.Add(feedHeader);
        split.Panel2.Controls.Add(statusHost);

        _teamStatus.HouseToggled += house =>
        {
            if (!_mapView.HiddenHouses.Remove(house)) _mapView.HiddenHouses.Add(house);
            _mapView.Refresh(heatChanged: true);
        };

        _mapView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space) { TogglePlay(); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { StepSeconds(-5); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { StepSeconds(5); e.Handled = true; }
        };
        _mapView.PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Space) e.IsInputKey = true; };

        // Fill first, then the rows above it from the bottom up: the control added last docks first.
        root.Controls.Add(split);
        root.Controls.Add(_winBar);
        root.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Background });
        root.Controls.Add(_scrubber);
        root.Controls.Add(transport);
        return root;
    }

    private Control BuildLayerBar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.Background,
            Padding = new Padding(8, 4, 8, 2),
        };

        CheckBox Toggle(string text, bool on, Action<bool> set, string tip)
        {
            var box = new CheckBox { Text = text, Checked = on, AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
            box.CheckedChanged += (_, _) => { set(box.Checked); _mapView.Refresh(heatChanged: false); };
            _toolTips.SetToolTip(box, tip);
            bar.Controls.Add(box);
            return box;
        }

        Toggle("Units", true, v => _mapView.ShowUnits = v, "Vehicles are squares, infantry dots, aircraft triangles.");
        Toggle("Buildings", true, v => _mapView.ShowBuildings = v, "Each building's footprint, in its owner's colour.");
        Toggle("Orders", true, v => _mapView.ShowOrders = v, "Orders given in the last few seconds: a dashed line from the unit to where it was sent, a cross for an attack.");
        Toggle("Trails", true, v => _mapView.ShowTrails = v, "Where each unit has been over the last 20 seconds (recorded positions only).");
        Toggle("Deaths", true, v => _mapView.ShowDeaths = v, "A cross where something was destroyed, fading over 15 seconds (recorded positions only).");
        Toggle("Camera", true, v => _mapView.ShowCamera = v, "Roughly what the recording player was looking at.");
        Toggle("Markers", true, v => _mapView.ShowMarkers = v, "Start positions, beacons and superweapon strikes.");

        var heat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(8, 1, 8, 0) };
        heat.Items.AddRange(["No heatmap", "Heatmap: fighting", "Heatmap: armies", "Heatmap: orders"]);
        heat.SelectedIndex = 0;
        heat.SelectedIndexChanged += (_, _) => { _mapView.Heat = (HeatLayer)heat.SelectedIndex; _mapView.Refresh(heatChanged: true); };
        _toolTips.SetToolTip(heat, "Fighting: where things were destroyed. Armies: where units are right now. Orders: where units were sent.");

        var window = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(0, 1, 8, 0) };
        (string Label, int Seconds)[] windows = [("last 15 s", 15), ("last 30 s", 30), ("last minute", 60), ("last 2 minutes", 120), ("last 5 minutes", 300), ("whole game so far", 100000)];
        foreach (var (label, _) in windows) window.Items.Add(label);
        window.SelectedIndex = 3;
        _mapView.WindowSeconds = 120;
        window.SelectedIndexChanged += (_, _) => { _mapView.WindowSeconds = windows[window.SelectedIndex].Seconds; _mapView.Refresh(heatChanged: true); };
        _toolTips.SetToolTip(window, "How far back the heatmap looks.");

        var fit = new Button { Text = "Fit map", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        fit.Click += (_, _) => { _mapView.FitToView(); _mapView.Invalidate(); };

        bar.Controls.AddRange([heat, window, fit]);
        return bar;
    }

    private readonly ToolTip _toolTips = new();

    private void PopulateMatch(ReplayDocument doc)
    {
        Pause();
        _mapView.HiddenHouses.Clear();
        if (_match is null || _teams is null || _win is null || _statistics is null) return;

        var types = doc.Statistics?.Types ?? TypeTable.Empty;
        _mapView.HouseColour = Theme.ForHouse;
        _mapView.HouseName = h => StatisticsAnalysis.HouseName(doc, h);
        _mapView.IsPlayer = h => _teams.TeamOf(h) is not null;
        _mapView.TypeOf = (kind, index) => types.Get(kind switch
        {
            ObjectKind.Infantry => AbstractType.InfantryType,
            ObjectKind.Aircraft => AbstractType.AircraftType,
            ObjectKind.Building => AbstractType.BuildingType,
            _ => AbstractType.UnitType,
        }, index);
        _mapView.TimeLabel = doc.TimeLabel;
        int fps = Math.Max(1, doc.Header.SimulationFps);
        _mapView.SecondsToFrames = s => (int)Math.Min(int.MaxValue / 2, (long)s * fps);
        _mapView.SetMatch(_match);

        _winBar.SetData(_win, _teams.Teams, _match.LastFrame);
        _teamStatus.SetData(doc, _teams, _statistics, _win, _mapView.HiddenHouses);

        var markers = new List<ScrubMarker>();
        foreach (var e in _match.Events)
        {
            switch (e.Kind)
            {
                case MatchEventKind.Defeat:
                    markers.Add(new ScrubMarker(e.Frame, Theme.ForHouse(e.House), $"{StatisticsAnalysis.HouseName(doc, e.House)} defeated"));
                    break;
                case MatchEventKind.Superweapon:
                    markers.Add(new ScrubMarker(e.Frame, Theme.ForHouse(e.House), $"{StatisticsAnalysis.HouseName(doc, e.House)} {e.Text}"));
                    break;
                case MatchEventKind.Fight:
                    markers.Add(new ScrubMarker(e.Frame, Theme.Danger, e.Text));
                    break;
            }
        }
        _scrubber.SetData(_match.LastFrame, _match.Intensity, markers, doc.TimeLabel, fps);

        _feedEvents = _match.Events;
        _positionsNote.Text = _match.HasRecordedPositions
            ? $"Recorded positions: {_match.ObjectList.Count:N0} objects tracked, {_match.Deaths.Count:N0} destroyed"
            : "Estimated positions: this recording predates object snapshots";
        _positionsNote.ForeColor = _match.HasRecordedPositions ? Theme.Good : Theme.Warning;

        _matchFrame = -1;
        SetMatchFrame(0);
    }

    private void SetMatchFrame(int frame)
    {
        if (_match is null) return;
        frame = Math.Clamp(frame, 0, _match.LastFrame);
        if (frame == _matchFrame) return;
        _matchFrame = frame;

        _mapView.SetFrame(frame);
        _winBar.Frame = frame;
        _scrubber.Frame = frame;
        _teamStatus.Frame = frame;
        _clock.Text = $"{_doc?.TimeLabel(frame)} / {_doc?.TimeLabel(_match.LastFrame)}";

        int count = CountEventsThrough(frame);
        if (count != _feedCount)
        {
            _feedCount = count;
            _feed.VirtualListSize = count;
            _feed.Invalidate();
        }
    }

    private int CountEventsThrough(int frame)
    {
        int lo = 0, hi = _feedEvents.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_feedEvents[mid].Frame <= frame) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private ListViewItem FeedItem(int index)
    {
        int at = _feedCount - 1 - index;
        if (at < 0 || at >= _feedEvents.Count || _doc is null) return new ListViewItem(["", "", ""]);
        var e = _feedEvents[at];
        string who = e.House >= 0 ? StatisticsAnalysis.HouseName(_doc, e.House) : "";
        var item = new ListViewItem([_doc.TimeLabel(e.Frame), who, e.Text]) { UseItemStyleForSubItems = false, Tag = e };
        if (e.House >= 0) item.SubItems[1].ForeColor = Theme.ForHouse(e.House);
        item.SubItems[2].ForeColor = e.Kind switch
        {
            MatchEventKind.Defeat or MatchEventKind.Fight or MatchEventKind.Destroyed => Theme.Danger,
            MatchEventKind.Superweapon => Theme.Warning,
            MatchEventKind.Chat => Theme.Accent,
            _ => Theme.Text,
        };
        return item;
    }

    private void JumpToFeedItem()
    {
        if (_feed.SelectedIndices.Count == 0) return;
        int at = _feedCount - 1 - _feed.SelectedIndices[0];
        if (at < 0 || at >= _feedEvents.Count) return;
        var e = _feedEvents[at];
        Pause();
        if (e.X is { } x && e.Y is { } y) _mapView.CentreOn(x, y);
        // Keep the clicked row where it is: moving to its frame shrinks the list to end at it.
        SetMatchFrame(e.Frame);
    }

    private long _lastDragPaint;

    /// <summary>
    /// A drag sends a stream of mouse moves, and Windows paints only once the queue is empty, so a
    /// steady drag would not redraw until the pointer rested. So every ~30 ms the moved-to frame is
    /// painted straight away, and the moves in between only record where the drag has got to.
    /// </summary>
    private void SeekWhileDragging(int frame)
    {
        Pause();
        SetMatchFrame(frame);
        if (Environment.TickCount64 - _lastDragPaint < 30) return;
        _scrubber.Update();
        _winBar.Update();
        _clock.Update();
        _mapView.Update();
        _teamStatus.Update();
        _lastDragPaint = Environment.TickCount64;
    }

    private void TogglePlay()
    {
        if (_playTimer.Enabled) { Pause(); return; }
        if (_match is null) return;
        if (_matchFrame >= _match.LastFrame) SetMatchFrame(0);
        _lastTick = Environment.TickCount64;
        _frameCarry = 0;
        _playTimer.Start();
        _playButton.Text = "Pause";
    }

    private void Pause()
    {
        _playTimer.Stop();
        _playButton.Text = "Play";
    }

    private void StepSeconds(int seconds)
    {
        if (_doc is null) return;
        SetMatchFrame(_matchFrame + seconds * _doc.GameSpeed.FpsAt(Math.Max(0, _matchFrame)));
    }

    /// <summary>Game time advances at the chosen multiple of real time, at whatever speed the game ran.</summary>
    private void PlaybackTick()
    {
        if (_match is null || _doc is null) { Pause(); return; }
        long now = Environment.TickCount64;
        double elapsed = Math.Min(0.25, (now - _lastTick) / 1000.0);
        _lastTick = now;
        double speed = Speeds[Math.Max(0, _speedBox.SelectedIndex)];
        _frameCarry += elapsed * speed * _doc.GameSpeed.FpsAt(Math.Max(0, _matchFrame));
        int step = (int)_frameCarry;
        if (step <= 0) return;
        _frameCarry -= step;
        SetMatchFrame(_matchFrame + step);
        if (_matchFrame >= _match.LastFrame) Pause();
    }
}
