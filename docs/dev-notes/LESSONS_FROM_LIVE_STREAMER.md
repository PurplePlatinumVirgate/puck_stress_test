# Lessons from `live_streamer` Perf Profiling

**Source.** Handoff from `live_streamer` mod's perf-instrumentation work
(commits `0a351ef`..`6fa4706` in `puck_mods/live_streamer`). That repo
shipped a `PerfMonitor` that brackets every phase of a mod's hot path
with `Stopwatch.GetTimestamp()`, plus per-tick GC counters, written to
a CSV row per tick. After 6.3 hours of gameplay capture and a stakeholder
report (`docs/perf/PERF_REPORT.md`) the load-bearing lessons distilled
to the points below.

**Audience.** Whoever owns `TelemetryMod` next, and anyone designing
the A/B mod-comparison harness. Most of these are corrections to things
*we* (live_streamer) initially got wrong — same traps await any naive
"just record `frame_ms` per tick" telemetry layer.

---

## Things to add to TelemetryMod's measurement set

### L1. Per-phase brackets, not just a single per-tick number

**Current state of TelemetryMod.** One row per sample window with
`frame_ms` (`Time.deltaTime * 1000`). When a tick is slow, you know it
was slow but cannot attribute the cost.

**Lesson.** A slow-frame canary with no per-phase attribution is nearly
useless for mod comparison. live_streamer's `PerfMonitor` brackets each
named phase (snapshot alloc, query, player loop, puck loop, game state,
enqueue) so when `total_us` exceeds budget the CSV row tells you *which
phase* was responsible. Without this, two mods with identical p99 frame
time can have completely different failure modes — one driven by NV
reads, the other by lock contention — and you can't distinguish them.

**Recommendation.** Define an `IPerfAware` interface mods can opt into.
TelemetryMod owns the bracket primitive (`PhaseBegin`/`PhaseEnd`); each
mod registers named brackets around its own hot paths. Add new CSV
columns dynamically per-mod, or write a separate `<mod_name>_phases.csv`
sidecar so the schema isn't fixed at build time.

---

### L2. Sample every tick, not at fixed wall-clock intervals

**Current state.** `SampleIntervalSeconds = 0.05f` → 20 Hz sampling.

**Lesson.** The interesting tail latency for mod impact lives at 1–3 ms.
On a 240 Hz server tick, 50 ms = 12 ticks. Sampling at 50 ms collapses
the worst tick of those 12 into one row, hiding the actual tail. We
discovered in live_streamer that p99 = 1.44 ms vs p99.9 = 4.89 ms — a
3.4× spread. Coarse sampling would have shown only the average and
missed the tail entirely.

**Recommendation.** Sample every server tick (not every UI frame, not
every 50 ms). At 240 Hz, ~240 rows/sec × ~150 bytes/row = 36 KB/sec —
trivial. At 360 Hz, 54 KB/sec. Buffer + flush periodically so disk I/O
doesn't enter the bracketed window.

---

### L3. Be explicit about *which thread* is measured

**Lesson.** The operator-relevant cost is **main-thread time** (the
thread that runs Unity physics, FixedUpdate, networking). Background
threads are the mod's business; they don't compete with game logic. In
the live_streamer report we had to add a prominent disclaimer that
"every number is main-thread time" because the natural reader question
was "does this include the bg sender's work?" (No.)

**Recommendation.** Rename CSV columns and report sections to make the
thread of measurement explicit. `frame_ms` → `main_thread_tick_ms`, or
similar. When mods opt-in via `IPerfAware`, document that brackets must
be on the main thread; cross-thread brackets produce unreliable
durations because of preemption between calls.

---

### L4. `Process.TotalProcessorTime` is unreliable at sub-millisecond scale

**Lesson — corrects something we initially shipped wrong.**
live_streamer recorded `cpu_total_us` per tick as a delta of
`Process.TotalProcessorTime` to answer "is the canary cost ours, or
stolen cycles (GC pause, OS preemption)?"

This **does not work** at sub-ms scale. On Linux, Mono's
`TotalProcessorTime` reads `/proc/self/stat` `utime+stime`, which are
in `USER_HZ` clock ticks (typically 100 Hz = **10 ms granularity**). On
Windows, the system tick is similar (~15.6 ms). At that resolution a
200 µs tick reads as 0 µs CPU; a 5 ms tick reads as 0 or exactly
10000 µs (one quantization bucket). The "wall-clock − CPU" gap
attribution is meaningless for the loads we actually care about.

