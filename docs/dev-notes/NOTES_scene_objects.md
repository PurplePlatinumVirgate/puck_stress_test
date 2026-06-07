# Scene-placed NetworkObjects in Puck (build B202)

Dumped by extended `ConfigCaptureMod` from
`NetworkManager.SpawnManager.SpawnedObjects` after a fresh server boot
in Warmup phase. **43 spawned NetworkObjects total** — 32 unique
scene-placed objects + 11 puck clones (the actual hockey pucks).

This was the missing piece behind why `OnNetworkSpawn` never fires on
the bot: the server packs all 43 of these into the initial scene-sync
SceneEventMessage. Hash `2834597543` (Spectator Manager) is the first
the bot can't resolve, and its failure cascades.

## The 32 unique scene-placed NetworkObjects

Sorted by runtime `GlobalObjectIdHash`. All have `isInScene=True` and a
distinct `InScenePlacedSourceGlobalObjectIdHash` (not shown — see
`testserver/server13.log:298–333` for the raw dump).

| hash | name | NetworkBehaviours |
|---|---|---|
| 174568750  | Replay Camera (scene) | `[ReplayCamera, ReplayCameraController]` |
| 377675813  | Vote Manager | `[VoteManager]` |
| 423488127  | LW (player position) | `[PlayerPosition]` |
| 859778618  | Goal Blue | `[GoalController]` |
| 902365249  | LW (player position) | `[PlayerPosition]` |
| 1239018275 | Puck Shooter | `[PuckShooter]` |
| 1293398339 | C (player position) | `[PlayerPosition]` |
| 1395376169 | LD (player position) | `[PlayerPosition]` |
| 1489441499 | Goal Red | `[GoalController]` |
| 1520830446 | RW (player position) | `[PlayerPosition]` |
| 1535010640 | WebSocket Manager | `[]` |
| 1606090899 | Player Position Manager | `[PlayerPositionManager, PlayerPositionManagerController]` |
| 1694779872 | Observer Camera | `[BaseCamera, BaseCameraController]` |
| 1768649635 | RD (player position) | `[PlayerPosition]` |
| 1849371321 | Blue Position Select Camera | `[BaseCamera, BaseCameraController]` |
| 1885677222 | G (player position) | `[PlayerPosition]` |
| 1982120363 | Player Manager | `[PlayerManager, PlayerManagerController]` |
| 2301446517 | C (player position) | `[PlayerPosition]` |
| 2653207325 | Puck Manager | `[PuckManager, PuckManagerController]` |
| 2780084574 | Red Position Select Camera | `[BaseCamera, BaseCameraController]` |
| **2834597543** | **Spectator Manager** | `[SpectatorManager, SpectatorManagerController]` |
| 2930314431 | G (player position) | `[PlayerPosition]` |
| 3169663068 | UI Manager | `[UIManager, UIManagerController, UIManagerStateController, UIMainMenu, ...32 UI behaviours]` |
| 3420402611 | Synchronized Object Manager | `[SynchronizedObjectManager]` |
| 3445996078 | Server Manager | `[ServerManager, ServerManagerController]` |
| 3634248248 | LD (player position) | `[PlayerPosition]` |
| 3885464518 | Game Manager | `[GameManager, GameManagerController]` |
| 3915051953 | Replay Manager | `[ReplayManager, ReplayManagerController, ReplayPlayer, ReplayRecorder, ReplayRecorderController]` |
| 3947503362 | RD (player position) | `[PlayerPosition]` |
| 3977717068 | RW (player position) | `[PlayerPosition]` |
| 4131349613 | Level | `[LevelManager, PuckShooter, PlayerPosition, BaseCamera, BaseCameraController, ReplayCamera, ReplayCameraController, SynchronizedAudio, SynchronizedAudioController, GoalController]` |
| 4205146926 | Scene Manager | `[SceneManager]` |

Plus puck clones: 11 instances of hash `3292036842` (already mirrored).

## What this implies

Earlier "register the 10 prefabs from `NetworkPrefabOverrideLinks`"
turned out to be a small subset. The complete picture:

1. **Prefab dict** (10 entries, what `ConfigCaptureMod`'s original walk
   reported) — used by NGO when `IsSceneObject == 0`.
2. **In-scene placed objects** (these 32) — used when `IsSceneObject ==
   1`. NGO matches by `InScenePlacedSourceGlobalObjectIdHash`. The
   bot doesn't load Puck's gameplay scene, so none of these are
   present and the lookup fails.

NGO logs `Failed to spawn NetworkObject for Hash X.` for each missing
one and apparently aborts further spawn processing in the same
SceneEventMessage. Result: our Player/Puck/Stick mirrors never get
their `OnNetworkSpawn` called.

## Realistic options

### A. Mirror every scene-placed NetworkObject

For each of the 32 entries above, transcribe the NetworkBehaviour
classes (e.g. `PlayerPosition`, `GameManager`, `ServerManager`) into
bot-side mirrors with their NetworkVariables, then place a copy of
each in the bot's `SampleScene` with the matching
`InScenePlacedSourceGlobalObjectIdHash` set via reflection. NGO would
then resolve the hashes correctly.

Effort: substantial. Many manager classes have NetworkVariables /
NetworkLists that need mirroring, plus `UIManager` has 32+ behaviours
on it. Several day's work, but mechanical.

### B. HarmonyX-patch NGO's `SceneObject.Deserialize` to skip unknowns

Patch the deserializer to read past unknown hashes without bailing.
Requires wiring HarmonyX (or equivalent) into the bot Unity project —
we got walls earlier when trying the game-shipped `0Harmony.dll`
because of `System.Reflection.Emit` strip. HarmonyX (a Unity-friendly
fork) avoids that.

Effort: moderate. The patch itself is small but understanding NGO's
exact byte layout for each spawn message is the hard part. If the
"skip past" logic guesses wrong, alignment breaks for everything else.

### C. Accept that bot can't read game state

The harness still produces useful load (12 connected clients,
NetworkVariable broadcast traffic, connection-approval RPCs, telemetry)
without bots needing to read game state. Tasks #6/#7 (skate/poke
behavior) require state, but for many profiling questions the current
harness is enough.

This is the pragmatic acceptance: ship behavior work as a
separately-scoped follow-up if and when a specific mod's profile needs
it.

## Recommended next move

If null-bot sufficiency keeps proving useful for profiling work,
defer A/B and stay at C. If/when behavior code becomes necessary,
**A** is the cleaner long-term path because B's "skip past" logic is
fragile against any wire-format change, and A scales naturally as
Puck's scene grows new managers.
