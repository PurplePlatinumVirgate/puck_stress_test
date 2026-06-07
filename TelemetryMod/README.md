# TelemetryMod

Server-side Puck mod that records per-tick performance metrics + per-event
network lifecycle to CSV files under `<server-cwd>/telemetry/`.

## Output

For each server run (each time the mod's `OnEnable` fires), three files
named `<utc-timestamp>_<kind>` are written.

### `*_metrics.csv` — per-tick samples (default 20 Hz / 50 ms)

| column | meaning |
|---|---|
| `t_ms` | wall-clock ms since the run started |
| `frame_ms` | `Time.deltaTime * 1000` — last server tick duration |
| `tick_idx` | monotonically increasing sample index |
| `connected` | `NetworkManager.Singleton.ConnectedClientsList.Count` |
| `game_phase` | `GameManager.Phase` (Warmup/FaceOff/Playing/...) |
| `gc_gen0`, `gc_gen1`, `gc_gen2` | cumulative GC.CollectionCount per gen |
| `total_alloc_b` | `GC.GetTotalMemory(false)` snapshot |

Rows are flushed every ~1 second of samples, so a crashed run still
preserves most data.

### `*_events.csv` — per-event lifecycle (Harmony hooks)

| column | meaning |
|---|---|
| `t_ms` | wall-clock ms |
| `event` | `approval` / `connected` / `disconnected` |
| `client_id` | NGO client network id |
| `detail` | event-specific (e.g. `approved=True;reason=...`) |

These rows fire at the moment of the event regardless of the metrics
sampling cadence — important for catching brief bot connections that
fall between two metrics samples.

### `*_summary.txt` — run header

Plain-text key/value lines:
- `start_utc` — ISO-8601 timestamp
- `host_name` — `Environment.MachineName`
- `sample_interval_s` — what the metrics sampler was set to
- `unity_version` — `Application.unityVersion`
- `target_frame_rate` — `Application.targetFrameRate`
- `is_batch_mode` — true for headless servers
- `end_utc`, `duration_ms`, `total_ticks` — only written if `OnDisable`
  runs (i.e. the server shuts down cleanly, not via a kill).

## Deploying to a server

```
<server-cwd>/Plugins/TelemetryMod/TelemetryMod.dll
```

The mod auto-enables in batch mode (Puck's `Event_Client_OnModAdded` calls
`mod.Enable(false)` when `Application.isBatchMode == true`).

## Overhead

Per sample (~50 ms): 9 column values, ~80 bytes appended to a buffered
StreamWriter. Negligible compared to a real server tick (sub-microsecond
on modern hardware).

Per event: one line, written and flushed immediately. Uncommon enough to
not matter.

## Reading the data

Both CSVs open in any spreadsheet / pandas. For comparing two runs the
key questions are:

- `frame_ms` distribution: P50/P95/P99/max under load.
- `gc_gen0` rate (deltas per second): GC pressure proxy.
- `total_alloc_b` slope: managed heap growth.
- `connected` over time: when bots actually held a connection.

## Source

Build from `puck_stress_test/TelemetryMod/`:
```
dotnet build -c Release
cp bin/Release/netstandard2.1/TelemetryMod.dll \
   <server>/Plugins/TelemetryMod/TelemetryMod.dll
```
