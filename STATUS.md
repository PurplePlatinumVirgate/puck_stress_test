# Status — Puck stress-test harness

_Current Puck build target: **B897** (`ProtocolVersion = 897`)._

## What works today

- **Protocol-level bots** connect to a Puck dedicated server and hold their
  seats for the full configured run duration. Each bot runs in its own OS
  process (one `NetworkManager` per process), so multi-bot runs scale across
  cores; multi-bot runs are routine and stable.
- **Bots stream real inputs.** Movement, look/aim, stick control, slides,
  sprints, and dashes all go over the wire through the same RPC chokepoints a
  real client uses — bots skate, carry the puck, and shoot.
- **Auth bypass.** `usePuckBannedSteamIds=false` in the server config plus the
  **BotAuthBypassMod** plugin let unauthenticated bots join a test server. No
  Puck Central / Steam dependency.
- **Protocol + scene sync matched.** The bots replicate Puck's `NetworkConfig`
  hash, network-prefab set, scene-placed object registration, and per-prefab
  NetworkBehaviour layouts so NGO accepts them as real clients.
- **Game-state visibility.** Bots mirror server state (game phase, time, score,
  puck positions, other players) via the `Mirror*` NetworkBehaviour
  transcriptions, so bot behavior can react to the real game.
- **Playbook-driven runs.** `BotHost.exe --playbook <json>` reads a
  schema-versioned JSON file (see `playbooks/README.md`) for behaviors, team
  assignment, and scripted actions.
- **Optional server-side telemetry.** TelemetryMod records per-tick + per-event
  CSVs under `<server-dir>/telemetry/` for profiling load and mod overhead.

## What it gets you

- **Per-client server overhead** — compare telemetry across bot counts to see
  frame-time / GC / alloc cost per extra connected client.
- **Connection-approval throughput** — burst joins exercise anything mods hook
  on connection approval.
- **Input + NetworkVariable cost under realistic play** — bots actually move and
  shoot, so server input handling and per-tick state broadcast are exercised.

## Getting started

See **[BUILD.md](BUILD.md)** (install + build) and **[SERVER_SETUP.md](SERVER_SETUP.md)**
(run a server with the auth bypass). Quick launch examples are in
`launch_commands.txt`.

## Repository layout

```
puck_stress_test/
  README.md                  Start here
  BUILD.md / SERVER_SETUP.md / TROUBLESHOOTING.md
  MISSION.md                 The why + architectural decisions
  STATUS.md                  This file
  BotHost/                   Unity 6000.0.44f1 project for the bots
    Assets/Scripts/          Bot host, instance, config, brains, mirrors
    Assets/Editor/           HeadlessBuild script for CLI builds
    Assets/Plugins/Puck/     Game DLLs (gitignored — you supply via copy_dlls.ps1)
  BotAuthBypassMod/          Server plugin: lets bots join a test server (required)
  TelemetryMod/              Server plugin: per-tick + per-event CSV metrics (optional)
  ConfigCaptureMod/          Server plugin: one-shot NetworkConfig/prefab dump (optional)
  server-template/           Sanitized server config + launch script to copy into
                             your own dedicated-server install
  playbooks/                 Machine-readable run scripts
  tests/                     Human-readable test plan template + checklists
  docs/dev-notes/            Deep reverse-engineering notes (historical reference)
```

> Build output, run logs, the local server install, and the ML training pipeline
> are intentionally gitignored and not part of the public repo.
