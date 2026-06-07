# Updating the harness for a new Puck build

When Puck ships a new build, its networking can change in ways that make the bots
look like the *wrong* kind of client and get rejected. This guide is the procedure
for re-syncing the harness. It's the hardest-to-rediscover knowledge in the project,
so follow it top to bottom.

> **Guiding principle:** the **running server is ground truth** — `ConfigCaptureMod`
> captures exactly what the bots must match. The **decompile** is the reference for
> *why* a value is what it is. **Never guess** a hash, prefab, or layout. The final
> arbiter is the NetworkConfig hash: once the bot computes the same `GetConfig()` hash
> the server does, the protocol handshake will accept it.

## When you need this

Symptoms that Puck drifted out from under the bots:

- Bots connect then get **kicked immediately**, or never finish the handshake.
- Logs show a **`NetworkConfig` hash mismatch** (the bot's computed hash ≠ the server's).
- **Scene-sync `OverflowException` / FastBufferReader overrun** during connection — a
  NetworkBehaviour layout no longer byte-aligns.
- **RPCs silently dropped** ("no handler for method-id …") — an RPC signature changed.
- The server-side **mods fail to load** after a Puck update (Puck's plugin API changed).

## Prerequisites

- **A decompile of the new Puck assembly.** Use any .NET decompiler (e.g. ILSpy /
  `ilspycmd`) on `<PUCK_INSTALL>\Puck_Data\Managed\Puck.dll` (and
  `Unity.Netcode.Runtime.dll` when chasing NGO internals). Keep it at your
  `<PUCK_DECOMPILE>` reference location — see `MISSION.md`.
- **`ConfigCaptureMod` built and deployed** to a dedicated server running the new build
  (see `SERVER_SETUP.md` for plugin deployment).
- **The new Puck install** for `PUCK_MANAGED`, so you can rebuild the mods and BotHost
  (see `BUILD.md`).

## Step 1 — Capture ground truth with ConfigCaptureMod

`ConfigCaptureMod` (`ConfigCaptureMod/Plugin.cs`) dumps everything the bots need, via
`Debug.Log` lines prefixed `[ConfigCaptureMod]`, on server boot. Deploy it, launch the
server, and collect these sections from the log:

| Log marker | What it gives you |
|------------|-------------------|
| `=== NetworkConfig HASH ===` → `GetConfig(): 0x…` | the target hash the bot must reproduce |
| `=== HASH INPUTS ===` (incl. `ProtocolVersion: N`) | the build's protocol version + the other hash inputs |
| `=== PREFAB LIST … ===` (`[i] hash=… name='…' NetworkBehaviours=[…]`) | every networked prefab: hash, name, NB list |
| `[NB-LAYOUT] … hash=…|count=N|nbs=[Type(nvCount),…]` | the exact, ordered NB layout per prefab (incl. duplicates) |
| `=== SPAWNED NETWORK OBJECTS … ===` | scene-placed singletons: hashes + ordered NB signatures |

Save the full dump — it's the source for every constant below.

## Step 2 — Update the per-build constants

All of these live in `BotHost/Assets/Scripts/`. Update each from the capture, then
recompile.

| Captured value | Update this symbol | File |
|----------------|--------------------|------|
| `ProtocolVersion: N` | `PuckProtocolVersion` | `BotInstance.cs` |
| `PREFAB LIST` entries (hash + name + NB list) | `PrefabRegistrar.Prefabs` (`Entry { Hash, Name, BehaviourTypes }`) | `PrefabRegistrar.cs` |
| `SPAWNED NETWORK OBJECTS` + `[NB-LAYOUT]` (scene singletons) | `ScenePlacedRegistrar.Entries` (`Entry { Hash, Name, NbTypes }`) | `ScenePlacedRegistrar.cs` |
| `GetConfig(): 0x…` | the "target hash" sanity-check comment | `BotInstance.cs` |

Notes:

- **Prefab/scene NB lists must match the capture exactly** — same *count*, same
  *declaration order*, and **keep duplicates** (e.g. repeated audio NBs). NGO reads
  sync bytes per-NetworkBehaviour in order; a missing or reordered entry misaligns the
  buffer and the connection aborts. Do **not** `.Distinct()` these lists.
- **Scene hashes are mostly auto-discovered.** `BotInstance.cs` pre-populates a small
  fallback list (`KnownPuckSceneHashes`), but unknown scene hashes are added on the fly
  (`EnsureSceneHashIfMissing`). You usually don't need to touch this; if a brand-new
  scene hash trips an error before auto-recovery, add it to the fallback list.

## Step 3 — Re-transcribe changed mirrors (from the decompile)

The bot does **not** reference `Puck.dll`; it hand-transcribes the Puck
NetworkBehaviour types it needs into `BotHost/Assets/Scripts/Mirror/`
(`MirrorPlayer.cs`, `MirrorOthers.cs`, and the enums/structs in `PuckEnums.cs`). When
Puck changes one of these classes, update the matching mirror from the decompile.

**Invariants — violating any of these silently breaks deserialization:**

1. **NetworkVariable declaration order** must match the Puck class exactly.
2. **Each `NetworkVariable<T>`'s generic `T` must match exactly**, including integer
   width (e.g. a field changing `int` → `ulong` changes the wire size).
3. **Enum underlying type and ordinal values** must match (Puck has reordered enums and
   prepended `None=0` between builds — that shifts every following value).
4. **NV count per class** must match; the class *name* does not matter (NGO binds by
   order, not name).

Each mirror file has a header comment pointing at the decompile source file + line range
it was transcribed from — update that pointer when you re-transcribe.

## Step 4 — Rebuild the server-side mods

Puck's plugin API can change across builds (e.g. the `IPuckMod` → `IPuckPlugin` interface
rename). After updating, rebuild the mods against the **new** Puck DLLs and fix any
compile breaks:

