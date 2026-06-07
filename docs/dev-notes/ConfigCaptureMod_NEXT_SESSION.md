# Next session — bot puck control, strike quality, and tactical play

Pick up here. Big strides this session: bots now vote to start the
game, run at full server tick rate (360 Hz), shoot from anywhere,
and use a real **wrist-shot blade-angle flick** to impart momentum.
Major mechanical gaps closed; remaining work is tactical.

## What's working as of session end (2026-04-28, late)

- **/vs voting**: bots send `/vs` chat command via `ChatSender.TrySend`
  during warmup; broadcasts the RPC across all 33 NBs of UI Manager
  (hash 3169663068) since UIChat's slot index isn't visible from
  outside the editor. Server log shows `Vote succeeded to start
  game (6/6)` and phase transitions Warmup → FaceOff → Playing.
  Stops voting once `MirrorGameManager.GameState.Phase != Warmup`.
  Server config has `allowVoting: true`.
- **360 Hz everywhere**: client + server tick rate at 360 (matching
  Puck's max). Bot brain ticks at 360 Hz via `--tick-hz 360` flag.
  Tick-count constants now scale via `Scaled(ticksAt30Hz)` helper
  so behavior is rate-independent. Affected: `ShotStrikeTicks`,
  `ShotCooldownTicks`, `ShotWindUpTicks`, `CradleSettleTicks`,
  `StuckBackoffTicks`, `PulseSlide/RecoverTicks`, `_logTickCounter`
  (10 s), `_refreshTickCounter` (1 s).
- **CS:GO bot names**: `BotName(NetworkObjectId)` maps NID into
  Valve's bot_names.txt list — "Bot Adrian", "Bot Gary", etc.
- **Stuck detection + step-in**: `_isStuck` flag set when a bot
  moves <0.5 m in 1 s while a target sits >1.5 m away. Stuck bots
  drop their carrier claim (`isCarrier = !goalie && !_isStuck && ...`)
  and back off (steerTarget = away-from-puck direction). The
  closest-claim then naturally hands off to a teammate, who skates
  in fresh.
- **Goal crease avoid**: steerTarget falling inside `(|x|<2.5,
  |z|∈[38.5, 42.5])` gets deflected to the rink-side edge so bots
  don't wedge the goal frame.
- **Body feedforward**: `steerTarget = puckPos + puckVel * (distToPuck/6)`
  in chase mode. Body skates toward puck's intercept point, not its
  current position. Head/look stays on `puckPos` itself (eyes on
  puck while body anticipates).
- **Stick feedforward**: aim at `puckPos + puckVel * (RTT + slewTime)`,
  where slewTime is computed from the engine's 15°/s PID clamp.
  Pulls blade IN for incoming pucks (bulldozer scoop), extends out
  for outgoing.
- **Stick PID slew model**: `_estStickAngleDeg` integrates the
  engine's `RotateRaycastOrigin` (`StickPositioner.cs:168` —
  P=0.75, output clamped ±15°/s). Used to lead the input target
  by slew time, and to rate-limit input deltas to ±25°/tick (avoids
  PID integral wind-up).
- **Wrist-shot mechanic**: `Idle → WindUp → Strike → Cooldown` FSM
  cocks `BladeAngleInput` to `-3` during WindUp (blade face cocked
  back), then snaps to `+3` on Strike. `Stick.cs:155-156` sets
  blade rotation directly via `Quaternion.AngleAxis(angle * 12.5°,
  Vector3.forward)` — NO PID slew on this axis. So a step from -3
  to +3 = 75° rotation in one tick → blade face sweeps through
  whatever's touching it. This is the actual impulse on stationary
  pucks. Sign comes from body-relative goal yaw (right-side-of-fwd
  → forward-sign positive).
