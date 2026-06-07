# Puck NetworkObject prefab map (build B202)

Captured 2026-04-27 by `ConfigCaptureMod` — 10 prefabs in
`NetworkPrefabOverrideLinks`. Each row is the GlobalObjectIdHash key the
server expects on the client side, plus the NetworkBehaviours and other
components we'd need to replicate for the byte stream to deserialize.

| Hash | Prefab name | NetworkBehaviours | Key non-net components |
|---|---|---|---|
| 340656796 | Stick Positioner | `StickPositioner`, `StickPositionerController`, `SynchronizedAudio`, `SynchronizedAudioController` | AudioSource |
| 923338123 | Player Body V2 (Attacker) | `PlayerBodyV2`, `PlayerBodyV2Controller`, `SynchronizedObject`, `SynchronizedObjectController`, `SynchronizedAudio`, `SynchronizedAudioController` | Rigidbody, CapsuleCollider, SphereCollider, KeepUpright, Hover, Movement, Skate, VelocityLean, MeshRenderer, AudioSource |
| 1396033496 | Player Body V2 (Goalie) | same as Attacker body | same |
| 1915519032 | Spectator Camera | `SpectatorCamera`, `SpectatorCameraController` | Camera, AudioListener |
| **2055993102** | **Player** | **`Player`, `PlayerController`, `PlayerInput`, `PlayerInputController`, `PlayerVoiceRecorder`, `PlayerVoiceRecorderController`** | **AudioSource (only)** |
| 3236080593 | Player Camera | `PlayerCamera`, `PlayerCameraController` | Camera, AudioListener |
| 3292036842 | Puck (the disc) | `Puck`, `SynchronizedObject`, `SynchronizedObjectController`, `NetworkObjectCollisionBuffer`, `SynchronizedAudio`, `SynchronizedAudioController` | Rigidbody, MeshCollider, SphereCollider, AudioSource, TrailRenderer, MeshRenderer, LineRenderer |
| 3464149273 | Stick (Goalie) | `Stick`, `StickController`, `SynchronizedObject`, `SynchronizedObjectController`, `NetworkObjectCollisionBuffer` | Rigidbody, MeshCollider, MeshRenderer |
| 3726304409 | Stick (Attacker) | same as Goalie stick | same |
| 4103617937 | Replay Camera | `ReplayCamera`, `ReplayCameraController` | Camera, AudioListener |

## What this means for the bot

For NGO's scene-synchronization step to deserialize cleanly, the bot's
client-side prefab for each hash must have the same NetworkBehaviour
layout as the server's prefab — same number of NetworkVariables, same
types, same field order — because NGO walks them in declaration order
when reading the byte stream.

The non-net components (Rigidbody, Camera, Renderers, etc.) are only
needed if the NetworkBehaviour scripts do `GetComponent<X>()` in `Awake`
or `OnNetworkSpawn`. Many will, so we can either:

- Add the heavy components to the stub prefab (functionally correct but
  drags rendering/physics into the bot process), OR
- Suppress / patch the lookups (Harmony patches on each `Awake`) — leaner
  but brittle.

## Hash → role at runtime

Of these 10 prefabs:
- **Player** (2055993102) is the only one that receives directed RPCs
  (`Client_*Rpc` per `Player.cs:1157, 1260, 1341, ...`) and that the bot
  must be able to act on. This is the prefab that needs to function
  correctly enough to receive `Client_SetPlayerStateRpc(Play)` and to be
  the source of `Server_*InputRpc` we send.
- The others are spawned for every other player in the match plus the
  shared puck/cameras. The bot needs them registered so NGO's spawn-
  message deserialization round-trips correctly, but it does NOT need
  them to do anything useful — they can be inert.

## Implementation order for task #16

1. Build the **Player** prefab properly (real `Player` + `PlayerInput` +
   `PlayerController` etc. from Puck.dll). It has no rendering/physics
   deps on its root, so this should be tractable.
2. Build minimal **stub prefabs** for the other 9 with their
   NetworkBehaviour types attached and just the components their Awake
   methods need to find — discover empirically by adding components and
   chasing NullReferenceExceptions until they stop.
3. Register all 10 via `NetworkPrefabHandler.AddHandler` (or by directly
   adding to `NetworkPrefabOverrideLinks`) right after `StartClient`,
   same place we currently inject just the keys.

## Artifacts
- Capture mod source: `ConfigCaptureMod/Plugin.cs` (extended with
  `DescribePrefab` reflection walker)
- Captured server log: `testserver/server4.log`
- Bot project ready to reference Puck.dll: `BotHost/Assets/Plugins/Puck/`
  (DLLs re-enabled, `validateReferences: 0` in `*.dll.meta`,
  `BotHost.asmdef` declares them as `precompiledReferences`).