**Recommendation.** Don't include a CPU-time column in TelemetryMod
without a real-resolution source. Three options, increasingly serious:

1. **Drop the column entirely.** Cheapest. Use Σ(per-phase) vs total
   (from L1) as the proxy for "time we accounted for vs unaccounted."
2. **`/proc/self/schedstat` run-delay** (Linux only). Field 2 of
   `/proc/PID/schedstat` is "ns waiting on a runqueue" — a direct
   preemption signal at full ns resolution. Cheap to read; sample once
   per tick.
3. **Per-thread CPU time via `pthread_getcpuclockid`** — there is no
   portable Mono wrapper, would need P/Invoke.

(2) is the right answer if you want to attribute to OS preemption.
Skip the false precision of `TotalProcessorTime` either way.

---

### L5. `GC.GetAllocatedBytesForCurrentThread()` is unreliable on Mono

**Lesson — another correction.** live_streamer recorded `allocs_bytes`
per tick as a delta of `GC.GetAllocatedBytesForCurrentThread()` to track
allocation pressure. The CSV showed `0` for every single tick. That is
*not* because we didn't allocate — `FrameSnapshotMessage` is a heap-
allocated `sealed class` and 12 GC events fired during the run — but
because the API itself appears to return 0 unconditionally on the Mono
build Puck uses.

We initially reported "zero allocs per tick" as a finding to
stakeholders, then had to retract.

**Recommendation.** Don't trust per-thread alloc bytes on Mono. Use
`GC.GetTotalMemory(false)` deltas across the bracket (signed; negative
when a collection ran inside the window — write the raw signed value
so the analyst can see "this row collected"). TelemetryMod already
records `total_alloc_b` from `GetTotalMemory` — keep that, don't bother
with the per-thread variant.

---

### L6. `Time.deltaTime` is not the metric you want

**Lesson.** `UnityEngine.Time.deltaTime` is wall-clock between
`Update()` invocations — including idle time when nothing happened.
For server-side perf you want **simulation-tick budget consumption**,
which is the duration of the actual `FixedUpdate` (or whatever the
authoritative simulation step is).

If `deltaTime` shows 16 ms but the physics step did 2 ms of work and
spent 14 ms idle, the server is fine. If `deltaTime` shows 16 ms and
physics did 16 ms of work, the server is saturated. Same `deltaTime`,
opposite reality.

**Recommendation.** Bracket `FixedUpdate` directly with
`Stopwatch.GetTimestamp()` and record that as `physics_tick_ms`. Keep
`Time.deltaTime` as a separate column if you want — it's still useful
as a "did the engine think this took 16 ms" sanity check — but don't
use it as the primary perf metric.

---

### L7. Idle/active gates and append-mode CSV create gaps

**Lesson.** live_streamer gates capture on (a) any client connected
and (b) viewers present on the relay. Captures during idle periods are
skipped; the CSV is in append mode so server restarts produce huge
`Δt` gaps in the data. We had to add a post-hoc "drop ticks following
Δt > 5 s, require players ≥ 2" filter to get a clean dataset.

**Recommendation.** Two fixes:

1. **Always record session boundaries.** Write a `session_start` /
   `session_end` event row whenever the gate flips, so the analyzer
   knows where one session ends and the next begins without inferring
   from `Δt`.
2. **Tag every row with a `session_id`** — a UUID per server-process
   run is fine. Then "filter to one session" is a SQL where-clause,
   not a heuristic.

This applies even more to the stress-test repo because you want to
align bot scenarios with measurement windows, not hunt for them.

---

### L8. Event-correlated outlier capture

**Lesson.** The worst ticks live_streamer observed (~6–8 ms) clustered
in the `player_loop` phase with no GC counter delta. We could not
correlate those timestamps with game events because the game's own
event log is in a different file and not synchronized with the perf
CSV. As a result we couldn't say whether those spikes happened during
period transitions, faceoffs, mass spawns, or goal celebrations.

**Recommendation.** TelemetryMod's `events.csv` is exactly the right
shape for this — but only if it's emitted *for the events that matter*:
period-transition, goal scored, faceoff dropped, all-bots-spawned, etc.
Use the same `t_ms` clock as `metrics.csv` so a join is one query.