- **Shot gate**: `engageShot = isCarrier && haveOffGoal && bladeClose`
  — fires whenever blade is within 1 m of the assigned puck. No
  body-aim restriction (per user: "shoot from anywhere as long as
  it'll go toward net"). Cradle settle requirement removed.

## Latest telemetry (run_wrist.log, 180s, 12 bots @ 360 Hz, Playing phase)

25 strikes attempted. Best outcomes (by puckMoved):

| Bot  | puckSpeed before→after | puckMoved | Notes                      |
|------|------------------------|-----------|----------------------------|
| 11   | 0.00 → 0.00            | **2.49 m** | stationary puck, real shot |
| 4    | 2.74 → 0.00            | **1.91 m** | running puck launched      |
| 6    | 1.01 → 0.00            | **1.79 m** | running puck launched      |
| 4    | 0.00 → 0.00            | **1.09 m** | stationary, real shot      |
| 7    | 2.58 → 0.00            | 0.27 m    | weak contact               |

`puckSpeed after` reads 0.00 in nearly every case because the
sample is 200 ms post-strike-fire, by which time the launched puck
has typically collided with another clustered bot or the boards.
**Use puckMoved as the impulse metric**, not puckSpeed-after.

## Diagnosis: where shots break down

- Mechanics are correct — the blade-angle flick imparts measurable
  impulses to stationary pucks (1+ m of motion).
- The 12-bot cluster around the puck means most shots immediately
  rebound off teammates. Need to either (a) thin the crowd or
  (b) measure peak velocity, not 200 ms post-sample.
- Replay baseline for comparison: median jump 4.34 m/s, p90 8.69
  m/s, BLADE TIP speed median 6.83 m/s. We don't capture peak puck
  velocity to compare directly.

## Candidates for next session, in priority order

1. **Track peak puck velocity during the strike outcome window**.
   Currently we sample once at `_strikeOutcomeTicksLeft == 0`.
   Replace with `max(_puckSpeedAfterStrike, EstimatePuckVelocity().magnitude)`
   each tick of the window. That tells us whether the flick is
   actually launching pucks or absorbing them.
2. **Stop the cluster** — Support bots are skating to rest
   positions inside the offensive zone (5 of them per team in a
   tight area near the puck). Spread the support fan further or
   send some to defensive zone. `SupportLateralSlots` and
   `SupportTrailDistance` are the knobs.
3. **Lock onto a single puck per team for longer** — currently
   the carrier claim flips bot-to-bot as positions shift, breaking
   the cradle hold. Add a "carrier sticky" period: once a bot is
   carrier, it keeps the role for ≥1 s even if a teammate gets
   transiently closer.
4. **Better Strike-vs-Cooldown timing** — at 360 Hz the strike
   fires + cools in <300 ms. With 12 bots converging, that's a lot
   of strike chances rebounding off each other. Maybe lengthen
   cooldown to 2 s so a bot doesn't immediately flick again on
   the same puck.
5. **Goalie engagement** — goalies currently anchor at crease
   and never strike. They can pick up loose pucks in the crease
   and clear them. Add a "goalie clear" mode when puck is in
   defensive crease.
6. **Replay-comparison telemetry per-strike**. Log body-rel puck
   FWD/SIDE/UP, body speed, blade-tip speed (we'd need to
   differentiate stick mirror pos+rot) into the STRIKE outcome
   line so we can compare distributions to `shot_stats.mjs`.

## Files / artifacts of interest

- `BotHost/Assets/Scripts/BotBrain.cs` — main behavior. Key
  sections: `Tick()` (~line 340), `IntegrateStickSlew` /
  `Scaled` helpers, `AdvanceShotState`, blade-angle output block.
- `BotHost/Assets/Scripts/Mirror/MirrorPlayer.cs` — bot name
  helper `BotName()`, jersey skin defaults.
- `BotHost/Assets/Scripts/Mirror/MirrorOthers.cs` — `ChatSender`
  class with the broadcast `/vs` send.
- `replay_web/scripts/shot_stats.mjs` — extracts human shot stats
  from .prp replays. Run with `node scripts/shot_stats.mjs replays`.
  Reference distributions: median jump 4.34 m/s, body speed median
  6.83 m/s with p10 0.70 (10% of shots from a stop), blade tip
  median 6.83 m/s.
- `testserver/server_configuration.json` — `serverTickRate: 360`,
  `clientTickRate: 360`, `targetFrameRate: 360`, `allowVoting: true`.
- `Logs/run_wrist.log` — last good telemetry sample.
- Memory: `project_shot_mechanics.md` — distilled rules from this
  session (real-hockey applies, flick > body speed, "on stick" =
  "next-to-broadside", puck simulates real stick-puck physics).

## Run commands

```pwsh
# Server (port 7777, allowVoting on, 360 Hz)
& "D:/SteamLibrary/steamapps/common/Puck/Puck.exe" -batchmode -nographics `
    --serverConfigurationPath "C:/Users/R/Desktop/Code/puck_stress_test/testserver/server_configuration.json"

# Build
& "C:/Program Files/Unity/Hub/Editor/6000.0.44f1/Editor/Unity.exe" `
    -batchmode -quit -nographics `
    -projectPath "C:/Users/R/Desktop/Code/puck_stress_test/BotHost" `
    -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildWindowsServer `
    -logFile "C:/Users/R/Desktop/Code/puck_stress_test/BotHost/Logs/build_next.log"

# Run 12 bots, 3 minutes, 360 Hz brain
& "C:/Users/R/Desktop/Code/puck_stress_test/BotHost/Build/BotHost/BotHost.exe" `
    --bots 12 --duration 180 --tick-hz 360 --server 127.0.0.1 `
    -batchmode -nographics `
    -logFile "C:/Users/R/Desktop/Code/puck_stress_test/BotHost/Logs/run_next.log"
```

## Hard rules (memory)

- Decompile FIRST for any "why does X behave Y" question. Runtime
  diagnostics are last resort.
- Real-hockey knowledge applies — Puck simulates stick-and-puck
  physics. Wrist shots = blade flick from cradle, body forward
  contributes a baseline.
- Keep code compatible with stress test + ML training data gen +
  ML inference (PLAN_ML.md).
- Server-side patches stay minimal/read-only. All instrumentation
  lives bot-side.
- Bot inputs go through `MirrorPlayerInput.Send*`. State reads via
  `MirrorSynchronizedObjectManager.LatestPositions` + mirror NVs.
