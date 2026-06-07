# Next session — close the warmup-RTT gap, ship passes, score goals

State as of end of session 2026-04-28 (late):
- **RTT during Play: ~15 ms** (down from 200 ms+ at session start).
- **RTT during Warmup phase: STILL spikes to 1000 ms+** — see priority #1 below.
- **78 SHOT strikes per 240 s** with peaks 17-20 m/s (replay p90 = 8.69 m/s).
- **0 PASS strikes triggered** — lane-clear gate too lax to ever fall through.
- **First goal scored** earlier in session (bot=4, peak 19.10 m/s).
- **Log volume: 6.17M → 1.7k lines** per 240 s. Zero protocol spam.
- **All NGO log/exception suppression patches REVERTED** — we ship clean error reporting on top of correct protocol.

## Priority 1 — Warmup-phase RTT 1000 ms+ regression

Symptom: when the server cycles back to Warmup (period end / game over → warmup), Player.Ping NV climbs to 1000 ms+ across all 12 bots within seconds. Persists until the next FaceOff.

What we already did and what didn't fix it:
- `BotBrain.Tick` early-returns when `Player.State.Value != PlayerState.Play` — clears Slide/Sprint/BladeAngle bool RPCs and resets FSMs. Helped during Replay/PositionSelect transitions but spike still appears in Warmup.
- `ClaimPositionLoop` deduped via `_claimPositionCo` so phase cycles don't stack overlapping coroutines.
- `MirrorPlayerInput` HasChanged-gates SendMove/SendRaycastOriginAngle/SendLookAngle.

