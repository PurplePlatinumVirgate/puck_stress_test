# Stress test plan: \<MOD NAME\>

> Copy this file to `tests/<mod-shortname>_<YYYY-MM-DD>.md` and fill it in
> before starting a profiling run. The filled-out copy is the artifact you
> attach to the run output (traces, metrics, screenshots).

## Run identity
- **Mod under test**: \<name + Steam Workshop ID, or "vanilla" for baseline\>
- **Puck build**: \<e.g. B202\>
- **Date**: YYYY-MM-DD
- **Operator**: \<your name\>
- **Run ID**: `<mod-shortname>_<YYYY-MM-DD>_<NN>`

## Hypothesis
What do you expect this run to show? One sentence.
> _e.g._ "This mod recomputes a per-tick global iteration over all players;
> we expect tick time to scale O(N²) and become visible at N=8+."

## Server config
- Bots: **N** (default 12 for full-load runs, smaller for sweeps)
- Duration: **\<seconds\>** (default 300 for steady-state, 60 for smoke)
- Server tick rate: **\<see server_configuration.json\>**
- Mods loaded server-side: \<list, in order\>
- Mods loaded client-side: none (bots run vanilla NGO + Puck.dll)
- `usePuckBannedSteamIds`: **false** (auth bypass)

## Pre-flight checklist
Tick each box before starting the run.

- [ ] Server `server_configuration.json` shows the right mod list
- [ ] `usePuckBannedSteamIds: false`
- [ ] Server launches cleanly (`testserver/launch_server.cmd`) and reaches
      the warmup phase before bots connect
- [ ] Profiler is attached / metric capture is running BEFORE bots connect
- [ ] Disk has room for the run's traces (rule of thumb: 100 MB/min/bot
      for full Unity profiler captures)
- [ ] Previous run's traces have been moved to the archive
- [ ] No other Puck servers are running on this machine on port 30609

## Baseline behaviors (every run)
Bots should exercise these regardless of mod. If a behavior is missing
from the bot harness today, list it under "TODO" instead of unchecking.

- [ ] All N bots connect successfully and reach `PlayerState.Play`
- [ ] Bots remain connected for the full run duration
- [ ] Each bot picks a team (alternating Red/Blue, or matching mod-specific
      assignment)
- [ ] Each bot picks a position (skater vs. goalie distribution per config)
- [ ] Bots skate toward the puck
- [ ] Bots rotate stick and head toward the puck (or other useful target)
- [ ] Bots attempt poke / slap when within reach of the puck
- [ ] Carrying bots bias movement toward the opposing goal
- [ ] No bot disconnects mid-run unexpectedly (rejection codes captured if
      so)

## Mod-specific behaviors
Add behaviors this mod's hot path needs to be exercised. Examples:

### Chat / commands
- [ ] Bots send `/<command>` slash commands at \<frequency\>
  - command list: \<...\>
- [ ] Bots send a chat message every \<N\> seconds
- [ ] Bots use quick-chats: \<list IDs\>

### Voting / ruleset
- [ ] At least one vote is initiated per period
- [ ] Bots cast votes (yes/no, mix per mod's expected distribution)

### Faceoffs / restarts
- [ ] Bots line up correctly at faceoff
- [ ] At least one goal is allowed to occur to trigger restart paths
- [ ] At least one period transition occurs

### Other (mod-specific)
- [ ] \<add lines for whatever this mod uniquely touches\>

## Measurements to collect
What you actually want out of the run.

- [ ] **`TelemetryMod` is loaded** in the server's `Plugins/` (writes the
      `<utc>_metrics.csv` and `<utc>_events.csv` artifacts described in
      `TelemetryMod/README.md`)
- [ ] Server tick-time histogram (P50 / P95 / P99 / max from
      `metrics.csv` `frame_ms` column)
- [ ] GC pressure (`gc_gen0`/`gc_gen1`/`gc_gen2` deltas per second)
- [ ] Managed heap slope (`total_alloc_b` over the run)
- [ ] Connection lifecycle (every approval/disconnect from `events.csv`)
- [ ] Mod-specific counters: \<list\>
- [ ] Any console errors / warnings server-side
- [ ] Any console errors / warnings client-side (for our bots)

## Comparison baseline
Which prior run is this measured against?
- **Baseline run ID**: \<id of the vanilla or prior-mod-version run\>
- **What changed**: \<one sentence\>

## Run results
Filled in AFTER the run.

- Outcome: pass / fail / partial
- Tick time P50 / P95 / P99: \<...\>
- Notable spikes: \<timestamp + cause if known\>
- Comparison to baseline: \<faster / slower / same / N/A\>
- Surprises: \<things that weren't in the hypothesis\>
- Artifacts:
  - profile trace: \<path\>
  - server log: \<path\>
  - bot logs: \<path\>

## Follow-up actions
- [ ] \<bug to file / fix to make / config to tweak\>