For the worst-of-the-worst ticks, consider an **outlier dump**: when a
tick exceeds 2× the running p99, write a richer row to a separate
`outliers.csv` with all phase breakdowns, GC counters, recent N events,
and the queue/network state. The first capture's worst tick is always
where you want the most information.

---

### L9. Reproducibility fingerprint per run

**Lesson.** live_streamer's CSV has `tick_idx`, `ts_ms`, `players`,
`pucks` and that's it for run identity. Two runs with different mod
versions, different game builds, or different server configs are
indistinguishable in the data. The summary file is separate (good!)
but easy to lose.

**Recommendation.** TelemetryMod should embed at least these into the
`summary.txt` (it already has some) and ALSO into the first row /
header of `metrics.csv` as comments:

- `run_id` (UUID)
- `mod_label` (e.g., `"vanilla"` / `"live_streamer-9fd09cf"`)
- `mod_versions` (full list with hashes)
- `bot_scenario` (e.g., `"smoke_12bots_30s"`)
- `seed`
- `tick_rate`
- `unity_version`
- `server_build_hash`

Without this, A/B comparisons across re-runs are unreliable: "the
numbers changed" might mean the mod changed, the workload changed, the
build changed, or the seed changed. You want to be able to assert in
the report "only the mod changed."

---

### L10. Lock-free ≠ contention-free

**Lesson.** When live_streamer's `enqueue` phase showed a 1.2 ms p99
tail, our first instinct was "lock contention" — but the queue is
`ConcurrentQueue<T>` (lock-free) plus a `SemaphoreSlim.Release()`. We
initially shipped a stakeholder report saying "main thread blocked on
sender lock" before realizing we'd misdescribed the primitive. The
*real* tail driver was likely the `SemaphoreSlim.Release` slow-path
acquiring its internal Monitor when the bg thread was in `WaitAsync` —
similar in effect, but a different code path.

**Recommendation.** When a stress-test mod-comparison report attributes
cost to "lock wait," verify the underlying primitive. `ConcurrentQueue`,
`Channel<T>`, `Interlocked.*`, and lock-free CAS loops all *can* show
multi-ms tails under contention without holding any user-visible lock.
Be precise in language: "main-thread time inside the enqueue path"
is honest; "blocked on sender lock" overcommits to a mechanism.

---

### L11. Sub-phase brackets when a phase has a fat tail

**Lesson.** live_streamer's `enqueue` phase had a 1.2 ms p99 — too big
to be any one of its four sub-steps (envelope alloc, queue Count,
queue Enqueue, semaphore Release). The single bracket couldn't tell us
which one. We shipped a sub-phase split (`eq_alloc_us`, `eq_queue_us`,
`eq_signal_us`) so the next capture pinpoints the culprit instead of
us speculating.

**Recommendation.** TelemetryMod's `IPerfAware` interface (L1) should
support **nested brackets** — a phase can sub-bracket itself. When a
phase's p99 is interesting, the mod author can drop in sub-brackets
without changing the schema, just adding columns. Same null-check
pattern as live_streamer's `PerfMonitor`.

---

### L12. NetworkVariable bandwidth is part of the cost

**Lesson.** live_streamer measured CPU only. But mods that read or
write a lot of NetworkVariables push more bytes onto the wire each
tick, which is a real cost on the server's NIC and on every connected
client's bandwidth. We didn't measure this and could have produced a
"this mod is cheap" report that hides a 2× wire-traffic increase.

**Recommendation.** TelemetryMod should record `bytes_sent_per_tick`
and `bytes_received_per_tick` from `NetworkManager.NetworkConfig` /
the transport's stats interface. For Puck this is either the NGO
transport's diagnostics or a manual hook on the snapshot serializer.
For mod comparison this is the difference between "1.4 ms p99 with
mod X" and "1.4 ms p99 with mod X **and 30 % more wire traffic**" —
the second one is the real story.

---

### L13. GC events contaminate adjacent ticks

**Lesson.** When a Mono GC fires, the tick it lands on costs ~4 ms
(observed: live_streamer's 12 GC ticks averaged 4,132 µs vs 258 µs for
non-GC ticks). One bad row pulls the mean way up. live_streamer's
analysis script splits stats by GC-fired vs no-GC to keep the steady-
state number honest.

**Recommendation.** TelemetryMod's CSV already has `gc_gen0/1/2`
counters. Make sure the analysis script (or report) computes
percentiles split by "tick had GC event" vs "tick did not." Otherwise
mod A and mod B can have identical 99.9th percentiles but the *cause*
is "GC fires every 30 min for both" vs "mod B is genuinely faster but
also GCs more" — different conclusions.

