# Game install + assemblies

## Game install
- **Path**: `<PUCK_INSTALL>\`
- **Steam appid**: `2994020`
- **Manifest**: `<STEAM_LIBRARY>\steamapps\appmanifest_2994020.acf`
- **Managed assemblies dir**: `<PUCK_INSTALL>\Puck_Data\Managed\`
- **Mono**: `Puck\MonoBleedingEdge\` — game ships Mono, so it's a managed (IL2CPP-free) build, which is good news for embedding.

## Assemblies the bot harness will reference

Core networking (the ones we came for):
- `Unity.Netcode.Runtime.dll` — NGO
- `Unity.Networking.Transport.dll` — UTP

Likely required by NGO/UTP at runtime:
- `Unity.Collections.dll` (FixedString32Bytes, NativeArray, etc.)
- `Unity.Burst.dll`, `Unity.Burst.Unsafe.dll`
- `Unity.Mathematics.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.dll`
- `UnityEngine.SharedInternalsModule.dll`
- Various `System.*` BCL shims that ship with the game

Game-specific (need these to know RPC method-IDs and ConnectionData layout):
- `Puck.dll` — the game assembly with all the `[Rpc]` methods and types
- `Assembly-CSharp-firstpass.dll`

## Open risk: Unity engine dependency

`NetworkManager` is a `MonoBehaviour`. NGO's normal entry path assumes a Unity
runtime is up — `Application`, `Time`, `PlayerLoop`, `GameObject`, etc. Three
ways to handle this when running outside Unity:

1. **Avoid `NetworkManager.Singleton`** — drive `NetworkConnectionManager`,
   `MessageManager`, and `UnityTransport` directly. Still needs some Unity
   shims (Time, Update tick) but no scene graph.
2. **Stub the Unity types** we touch (`Time.deltaTime`, a fake `PlayerLoop`).
   Several open-source NGO-headless-client projects do exactly this.
3. **Run a true headless Unity** — explicitly rejected by mission.

We'll find out which is needed when we actually try to `new NetworkManager()`
during task #4 (null bot). Decision deferred until then; recording so we
don't forget the risk.

## Game-side flag for auth bypass
`banned_steam_ids.json` is present in the game install root. The mission-doc
auth-bypass plan hinges on `ServerConfiguration.usePuckBannedSteamIds=false`.
Server config lives under `<PUCK_INSTALL>\config\` —
need to check the exact JSON key name when standing up the test server
(task #3).
