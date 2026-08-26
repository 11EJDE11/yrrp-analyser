using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

/// <summary>One line in the event log. Events and side-channel records share the timeline.</summary>
internal readonly struct LogRow
{
    public int Frame { get; init; }
    public int HouseIndex { get; init; }
    public string Player { get; init; }
    public string Category { get; init; }
    public string Name { get; init; }
    public string Detail { get; init; }
    public bool IsTiming { get; init; }
    public bool IsSideChannel { get; init; }
    public uint ScheduledFrame { get; init; }

    /// <summary>Lower-cased once at build time; the search box hits this on every keystroke.</summary>
    public string SearchKey { get; init; }
}

internal sealed partial class MainForm
{
    private List<LogRow> _allRows = [];
    private List<LogRow> _visibleRows = [];

    /// <summary>-1 means every house; -2 means only the rows that carry no house.</summary>
    private int _selectedHouse = -1;

    private SplitContainer? _eventsSplit;

    private Control BuildEventsTab()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1MinSize = 180,
            BackColor = Theme.Background,
        };
        // SplitterDistance is clamped against the control's current width, which is still the
        // default until the form lays out, so it has to be applied after the handle exists.
        _eventsSplit = split;
        split.HandleCreated += (_, _) => TrySetSplitterDistance(split, 250);
        split.Panel1.Padding = new Padding(12, 12, 6, 12);
        split.Panel2.Padding = new Padding(6, 12, 12, 12);

        var leftHeader = new Label
        {
            Text = "Show events from",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.Muted,
        };
        _playerFilter.DrawItem += DrawPlayerFilterItem;
        _playerFilter.SelectedIndexChanged += (_, _) =>
        {
            _selectedHouse = _playerFilter.SelectedIndex switch
            {
                <= 0 => -1,
                _ when _playerFilter.SelectedItem is PlayerFilterEntry entry => entry.HouseIndex,
                _ => -1,
            };
            RefreshEventRows();
        };
        split.Panel1.Controls.Add(_playerFilter);
        split.Panel1.Controls.Add(leftHeader);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Theme.Background,
            Margin = new Padding(0),
        };

        _showTiming.Checked = _settings.ShowTimingEvents;
        _showChat.Checked = _settings.ShowChatAndBeacons;
        _showTiming.CheckedChanged += (_, _) =>
        {
            _settings.ShowTimingEvents = _showTiming.Checked;
            RefreshEventRows();
        };
        _showChat.CheckedChanged += (_, _) =>
        {
            _settings.ShowChatAndBeacons = _showChat.Checked;
            RefreshEventRows();
        };
        _categoryFilter.SelectedIndexChanged += (_, _) => RefreshEventRows();
        _search.TextChanged += (_, _) => RefreshEventRows();

        toolbar.Controls.Add(_showTiming);
        toolbar.Controls.Add(_showChat);
        toolbar.Controls.Add(new Label { Text = "Category", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 6, 6, 0) });
        toolbar.Controls.Add(_categoryFilter);
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(_eventCountLabel);

        _eventList.Columns.Add("Frame", 78, HorizontalAlignment.Right);
        _eventList.Columns.Add("Time", 62, HorizontalAlignment.Right);
        _eventList.Columns.Add("Player", 150);
        _eventList.Columns.Add("Category", 92);
        _eventList.Columns.Add("Event", 130);
        _eventList.Columns.Add("Detail", 620);
        _eventList.RetrieveVirtualItem += RetrieveEventItem;
        _eventList.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelectedRows(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.A)
            {
                _eventList.VirtualListSize = _visibleRows.Count;
                e.SuppressKeyPress = true;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy selected rows", null, (_, _) => CopySelectedRows());
        _eventList.ContextMenuStrip = menu;

        split.Panel2.Controls.Add(_eventList);
        split.Panel2.Controls.Add(toolbar);
        return split;
    }

    private static void TrySetSplitterDistance(SplitContainer split, int distance)
    {
        try
        {
            if (split.Width > distance + split.Panel2MinSize) split.SplitterDistance = distance;
        }
        catch (InvalidOperationException)
        {
            // The panel is briefly too narrow to honour the request; the default is fine.
        }
    }

    private sealed record PlayerFilterEntry(int HouseIndex, string Label, int Count)
    {
        public override string ToString() => Label;
    }

    private void DrawPlayerFilterItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _playerFilter.Items.Count) return;

        var entry = _playerFilter.Items[e.Index] as PlayerFilterEntry;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var text = entry?.Label ?? _playerFilter.Items[e.Index].ToString() ?? "";
        var color = entry is null || entry.HouseIndex < 0 ? Theme.Text : Theme.ForHouse(entry.HouseIndex);

        using var brush = new SolidBrush(selected ? SystemColors.HighlightText : color);
        if (entry is { HouseIndex: >= 0 })
        {
            using var swatch = new SolidBrush(Theme.ForHouse(entry.HouseIndex));
            e.Graphics.FillRectangle(swatch, e.Bounds.Left + 6, e.Bounds.Top + 7, 8, 8);
        }

        float textLeft = e.Bounds.Left + (entry is { HouseIndex: >= 0 } ? 20 : 6);
        float countWidth = entry is null
            ? 0
            : e.Graphics.MeasureString(entry.Count.ToString("N0"), Theme.MonoSmall).Width + 14;
        var textBounds = new RectangleF(textLeft, e.Bounds.Top + 3,
            Math.Max(10, e.Bounds.Right - textLeft - countWidth), e.Bounds.Height);
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        e.Graphics.DrawString(text, selected ? Theme.UiBold : Theme.Ui, brush, textBounds, format);

        if (entry is not null)
        {
            using var countBrush = new SolidBrush(selected ? SystemColors.HighlightText : Theme.Muted);
            var count = entry.Count.ToString("N0");
            var size = e.Graphics.MeasureString(count, Theme.MonoSmall);
            e.Graphics.DrawString(count, Theme.MonoSmall, countBrush,
                e.Bounds.Right - size.Width - 8, e.Bounds.Top + 4);
        }

        e.DrawFocusRectangle();
    }

    private void PopulateEvents(ReplayDocument doc)
    {
        _allRows = BuildRows(doc);

        var categories = _allRows.Select(r => r.Category).Distinct().OrderBy(c => c).ToList();
        _categoryFilter.Items.Clear();
        _categoryFilter.Items.Add("All categories");
        foreach (var c in categories) _categoryFilter.Items.Add(c);
        _categoryFilter.SelectedIndex = 0;

        _playerFilter.Items.Clear();
        _playerFilter.Items.Add(new PlayerFilterEntry(-1, "All players", _allRows.Count));
        foreach (var player in doc.Roster.Players.OrderBy(p => p.HouseIndex))
        {
            int count = _allRows.Count(r => r.HouseIndex == player.HouseIndex);
            _playerFilter.Items.Add(new PlayerFilterEntry(player.HouseIndex,
                $"{player.DisplayName}{(player.IsRecordingPlayer ? "  ·  recorder" : "")}", count));
        }

        int unattributed = _allRows.Count(r => r.HouseIndex < 0);
        if (unattributed > 0)
            _playerFilter.Items.Add(new PlayerFilterEntry(-2, "No house (session events)", unattributed));

        _playerFilter.SelectedIndex = 0;
        _selectedHouse = -1;
        RefreshEventRows();
    }

    private List<LogRow> BuildRows(ReplayDocument doc)
    {
        var rows = new List<LogRow>(doc.EventCount + 64);

        foreach (var frame in doc.Frames)
        {
            // Side-channel records come first on a frame: chat and a beacon are what the player
            // saw before the frame's orders landed.
            if (frame.SideChannel is not null)
            {
                foreach (var e in frame.SideChannel)
                {
                    string detail = e.Type switch
                    {
                        SideChannelEventType.ChatMessage => e.Text,
                        SideChannelEventType.BeaconText => e.Text,
                        SideChannelEventType.BeaconPlace =>
                            $"cell ({e.Coord.X / 256},{e.Coord.Y / 256}), slot {e.Aux}",
                        SideChannelEventType.BeaconDelete => $"slot {e.Aux}",
                        SideChannelEventType.Taunt => $"command {e.Aux}",
                        _ => e.Text,
                    };

                    string player = e.SenderName.Length > 0
                        ? e.SenderName
                        : doc.Roster.HouseLabel(e.House);

                    rows.Add(new LogRow
                    {
                        Frame = frame.FrameNumber,
                        HouseIndex = e.House,
                        Player = player,
                        Category = "Chat",
                        Name = e.TypeName,
                        Detail = detail,
                        IsSideChannel = true,
                        ScheduledFrame = (uint)frame.FrameNumber,
                        SearchKey = $"{player} {e.TypeName} {detail}".ToLowerInvariant(),
                    });
                }
            }

            for (int i = 0; i < frame.EventCount; i++)
            {
                var e = doc.GetEvent(frame.EventStart + i, frame.FrameNumber);
                string name = EventTypes.Name(e.Type);
                string detail = _describer.Describe(e);
                string player = doc.Roster.HouseLabel(e.HouseIndex);

                rows.Add(new LogRow
                {
                    Frame = frame.FrameNumber,
                    HouseIndex = e.HouseIndex,
                    Player = e.HouseIndex >= 0 ? player : "—",
                    Category = EventDescriber.Category(e.Type),
                    Name = name,
                    Detail = detail,
                    IsTiming = EventTypes.IsTiming(e.Type),
                    ScheduledFrame = e.ScheduledFrame,
                    SearchKey = $"{player} {name} {detail}".ToLowerInvariant(),
                });
            }
        }

        return rows;
    }

    private void RefreshEventRows()
    {
        string search = _search.Text.Trim().ToLowerInvariant();
        string? category = _categoryFilter.SelectedIndex > 0
            ? _categoryFilter.SelectedItem?.ToString()
            : null;

        bool showTiming = _showTiming.Checked;
        bool showChat = _showChat.Checked;

        _visibleRows = new List<LogRow>(_allRows.Count);
        foreach (var row in _allRows)
        {
            if (row.IsTiming && !showTiming) continue;
            if (row.IsSideChannel && !showChat) continue;

            if (_selectedHouse == -2)
            {
                if (row.HouseIndex >= 0) continue;
            }
            else if (_selectedHouse >= 0 && row.HouseIndex != _selectedHouse)
            {
                continue;
            }

            if (category is not null && row.Category != category) continue;
            if (search.Length > 0 && !row.SearchKey.Contains(search, StringComparison.Ordinal)) continue;

            _visibleRows.Add(row);
        }

        _eventList.VirtualListSize = _visibleRows.Count;
        _eventList.Invalidate();

        int hiddenTiming = showTiming ? 0 : _allRows.Count(r => r.IsTiming);
        _eventCountLabel.Text = $"{_visibleRows.Count:N0} of {_allRows.Count:N0} rows" +
                                (hiddenTiming > 0 ? $"   ({hiddenTiming:N0} timing rows hidden)" : "");
    }

    private void RetrieveEventItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visibleRows.Count)
        {
            e.Item = new ListViewItem("");
            return;
        }

        var row = _visibleRows[e.ItemIndex];
        string time = _doc?.TimeLabel(row.Frame) ?? "";

        var item = new ListViewItem([
            row.Frame.ToString("N0"),
            time,
            row.Player,
            row.Category,
            row.Name,
            row.Detail,
        ]);

        if (row.IsSideChannel)
        {
            item.Font = Theme.UiBold;
            item.ForeColor = Theme.ForHouse(row.HouseIndex);
        }
        else if (row.IsTiming)
        {
            item.ForeColor = Theme.Muted;
        }
        else
        {
            item.ForeColor = Theme.ForHouse(row.HouseIndex);
        }

        e.Item = item;
    }

    private void CopySelectedRows()
    {
        var indices = _eventList.SelectedIndices.Cast<int>().ToList();
        if (indices.Count == 0) return;

        var text = string.Join(Environment.NewLine, indices
            .Where(i => i >= 0 && i < _visibleRows.Count)
            .Select(i =>
            {
                var row = _visibleRows[i];
                return string.Join('\t', row.Frame, _doc?.TimeLabel(row.Frame) ?? "",
                    row.Player, row.Category, row.Name, row.Detail);
            }));

        if (text.Length > 0) Clipboard.SetText(text);
    }
}