```powershell
$env:PUCK_MANAGED = "<...>\Puck\Puck_Data\Managed"
dotnet build BotAuthBypassMod\BotAuthBypassMod.csproj -c Release
dotnet build ConfigCaptureMod\ConfigCaptureMod.csproj -c Release
dotnet build TelemetryMod\TelemetryMod.csproj         -c Release
```

## Step 5 — Watch the NGO patch

There is a **load-bearing local patch** to Netcode for GameObjects: Puck serializes the
NB-sync size prefix as a `ushort` where stock NGO uses a wider type, so the embedded
package `BotHost/Packages/com.unity.netcode.gameobjects/` (`Runtime/Core/NetworkObject.cs`)
is patched to match. This patch is tracked and ships with the repo. **If you ever
re-resolve or upgrade NGO, re-apply it** — without it, scene-sync overruns and every bot
fails to connect.

## Step 6 — Validate

1. Recopy the Harmony DLLs if Puck updated them (`copy_dlls.ps1`) and rebuild BotHost
   (see `BUILD.md`).
2. Run **one** bot against the new server. Confirm the bot's logged computed
   `NetworkConfig` hash **equals** the server's `GetConfig()` from Step 1. That match is
   the definitive "constants are correct" signal.
3. Run a **4-bot** smoke (`playbooks/smoke_12bots_30s.json --bots 4`). Watch for
   `[NB-LAYOUT]` mismatches or FastBufferReader overflow — those mean a prefab/scene NB
   list is still wrong.
4. Scale up once it's clean.

## Worked example — what changed across builds

A realistic picture of what an update touches (full detail in
`docs/dev-notes/NOTES_null_bot_progress.md`):

- **B202 → B323** (a large refactor): `ProtocolVersion` 202 → 323; the `Player` class
  collapsed **36 flat NetworkVariables into 14** (state/team/role merged into a struct,
  ~23 cosmetic strings collapsed into one customization struct); several **enums
  reordered** (e.g. `PlayerTeam`, `PlayerHandedness` gained `None=0`); a `Ping` field
  changed `int` → `ulong`; **RPCs changed** from direct setters to request/state-machine
  RPCs (new method-ids); **prefab set** changed (cameras added/renamed); and the plugin
  interface was renamed `IPuckMod` → `IPuckPlugin`. The connection-approval flow changed
  enough that **`BotAuthBypassMod`** became required (it intercepts bot SteamIds server-side
  instead of going through the backend).
- **B323 → B897** (small): `ProtocolVersion` 323 → 897 with an otherwise-identical prefab
  list — effectively a one-line constant bump plus a re-capture to confirm nothing else moved.

The lesson: most updates are a re-capture + a constant bump; occasionally Puck refactors a
networked class and you re-transcribe the affected mirror. The procedure is the same either
way — capture, update, match the hash, smoke test.

## Reference

- **Deep history / blow-by-blow:** `docs/dev-notes/` (esp. `NOTES_null_bot_progress.md`,
  `NOTES_prefab_mapping.md`, `NOTES_scene_objects.md`, `NOTES_input_rpcs.md`).
- **Decompile reference dirs:** `<PUCK_DECOMPILE>` (game) and `<PUCK_MODS_DECOMPILE>`
  (workshop mods, for interaction patterns) — see `MISSION.md`.
- **Server/plugin deployment + the protocol-match caveat:** `SERVER_SETUP.md`.
- **Build/toolchain:** `BUILD.md`.
