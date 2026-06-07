# Server setup

Bots connect to a **Puck dedicated server that you run**. This page covers
getting that server running with the authentication bypass the bots need, and
deploying the optional measurement plugins.

> Run this against a server you own. The auth bypass exists so synthetic clients
> can join a local/self-hosted test server. See the disclaimer in
> [README.md](README.md).

## 1. Install the Puck dedicated server

The dedicated server is a separate SteamCMD app from the game:

- **Dedicated server AppID: `3481440`** (not the game's `2994020`).
- Install it with SteamCMD, e.g.:
  ```
  steamcmd +login anonymous +app_update 3481440 validate +quit
  ```
  (See Valve's SteamCMD docs for setup.) This gives you `Puck.exe` and the Unity
  runtime files for a headless server.

## 2. Drop in the config + launch script

The real local server install is intentionally **not** in this repo. Copy the
sanitized templates from [`server-template/`](server-template/) into your server
directory:

- `server_configuration.json` — the server config. Key setting for bots:
  ```json
  "usePuckBannedSteamIds": false
  ```
  With this **off**, the gameplay server admits clients immediately instead of
  consulting Puck Central. No password is set.
- `launch_server.cmd` — launches `Puck.exe` headless against that config. Point
  it at your install by setting the `PUCK_EXE` environment variable, or by
  editing the fallback path inside the script.

The server only loads a config when it runs in **batch mode** (`-batchmode
-nographics`), which is what the launch script does.

On first boot Puck **auto-creates** its admin / ban / whitelist / game-mode files
(`admin_steam_ids.json`, `banned_steam_ids.json`, `banned_ip_addresses.json`,
`whitelisted_steam_ids.json`, `public_game_mode_config.json`) in the server
directory — the `... file not found, creating default...` warnings on first run are
normal and harmless. You don't need to ship these.

## 3. Deploy the auth-bypass plugin (required)

Puck authenticates joining clients over a websocket handshake. For bots to be
admitted you need **BotAuthBypassMod** in addition to the config flag above.

Plugins are loaded from a `Plugins/` folder **relative to the server's working
directory**, so the layout must be:

```
<your-server-dir>/
  Puck.exe
  server_configuration.json
  launch_server.cmd
  Plugins/
    BotAuthBypassMod/
      BotAuthBypassMod.dll
```

Copy the DLL you built in [BUILD.md](BUILD.md):

```powershell
$dst = "<your-server-dir>\Plugins\BotAuthBypassMod"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "BotAuthBypassMod\bin\Release\netstandard2.1\BotAuthBypassMod.dll" $dst
```

> **Always launch the server from its own directory** (the launch script does
> `cd /d "%~dp0"`), otherwise `Plugins/` won't be found.

## 4. Deploy measurement plugins (optional)

Same `Plugins/<ModName>/<ModName>.dll` layout:

- **TelemetryMod** — per-tick + per-event CSV metrics, written to
  `<server-dir>/telemetry/`. Useful for profiling load and mod overhead.
- **ConfigCaptureMod** — one-shot dump of the server's `NetworkConfig` hash and
  prefab list. Run it once after a Puck update to confirm the bots' protocol
  constants still match (see TROUBLESHOOTING.md → protocol mismatch).

Neither is required for bots to connect.

## 5. Run the server, then the bots

```powershell
# Console 1 — from inside your server directory
.\launch_server.cmd

# Console 2 — from the repo
& "<REPO>\BotHost\Build\BotHost\BotHost.exe" -batchmode -nographics `
    --playbook "<REPO>\playbooks\smoke_12bots_30s.json" `
    --bots 4 --server 127.0.0.1 --port 30609
```

**Port:** Puck's game server listens on **UDP 30609** (its default game port).
You can confirm it in the server log at startup:

```
[ServerManager] Starting Puck listener (897)
[TCPServer] Server started on port 30609
```

Connect bots there with `--port 30609` (the bot's default is also 30609).
Note: the `port` field in `server_configuration.json` is **not** the game-socket
port — Puck binds 30609 regardless. Use the value from the log line above.

## Keeping protocol in sync after Puck updates

The bots hard-code Puck's `ProtocolVersion` and network-prefab hashes
(`BotHost/Assets/Scripts/BotInstance.cs`, `ScenePlacedRegistrar.cs`). When Puck
updates, these can change. The full re-sync procedure (capture with
ConfigCaptureMod → update constants → re-transcribe mirrors → validate) is in
**[UPDATING.md](UPDATING.md)**. Symptoms of a mismatch are in
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).
