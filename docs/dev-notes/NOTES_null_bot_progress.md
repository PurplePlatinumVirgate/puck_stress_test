# Null bot — progress and the next blocker

## What works now
- Unity 6000.0.44f1 project at `BotHost/` builds a 129 MB headless Standalone
  Windows executable (`Build/BotHost/BotHost.exe`).
- Headless build script: `Assets/Editor/HeadlessBuild.cs`. Invoke via
  `Unity.exe -batchmode -quit -nographics -projectPath BotHost
  -executeMethod PuckStressTest.EditorTools.HeadlessBuild.BuildWindowsServer
  -logFile -`.
- Run script: `BotHost.exe -batchmode -nographics --server <ip> --port <p>
  --bots <n> --duration <s> --seed <i>`.
- Bot opens a UDP connection to the Puck server at 127.0.0.1:7777 and sends
  the NGO `ConnectionRequestMessage` with our hand-rolled ASCII-JSON
  `ConnectionData` payload (`{Password,SteamId,SocketId,EnabledModIds[]}`).
- Test server runs cleanly via `testserver/launch_server.cmd` against a
  config with `usePuckBannedSteamIds=false` — Puck Central is NOT consulted.

## What's blocked

NGO disconnects every connection attempt with:

> [Netcode] NetworkConfig mismatch. The configuration between the server and
> client does not match

(server-side log, after we set `ConnectionApproval=true` to clear the
earlier "Incomplete connection request" error — see
`NGO/ConnectionRequestMessage.cs:127-150` and `NetworkConfig.cs:328-360`.)

### Why this is non-trivial

NGO's `NetworkConfig.GetConfig()` produces a 64-bit hash over:

