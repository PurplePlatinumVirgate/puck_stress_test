# Troubleshooting

## Build

### `Could not resolve Puck.dll` / `Unity.Netcode.Runtime` when building mods
The `PUCK_MANAGED` path is wrong or unset. It must point at your Puck install's
`...\Puck\Puck_Data\Managed` directory. Set the environment variable (see
[BUILD.md](BUILD.md)) or fix the `<PuckManaged>` fallback in the `.csproj`.

### `copy_dlls.ps1` says "Set PUCK_MANAGED..."
The script needs your Puck managed-DLL path. Set `$env:PUCK_MANAGED`, or pass the
path as the first argument:
```powershell
pwsh BotHost\Assets\Plugins\Puck\copy_dlls.ps1 "C:\...\Puck\Puck_Data\Managed"
```

### Unity build fails with API-updater / NGO errors
Confirm you opened the project with **exactly** Unity 6000.0.44f1. A different
editor version will try to upgrade packages and break the pinned NGO 2.4.0.

### `Puck.exe not found at ...`
`launch_server.cmd` can't find the server binary. Set the `PUCK_EXE` environment
variable to your `Puck.exe`, or edit the fallback in the script.

## Connecting

### Bots are kicked / never get admitted
- Make sure `usePuckBannedSteamIds` is `false` in `server_configuration.json`.
- Make sure **BotAuthBypassMod** is deployed at
  `<server-dir>/Plugins/BotAuthBypassMod/BotAuthBypassMod.dll` **and** the server
  was launched from its own directory (so `Plugins/` is found). See
  [SERVER_SETUP.md](SERVER_SETUP.md).

### Plugins don't load
Puck resolves `Plugins/` relative to the current working directory. Always launch
the server from inside the server directory (the launch script does this for
you). Launching `Puck.exe` from elsewhere silently skips the mods.

### Wrong port / can't reach the server (repeated "Socket error … attempting recovery")
Bots that endlessly log `Socket error encountered; attempting recovery by creating
a new one.` and never connect are almost always pointed at the **wrong UDP port**.

Puck listens on **UDP 30609** (its default game port), **not** the `port` value in
`server_configuration.json`. Confirm the real port in the server log:

```
[TCPServer] Server started on port 30609
```

Connect bots with `--port 30609` (the bot default is 30609). The old `7777`/`7778`
values are stale — do not use them.

### `NetworkConfig hash mismatch` / `Scene Hash X does not exist` / immediate disconnect
The bots' pinned protocol constants no longer match the server's Puck build. This
usually happens after a Puck update. Run **ConfigCaptureMod** on the server to dump
the current `ProtocolVersion` + prefab hashes, then update the constants in
`BotHost/Assets/Scripts/BotInstance.cs` and `ScenePlacedRegistrar.cs` and rebuild.

## Running

### Bot falls back to a 30-second default playbook
`--playbook` must be an **absolute** path. The launcher passes the same string to
its child processes, whose working directory differs, so a relative path resolves
to nothing and the child uses its built-in default. See `launch_commands.txt`.

### Rebuilding BotHost kills a live run
Don't rebuild while bots are running — the build overwrites `BotHost.dll` under
the running child processes and they crash. Stop the run first.

### Can't redeploy a mod DLL / stale mod behavior
A running `Puck.exe` holds a write lock on the plugin DLLs, so copying a new build
can silently fail and leave the old DLL loaded. Stop the server before redeploying
mod DLLs.

### Too many bot processes bog down my machine
Each bot runs in its own OS process by default. Use fewer bots (`--bots 4`) for
debugging; reserve large counts for actual stress/data runs.