---

### L14. Linear AND log scale on the report charts

**Lesson.** Stakeholders (game server operators in our case) found
log-scale-only charts confusing — they could not tell at a glance how
big the cost was relative to a tick budget. Pure linear hides the tail.
Solution: dual-panel charts, linear top + log bottom (or side-by-side),
so the reader sees both relative scale and full distribution.

**Recommendation.** When TelemetryMod's analysis script produces
mod-comparison charts, render them dual-panel by default. Cost is
trivial; clarity gain is large.

---

### L15. The "is this mod the cause of stutter?" question deserves a
direct answer

**Lesson.** The single most-asked operator question is *"my server
stuttered, was your mod the cause?"* The live_streamer report
addressed this with an attribution chart: of slow ticks ≥1 ms, what
share went to (a) our code, (b) the game's NV reads we called, (c)
GC, (d) outside-bracket residual. The chart, more than any other,
moved stakeholder confidence.

**Recommendation.** TelemetryMod's mod-comparison report should make
the question a first-class output. For each mod under test, produce:
"of the slow ticks observed during this run, what fraction were
attributable to this mod's bracketed phases vs everything else." This
is operationally what operators need to see; everything else is
supporting evidence.

---

## Suggested column additions for `metrics.csv`

Beyond what's already there:

| New column | Source | Why |
|---|---|---|
| `session_id` | UUID per process run | L7 |
| `physics_tick_ms` | `Stopwatch` around `FixedUpdate` | L6 |
| `bytes_sent_per_tick` | NGO transport stats | L12 |
| `bytes_recv_per_tick` | NGO transport stats | L12 |
| `runqueue_wait_ns` | `/proc/self/schedstat` field 2 | L4 |
| `<mod>_<phase>_us` | per opt-in mod brackets | L1, L11 |

Drop:
| Column | Reason |
|---|---|
| `cpu_total_us` (if present) | L4 — quantization makes it lie |
| Per-thread alloc bytes (if present) | L5 — Mono returns 0 |

Keep:
| Column | Reason |
|---|---|
| `frame_ms` | Useful sanity check, but rename to `update_dt_ms` to avoid confusing it with `physics_tick_ms` |
| `gc_gen0/1/2` | L13 — split percentiles by GC vs no-GC |
| `total_alloc_b` (`GetTotalMemory(false)`) | L5 — the reliable allocation proxy |

---

## Suggested mod-comparison harness shape

Putting L1–L15 together, the A/B comparison output for "vanilla vs
mod X" should be a single dual-panel report with:

1. **TLDR table** — mean / p99 / p99.9 main-thread tick time, GC events
   per minute, bytes/sec wire traffic. Two columns: vanilla, mod X.
   Bold any row where mod X is more than 1.2× vanilla.
2. **Tick-cost ECDF, dual panel** — vanilla curve and mod X curve
   overlaid. Linear panel for relative scale, log panel for tail.
3. **Per-phase contribution bar chart** — mod X's bracketed phases on
   the y-axis, mean and p99 on the x-axis. Tick-budget reference line.
4. **Lag attribution stacked bars** — for ticks ≥1 ms, share
   attributed to (mod X bracketed code) / (game NV reads we called) /
   (GC) / (outside brackets). Same chart shape live_streamer's report
   uses.
5. **Wire-traffic delta** — bytes/sec scatter over time, vanilla vs
   mod X. Wire load isn't visible in CPU plots.
6. **Reproducibility footer** — both runs' `run_id`, seed, scenario,
   server build, mod versions. Without this the report is anecdote.

If you produce this report shape, an operator can answer "should I
install mod X" from one PDF/markdown without diving into raw CSVs.
That is the deliverable; everything else is plumbing.

---

## What live_streamer's own theories doc covers

If you want a model for how to record performance hypotheses ahead
of measurement, see
`puck_mods/live_streamer/docs/perf/THEORIES.md`. Pattern: every theory
has explicit kill/confirm criteria written *before* the next capture,
plus the targeted fix that follows from each outcome. Stops post-hoc
goalpost-moving and turns each capture run into a binary update on
each theory.

For mod comparison this means: when you suspect "mod X is slow because
of feature Y," write that as a theory with a kill criterion (e.g.,
"if `<mod>_<phase>_us` p99 < 200 µs, this theory is killed") *before*
running the comparison.
