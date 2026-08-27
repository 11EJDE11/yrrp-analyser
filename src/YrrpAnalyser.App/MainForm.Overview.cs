using System.Drawing;
using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal sealed partial class MainForm
{
    private void PopulateOverview(ReplayDocument doc, NetworkAnalysis network, ActivityAnalysis activity)
    {
        _overviewFlow.SuspendLayout();
        _overviewFlow.Controls.Clear();

        foreach (var control in BuildOverviewControls(doc, network, activity))
            _overviewFlow.Controls.Add(control);

        FitWidths(_overviewFlow);
        _overviewFlow.ResumeLayout();
    }

    private IEnumerable<Control> BuildOverviewControls(ReplayDocument doc, NetworkAnalysis network,
        ActivityAnalysis activity)
    {
        var ini = doc.SpawnIni;

        yield return new Label
        {
            Text = doc.Header.MapName.Length > 0 ? doc.Header.MapName : "(map name not recorded)",
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        };

        yield return new Label
        {
            Text = $"{doc.Header.RecordedAt.LocalDateTime:dddd d MMMM yyyy, HH:mm}  ·  " +
                   $"{doc.Header.GameModeName}  ·  {ReplayDocument.FormatTime(doc.Duration)}  ·  " +
                   $"{doc.Roster.Players.Count} players",
            ForeColor = Theme.Muted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };

        if (!doc.Header.CleanShutdown || doc.Truncated || doc.Warnings.Count > 0)
            yield return BuildWarningBanner(doc);

        yield return SectionHeading("Recording");
        yield return FactGrid(
        [
            ("Frames", $"{doc.EffectiveFrameCount:N0}" +
                       (doc.Header.TotalFrames == 0 ? "  (header not stamped; taken from the stream)" : "")),
            ("Duration", ReplayDocument.FormatTime(doc.Duration)),
            ("Game speed", doc.GameSpeed.SpeedChanged
                ? $"index {doc.Header.RecordedGameSpeed} — {doc.Header.SimulationFps} FPS, " +
                  $"then {doc.GameSpeed.Changes.Count()} change(s)"
                : $"index {doc.Header.RecordedGameSpeed} — {doc.Header.SimulationFps} FPS"),
            ("Seed", doc.Header.Seed.ToString()),
            ("Unique ID counter", doc.Header.UniqueIDCounter.ToString()),
            ("Random next", $"{doc.Header.RandomNext1} / {doc.Header.RandomNext2}"),

            ("Spawner version", doc.Header.SpawnerVersion),
            ("Game client", doc.Header.GameClientVersion),
            ("Replay format", $"version {doc.Header.Version}, header {doc.Header.HeaderSize} bytes"),
            ("Shutdown", doc.Header.CleanShutdown ? "clean" : "cut short — the game did not close the file"),
            ("File", $"{doc.FileSize:N0} bytes"),
            ("Frame stream", $"{doc.CompressedStreamBytes:N0} → {doc.InflatedStreamBytes:N0} bytes " +
                             $"({doc.CompressionRatio:0.0}x)"),
        ]);

        yield return SectionHeading("Lobby");
        yield return FactGrid(
        [
            ("Map ID", ini.GetString("Settings", "MapID", "—")),
            ("Map SHA1", ini.GetString("Settings", "MapSHA1", "—")),
            ("Game mode", ini.GetString("Settings", "UIGameMode", "—")),
            ("Game ID", ini.GetString("Settings", "GameID", "—")),
            ("Protocol", $"{network.Protocol}  (FrameSendRate {network.ConfiguredFrameSendRate})"),
            ("Tunnel", $"{ini.GetString("Tunnel", "Ip", "—")}:{ini.GetString("Tunnel", "Port", "—")}" +
                       "   (address blanked by the recorder)"),

            ("Starting credits", ini.GetString("Settings", "Credits", "—")),
            ("Unit count", ini.GetString("Settings", "UnitCount", "—")),
            ("Short game", ini.GetString("Settings", "ShortGame", "—")),
            ("Superweapons", ini.GetString("Settings", "Superweapons", "—")),
            ("Crates", ini.GetString("Settings", "Crates", "—")),
            ("Bases / Fog", $"{ini.GetString("Settings", "Bases", "—")} / " +
                            $"{ini.GetString("Settings", "FogOfWar", "—")}"),
        ]);

        yield return SectionHeading("Players");
        yield return BuildRosterGrid(doc, network, activity);

        yield return new Label
        {
            Text = "House index is the index every recorded event carries. It is not the spawn.ini " +
                   "slot: the engine builds HouseClass::Array in player-colour order, so this column " +
                   "is what maps an event back to a name.",
            ForeColor = Theme.Muted,
            AutoSize = false,
            Width = 900,
            Height = 34,
            Margin = new Padding(0, 4, 0, 8),
        };

        var chat = doc.EnumerateSideChannel().ToList();
        yield return SectionHeading("What is in the stream");
        yield return FactGrid(
        [
            ("Frame records", $"{doc.Frames.Count:N0}"),
            ("Events", $"{doc.EventCount:N0}"),
            ("Gameplay events", $"{activity.EventTotals.Where(kv => !EventTypes.IsTiming(kv.Key)).Sum(kv => kv.Value):N0}"),
            ("Network events", $"{activity.EventTotals.Where(kv => EventTypes.IsTiming(kv.Key)).Sum(kv => kv.Value):N0}"),
            ("Chat and beacons", $"{chat.Count:N0}"),
            ("Frames with a state hash", $"{doc.Frames.Count(f => f.GameCrc.HasValue):N0}"),
            ("Camera moves recorded", $"{doc.Frames.Count(f => f.TacticalPos.HasValue):N0}"),
            ("Selection changes", $"{doc.Frames.Count(f => f.SelectionIds is not null):N0}"),
            ("Object censuses", doc.CensusFrameCount > 0
                ? $"{doc.CensusFrameCount:N0}"
                : "none (recorded before the census was added)"),
            ("Embedded map", doc.HasEmbeddedMap
                ? $"spawnmap.ini, {doc.Header.SpawnMapSize:N0} bytes"
                : "not embedded — the scenario lives in the game's own mixes"),
            ("Extension blocks", doc.HasExtensionBlocks ? "present" : "none (nothing writes one yet)"),
            ("Type names", _types.HasData
                ? $"resolved from {_types.SourceDescription}"
                : "not resolved — set a rules INI under Tools"),
        ]);

        if (chat.Count > 0)
        {
            yield return SectionHeading("Chat and beacons");
            yield return BuildChatList(doc, chat);
            yield return new Label
            {
                Text = "A recording holds every beacon in the game, but only the chat the recording " +
                       "player sent or was addressed to receive — the sender picks who a message goes " +
                       "to, so a message between two other players never reached this machine.",
                ForeColor = Theme.Muted,
                AutoSize = false,
                Width = 900,
                Height = 34,
                Margin = new Padding(0, 4, 0, 8),
            };
        }
    }

    private static Control BuildWarningBanner(ReplayDocument doc)
    {
        var lines = new List<string>();
        if (!doc.Header.CleanShutdown)
            lines.Add("The header was never stamped with a frame count, so the game did not shut this " +
                      "recording down — it crashed, was killed, or is still being written. Everything up " +
                      "to the last sync flush is still readable.");
        lines.AddRange(doc.Warnings);

        var panel = new Panel
        {
            BackColor = Color.FromArgb(0xFF, 0xF6, 0xE5),
            Padding = new Padding(12, 10, 12, 10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = 940,
            Margin = new Padding(0, 4, 0, 10),
        };
        panel.Controls.Add(new Label
        {
            Text = string.Join("\n\n", lines),
            ForeColor = Theme.Warning,
            AutoSize = false,
            Width = 900,
            Height = 20 + lines.Count * 34,
        });
        return panel;
    }

    private static Control BuildRosterGrid(ReplayDocument doc, NetworkAnalysis network,
        ActivityAnalysis activity)
    {
        var view = MakeListView(
            ("House", 55), ("Slot", 45), ("Player", 170), ("Country", 110), ("Colour", 55),
            ("Start", 50), ("Kind", 140), ("Orders", 70), ("Commands", 80), ("APM", 60),
            ("Round trip", 90), ("Process", 80), ("Left at", 80));
        view.Dock = DockStyle.None;
        view.Width = 1060;
        view.Height = 28 + Math.Max(1, doc.Roster.Players.Count) * 20;
        view.Margin = new Padding(0, 0, 0, 4);
        FillWidth(view);

        foreach (var player in doc.Roster.Players.OrderBy(p => p.HouseIndex))
        {
            var net = network.Series.FirstOrDefault(s => s.HouseIndex == player.HouseIndex);
            var act = activity.Players.FirstOrDefault(a => a.HouseIndex == player.HouseIndex);

            var kind = player.IsHuman
                ? player.IsSpectator ? "spectator" : "human"
                : $"AI {player.DifficultyName}".TrimEnd();
            if (player.IsRecordingPlayer) kind += " (recorder)";

            var item = new ListViewItem(
            [
                player.HouseIndex.ToString(),
                player.Slot.ToString(),
                player.DisplayName,
                player.SideName.Length > 0 ? $"{player.SideName} ({player.Side})" : player.Side.ToString(),
                player.Color.ToString(),
                player.SpawnLocation >= 0 ? player.SpawnLocation.ToString() : "auto",
                kind,
                act is not null ? $"{act.TotalOrders:N0}" : "—",
                act is not null ? $"{act.TotalCommands:N0}" : "—",
                act is not null ? $"{act.AverageApm:0.0}" : "—",
                net is { RoundTripMs.Count: > 0 } ? $"{net.MedianRoundTripMs:0} ms" : "—",
                net is { ProcessMs.Count: > 0 } ? $"{net.MedianProcessMs:0.0} ms" : "—",
                act is { LastActionFrame: >= 0 } ? doc.TimeLabel(act.LastActionFrame) : "—",
            ])
            {
                ForeColor = Theme.ForHouse(player.HouseIndex),
                Font = player.IsRecordingPlayer ? Theme.UiBold : Theme.Ui,
            };
            view.Items.Add(item);
        }

        return view;
    }

    private static Control BuildChatList(ReplayDocument doc, List<SideChannelEvent> chat)
    {
        var view = MakeListView(("Time", 70), ("Frame", 70), ("Kind", 110), ("From", 150), ("Text", 520));
        view.Dock = DockStyle.None;
        view.Width = 1060;
        view.Height = 28 + Math.Min(14, Math.Max(1, chat.Count)) * 20;
        view.Margin = new Padding(0, 0, 0, 4);
        FillWidth(view);

        foreach (var e in chat)
        {
            string from = e.SenderName.Length > 0 ? e.SenderName : doc.Roster.HouseLabel(e.House);
            string text = e.Type switch
            {
                SideChannelEventType.BeaconPlace =>
                    $"placed at ({e.Coord.X / 256},{e.Coord.Y / 256}), slot {e.Aux}",
                SideChannelEventType.BeaconDelete => $"removed, slot {e.Aux}",
                SideChannelEventType.Taunt => $"taunt {e.Aux}",
                _ => e.Text,
            };

            view.Items.Add(new ListViewItem([
                doc.TimeLabel(e.FrameNumber),
                e.FrameNumber.ToString("N0"),
                e.TypeName,
                from,
                text,
            ])
            { ForeColor = Theme.ForHouse(e.House) });
        }

        return view;
    }
}
