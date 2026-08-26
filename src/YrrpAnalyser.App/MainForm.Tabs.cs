using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private readonly FlowLayoutPanel _overviewFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16, 12, 16, 24),
        BackColor = Theme.Background,
    };

    private readonly ListBox _playerFilter = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Panel,
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 22,
    };

    private readonly ListView _eventList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        VirtualMode = true,
        FullRowSelect = true,
        HideSelection = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Panel,
    };

    private readonly CheckBox _showTiming = new()
    {
        Text = "Timing / network events",
        AutoSize = true,
        Margin = new Padding(0, 4, 16, 4),
    };

    private readonly CheckBox _showChat = new()
    {
        Text = "Chat and beacons",
        AutoSize = true,
        Checked = true,
        Margin = new Padding(0, 4, 16, 4),
    };

    private readonly ComboBox _categoryFilter = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 150,
        Margin = new Padding(0, 2, 16, 2),
    };

    private readonly TextBox _search = new()
    {
        Width = 260,
        PlaceholderText = "Search text, type or detail...",
        Margin = new Padding(0, 2, 16, 2),
    };

    private readonly Label _eventCountLabel = new()
    {
        AutoSize = true,
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 6, 0, 4),
    };

    private readonly FlowLayoutPanel _networkFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16, 12, 16, 24),
        BackColor = Theme.Background,
    };

    private readonly FlowLayoutPanel _activityFlow = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(16, 12, 16, 24),
        BackColor = Theme.Background,
    };

    private readonly TextBox _spawnIniBox = MakeCodeBox();
    private readonly TextBox _spawnMapBox = MakeCodeBox();
    private readonly TextBox _diagnosticsBox = MakeCodeBox();

    private readonly TextBox _spawnMapSearch = new() { Width = 240, PlaceholderText = "Find in spawnmap.ini..." };
    private readonly TextBox _spawnIniSearch = new() { Width = 240, PlaceholderText = "Find in spawn.ini..." };

    private readonly ChartGroup _networkCharts = new();
    private readonly ChartGroup _activityCharts = new();

    private void BuildTabs()
    {
        _tabs.Padding = new Point(14, 6);

        foreach (var flow in new[] { _overviewFlow, _networkFlow, _activityFlow })
        {
            var captured = flow;
            captured.SizeChanged += (_, _) => FitWidths(captured);
        }

        _tabs.TabPages.Add(NewPage("Overview", Scrollable(_overviewFlow)));
        _tabs.TabPages.Add(NewPage("Events", BuildEventsTab()));
        _tabs.TabPages.Add(NewPage("Network", Scrollable(_networkFlow)));
        _tabs.TabPages.Add(NewPage("Activity", Scrollable(_activityFlow)));
        _tabs.TabPages.Add(NewPage("spawn.ini", BuildIniTab(_spawnIniBox, _spawnIniSearch,
            "The lobby exactly as the client wrote it, with every IP blanked to 0.0.0.0 by the " +
            "recorder. Player names, sides, colours, game options and the client's file hashes are verbatim.")));
        _tabs.TabPages.Add(NewPage("spawnmap.ini", BuildIniTab(_spawnMapBox, _spawnMapSearch,
            "The map the game actually loaded, embedded whole. This is the file the client has to " +
            "write back out before a replay will play.")));
        _tabs.TabPages.Add(NewPage("Diagnostics", BuildDiagnosticsTab()));
    }

    private static TabPage NewPage(string title, Control content) =>
        new(title) { BackColor = Theme.Background, Controls = { content }, Padding = new Padding(0) };

    private static TextBox MakeCodeBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Theme.Mono,
        BackColor = Theme.Panel,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.FixedSingle,
        MaxLength = 0,
    };

    private Control BuildIniTab(TextBox box, TextBox search, string blurb)
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(12) };

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Background,
        };

        var find = new Button { Text = "Find next", AutoSize = true, Margin = new Padding(6, 1, 6, 0) };
        var copy = new Button { Text = "Copy all", AutoSize = true, Margin = new Padding(0, 1, 0, 0) };

        void FindNext()
        {
            if (search.TextLength == 0) return;
            int from = box.SelectionStart + Math.Max(1, box.SelectionLength);
            int at = box.Text.IndexOf(search.Text, Math.Min(from, box.TextLength),
                StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = box.Text.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { SystemSounds_Beep(); return; }
            box.Select(at, search.TextLength);
            box.ScrollToCaret();
            box.Focus();
        }

        find.Click += (_, _) => FindNext();
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            FindNext();
        };
        copy.Click += (_, _) =>
        {
            if (box.TextLength > 0) Clipboard.SetText(box.Text);
        };

        header.Controls.Add(search);
        header.Controls.Add(find);
        header.Controls.Add(copy);

        var note = new Label
        {
            Text = blurb,
            Dock = DockStyle.Top,
            ForeColor = Theme.Muted,
            AutoSize = false,
            Height = 34,
            Padding = new Padding(0, 6, 0, 6),
        };

        root.Controls.Add(box);
        root.Controls.Add(note);
        root.Controls.Add(header);
        return root;
    }

    private static void SystemSounds_Beep() => System.Media.SystemSounds.Beep.Play();

    private Control BuildDiagnosticsTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(12) };
        root.Controls.Add(_diagnosticsBox);
        return root;
    }

    private void ShowEmptyState()
    {
        _overviewFlow.Controls.Clear();
        _overviewFlow.Controls.Add(new Label
        {
            Text = "No replay open.\n\nFile > Open, or drop a .yrrp file onto this window.",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 40, 0, 0),
        });
    }
}
