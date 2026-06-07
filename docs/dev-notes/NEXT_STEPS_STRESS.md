# Next steps — stress-test / mod impact measurement

**State as of 2026-04-29.** The harness's original mission per
`MISSION.md` is to produce **repeatable, comparable Puck server load**
so that the impact of a candidate server mod (CPU cost, GC pressure,
NV bandwidth, RTT degradation) can be measured A/B against a clean
baseline. With M0–M1 of the ML branch landed, the harness now also
emits per-bot (obs, action) snapshots — but the stress-test side has
been the secondary beneficiary of recent work, not the focus. This
document is what to do next on the stress side specifically.

If you're picking this up cold: read `MISSION.md` then `STATUS.md`.
Memory `feedback_server_minimal_patches.md` is the binding rule —
the server is the measurement target, so server-side patches stay
ConfigCaptureMod (read-only) + TelemetryMod (passive) only.

## What works today

- **12 spec-perfect protocol clients** in 12 forked processes (matches
  real-game architecture, no main-thread contention spike).
- **TelemetryMod** writes per-tick `metrics.csv`
  (frame_ms, connected, GC counters, heap, phase) and per-event
  `events.csv` (approval, disconnect) under `testserver/telemetry/`.
- **ConfigCaptureMod** dumps NGO NetworkConfig hash + prefab list +
  runtime NB layouts on demand. Used to validate the wire surface
  matches what the harness expects.
- **livestreamer** integration — runs visibly when launched from
  `testserver/` (HARD RULE: cwd must be `testserver/`; see
  `feedback_launch_server_from_testserver.md`).
- **/vw + /vs cycle test** — bots can ping-pong Play↔Warmup on demand
  for repro of phase-transition load.

## The measurement ask, restated

A user wants to know: "If I install mod X on my Puck server, what's
the cost?" To answer that, we need to:

1. Run the SAME deterministic bot workload against the server
   with **no mod loaded** (baseline) and **mod X loaded** (candidate).
2. Diff the per-tick telemetry between the two runs.
3. Report `Δfps`, `Δheap_alloc/min`, `Δconnected_RTT_p99`, `Δgc_gen0_per_min`,
   `Δserver_NV_bytes_out_per_sec`, etc.

We have (1) at single-run granularity. (2) and (3) need infrastructure
that doesn't exist yet — that's the next-steps work below.

## Highest-leverage next steps

### S1 — Deterministic playbook for baseline measurement (1 day)

The test harness already supports `--playbook`. Today's playbooks
focus on smoke tests; we need a "measurement run" playbook that:

- Locks `--seed` so bot decisions are reproducible.
- Runs for a fixed wall-clock window (e.g. 600 s).
- Cycles `/vs` → Play, runs N pre-warm seconds, then runs M seconds
  of measurement window with the bot brain in a deterministic mode
  (skip exploration / randomness).
- Emits a `BotBrain` "deterministic" flag that pins moveX/y to the
  same trajectory regardless of stick mirror jitter.

**Deliverable:** `playbooks/measure_12bots_600s.json` + a
`--measurement-mode` CLI flag that suppresses any non-deterministic
brain logic (Random.Range calls, sin oscillations with Time.realtime
inputs, etc.).

**Validation:** two runs with the same seed produce CSVs whose
`tick_idx`-aligned `frame_ms` rows differ by < 0.5 ms RMS.

### S2 — A/B diff tool (2 days)

A Python script (`tools/telemetry_diff.py`) that takes two
`telemetry/*_metrics.csv` files (baseline + candidate) and outputs
a comparison table.

**Deliverable:** a script that reports — per-phase, since
phase-transition load is its own thing —

| metric | baseline mean | candidate mean | Δ | Δ % | p99 baseline | p99 candidate |
|---|---|---|---|---|---|---|
| frame_ms | … | … | … | … | … | … |
| gc_gen0_per_min | … | … | … | … | … | … |
| total_alloc_b/sec | … | … | … | … | … | … |
| heap_resident_b | … | … | … | … | … | … |

Plus a "regression flag" — any metric whose Δ exceeds a threshold
(say 5% on means, 10% on p99) marked clearly.

**Validation:** run it on two baseline-vs-baseline runs (no mod
change between them); should report all Δ < 1%. Then change one
constant in TelemetryMod that affects allocations and verify
the diff catches the regression.

### S3 — Server-side bandwidth measurement (3 days)

Frame-time and GC are CPU-side. The other axis users care about is
**network bandwidth**: how many bytes/sec does the server push to
each client. Mods that add NetworkVariables increase this.

NGO doesn't expose per-client bytes-out directly, but UTP does
(`NetworkDriver.GetPipelineBuffers` per pipeline + per connection,
or hooking `INetworkUpdateSystem` for transport-layer counters).

**Deliverable:** `TelemetryMod` extension that, every K server
ticks, samples per-client bytes-sent / bytes-received and writes
to `telemetry/<utc>_bandwidth.csv` with columns
`t_ms, client_id, bytes_out, bytes_in, reliable_in_flight`.

