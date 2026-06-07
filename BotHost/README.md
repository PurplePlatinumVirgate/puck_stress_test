# BotHost — minimal Unity bot client for Puck stress tests

A Unity 6000.0.44f1 project that hosts N protocol-level bot clients in a
single headless process. Bots speak NGO/UTP to a Puck server. Not running
the game — just the network protocol.

See `MISSION.md` (parent dir) for the full mission and architectural
decisions.

## First-time setup

1. **Install Unity Hub + Editor 6000.0.44f1.** The Hub is at
   <https://unity.com/download>. Editor version must match Puck.
2. **Copy Puck DLLs** into `Assets/Plugins/Puck/` (they are gitignored and not
   shipped — you supply them from your own Puck install). Set `PUCK_MANAGED`
   first (see `../BUILD.md`), then:
   ```
   pwsh Assets/Plugins/Puck/copy_dlls.ps1
   ```
   Re-run after every Puck update.
3. **Open this folder in Unity Hub** ("Add project from disk" → select
   `BotHost/`). First open will resolve all packages from
   `Packages/manifest.json` and may take a few minutes.

## Running bots

A `BotHost` GameObject is auto-created at startup
(`Assets/Scripts/Bootstrap.cs` uses `RuntimeInitializeOnLoadMethod`), so
no scene setup is needed.

### From the Editor (during development)
1. Make sure a Puck test server is running (see `../SERVER_SETUP.md`).
2. Open this project in Unity Hub, press Play in any scene (the empty
   default `SampleScene` works).
3. Watch the Console. Default config: 1 bot → `127.0.0.1:30609`, 30 s run.
   Override defaults by editing the `BotHost` component fields in the
   Inspector during play, OR by running headless with command-line args.

### Headless from the command line (for actual stress runs)
First build a Windows headless build via the Editor (`File → Build Settings
→ Windows → Headless / Server Build → Build`), then:
```
BotHost.exe -batchmode -nographics \
  --server 127.0.0.1 --port 30609 \
  --bots 12 --duration 300 --seed 42
```

Server must be running first. See `../SERVER_SETUP.md`.

## Layout
```
Assets/
  Plugins/Puck/        Puck.dll + Assembly-CSharp-firstpass.dll (gitignored)
  Prefabs/             PlayerStub + other minimal NetworkObject prefabs
  Scenes/              Bootstrap.unity hosts a single BotHost component
  Scripts/             BotHost, BotInstance, BotConfig, ConnectionData
Packages/
  manifest.json        NGO + UTP + InputSystem pinned for Unity 6000.0.44
```

## Status
- Connects to a real Puck dedicated server: **yes** (current Puck build).
- Streams inputs (movement, look, stick, dashes): **yes** — bots skate, carry,
  and shoot.
- Runs many bots: each bot runs in its own OS process; multi-bot runs are
  routine. Use a small count (e.g. 4) for debugging, larger for stress runs.

See `../STATUS.md` for current capabilities and `../BUILD.md` for the full build
steps (this file is the BotHost-specific quick reference).