Likely causes to investigate (in order):
1. **Bots don't transition to Spectate during Warmup.** Real Puck players go Play→Spectate during Warmup; our state-machine code may keep them in Play, so the Tick gate doesn't fire and we keep spraying inputs to a despawned body. Verify by logging `State` transitions at every phase change.
2. **Server respawn burst on Warmup→FaceOff transition.** 12 × (Body + Stick + StickPositioner + Camera) reliable spawn messages at once → UTP reliable window saturates. Real client has the same spawn burst though, so it should be tolerable. Check if bot CPU is spiking on those spawns (could be MirrorPlayerBodyV2.OnNetworkSpawn doing too much).
3. **Vote system spam.** With `--vote-start` enabled (testing), each bot fires `/vs` chat commands during Warmup. Server broadcasts each chat to all 12 clients reliably. 12×12 = 144 reliable messages per `/vs` send × resend interval. **Easy first test:** run with default `--no-vote-start` and observe Warmup RTT — if it drops, this is the culprit.
4. **MirrorPlayer state-machine handler kicks back to TeamSelect → ClaimPositionLoop fires fresh Client_ClaimPositionRpc.** Even with the dedup, the loop runs for 30 attempts × 600ms — that's 18s of reliable RPCs per cycle.
5. **NGO scene-sync re-fires on phase transition?** Unlikely (scene doesn't change), but check for `Spawn` / `Synchronize` events in the bot log around Warmup entry.

Diagnostic to add first: **log the bot's State.Value AND the GameManager.GameState.Phase together** every second during a Warmup window. If our State is stuck on Play, that's the gate problem.

## Priority 1b — Add /vw (vote-warmup) opt-in alongside /vs

Symmetric companion to `/vs` (vote-start). Source: `VoteManagerController.cs:37` accepts `/vw` and `/votewarmup`; the server's GameManager honours `VoteType.Warmup` to flip mid-game state back to Warmup.

Why we need it: priority #1 above is investigating a Warmup-phase RTT spike. Reproducing the spike on demand requires forcing the game **back to Warmup** mid-run; right now we can only enter Warmup naturally (period end / GameOver) which is slow and unreliable. With `/vw` available we can:
- Enter Play (via `--vote-start`), record baseline RTT.
- Trigger `/vw` mid-session (via a CLI flag, playbook entry, or scripted action) → server transitions back to Warmup.
- Capture RTT spike with timing tied to a known event.

Implementation sketch:
- Mirror the existing `/vs` plumbing: `BotConfig.VoteWarmup = false`, `--vote-warmup` / `--no-vote-warmup` CLI flags, `vote_warmup: bool` playbook field, `BotBrain.VoteWarmup` wired in `MirrorPlayer.OnNetworkSpawn`.
- Send via `ChatSender.TrySend(NetworkManager, "/vw")` — same chat path as `/vs`.
- Trigger condition: when `VoteWarmup=true` AND game phase != Warmup AND we haven't fired yet this game-cycle. Maybe gate on a `--vote-warmup-after-seconds` to delay the trigger so we get a clean Play-phase baseline first.
- Both `VoteStart` and `VoteWarmup` should be safe to combine (start fires only in Warmup, warmup fires only outside Warmup); they don't conflict.

This becomes the harness's standard cycle test: opt into both flags and the bots will keep ping-ponging the game between Play and Warmup, producing repeatable RTT measurements across the transition.

## Priority 2 — Pass logic never triggers

Last run: 78 SHOT, 0 PASS. The lane-clear gate (`IsLaneClear` with 0.8m blocker radius) **never** finds the goal lane blocked, so we never fall through to `TryFindPassTarget`.

Diagnose and fix:
- Log `IsLaneClear` decision per strike: `[BotBrain bot=N] LANE clear=TRUE/FALSE blockers=K`. Verify whether blockers really are absent or whether our search is buggy.
- If lanes are mostly clear (likely with 12 bots all clustered around the puck not between puck and goal), tighten: a "scoring chance" gate. Pass when:
  - Distance to goal > 15 m (we're far from the net), AND
  - A teammate exists within 3-12 m closer to goal with a clear lane to net.
- Better still: replace lane-clear with **goalie-aim**. If we're close to goal AND aim is roughly at the goalie's position (not the corners), pass to a teammate at a better angle.

## Priority 3 — Aim accuracy: 78 shots, 0 goals (this run)

The blade-angle flick mechanic delivers replay-p99 impulse but doesn't reliably aim for the net. The strike sweep direction picks ±sign from body-rel target yaw, and sweep `ShotStrikeDeg=45°` overshoots. Final puck direction depends on blade-face normal at impact (body forward + stick yaw + blade angle).

To improve:
- Predict the blade-face direction at impact and only fire when the resulting launch vector points toward goal mouth (±~3 m of net center).
- Aim slightly off-center to the goalie's far post (use Goalie body position from `MirrorSynchronizedObjectManager.LatestPositions` — we already track it for assigned-puck assignment).

## Priority 4 — Sticky carrier

Once a bot is cradling, it can lose `isCarrier` claim if a teammate transiently passes within range, breaking the cradle. Add: hold carrier for ≥1 s after entering inCradle, regardless of teammate distance.

## Priority 5 — Dump raycastOrigin local offset (task #5 still pending)

ConfigCaptureMod doesn't yet dump the StickPositioner.raycastOrigin local position (relative to body). With that one constant we can compute the predicted blade-target position from body pose alone (matches `StickPositioner.cs:176-225` geometry), without depending on the stick-rigidbody mirror lagging behind input. Useful for ML state extraction too.

## Run commands (still valid)

```pwsh
# Server (port 7777, allowVoting on, 360 Hz). Run from testserver/ so
# Plugins/ directory resolves to live_streamer + ConfigCaptureMod + TelemetryMod.
& "D:/SteamLibrary/steamapps/common/Puck/Puck.exe" -batchmode -nographics `
    --serverConfigurationPath "C:/Users/R/Desktop/Code/puck_stress_test/testserver/server_configuration.json"

# Build
& "C:/Program Files/Unity/Hub/Editor/6000.0.44f1/Editor/Unity.exe" `
    -batchmode -quit -nographics `
    -projectPath "C:/Users/R/Desktop/Code/puck_stress_test/BotHost" `
    -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildWindowsServer `
    -logFile "C:/Users/R/Desktop/Code/puck_stress_test/BotHost/Logs/build_next.log"

# Bots — note --vote-start is NOW OPT-IN. Default is no-vote-start
# so connecting to community servers is safe. Pass --vote-start when
# testing against our own dedicated server.
& "C:/Users/R/Desktop/Code/puck_stress_test/BotHost/Build/BotHost/BotHost.exe" `
    --bots 12 --duration 240 --tick-hz 360 --vote-start --server 127.0.0.1 `
    -batchmode -nographics `
    -logFile "C:/Users/R/Desktop/Code/puck_stress_test/Logs/run_next.log"
```

## Hard rules (memory — re-read before changing anything)

- Decompile FIRST for any "why does X behave Y" question. Runtime diagnostics are last resort.
- **Real-client protocol match is mandatory** — gate inputs by HasChanged + State==Play, mirror NGO NB layouts exactly. Don't suppress NGO errors; fix the underlying protocol.
- Server-side patches stay minimal/read-only. ConfigCaptureMod / TelemetryMod only.
- Bot inputs go through `MirrorPlayerInput.Send*`. State reads via `MirrorSynchronizedObjectManager.LatestPositions` + mirror NVs.
- /vs voting is OPT-IN (`--vote-start` CLI / `vote_start: true` playbook). Default OFF.

## Telemetry that's now in place

- `STRIKE-ENTRY SHOT` / `STRIKE-ENTRY PASS` log (current always SHOT — see priority #2).
- `STRIKE outcome: puckSpeed before=X peak=Y end=Z m/s, puckMoved=W m`. Compare peak to replay baseline.
- `[NB-LAYOUT]` dump from ConfigCaptureMod — re-run after any Puck update.
- `[BotBrain] Carrier|Support|Goalie ... minBladePuckDist maxCradleTicks strikes stuckEpisodes` 10s-period summary.
