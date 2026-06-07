# Player-prefab strategy for the bot client

## Decision: lightweight stubs via `NetworkPrefabHandler`, with `Puck.dll` referenced

The bot Unity project will reference `Puck.dll` directly so we get the
authoritative `Player` / `PlayerInput` class definitions (NetworkVariable
layouts, RPC method-IDs) without reimplementing them. We will **not** bring
in Puck's scenes, art, audio, or game-state managers.

For each NetworkObject the server may spawn, we register a *minimal* prefab
in our project and use `NetworkPrefabHandler` to map the server's prefab
hash to our stub instance.

## Decompile findings (build B202)

- **`Player` root** (`Player.cs:9`) is a single `NetworkBehaviour`. Only
  sibling component on the root is `PlayerInput` (`Player.cs:44`). No hard
  rendering or physics dependencies on the root itself.
- **Body, stick, camera, stick-positioner, spectator-camera are separate
  NetworkObjects**, spawned by the server via `SpawnWithOwnership` in
  `Player.cs:909–1074` — not children of a single Player prefab.
- **Body / Stick / Camera have hard rendering+physics deps**
  (`PlayerBodyV2.cs:180–192`, `Stick.cs:90–91`, `BaseCamera.cs:22`):
  Rigidbody, MeshRendererHider, Skate, Hover, KeepUpright, Camera, etc.
  Cannot be instantiated client-side without dragging in the rendering /
  physics modules.
- **All `Client_*Rpc` methods on Player target the Player NetworkObject
  itself** (`Player.cs:1157, 1260, 1341, 1959–2042`). None dispatch into
  PlayerBodyV2 / Stick / PlayerCamera. So missing those child objects on
  the client does not break Player-state RPCs.
- **Bot-relevant NetworkVariables** are tiny: `State`, `Team`, `Role`,
  `PlayerPositionReference` (`Player.cs:2160, 2176, 2180, 2196`). All
  cosmetics (visors, jerseys, etc.) ignorable.

## What the bot prefabs look like

For the null bot (just connect, handshake, idle, disconnect) we register
**only the Player prefab**. Other NetworkObjects the server tries to spawn
(PlayerBody, Stick, etc.) will fail to spawn client-side — NGO logs an
error per missing prefab but the connection stays alive.

| Prefab | Components | Required for null bot? |
|---|---|---|
| Player (stub) | NetworkObject + Player + PlayerInput | **yes** |
| PlayerBodyV2 (stub) | NetworkObject only | no — skip, accept log spam |
| Stick (stub) | NetworkObject only | no — skip |
| StickPositioner (stub) | NetworkObject only | no — skip |
| PlayerCamera (stub) | NetworkObject only | no — skip |
| Puck (real hockey puck) | NetworkObject + Puck script | only when we want to read puck position |

The `NetworkPrefabHandler.AddHandler` API lets us map the server's
GlobalObjectIdHash (computed against Puck's project) to our stub prefab
instance, so hash-mismatch isn't a blocker.

## Why this over "real prefab from Puck.dll"

- The real Player prefab pulls Rigidbody, Animators, audio sources, custom
  movement scripts, and the entire `Skate`/`Hover`/`KeepUpright` physics
  stack via the body / stick / camera children. We don't need any of it
  for stress testing.
- Stubs keep per-bot memory and CPU minimal — important when running 12 in
  one process.
- Server-side logs may complain about despawns of non-existent client
  objects. Acceptable: connection stays up and inputs flow.

## Risks to monitor

- **NGO might drop the client** if too many prefab hashes are unknown at
  connect time. If so, we fall back to registering bare-NetworkObject
  stubs for *every* prefab the server might spawn, not just Player.
- **`Player` class might transitively reference rendering/audio code in
  `OnNetworkSpawn`** even if its root component list is clean. If
  instantiating our Player stub throws on missing components, we strip
  those code paths via reflection / partial mocking, or substitute a
  minimal `Player`-shaped class that mirrors only the network surface.
  Will know at task #4 (null bot).

## Open question deferred

When `Player.PlayerInput = base.GetComponent<PlayerInput>()` runs, it
needs `PlayerInput` on the same GameObject. `PlayerInput` likely pulls
the Unity InputSystem package (a real Unity package, not a Puck-specific
binary). The bot's Unity project has to install the matching InputSystem
package version. Verify when we open the project in Unity.
