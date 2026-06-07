# Spike: load NGO from a stock .NET console

## What it does
`spike_load/` is a `net8.0` console app that does no networking — it just
references the game's Unity DLLs and tries to resolve types from them via
an `AssemblyResolve` hook pointed at the game's `Managed` directory.

Run with: `cd spike_load && dotnet run`.

## Result: all assemblies load, all key types resolve

```
[1] Loaded Unity.Netcode.Runtime, Version=0.0.0.0
[2] NGO referenced assemblies:
    mscorlib                                           ok
    System                                             ok
    UnityEngine.CoreModule                             ok
    System.Core                                        ok
    Unity.Collections                                  ok
    Unity.Multiplayer.Tools.NetStats                   ok
    Unity.Multiplayer.Tools.MetricTypes                ok
    Unity.Multiplayer.Tools.NetworkSolutionInterface   ok
    Unity.Burst                                        ok
    Unity.Networking.Transport                         ok
    Unity.Mathematics                                  ok
    UnityEngine.AnimationModule                        ok
    UnityEngine.PhysicsModule                          ok
    UnityEngine.Physics2DModule                        ok
    Unity.Multiplayer.Tools.NetStatsReporting          ok
    netstandard                                        ok
[3] Resolved type: Unity.Netcode.NetworkManager
    BaseType: UnityEngine.MonoBehaviour
    IsSubclassOf MonoBehaviour: True
[4] Key NGO types resolution:
    Unity.Netcode.NetworkManager                            ok
    Unity.Netcode.NetworkConfig                             ok
    Unity.Netcode.NetworkConnectionManager                  ok
    Unity.Netcode.NetworkMessageManager                     ok
    Unity.Netcode.Transports.UTP.UnityTransport             ok
    Unity.Netcode.NetworkObject                             ok
    Unity.Netcode.FastBufferWriter                          ok
    Unity.Netcode.FastBufferReader                          ok
```

## What this proves
- The .NET host can resolve every NGO/UTP/Burst type without a Unity engine
  process. No mscorlib mismatch, no IL2CPP wall.
- The DLL graph is self-contained inside `Puck_Data\Managed\`.

## What this does NOT prove
- That `NetworkManager` will actually run. It is a `MonoBehaviour`, which
  means NGO expects a Unity engine: a scene, a registered `PlayerLoop`,
  `Time.deltaTime`, `Application.platform`, etc.
- That UTP can drive a UDP socket from outside Unity. UTP itself is mostly
  engine-independent, but the `UnityTransport` adapter (the class NGO uses
  by default) sits on top of MonoBehaviour.

## The fork in the road

To actually send a packet, we need one of:

### Path A — embedded NGO with engine shim
- Host the Unity engine in our process via `UnityEngine.dll`'s public
  surface: register a custom `PlayerLoop`, drive `Time` ourselves, create a
  `GameObject` and `AddComponent<NetworkManager>` programmatically.
- There is precedent (community headless-NGO clients do this) but it's
  undocumented and brittle across Unity versions.
- Pro: bots speak NGO natively. Con: we are essentially running a stripped
  Unity process inside a console wrapper.

### Path B — bypass NetworkManager, drive lower-level NGO
- Skip `NetworkManager.Singleton`. Construct `NetworkConnectionManager`,
  `NetworkMessageManager`, `UnityTransport` directly via reflection /
  internals access.
- We supply the tick loop. No PlayerLoop, no scene.
- Pro: cleanest separation from Unity engine. Con: NGO's internals are
  marked `internal`; we'd be using reflection or `InternalsVisibleTo`
  workarounds, and NGO version bumps will break us.

### Path C — actual headless Unity client
- Build a minimal Unity project that imports nothing from Puck except its
  network types, runs in `-batchmode -nographics`, and hosts the bots.
- Pro: known-working path used by every Unity stress harness. Con: this is
  what we explicitly said no to in the mission.

## Recommendation

**Path A** is the highest-likelihood path that honors the "no full game
client" constraint. The Unity engine surface we'd need is small (PlayerLoop,
Time, a GameObject host) and there are public APIs for all of it. Path B
is more architecturally clean but the reflection burden against `internal`
NGO classes is significant and fragile. Path C is the safe fallback if
Path A turns out to require more shimming than expected.

Whichever path: the next concrete step is the same — try to instantiate
`NetworkManager` and observe what specifically breaks. That fail-trace
tells us exactly which engine pieces we need to provide.

## Status
Spike answers the "can the assemblies load at all?" question with a yes.
Real work starts at the next prompt.