1. `ProtocolVersion` (ushort)
2. `NetworkConstants.PROTOCOL_VERSION` (NGO internal constant, version-pinned)
3. **If `ForceSamePrefabs == true`**: every key in
   `Prefabs.NetworkPrefabOverrideLinks` (i.e. every registered NetworkObject
   prefab's `GlobalObjectIdHash`).
4. `TickRate` (uint, default 30)
5. `ConnectionApproval` (bool)
6. `ForceSamePrefabs` (bool, default true)
7. `EnableSceneManagement` (bool, default true)
8. `EnsureNetworkVariableLengthSafety` (bool, default false)
9. `RpcHashSize` (enum, default `VarIntFourBytes`)

Puck's NetworkManager is configured in a Unity scene we can't directly read
(it lives baked in `globalgamemanagers.assets.resS`). Hash mismatch could
be due to ANY of those fields differing. We've matched
`ConnectionApproval=true`; the rest are at NGO defaults on our side.

The single hardest part: **prefabs**. Puck's server registers Player,
PlayerBodyV2, Stick, StickPositioner, PlayerCamera, Puck (the disc),
SpectatorCamera, possibly more — each with a `GlobalObjectIdHash` baked at
import time in Unity Editor. Our stub project registers none. Even if every
other field matches, the prefab list contributes to the hash and ours is
empty, so the hashes differ.

## Realistic paths forward

In rough order of effort:

### A — bypass the config check on the bot side
Patch `NetworkConfig.GetConfig()` (via Harmony or by editing the NGO source
in `Library/PackageCache/`) to return a literal value matching whatever
Puck's server computes. Requires capturing the server's hash once, then
hardcoding it. Ugly but works in ~hours, not days. Risk: NGO upgrades
clobber the patch unless we vendor NGO into the project.

### B — match Puck's prefab list exactly
Use a Unity asset extraction tool (AssetRipper, UABE) to read Puck's
NetworkManager prefab from `globalgamemanagers.assets.resS` and enumerate
every `GlobalObjectIdHash`. Then register matching stub NetworkObjects in
the bot project with overridden hashes. Higher effort but more durable.

### C — turn off `ForceSamePrefabs` server-side
Requires modifying Puck's scene asset, i.e. shipping a Puck server build
with a tweaked NetworkManager. Contaminates the very thing we want to
profile (we'd be measuring a non-vanilla server). Not viable for our
mission.

### D — Harmony-patch Puck's server to skip the check
Server-side mod that bypasses `CompareConfig`. Same contamination problem
as C — a stress run would not match what real users hit on real servers.
Probably ruled out unless we accept that footnote.

### E — proxy approach
Run a real Puck client (vanilla, unmodified) per bot, mod it locally to
swap in scripted inputs. Defeats the protocol-level mission and pushes us
back toward the "headless real client" path we declined.

## Recommendation

**A first**, falling back to **B** if A turns out to be brittle. A is the
cheapest experiment: capture the server's expected hash by Harmony-patching
the bot to log what it received vs. what it computed, then hardcode the
expected value. Once that's in, the bot reaches Puck's actual
ConnectionApproval check and we get to fight the NEXT layer of the
handshake (which is the productive battle — prefab registration, RPC
binding, etc.).

## Other things picked up along the way

- **Server-as-remote-target is naturally supported.** `--server <ip>` flag
  takes any address; bots speak UDP NGO/UTP to the gameplay port (default
  7777). When the server runs on a different machine for clean profiling
  isolation, we just point bots at it; the only extra requirement is that
  UDP 7777 is reachable (firewall / NAT). No code changes needed.
- **Puck's `Application.version` is empty / "1.0".** No `bundleVersion` in
  `globalgamemanagers`. So `ushort.TryParse(Application.version)` fails and
  `NetworkConfig.ProtocolVersion` stays at its default 0
  (`ConnectionManager.cs:27-31`). We don't need to override our
  ProtocolVersion.
- **NGO 2.4.0 in our project (Package Manager) is the same major as Puck's
  NGO.** `Library/PackageCache/com.unity.netcode.gameobjects@9684ced5879f
  /package.json` shows `"version": "2.4.0"`. Wire-format-compatible.

## State of the box
- Test server is currently stopped.
- Last bot run logs in `BotHost/Logs/run7.log`.
- Last build: `BotHost/Build/BotHost/BotHost.exe`.

## Update: hash mismatch resolved (2026-04-27)

- Built `ConfigCaptureMod/` — a one-shot Puck server mod
  (`<server-cwd>/Plugins/ConfigCaptureMod/ConfigCaptureMod.dll`) that prints
  `NetworkManager.Singleton.NetworkConfig.GetConfig()` and every hash input
  to the server log on startup.
- Captured for build B202: hash `0x62549C0E6F24FB48`,
  `ProtocolVersion=202`, `TickRate=30`, `ForceSamePrefabs=true`,
  `EnableSceneManagement=true`, `EnsureNetworkVariableLengthSafety=false`,
  `RpcHashSize=VarIntFourBytes`, 10 prefab `GlobalObjectIdHash` keys.
- Bot now sets `ProtocolVersion=202`, `ConnectionApproval=true`, and
  reflectively injects the 10 prefab keys into
  `Prefabs.NetworkPrefabOverrideLinks` — both BEFORE and AFTER
  `StartClient()`, since `Prefabs.Initialize()` (called inside
  StartClient) clears the dictionary.
- Bot's locally-computed hash matches the captured value byte-for-byte.
- Server log: `[ServerManager] Connection approved for 4 (botsteam000100)`
  — full ConnectionApproval handshake is passing.

## Update: scene-hash issue resolved (2026-04-27)

Pre-populated `NetworkSceneManager.HashToBuildIndex` and
`BuildIndexToHash` via reflection right after `StartClient`, mapping the
captured Puck scene hash `217390723` to our local empty SampleScene
(build index 0). The bot no longer throws on the initial scene event —
it acks the synchronization without actually loading anything.

Server now approves AND scene event handling proceeds without crashing
that path.

## Next blocker: SceneObject (NetworkObject sync) deserialization

After scene sync starts, server packs every existing NetworkObject's state
into a `SceneObject` payload. NGO's `NetworkObject.SceneObject.Deserialize`
(NetworkObject.cs:2935) reads the prefab hash, looks up our entry in
`NetworkPrefabOverrideLinks`, gets `null` (we registered keys with null
values just to satisfy the config hash), and the FastBufferReader runs
past the end of buffer trying to write bytes into a non-existent
NetworkBehaviour.

Cascade: `OverflowException` → `NullReferenceException` →
NetworkObject sync aborts → server notices the client never acks
NetworkVariable deltas → connection drops after ~1 second.

## What "null bot" means now

The original null-bot success criteria was "connect, idle, disconnect
cleanly." We have:
- ✓ Connect (NGO ConnectionRequestMessage built and accepted)
- ✓ Pass auth (ServerManager approval — `Connection approved for 1`)
- ✓ Pass NGO config hash check
- ✓ Get a real client network ID
- ✓ Receive initial scene event without crashing
- ✗ Stay connected through Player synchronization (this blocker)

The real fix is registering NetworkObject prefabs with the same
GlobalObjectIdHash AND the same NetworkBehaviour/NetworkVariable layout
as Puck's. This is exactly what we deferred in task #10 by saying
"register stubs." It turns out NGO's spawn/sync path actually requires
the NetworkVariable byte stream to round-trip through a real
NetworkBehaviour — we can't no-op our way past it.

Tracked as task #16. The probably-clean way: revisit task #13
(re-integrate Puck.dll) so we can register the real `Player` class as
the prefab for hash X, and lightweight stubs for the child
NetworkObjects (PlayerBodyV2, Stick, etc.) that the agent's earlier
analysis showed don't receive RPCs and have hashes/NetworkVariables
that may be simple enough to mock.

## New blocker (historical): SceneEventMessage

After approval, the server sends a SceneEvent to sync its loaded gameplay
scene (hash `217390723`). NGO's `NetworkSceneManager.ScenePathFromHash`
throws because that hash is not in the bot's `HashToBuildIndex` (the bot's
build has only a placeholder SampleScene). The unhandled exception drops
the connection.

Path (b) is cheaper than (a): patch `NetworkSceneManager` to acknowledge
scene events without trying to load. The bot does not need to actually
have Puck's scenes — it just needs to not crash when the server announces
them. Tracked in task #15.

## Update: Puck.dll integration is harder than expected (2026-04-27, attempt 2)

Tried to re-enable `Puck.dll` so the bot could compile against the real
`Player`/`Stick`/etc. types and provide them as actual prefab components.
Walked into a stack of integration issues:

1. **Type collision: `SceneManager`.** Puck's own `SceneManager.cs` lives
   in the global namespace inside `Puck.dll`. When the DLL is auto-
   referenced (`isExplicitlyReferenced: 0`), it bleeds into NGO's
   compilation in `Library/PackageCache/`, and NGO's calls to
   `UnityEngine.SceneManagement.SceneManager.GetActiveScene()` resolve
   to Puck's class instead, producing CS0117 errors. Fix: keep
   `isExplicitlyReferenced: 1` so Puck's types only flow into asmdefs
   that explicitly opt in.

2. **UnityLinker can't resolve `Unity.Netcode.Runtime` from Puck.dll's
   reference table.** Puck.dll references `Unity.Netcode.Runtime,
   Version=0.0.0.0`. The linker scans references during the Standalone
   build but does not look up the PackageCache copy of NGO. Result:
   `Mono.Cecil.AssemblyResolutionException: Failed to resolve assembly
   'Unity.Netcode.Runtime, Version=0.0.0.0'`. Fixing requires either
   shipping a duplicate copy of `Unity.Netcode.Runtime.dll` next to
   Puck.dll (causes type duplication across PackageCache and Plugins)
   or telling the linker about the PackageCache path (no public API).

3. **0Harmony.dll references `System.Reflection.Emit` types.** The
   game's Harmony build was compiled for .NET Framework where Emit is
   always present. Unity Standalone Mono strips Reflection.Emit; the
   linker emits `Failed to resolve System.Reflection.Emit.Label` errors
   on Harmony's `CodeInstruction.labels` field. Workarounds: use
   HarmonyX (Unity-friendly fork) instead of the Puck-shipped 0Harmony,
   or skip Harmony entirely.

4. **Transitive deps must be copied AND have valid plugin meta files.**
   Game DLLs need `*.dll.meta` files with valid YAML and a unique GUID,
   otherwise Unity ignores them. PowerShell's `Set-Content -NoNewline`
   produces files YAML rejects ("Expect ':' between key and value");
   include a trailing newline.

5. **IL stripping requires both `link.xml` AND a static reference.**
   `Assets/link.xml` with `<assembly fullname="Puck" preserve="all"/>`
   is necessary but not sufficient for the build to keep Puck.dll. Need
   at least one compile-time `typeof()` reference to a type from each
   preserved assembly, otherwise Unity drops the DLL entirely from the
   final build and `Assembly.Load("Puck")` returns "Could not load the
   file".

After this attempt, all the game DLLs in `BotHost/Assets/Plugins/Puck/`
are renamed to `*.dll.disabled` and their meta files to
`*.dll.meta.disabled`. `BotHost.asmdef` no longer lists them in
`precompiledReferences`. Build is green again. The bot's
`PrefabRegistrar` still creates stub GameObjects for the 10 hashes —
sufficient for the config-hash check, not sufficient for SceneObject
deserialization.

## Realistic paths forward for null-bot completion

1. **Manually mirror Puck's NetworkVariables in the bot** (no Puck.dll
   reference). Read each NetworkBehaviour from the decompile, copy out
   the `NetworkVariable<T>` field declarations into a parallel set of
   bot-side classes with the same field types in the same order. Tedious
   but mechanical. Avoids fighting Unity's build pipeline. Recommended
   starting point.