Aggregate stats appear in S2's diff tool: `Δserver_bytes_out_per_sec`,
`Δreliable_queue_depth_p99`.

**Validation:** verify reported numbers add up to physical-NIC
counters within ±5% during a baseline run.

### S4 — RTT measurement that doesn't depend on the bot (2 days)

Today bots log `Player.Ping.Value` from MirrorPlayer NV (server-set
every 10s). For mod measurement we want **higher-frequency** RTT
samples and we want them server-side so the bot's own scheduling
jitter doesn't contaminate.

**Deliverable:** `TelemetryMod` patches `UnityTransport.GetCurrentRtt`
results into `telemetry/<utc>_rtt.csv` per server tick, columns
`t_ms, client_id, rtt_ms`. This is the same data Player.Ping NV
already has but at server-tick rate (~11 ms), not 10 s.

**Validation:** during a Play→Warmup cycle, RTT trace should show
the spawn-burst stall with ~ms granularity. With multi-process
forking landed (2026-04-29) the spike is gone, but the channel
still measures useful per-cycle variance.

### S5 — Mod-load sweep harness (3 days)

Once S1+S2 work, automate the A/B measurement so a user can drop a
mod DLL into a watched directory and the harness produces a report.

**Deliverable:** `tools/sweep_mods.py` that:
- Takes a list of mod DLLs.
- For each: copies into `testserver/Plugins/<modname>/`, restarts the
  Puck server, runs the playbook, captures telemetry, computes diff
  vs the cached baseline, generates a Markdown report.
- Runs all unattended; produces `reports/<utc>_<modname>.md`.

**Validation:** sweep against three known mods of varying cost
(read-only chat logger, NV-heavy stats tracker, unbounded allocator)
and verify the report ranks them in the expected order.

### S6 — Memory leak detection (2 days)

A common ask for mod authors: "does my mod leak?" Run for
30 minutes and watch heap trend.

**Deliverable:** `tools/leak_detect.py` consumes a long
`metrics.csv` and reports:
- `heap_resident_b` regression slope (bytes/min) over the run.
- Per-phase break (stable during Playing? leaks during Warmup?).
- GC generation 2 collection frequency increases over time?

**Validation:** instrument TelemetryMod to deliberately leak 100 KB
per tick, run 30 min, verify the tool reports a leak slope of
~360 KB/sec.

## Lower-priority, but useful

- **Per-mod-event tagging.** Mods often emit Debug.Log lines.
  TelemetryMod could harvest those + tag them with t_ms and cross-
  reference with metric spikes. Manual today; could be automated.
- **Reference baseline corpus.** Pin a "known-good baseline run"
  CSV in the repo so future regressions in the harness itself
  (not the SUT) are caught. ~5 MB committed, regenerated on each
  Puck game update.
- **Multi-server farm mode.** For users with bigger workloads,
  `--bots N` could be split across multiple physical machines
  using the same launcher pattern as `--bots-per-process`. Bigger
  scope; only needed if someone wants to stress-test a mod under
  >12 bots.
- **HTML report output** for S5. Markdown is fine for repo; HTML
  with embedded plots is nicer for sharing.

## Risks specific to the measurement side

- **Determinism is fragile.** Even with `--seed`, server
  `Time.realtimeSinceStartup` differs run-to-run, server-side
  random calls (e.g. faceoff puck position) can vary. S1
  validation must include "does the SERVER produce the same
  outputs?", not just "do the bots?".
- **Mod-loaded run inherits library state.** Loading a mod can
  affect things at static-init time before measurement starts.
  Always tear down and restart the server process between
  baseline and candidate; never just load the mod mid-run.
- **CPU thermal throttling.** A 30-min run can trip thermal
  limits on some workstations and drop CPU clock — invalidates
  comparisons. Mitigate: cap Puck server's CPU affinity to
  performance-cores and pin clock with the OS power plan, OR
  measure CPU clock per tick and reject samples below threshold.
- **2070 Super machine doubles as ML trainer.** While ML training
  runs (`ml/ppo_train.py` later), CPU is split with the trainer
  Python process. Don't run mod measurements concurrently with
  ML training. Document this in the operator runbook.

## Quick-restart checklist when you resume

1. Read this file + `MISSION.md` + `STATUS.md`.
2. Confirm baseline still runs cleanly:
   ```
   cd testserver && Puck.exe ... (server with NO custom mods loaded)
   BotHost.exe --bots 12 --bots-per-process 1 --duration 60 --vote-start
   ```
   Look at `testserver/telemetry/<utc>_metrics.csv`. Phase distribution
   should match older baseline runs (~95% Playing for a 60s run that
   /vs's into Play immediately).
3. Decide the entry point: S1 if you don't have a deterministic
   playbook yet (most likely); S5 if S1+S2 are already done.
4. Don't open any new server-side patch surface beyond
   ConfigCaptureMod + TelemetryMod (memory rule:
   `feedback_server_minimal_patches.md`).
