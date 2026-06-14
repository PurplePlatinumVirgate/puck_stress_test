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

> **Length-bias caveat:** the 20 Hz sampler lands on a frame with
> probability proportional to its duration, so `frame_ms` tail
> percentiles computed from these rows are time-weighted (inflated).
> Use `*_frames.csv` below for exact per-frame percentiles; the metrics
> rows remain the source for heap/GC/connected/phase.

### `*_frames.csv` — exact per-frame histogram (always on, 1 s windows)

One row per (window, game phase with frames). Every frame is counted —
no sampling, no length bias.

| column | meaning |
|---|---|
| `t_ms` | wall-clock ms at flush |
| `window_ms` | nominal window length (default 1000) |
| `game_phase` | phase the frames were attributed to (`?` = pre-init) |
| `connected_min`, `connected_max` | client-count range during the window |
| `count`, `sum_us`, `max_us` | frame count / total time / worst frame |
| `u500`, `u530`, ... `uinf` | log-binned counts; column name = upper bin edge in µs (12 bins/octave from 0.5 ms; `u500` = underflow, `uinf` = overflow ≥ 2048 ms) |

Frame durations come from `Stopwatch.GetTimestamp()` deltas, not
`Time.deltaTime`, so `max_us` is not clamped by `Time.maximumDeltaTime`
and can exceed the metrics `frame_ms` on big hitches. Bin edges are
self-describing via the header — parsers should read them from there.

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