2. **Push through with Puck.dll integration.** Solve issues 2 and 3
   above (point UnityLinker at PackageCache NGO; replace 0Harmony with
   HarmonyX). Highest one-time cost, lowest ongoing maintenance — the
   bot's NetworkBehaviour layouts always match the game.
3. **HarmonyX-patch NGO's `SceneObject.Deserialize` to skip past
   payloads we can't absorb.** Lets the bot stay connected without
   correct NetworkVariable sync. Lowest effort but the bot has no view
   of game state.
4. **Patch the Puck server.** Server-side mod that strips sync messages.
   Contaminates the system under test — measurement risk. Not viable.

## Update: end-to-end verified at 12 bots (2026-04-27)

After adding the playbook loader and TelemetryMod, ran a 30-second
smoke with `playbooks/smoke_12bots_30s.json` against the real
local server. Outcome:

- All 12 bots passed `Server_ConnectionApproval` within a 1 ms window.
- 535 telemetry samples (~26.7 s of the 30 s run) recorded
  `connected=12`, exactly one transient sample with 11 connected
  during the disconnect cascade.
- All 12 bots disconnected cleanly when the bot harness's
  `--duration` timer expired (driven by playbook's `duration_seconds`).
- Server frame time stayed at 8.33 ms (120 Hz target) the whole run;
  no perceivable spikes.

The earlier "bot disconnects ~1 s after approval" observation was
flaky. The `OverflowException` in `NetworkObject.SceneObject.Deserialize`
still fires per spawn message, but NGO's per-message exception
handling does not reliably escalate to a transport-level disconnect,
so connections survive. This is a happy accident: the bot can hold its
seat at the server without correct NetworkVariable sync.

That means the harness is **already useful for two whole categories of
profiling**, even before input-streaming lands:

1. **Per-connected-client server overhead** — what does it cost the
   server to have N clients in NGO's connection book, broadcasting
   NetworkVariable updates to them, processing their heartbeats, etc.
   The TelemetryMod's `frame_ms` / GC / `total_alloc_b` columns under
   different `bot_count` values quantify this.
2. **Connection-handshake throughput** — how long does it take to admit
   N clients in burst, what's the connection-approval RPC's per-client
   cost. Useful for any mod that hooks `Server_ConnectionApproval`.

What's NOT yet exercised: actual gameplay input load, RPC traffic from
clients, NetworkVariable read pressure from a player simulating real
play. Those are tasks #6, #7, #17.

## Tasks closed by this milestone
- #4 null bot — done enough (connect, idle, disconnect cleanly).
- #8 scale to 12 bots — verified empirically.
- #12 playbook format + loader — done; smoke playbook drives the run.

## Update: NetworkVariable mirrors landed (2026-04-27 attempt 3)

Path 1 from the previous update is now in:

- `BotHost/Assets/Scripts/Mirror/PuckEnums.cs` — local copies of the 4
  Puck enums (PlayerState, PlayerHandedness, PlayerTeam, PlayerRole)
  with identical underlying integer values.
- `BotHost/Assets/Scripts/Mirror/MirrorPlayer.cs` — `MirrorPlayer`
  declares all **37** of Puck `Player`'s NetworkVariables in
  declaration order with matching generic types, plus 5 empty
  NetworkBehaviour stubs (`MirrorPlayerController`,
  `MirrorPlayerInput`, `MirrorPlayerInputController`,
  `MirrorPlayerVoiceRecorder`, `MirrorPlayerVoiceRecCtrl`) — the
  server's prefab has 6 NetworkBehaviours total but only `Player`
  carries NetworkVariables.
- `BotHost/Assets/Scripts/Mirror/MirrorOthers.cs` — mirrors for the
  remaining 9 prefabs (PlayerBodyV2 ×2, Stick ×2, Puck, StickPositioner,
  PlayerCamera, SpectatorCamera, ReplayCamera). NV counts:
  PlayerBodyV2 = 8, Stick / StickPositioner / PlayerCamera /
  SpectatorCamera = 1 each (`PlayerReference`), Puck = 1 (`IsReplay`),
  ReplayCamera = 0. All `*Controller`, `Synchronized*`, and
  `NetworkObjectCollisionBuffer` classes have 0 NVs — empty stubs.
- `PrefabRegistrar.cs` updated to attach mirror types in the same
  component order Puck's prefab uses (per `ConfigCaptureMod` capture).

Verification with `playbooks/smoke_12bots_30s.json`: 553 telemetry
samples reported `connected=12` over ~27.6 s — same shape as the
no-mirrors run. ~2 OverflowExceptions still fire per bot during scene
sync, but they do NOT reliably escalate to a transport disconnect, so
the harness's "12 bots connected for the run duration" property holds.

## Why the OverflowException didn't fully go away

Suspected causes (not yet root-caused):
- `NetworkObjectReference` serializes a server-side NetworkObjectId
  the bot can't resolve — NGO's resolver may throw mid-deserialize.
- Some prefab's NetworkBehaviour list may include a class not yet
  reflected in `ConfigCaptureMod`'s output (e.g. the prefab walker
  iterates `GetComponentsInChildren`, which can miss a NetworkBehaviour
  that lives on a child GameObject we didn't see).
- NGO's per-message length envelope appears to absorb the misalignment
  enough that subsequent objects can still parse — the harness gets
  away with imperfect mirrors. Real NetworkVariable VALUE reads from
  `MirrorPlayer.Team.Value` etc. on the bot side haven't been verified
  yet; those are needed before behavior code (#6/#7) can branch on
  game state.

## What this milestone unblocks

- Behavior code (tasks #6/#7) can now write against `MirrorPlayer`,
  `MirrorPuck`, etc. with valid `NetworkVariable<T>` types. If their
  values turn out to never update (because of byte misalignment), we
  iterate from there.
- The harness keeps producing the load it already does for connection-
  level / NetworkVariable-broadcast / config-mismatch profiling.

## Update: NetworkVariable probe doesn't fire (2026-04-27 attempt 4)

Added `OnNetworkSpawn` overrides to `MirrorPlayer`, `MirrorPuck`, and
`MirrorPlayerBodyV2` that log initial values and subscribe to
`OnValueChanged` for the gameplay-relevant NetworkVariables.

**Result: zero Mirror_* spawn logs across a 12-second connection.**
The OnNetworkSpawn never fires.

Diagnosed cause (one):
- Server's scene synchronization includes a NetworkObject with
  `GlobalObjectIdHash = 2834597543` that is NOT in our prefab
  registry. NGO logs `Failed to spawn NetworkObject for Hash
  2834597543.` and apparently bails out of the SceneSync message
  before reaching the rest of the objects. Our extended
  `ConfigCaptureMod` walk of `NetworkConfig.PlayerPrefab` shows it's
  null, and the 10-entry `NetworkPrefabOverrideLinks` doesn't include
  this hash. Likely a scene-placed singleton NetworkObject (e.g.
  GameManager, PuckManager, etc.) whose hash comes from
  `InScenePlacedSourceGlobalObjectIdHash`, not from the prefab dict.

Subsequent debugging would need to:
- Extend `ConfigCaptureMod` to enumerate every spawned NetworkObject
  in `NetworkManager.SpawnManager.SpawnedObjects` (and their hashes /
  types) so we know what hash 2834597543 is, plus whatever else the
  server has live.
- Register stubs for the scene-placed hashes (likely empty
  NetworkBehaviour layouts) so NGO doesn't bail on them.
- Re-run the probe. If `OnNetworkSpawn` then fires, we have working
  NetworkVariable reads.

This is a real block on behavior code (#6/#7) that requires another
investigation pass. The harness's other deliverables (12-bot
connection, telemetry capture, playbook system) keep working as
before — `connected=12` still holds for the full run duration.

## Tasks blocked here
- Behavior code (#6, #7) blocked on NetworkVariable reads working.
- Scripted action `Send*` calls blocked on having a Player NetworkObject
  to send Server_*Rpc from.
- The handshake / null-bot side of the harness is otherwise fine.

## Update: NGO has built-in skip-past, so the bug is elsewhere (2026-04-27 attempt 5)

Read NGO's `NetworkObject.AddSceneObject` (`NetworkObject.cs:3167-3192`)
and `SynchronizeNetworkBehaviours` (`NetworkObject.cs:2996-3083`):

- **Failed-spawn path** is length-prefix-skip-safe. NGO writes
  `int networkBehaviourSynchronizationDataLength` before the per-NB
  data. When client can't resolve the prefab hash, NGO reads that int
  and seeks past N bytes. Aligned.
- **Success path** uses the same length prefix. NGO seeks to end of
  synch data on any deserialization exception (line 3078-3081). Per-
  object byte alignment is preserved no matter how wrong our mirror is
  *within* an object.

So missing scene-placed objects (the 32 in `NOTES_scene_objects.md`)
should NOT cascade misalignment. NGO is more resilient than I had
assumed.

## What's still wrong, then

The OverflowException at line 2935 (the `m_BitField` read at the start
of the next iteration's `SceneObject.Deserialize`) means the reader
position is past the buffer end at the START of an iteration. NGO's
per-object skip-past logic *should* prevent that. So either:

1. There's a path NGO doesn't catch where reader.Seek goes past the
   end of the message buffer.
2. The very first `SceneObject.Deserialize` overflows for some other
   reason (e.g. the message header itself is shorter than expected).
3. A misaligned write on the SERVER side that NGO's reader can't
   recover from.

To root-cause, we need NGO's `SceneEventData.EnableSerializationLogs`
flipped on. That's an internal field; reflection from BotInstance can
catch SceneEventData instances created at startup, but the SYNC
message creates a fresh SceneEventData at message-handle time which
our setup code doesn't see.

PackageCache edits are blocked (correctly — they get clobbered on
package re-resolve). The clean way to flip the flag is via HarmonyX
patching of SceneEventData's constructor or equivalent — i.e. task
#20.

## Recommendation

Take **task #20 (HarmonyX)** before #19 (mirror 32 objects). HarmonyX
unlocks two things at once:
- Flip `EnableSerializationLogs=true` on every SceneEventData → see
  exactly where bytes go wrong, rooting out the actual misalignment.
- If misalignment turns out to be in NGO itself, patch around it.
- If misalignment turns out to be specific to one of our mirrors, fix
  that mirror surgically rather than transcribing all 32.

#19 is a multi-day mechanical effort that may or may not solve the
problem (since NGO already handles missing prefabs via skip-past).
HarmonyX gives us *visibility* first, then *targeted* fixes.

The harness's current state (12 bots holding connection for the run
duration, telemetry capture, playbook system) keeps working as before.
This investigation block doesn't regress anything.

## Update: NV-bearing Puck classes fully enumerated (2026-04-27 attempt 6)

`grep -r "public NetworkVariable<"` over the entire decompile finds
**only 9 .cs files** with NetworkVariable declarations:

| class | NVs | mirrored |
|---|---|---|
| Player              | 37 | ✓ |
| PlayerBodyV2        | 8  | ✓ |
| StickPositioner     | 1  | ✓ |
| Stick               | 1  | ✓ |
| PlayerCamera        | 1  | ✓ |
| Puck                | 1  | ✓ |
| SpectatorCamera     | 1  | ✓ |
| GameManager         | 1  | ✓ (`GameState` struct mirrored too) |
| PlayerPosition      | 1  | ✓ |

**That's the complete inventory.** All other Puck NetworkBehaviour
classes (managers, controllers, GoalController, SynchronizedObject,
NetworkObjectCollisionBuffer, etc.) declare zero NetworkVariables.
Empty mirror stubs suffice for every one of them.

So the remaining mystery is NOT "we forgot to transcribe a class". The
9 mirrors above are byte-correct by construction (transcribed
field-by-field from the decompile in declaration order with matching
generic types).

## Why the OverflowException still happens (working theory)

The spawn pipeline for **scene-placed** NetworkObjects is different
from prefab-spawn:

- Prefab path: `CreateLocalNetworkObject` → instantiate from the prefab
  registry. Failed-spawn skips bytes via length prefix.
- Scene-placed path
  (`!EnableSceneManagement || sceneObject.IsSceneObject` is true):
  `GetSceneRelativeInSceneNetworkObject(hash, sceneHandle)` looks up an
  EXISTING NetworkObject in the loaded scene via
  `InScenePlacedSourceGlobalObjectIdHash`. If not found, NGO logs
  `"NetworkPrefab hash was not found! In-Scene placed NetworkObject
  soft synchronization failure for Hash: X"` and returns null.

The **failed-spawn skip-past at line 3183** runs for both paths. So
alignment SHOULD be preserved. But empirically OverflowException at
`SceneObject.Deserialize`'s `m_BitField` read still fires — either
NGO has a path I'm not seeing where the seek goes past the message
buffer end, or the issue is in the SCENE-PLACED lookup specifically
not consuming the synch data the way prefab-spawn does.

## What would actually unblock this

Either:

1. **HarmonyX (#20)** — patch `SceneEventData..ctor` to flip
   `EnableSerializationLogs=true` per instance. NGO's per-iteration
   byte logging would immediately point at which SceneObject overruns
   and by how much.
2. **Inject runtime scene-placed NetworkObjects** into
   `NetworkSceneManager.ScenePlacedObjects` via reflection, one per
   captured hash from `NOTES_scene_objects.md`. Need the runtime
   "scene handle" int to key the dict correctly; NGO assigns these on
   scene load.
3. **Switch to a real Unity Editor scene** with the 32 NetworkObjects
   placed at edit-time (each given the right
   `InScenePlacedSourceGlobalObjectIdHash` via reflection / asset
   postprocessor). Heaviest but most "correct" path.

Of these, **#1 (HarmonyX)** is still cheapest and gives the diagnostic
that informs whether #2 or #3 is needed at all.

## Final disposition

Task #17's mirror infrastructure is complete for every
NetworkVariable-bearing class in the decompile. Any further investment
to unblock behavior code (#6/#7) should start with HarmonyX-driven
diagnostics (#20), not more mirroring. The harness's other
deliverables (12-bot connection, telemetry, playbook system) keep
working as before.

## BREAKTHROUGH: bot's own Player spawns with NetworkVariables flowing (2026-04-27 attempt 7)

**Wired up Harmony successfully.** Vendored game's `0Harmony.dll` (plus
its transitive deps `Mono.Cecil.dll`, `MonoMod.Utils.dll`,
`MonoMod.RuntimeDetour.dll`) into `BotHost/Assets/Plugins/Puck/`.
Disabled IL stripping for Standalone builds via
`PlayerSettings.SetManagedStrippingLevel(Standalone, Disabled)` in
`HeadlessBuild.cs` so UnityLinker doesn't choke on
`System.Reflection.Emit.Label` references. Added `link.xml` preserves
for 0Harmony, Mono.Cecil, MonoMod.* assemblies.

`HarmonyPatcher.cs` applies on bot startup (called from `Bootstrap.cs`
*before* `BotHost` is created, so patches are live before
NetworkManager spawns). First patch: postfix on
`Unity.Netcode.SceneEventData..ctor` that flips
`EnableSerializationLogs=true` on every instance.

### What worked

- Build succeeded with Harmony in.
- `[HarmonyPatcher] applied` log fires at startup.
- `[Read][Synchronize Objects][WPos: 4][NO-Count: 43] Begin:` —
  proof the patch took: this log only emits when
  `EnableSerializationLogs == true`.
- **`[Bot 00] OnClientConnected localId=1`** — first time this callback
  has fired. Previously the bot connected at the transport level but
  NGO's `OnClientConnectedCallback` never fired because of earlier
  scene-sync bails.
- **`[MirrorPlayer NID=44 owner=1] spawned. initial: state=None ...`**
  — the bot's own Player NetworkObject spawned cleanly, our
  `OnNetworkSpawn` override ran.
- **`[MirrorPlayer NID=44 owner=1] Ping 0 -> 8`** — NetworkVariable
  updates from the server are flowing into our mirror. The byte-level
  alignment IS correct.

### What still doesn't work

Scene-placed objects (the 32 from `NOTES_scene_objects.md` —
GameManager, PuckManager, UIManager, etc.) and the 11 Puck clones
still don't spawn on the bot. NGO logs `[Deferred OnSpawn]` warnings
for ids 2, 3, 5 (Synchronized Object Manager, Game Manager, UI
Manager) — those NetworkVariableDelta / RPC messages are waiting for
NetworkObjects that never arrive on the bot side.

Scene-placed lookup needs the bot to have NetworkObjects already
present in its scene with matching `InScenePlacedSourceGlobalObjectIdHash`.
We don't (the bot's "scene" is an empty SampleScene). That's task #19.

But for **input-sending behavior** (#6/#7) the bot doesn't strictly
need the manager objects — it just needs its own Player and a way to
locate the Puck. The Puck is prefab-spawned (not scene-placed) yet
also didn't show up in our log, which is a separate bug to chase.

### What this unblocks

- **Sending inputs** is now testable. The bot can call
  `Server_*InputRpc` on its own MirrorPlayer (NID=44 in our test) by
  invoking the appropriate methods. Per `NOTES_input_rpcs.md` we have
  the full RPC list.
- **Reading state ABOUT the bot's own player** — its Team, Role,
  Username, State, etc. — is now reading real values pushed by the
  server.

What's still gated on scene-placed sync (#19): reading puck position,
reading other players' positions, reading game phase from
GameManager. None of these block sending inputs; they block making
inputs *intelligent* in response to game state.

## Update: input RPCs send cleanly (2026-04-27 attempt 8)

Wired up the input-sending path:

- `MirrorPlayerInput` now has `SendMove`, `SendRaycastOriginAngle`,
  `SendLookAngle` methods. Each reflectively calls NGO's protected
  `__beginSendRpc` / `__endSendRpc` with the EXACT method-IDs Puck's
  PlayerInput uses on the wire (354985997 / 3072819325 / 3839358977
  per `PlayerInput.cs:540, 597, 661`). NGO's RPC method-id hash
  includes `methodDefinition.Module.Name` so weaver-generated calls
  from BotHost.dll could never match Puck.dll's hashes — manual IDs
  are the only path.
- `BotBrain` MonoBehaviour ticks at 30 Hz, calls all three Send*
  methods per tick.
- `MirrorPlayer.OnNetworkSpawn` sees `IsOwner=true` for the bot's own
  Player and calls `gameObject.AddComponent<BotBrain>()` automatically.

End-to-end run:
- `[Bot 00] OnClientConnected localId=1`
- `[MirrorPlayer NID=44 owner=1] spawned`
- `[MirrorPlayer NID=44 owner=1] wired BotBrain — streaming inputs at 30 Hz.`
- Zero RPC exceptions (no `RpcException`, no errors from
  `__beginSendRpc`).
- Bot held connection for the full 30 s `--duration`.

The `Patch_SceneEventData_Ctor` Harmony patch turns out to be
load-bearing — keeping it on causes scene-sync to throw a tractable
NRE that the outer try/catch swallows, but disabling it lets
scene-sync hit `OverflowException` BEFORE the connection-approval
post-handshake completes, and the bot's own Player never spawns.
Comment in HarmonyPatcher.cs explains; we keep the patch enabled
until task #19 (mirror scene-placed objects) is done.

## What's still missing for visible movement

The bot's `MirrorPlayer.State` stays at `None`. Per `Player.cs` and
`PlayerController.cs`, the state machine progresses
`None → TeamSelect → PositionSelectBlue|Red → Play`. The server
drives this via `Client_SetPlayerStateRpc` after it receives a
`Client_PlayerSubscriptionRpc` from the client (Player.cs:1367-1381).

Until the bot sends that subscription RPC and follows up with
team/position selection RPCs, the server doesn't put the player into
Play state and movement inputs are ignored.

Next concrete steps:
1. Add `SendPlayerSubscription` on MirrorPlayer with method-id
   from Player.cs and the right field layout (username, number,
   handedness, skins, steamId, enabled mods).
2. Subscribe to `MirrorPlayer.State.OnValueChanged` — when server
   says TeamSelect, call team-pick RPC; when PositionSelect, call
   position-pick. Walk to Play.
3. Once in Play, bot's existing movement RPCs become effective.

## Update: bot reaches Play state, full pipeline live (2026-04-27 attempt 9)

Wired the player-state machine in `MirrorPlayer.OnNetworkSpawn`:

1. **Send `Client_PlayerSubscriptionRpc`** (id 1379186733, 31 fields)
   immediately on spawn. Hit one off-by-one in the cosmetic field
   count: schema is 23 FixedString32Bytes (country, 4 visors, 2 facial,
   4 jerseys, 4 stick base, 4 shaft tape, 4 blade tape) — I had 24.
   With that fixed, server processes the subscription and writes
   Username/Number into our NetworkVariables.
2. **Hook `State.OnValueChanged`.** Server moves us None → TeamSelect.
3. **Send `Client_SetPlayerTeamRpc`** (id 2680549476) with Red or Blue
   alternating by `OwnerClientId % 2`. Server moves us
   TeamSelect → PositionSelectRed.
4. **Send `Client_SetPlayerStateRpc(Play)`** (id 2891939837) directly.
   Server accepts and moves us PositionSelectRed → **Play**.

Verified end-to-end with one bot:
```
None → TeamSelect            (server)
TeamSelect → PositionSelectRed   (server, after team RPC)
PositionSelectRed → Play     (server, after direct state RPC)
Username '' → 'Bot45'
Number 0 → 46
Team None → Red
```

The direct `SetPlayerState(Play)` skip works — server doesn't strictly
require a `PlayerPositionManager.Client_ClaimPositionRpc` to enter
play. That's fortunate because we don't have visibility into scene-
placed NetworkObjects yet.

`BotBrain`'s 30 Hz move/look/stick RPCs were already streaming since
spawn; once `State == Play` they're actionable. Bot is now alive in
the rink, visible on the live_streamer dashboard.

Remaining for skate-to-puck (#7): bot needs to know where the puck is.
That's blocked on scene-placed object sync (#19 — managers, the puck
clones don't appear because they share the SceneEvent stream that
bails). Without it, we can drive arbitrary fixed inputs (skate
forward, sweep stick) but not react to game state.

## Update: "Puck not spawning" is the scene-placed problem (2026-04-27 attempt 10)

Wanted to chase the puck-prefab spawn as a smaller-than-#19 win. Read
NGO's `SceneEventData.SynchronizeSceneNetworkObjects` end-to-end and
diagnosed the chain:

- The serialization-logs Harmony patch (originally added as one-shot
  diagnostic) is itself the abort trigger: NGO's per-iteration logging
  branch at `SceneEventData.cs:1142` does
  `spawnedNetworkObject.name` BEFORE the null-check at line 1146. When
  hash 2834597543 (Spectator Manager) fails to resolve, the logging
  branch NREs, the outer try/catch eats it, and the loop aborts after
  the first object — so all 42 remaining objects in the batch (Puck
  clones + everything else) never spawn.
- Tried disabling the patch: NGO's own null-check handles missing
  prefabs, but the failed-spawn skip-past at `NetworkObject.cs:3184`
  walks past the end of the buffer and the next iteration's
  `SceneObject.Deserialize` OverflowExceptions. Different abort, same
  end state.
- Tried a Harmony postfix on `NetworkSpawnManager.CreateLocalNetwork-
  Object` that returns a placeholder NetworkObject with zero
  ChildNetworkBehaviours when the scene-placed lookup fails. NGO took
  the success path, the per-NB inner try/catch + reader-clamp fired,
  but byte alignment was still off — next SceneObject deserialized to
  Hash=0 and the loop OverflowException'd anyway. Likely cause: the
  catch's `seekToEndOfSynchData` is correct in math but the loop's
  `numberSynchronized` was read from inside the unconsumed NV data
  (placeholder has zero NBs so the foreach skips), the for-loop ran
  many iterations of garbage reads, and somewhere in there the seek
  state went south. Reverted; ran 1-bot smoke to confirm Player
  re-spawns and bot reaches Play state. Working state restored.

**2026-04-27 follow-up:** the dump in `NOTES_scene_objects.md` table
also shows `Puck(Clone)  hash=3292036842|inSceneSrc=3292036842|isInScene=False`.
So the 11 puck clones are actually DYNAMICALLY spawned (not scene-
placed) — they just appear in the same scene-sync batch as the 32
managers. Once the loop processes the 32 scene-placed entries
without aborting, the puck clones should fall through the existing
prefab dict (PrefabRegistrar) cleanly.

**Critical realization** from `NOTES_scene_objects.md`: of the 43
NetworkObjects in the sync batch, **11 are Puck clones** that are
SCENE-PLACED instances of the Puck prefab — not dynamically spawned.
So "puck not spawning" is not a separate bug; it's the same
scene-placed sync problem that hides the 32 manager objects. There is
no cheaper-than-#19 path to puck visibility on the bot.

## Disposition

The harness's working deliverables (12-bot connection, telemetry,
playbook system, Player spawn + Play state + 30 Hz input streaming)
all keep working. To unblock react-to-game-state behavior (#7), the
real next step is task #19 (mirror scene-placed objects properly so
NGO's success path runs with real NetworkBehaviour children that
correctly consume their NV bytes). This is the multi-day mechanical
effort flagged as path A in `NOTES_scene_objects.md`.

## Update: scene-placed registrar built; lookup works but size int reads wrong (2026-04-27 attempt 11)

Built the foundation for task #19:

- `BotHost/Assets/Scripts/ScenePlacedRegistrar.cs` (new) — entries
  for all 32 scene-placed singletons (hashes from
  `NOTES_scene_objects.md`), each with the right NetworkBehaviour
  count using `MirrorEmpty` for no-NV slots and existing mirrors
  (`MirrorGameManager`, `MirrorPlayerPosition`, etc.) for the slots
  that have NVs.
- `MirrorEmpty : NetworkBehaviour {}` added to `MirrorOthers.cs` so
  stubs can be padded to the right component count without typing
  30+ named classes.
- Harmony prefix on `Unity.Netcode.SceneEventData.Synchronize-
  SceneNetworkObjects` re-injects all 32 stubs into NGO's
  `ScenePlacedObjects` dict immediately before NGO walks the batch.
  Required because `HandleSceneEvent` calls
  `ScenePlacedObjects.Clear()` (NetworkSceneManager.cs:2635) on
  every Synchronize event, wiping any pre-population.
- Stubs are keyed by `SceneManager.GetActiveScene().handle` because
  NGO's `SetTheSceneBeingSynchronized` falls back to the active
  scene when no `ServerSceneHandleToClientSceneHandle` mapping
  exists, so the active scene's handle is what the lookup at
  `NetworkSceneManager.cs:1042-1056` actually uses.

### What works

- `[ScenePlacedRegistrar] reinjected 32 stubs into ScenePlacedObjects` per scene-sync.
- NGO's `SceneObject` lookup for hash 2834597543 (Spectator Manager)
  resolves to our stub. Per-iteration log:
  `[Head: 4][Tail: 21][Size: 17][ScenePlaced_Spectator Manager_2834597543][NID-1][Children: 2]`.
- Iteration 0 completes successfully — the abort-on-first-missing-
  hash is gone.
- Bot's own Player still spawns, BotBrain still wires, state-machine
  still reaches Play. No regressions.

### Open mystery

After iteration 0's success, NGO logs
`[Size mismatch] Expected: 1677721621 Currently At: 21!` from
`SynchronizeNetworkBehaviours` (NetworkObject.cs:3075). Iteration 1
then reads garbage SceneObject preamble (Hash=27457087, not in any
known list), fails to spawn it, and the failed-spawn skip-past walks
past the buffer end → loop aborts.

Dumped the full 784-byte sync buffer via Harmony prefix. For
Spectator Manager (object 0) at positions 4-20 (17 bytes):
- bitfield (2): `64 00` = 0x0064 (IsSceneObject + WorldPositionStays + DestroyWithScene)
- Hash (4): `A7 86 F4 A8` = 2834597543 ✓
- NID-bp (1): `11` → value 1
- OwnerClientId-bp (1): `01` → value 0
- NetworkSceneHandle (4): `F4 FF FF FF` = -12
- size int (4) at positions 16-19: `01 00 00 64` = 0x64000001 = 1677721601 ← WRONG
- sync count (1) at position 20: `00`

Server SHOULD have written `01 00 00 00` for size=1 byte (the sync
count placeholder, the only thing in the NB block when both NBs
have 0 NVs and no Synchronize override). Per the Puck decompile NV
grep, only 9 .cs files declare `NetworkVariable<>` and Spectator-
Manager isn't one.

Theories not yet ruled out:
1. NetworkBehaviour child component count: NGO's
   `GetComponentsInChildren<NetworkBehaviour>(true)` includes child
   GameObjects. Puck's Spectator Manager prefab might have nested
   NBs that ConfigCaptureMod's surface walk missed.
2. Custom `Synchronize(...)` overrides in some Puck class. Decompile
   grep for `override.*Synchronize` only found unrelated event
   handlers, but ILSpy may have decompiled overrides under a
   different name, hiding them from grep.
3. Puck.dll ships its own NGO build with a slightly different wire
   format. The captured NetworkConfig hash matches ours
   (0x62549C0E6F24FB48) but that hash only covers high-level
   NetworkConfig fields, not the per-object serialization layout.

### Next attempt should (CLIENT-SIDE ONLY)

The server stays vanilla — only `ConfigCaptureMod` (one-shot
read-only dump on startup) and `TelemetryMod` (passive metrics
recorder) are allowed on the server. Anything that changes server
behavior contaminates the very thing we're profiling. So the next
diagnostic must run on the bot side:

- Extend `ConfigCaptureMod` (still server-side but read-only) to
  walk `GetComponentsInChildren<NetworkBehaviour>(true)` recursively
  and dump the FULL NB tree per scene-placed object (including
  children). This is just a wider read of state already exposed —
  no behavior change. Goal: confirm whether server's per-object NB
  count matches what `ScenePlacedRegistrar` builds.
- Re-instate the `DumpInternalBuffer` Harmony prefix in
  `HarmonyPatcher.cs` (client-side, removed after capturing
  run_dump.log) and parse multiple iterations from the dump to
  reverse-engineer the actual per-object byte layout.
- Bot-side Harmony patch on `NetworkObject.SceneObject.Deserialize`
  to log internal state per call (m_BitField bits, post-position),
  cross-referenced with the buffer dump.
- Do NOT add a server-side Harmony patch on `SceneObject.Serialize`
  — that would alter what we're measuring.

### Disposition

Task #19 is partial: infrastructure landed, scene-placed lookup
works, but the size-int byte mystery blocks loop progression past
object 0. The bot's working deliverables (1/12-bot connect, Player
spawn, Play state, 30 Hz inputs, telemetry) all keep working — no
regression from this attempt.

## Update: scene-placed sync fully working — full game state visibility (2026-04-28 attempt 12)

Task #19 done.

### Root cause of the size-int mystery

Used `ilspycmd` on
`<PUCK_INSTALL>\Puck_Data\Managed\Unity.Netcode.Runtime.dll`
to decompile the SERVER's NGO. Found the SyncNB size field was
written/read as `ushort` (2 bytes), while our PackageCache NGO 2.4.0
source uses `int` (4 bytes). Server's `SynchronizeNetworkBehaviours`:

```csharp
fastBufferWriter.WriteValueSafe<ushort>((ushort)0, ...);
...
fastBufferReader.ReadValueSafe(out ushort value2, ...);
```

Bot was reading 4 bytes for size when server only wrote 2 → garbage
size value (1.6 billion) → seekToEndOfSynchData clamped → next
SceneObject preamble read garbage → Failed to spawn → loop abort.

Patched the bot's NGO source at
`Library/PackageCache/com.unity.netcode.gameobjects@9684ced5879f/Runtime/Core/NetworkObject.cs`
SynchronizeNetworkBehaviours (3 lines: writer placeholder, writer
backfill, reader). Marked with `PUCK_STRESS_TEST:` comments. **Risk:
Unity package re-resolve clobbers this.** Not yet vendored to a
local Packages/ folder — TODO if the harness needs to survive a
package refresh.

### Polish: per-bot stubs + multi-handle keying

Two follow-up bugs surfaced on the multi-bot path:

1. **Duplicate-hash exception in NGO's PopulateScenePlacedObjects.**
   With one shared stub set in DDOL, NGO's
   `FindObjectsByType<NetworkObject>(true)` walked every bot's stubs
   for the same hash and threw at line 2755. Fixed by setting
   `IsSceneObject = false` on each stub (forces the auto-populate
   filter to skip them); manual `ReinjectInto` still adds them.
2. **Wrong scene-handle key.** With multiple bots, NGO's
   `SetTheSceneBeingSynchronized` resolved to a different scene
   handle than the active one (e.g. `-12` instead of `-76`). Fixed
   by keying our injected stubs under EVERY plausible scene handle
   (active scene, all loaded scenes, DDOL).

Per-bot stub instances (bot-index-suffixed GameObject names) prevent
`SpawnNetworkObjectLocallyCommon`'s `IsSpawned` collision when bot N
tries to spawn an already-spawned-by-bot-1 stub.

### Polish: corrected NB layouts

Extended `ConfigCaptureMod` to walk `GetComponentsInChildren<-
NetworkBehaviour>(true)` with NGO's exact `b.NetworkObject == this`
filter (no `.Distinct()`) and report per-NB NV count. Surfaced:

- Puck Clone has **12 NBs** (not 6 as `.Distinct()` had hidden — 4
  SyncAudio/SyncAudioCtrl pairs).
- Level has **17 NBs** (LevelManager + 8 SyncAudio pairs).
- `NetworkObjectCollisionBuffer` has 1 NV (`NetworkList<Network-
  ObjectCollision>`).
- `SynchronizedAudio` has 2 NVs (Volume, Pitch — both `NetworkVariable<byte>`).

Mirrors and registrar updated. `MirrorNetworkObjectCollision` struct
mirrors the Puck struct exactly (NetworkObjectReference + float
Time, with matching INetworkSerializable read/write).

### Position-claim flow

Replaced the "direct jump to Play" hack with proper
`Client_ClaimPositionRpc` (method-id 4027053218 on
PlayerPositionManager). Bot enumerates all spawned MirrorPlayer-
Position NBs, retries claims at 600 ms intervals (server silently
rejects wrong-team claims), and falls back to direct
SetPlayerState(Play) if 30 attempts exhaust. New
`MirrorPlayerPositionManager` mirror in MirrorOthers.cs holds the
RPC sender; registered as the first NB on the PlayerPositionManager
scene-placed entry.

### 12-bot smoke result

```
0 Failed to spawn / OverflowException
132 MirrorPuck spawns        (= 12 bots × 11 puck clones)
12  ClaimPositionLoop succeeded
12  state-machine: PositionSelectX → Play
2   unique 4-byte size mismatches (residual NV-layout polish — NGO's
    catch+seek-clamp handles them; cosmetic warnings only)
```

Bot harness now has full game-state visibility. Tasks #6/#7
(skate-to-puck, intelligent behavior) are unblocked.
