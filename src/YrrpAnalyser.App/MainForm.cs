using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();

    private ReplayDocument? _doc;
    private EventDescriber _describer = new(TypeNameResolver.Empty);
    private TypeNameResolver _types = TypeNameResolver.Empty;
    private NetworkAnalysis? _network;
    private ActivityAnalysis? _activity;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusRight = new();
    private ToolStripMenuItem _recentMenu = new("Open &recent");

    public MainForm()
    {
        Text = "yrrp Analyser";
        MinimumSize = new Size(1000, 640);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        Font = Theme.Ui;
        AllowDrop = true;

        BuildMenu();
        BuildTabs();

        var strip = new StatusStrip { BackColor = Theme.Panel };
        strip.Items.AddRange([_status, _statusRight]);
        Controls.Add(_tabs);
        Controls.Add(strip);
        _tabs.BringToFront();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                LoadReplay(files[0]);
        };

        FormClosing += (_, _) => _settings.Save();

        SetStatus("Open a .yrrp recording, or drop one onto the window.");
        ShowEmptyState();
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip { BackColor = Theme.Panel };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Open replay...", null, (_, _) => PromptOpen())
        { ShortcutKeys = Keys.Control | Keys.O });
        _recentMenu = new ToolStripMenuItem("Open &recent");
        file.DropDownItems.Add(_recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());

        var export = new ToolStripMenuItem("&Export");
        export.DropDownItems.Add(new ToolStripMenuItem("Everything to a folder...", null, (_, _) => ExportAll()));
        export.DropDownItems.Add(new ToolStripSeparator());
        export.DropDownItems.Add(new ToolStripMenuItem("Events as CSV...", null, (_, _) => ExportEventsCsv()));
        export.DropDownItems.Add(new ToolStripMenuItem("Chat and beacons as CSV...", null, (_, _) => ExportChatCsv()));
        export.DropDownItems.Add(new ToolStripMenuItem("Network samples as CSV...", null, (_, _) => ExportNetworkCsv()));
        export.DropDownItems.Add(new ToolStripMenuItem("Per-frame CRCs as CSV...", null, (_, _) => ExportCrcCsv()));
        export.DropDownItems.Add(new ToolStripMenuItem("Summary as JSON...", null, (_, _) => ExportSummaryJson()));
        export.DropDownItems.Add(new ToolStripSeparator());
        export.DropDownItems.Add(new ToolStripMenuItem("Embedded spawn.ini...", null, (_, _) => ExportIni(false)));
        export.DropDownItems.Add(new ToolStripMenuItem("Embedded spawnmap.ini...", null, (_, _) => ExportIni(true)));
        file.DropDownItems.Add(export);

        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));
        menu.Items.Add(file);

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add(new ToolStripMenuItem(
            "&Compare with another recording (desync diff)...", null, (_, _) => PromptCompare()));
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(new ToolStripMenuItem(
            "Set &rules INIs for type names...", null, (_, _) => PromptRulesIni()));
        tools.DropDownItems.Add(new ToolStripMenuItem(
            "&Forget rules INIs", null, (_, _) =>
            {
                _settings.RulesIniPaths.Clear();
                _settings.Save();
                ReloadTypeNames();
            }));
        menu.Items.Add(tools);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("What is in a replay, and what is not", null,
            (_, _) => MessageBox.Show(this, NetworkAnalysis.ProvenanceNote, "Where these numbers come from",
                MessageBoxButtons.OK, MessageBoxIcon.Information)));
        menu.Items.Add(help);

        MainMenuStrip = menu;
        Controls.Add(menu);
        RefreshRecentMenu();
    }

    private void RefreshRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        if (_settings.RecentFiles.Count == 0)
        {
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem("(nothing yet)") { Enabled = false });
            return;
        }
        foreach (var path in _settings.RecentFiles.ToList())
        {
            string label = Path.GetFileName(path);
            _recentMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => LoadReplay(path))
            { ToolTipText = path });
        }
    }

    private void PromptOpen()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Red Alert 2 replays (*.yrrp)|*.yrrp|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.LastFolder) ? _settings.LastFolder : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadReplay(dialog.FileName);
    }

    private void PromptRulesIni()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Pick the rules INIs, in the order the game reads them",
            Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.RulesIniPaths = [.. dialog.FileNames];
        _settings.Save();
        ReloadTypeNames();
    }

    private void ReloadTypeNames()
    {
        _types = TypeNameResolver.Load(_settings.RulesIniPaths, _doc?.SpawnMapIni);
        _describer = new EventDescriber(_types);
        if (_doc is not null) Analyse(_doc);
    }

    /// <summary>Opens a file named on the command line, once the window exists.</summary>
    public void OpenOnStartup(string path) => LoadReplay(path);

    private void LoadReplay(string path)
    {
        UseWaitCursor = true;
        SetStatus($"Reading {Path.GetFileName(path)}...");
        Application.DoEvents();

        try
        {
            var doc = ReplayReader.Load(path);
            _doc = doc;
            _settings.AddRecent(path);
            _settings.Save();
            RefreshRecentMenu();

            _types = TypeNameResolver.Load(_settings.RulesIniPaths, doc.SpawnMapIni);
            _describer = new EventDescriber(_types);
            Analyse(doc);

            Text = $"{doc.FileName} - yrrp Analyser";
            SetStatus($"{doc.Frames.Count:N0} frame records, {doc.EventCount:N0} events, " +
                      $"{doc.EnumerateSideChannel().Count():N0} chat/beacon records.");
            _statusRight.Text = doc.Header.CleanShutdown
                ? "Recording closed cleanly"
                : "Recording was cut short";
            _statusRight.ForeColor = doc.Header.CleanShutdown ? Theme.Good : Theme.Warning;
        }
        catch (ReplayLoadException ex)
        {
            MessageBox.Show(this, ex.Message, "This file cannot be read",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "Unexpected error while reading the replay",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Failed to read the replay.");
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Analyse(ReplayDocument doc)
    {
        _network = NetworkAnalysis.Build(doc);
        _activity = ActivityAnalysis.Build(doc, _describer);

        PopulateOverview(doc, _network, _activity);
        PopulateEvents(doc);
        PopulateNetwork(doc, _network);
        PopulateActivity(doc, _activity);
        PopulateIniTabs(doc);
        PopulateDiagnostics(doc);
    }

    private void SetStatus(string text) => _status.Text = text;

    private static Label SectionHeading(string text) => new()
    {
        Text = text,
        Font = Theme.Heading,
        ForeColor = Theme.Text,
        AutoSize = true,
        Margin = new Padding(0, 12, 0, 6),
    };

    /// <summary>Two-column label/value block used all over the overview.</summary>
    private static TableLayoutPanel FactGrid((string Label, string Value)[] facts, int columns = 2)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = columns * 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
        };

        for (int i = 0; i < columns; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        int rows = (facts.Length + columns - 1) / columns;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = row + column * rows;
                if (index >= facts.Length) continue;
                var (label, value) = facts[index];

                grid.Controls.Add(new Label
                {
                    Text = label,
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Margin = new Padding(0, 3, 14, 3),
                }, column * 2, row);

                grid.Controls.Add(new Label
                {
                    Text = value,
                    ForeColor = Theme.Text,
                    Font = Theme.Mono,
                    AutoSize = true,
                    Margin = new Padding(0, 3, 34, 3),
                }, column * 2 + 1, row);
            }
        }

        return grid;
    }

    private static ListView MakeListView(params (string Header, int Width)[] columns)
    {
        var view = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            OwnerDraw = false,
        };
        foreach (var (header, width) in columns) view.Columns.Add(header, width);
        return view;
    }

    private static Panel Scrollable(Control content)
    {
        var host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
        content.Dock = DockStyle.Top;
        host.Controls.Add(content);
        return host;
    }

    /// <summary>Marks a control that should stretch to the width of its scrolling column.</summary>
    private const string FillWidthTag = "fill-width";

    private static T FillWidth<T>(T control) where T : Control
    {
        control.Tag = FillWidthTag;
        return control;
    }

    /// <summary>
    /// A FlowLayoutPanel does not stretch its children, and Dock.Top inside one is ignored, so the
    /// charts and the wide tables are resized by hand whenever the column changes width.
    /// </summary>
    private static void FitWidths(FlowLayoutPanel flow)
    {
        int available = flow.ClientSize.Width - flow.Padding.Horizontal;
        if (available < 400) return;

        foreach (Control control in flow.Controls)
        {
            if (control.Tag as string != FillWidthTag) continue;
            int width = available - control.Margin.Horizontal;
            if (control.Width != width) control.Width = width;
        }
    }
}
