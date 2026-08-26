# yrrp Analyser

Reads `.yrrp` replays — the recording format written by the CnCNet [yrpp-spawner](https://github.com/CnCNet/yrpp-spawner)
for Red Alert 2: Yuri's Revenge. It does not play them; it opens one and shows what is actually
inside: who played, what they did, how their connections behaved, and — given both peers'
recordings of one match — the exact frame a desync started on.

```
dotnet build
dotnet run --project src/YrrpAnalyser.App -- "path\to\replay.yrrp"
```

.NET 8, no external dependencies. `src/YrrpAnalyser.Core` is the parser and analysis on its own;
`src/YrrpAnalyser.Cli` is a headless `yrrp` command for batch work (`--scan` a folder, `--compare`
two recordings, `--export` every CSV/JSON).

## Overview

Header, lobby and roster. House index is what every recorded event carries, and it is not the
spawn.ini slot — the engine orders `HouseClass::Array` by player colour, which the analyser
reproduces.

![Overview](docs/overview.png)

## Events

Every event, chat message and beacon on one timeline, with payloads decoded. Pick a player on the
left; timing and network events are hidden by default.

![Events](docs/events.png)

## Network

Per-player round trip, latency level, MaxAhead, process time and order gap, sharing one time axis —
pan any chart and the rest follow, scroll to zoom. Stalls are marked in red.

Two caveats, both in the format rather than the tool: the recording player has no MaxAhead line,
because its own `FRAMEINFO` goes straight into the outgoing packet and never reaches the event
queue; and dropped packets and retransmissions are counted below the event queue and are not in a
replay at all. Order gap is the closest proxy the file holds.

![Network](docs/network.png)

## Activity

Commands per minute, event density, the recording player's camera and selection, build order, and
an event breakdown by house.

![Activity](docs/activity.png)

## spawn.ini and spawnmap.ini

Both embedded files, searchable and exportable. IPs are already blanked by the recorder.

![spawnmap.ini](docs/spawnmap-ini.png)

## Diagnostics

Parse results, record-flag histogram, frame-sequence checks, every header field, and the derived
house-index map.

![Diagnostics](docs/diagnostics.png)

## Keeping up with the format

`src/YrrpAnalyser.Core/ReplayFormat.cs` mirrors the spawner's `ReplayFormat.h` by hand, the same
way the CnCNet client's `ReplayGame.cs` does. There is no compile-time link, so a header change has
to be made here too — a size or offset drift is silent, not an error. The spawner's
`docs/replay-format.md` is the reference.
