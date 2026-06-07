# Puck Stress Test — Mission

## Goal

Build tooling that automates **up to 12 fake players** joining a Puck multiplayer
test server and behaving like real users so we can **profile the performance of
different Puck server mods** under realistic load.

Puck is a Unity-based multiplayer hockey game. The server runs the simulation
authoritatively and accepts inputs from connected clients. A "real" 12-player
match is the worst case for any server mod; reproducing that load on demand —
with deterministic, scriptable behavior — is the prerequisite for honest
performance comparisons across mods.

## Why this exists

- We want to compare server mods (CPU time per tick, GC pressure, network egress,
  tick-rate stability, frame spikes) under a **repeatable** load.
- Recruiting 11 humans to load-test every mod change is not viable.
- Headless Unity + scripted input is the cheapest way to generate authentic
  network traffic and physics interactions.

## What "acts like a user" means

Each fake player should at minimum:

1. **Connect** to a target server (host/port or Steam lobby), pick a
   team/position, and spawn into a match. Real auth via the Puck Central
   websocket is **out of scope** — see "Authentication" below.
2. **Skate toward the puck** using the same input surface a human uses
   (movement axes, sprint, etc.) — not by teleporting or directly mutating
   physics state.
3. **Rotate stick and head** — orient the stick blade and look direction toward
   useful targets (puck, pass lane, net).
4. **Attempt to hit / poke the puck** with the stick (slap/wrist, poke check).
5. **Try to move the puck toward the opposing goal** — a crude offensive
   heuristic is enough; we don't need a good hockey AI, we need plausible
   continuous activity that exercises the same code paths real games do.
6. **Disconnect cleanly** at end of run.

Stretch behaviors (only if cheap to add): line changes, faceoffs, occasional
passing toward a teammate, basic defensive positioning when on the wrong side
of the puck.

## What this is NOT

- Not a competitive hockey AI. Bots that play *poorly but constantly* are fine.
- Not a cheat / trainer / network exploit. Only legitimate client inputs.
- Not a single-machine benchmark — the goal is **server-side** profiling, so
  the measurement target is the server process running the mod, not the bots.
- Not a replacement for real playtesting; it is a load-generation harness.

## Success criteria

- Can launch N bots (1 ≤ N ≤ 12) from a single command, pointed at a server.
- Bots stay connected for the full run without crashing the server or
  themselves.
- Server-side profile traces collected during a bot run are **reproducible**:
  two runs with the same seed/config produce comparable load curves.
- Switching the server mod and re-running produces an apples-to-apples delta
  we can attribute to the mod.

## Key reference directories

These are the source-of-truth locations for understanding the game and
existing mods. Read from them; do not modify them.

- **Puck game decompile (B202):** `<PUCK_DECOMPILE>` (a local decompile of the
  shipping Puck assembly — you produce your own; nothing proprietary is shipped here)
  Decompiled C# of the shipping client/server. This is where to learn the
  networking model, input pipeline, player/stick/puck physics, and how a real
  client joins a match. Notable starting points:
  - `ConnectionManager.cs`, `NetworkManagerEventEmitter.cs`, `NetworkingUtils.cs`
    — connection lifecycle.
  - `PlayerInput.cs`, `PlayerInputController.cs`, `InputManager.cs`,
    `NetworkedInput.cs` — the input surface bots must drive.
  - `Player.cs`, `PlayerController.cs`, `PlayerBodyV2.cs`, `PlayerPosition.cs`
    — player state and movement.
  - `Stick.cs`, `StickController.cs`, `StickPositioner.cs` — stick control.
  - `GameManager.cs`, `LevelManagerController.cs` — match/level lifecycle.
  - `IPuckMod.cs`, `ModManagerControllerV2.cs` — mod loading interface.

- **Puck mods decompiles (Steam Workshop):** `<PUCK_MODS_DECOMPILE>`
  Decompiled C# of community mods, organized by Steam Workshop ID. Useful for
  seeing real `IPuckMod` implementations and the kinds of server hooks we'll
  end up profiling.

## Decisions made

- **Bot runtime: protocol-level.** Bots speak the game's wire protocol directly
  rather than running headless Unity clients. Lighter weight, scales to 12 bots
  cheaply, and keeps the harness independent of the game binary.
- **Authentication: skipped.** We do **not** integrate with the Puck Central
  websocket. Test servers will be configured so this is a non-issue:
  - Puck Central is a separate SocketIO websocket (`wss://puck1.nasejevs.com`,
    `WebSocketManagerController.cs`), invoked from the gameplay server only when
    `ServerConfiguration.usePuckBannedSteamIds == true`
    (`ServerManager.cs:255–279`).
  - With that flag off (true in B202), the gameplay server admitted the client
    immediately. **In the current build (B323+), `usePuckBannedSteamIds=false`
    alone is not enough** — Puck routes joins through `ConnectionApprovalManager`
    with a backend auth handshake, so a server-side mod, **`BotAuthBypassMod`**, is
    also required to admit bots (it intercepts approval for `botsteam*` SteamIds).
    See [SERVER_SETUP.md](SERVER_SETUP.md) Step 3. Either way, bots have zero
    dependency on Steam, Puck Central, or real player identities.

## Protocol facts (historical — build B202)

> ⚠️ **This section describes the original B202 protocol and is kept for context.**
> Puck has since changed it substantially (B323 rewrote the Player NVs, the
> connection payload, the auth/approval flow, and removed
> `Client_PlayerSubscriptionRpc`). For the **current** protocol see the live code
> (`BotHost/Assets/Scripts/ConnectionData.cs`, `Mirror/`, `BotInstance.cs`) and the
> maintainer guide [UPDATING.md](UPDATING.md). The networking-stack fundamentals
> below (NGO/UTP, RPC method-IDs, NetworkConfig hashing) still hold.

- **Networking stack**: Unity Netcode for GameObjects (NGO) over Unity
  Transport (UTP) — UDP underneath. No encryption on the gameplay channel.
- **RPCs**: weaver-generated `[Rpc(SendTo.X)]` methods with a uint method-ID
  discriminator and `FastBufferWriter` binary serialization (e.g.
  `Player.cs:1156–1174`).
- **Connection payload** (`ConnectionData.cs`, ASCII-JSON inside NGO's
  connection-approval message): `{ Password, SteamId, SocketId, EnabledModIds: ulong[] }`.
- **Server accept/reject** (`ServerManager.cs:229–340`,
  `ConnectionRejectionCode.cs`): rejects on empty SocketId, empty SteamId,
  server full, timed out, banned, missing/wrong password, missing required
  mods. No Steam-ID format check, no version/hash gate beyond NGO's
  `ProtocolVersion = Application.version as ushort`
  (`ConnectionManager.cs:27–31`).
- **Minimum identity to be admitted**: any non-empty SocketId, any non-empty
  SteamId (string — no format validation), correct password if the server has
  one, EnabledModIds covering all server-required mods.
- **Handshake order** (socket-open → can send inputs):
  1. C→S: open + `ConnectionData` payload via NGO connection approval.
  2. S→C: Player NetworkObject spawn + ~30 NetworkVariables (`Player.cs:48–160`).
  3. S→C: `Server_ServerConfigurationRpc` (`ServerManager.cs:415–444`).
  4. C→S: `Client_PlayerSubscriptionRpc` (`Player.cs:1367–1381`) — username,
     number, handedness, skins, steamId, mods. Sent from `OnNetworkPostSpawn`.
  5. S→C: `Client_SetPlayerStateRpc(PlayerState.Play)` — **the boundary**;
     after this the bot may stream input RPCs.
  6. C→S: input RPCs (`PlayerInput.cs:1533–1547`) — movement, look angle,
     blade angle, etc., sent reliably each tick.
- **Heartbeat**: server pings every 10s (`PlayerController.cs:61–68`); no
  client response required.

## Architectural implication of "protocol-level"

NGO is itself a layered protocol on top of UTP — RPC method-ID hashing,
NetworkObject spawn messages, NetworkVariable delta sync, FastBufferWriter
framing, transport-layer reliability/sequencing. "Protocol-level" therefore
splits into a few real options:

- **(A) Embed NGO + UTP as libraries** in a plain .NET console app. No Unity
  engine, no scene graph, no MonoBehaviour lifecycle. We proved the
  assemblies *load* (`spike_load/`), but `NetworkManager` is a
  `MonoBehaviour` and Unity's C# DLLs are thin wrappers over the native
  `UnityPlayer.dll` runtime. "Tiny engine shim" risks turning into
  "reimplement the parts of UnityPlayer that NGO touches." Multi-day
  rabbit-hole risk. **Rejected.**
- **(B) Reimplement NGO's wire format** from the decompile in another
  language. Maximum control, much more work, brittle to NGO upgrades.
  **Rejected.**
- **(C) Minimal Unity host for the bots.** Build a tiny Unity project — empty
  scene, no rendering, no Puck game assets — that hosts NGO and our scripted
  bot behavior as MonoBehaviours. Bots speak the same NGO/UTP wire protocol
  to the Puck server. Crucially, this is **not** running Puck.exe as a
  client; it's a custom client that happens to use Unity to host NGO (which
  is what NGO is designed to be hosted by). **Chosen.**

### Why (C) over (A)

| | A (engine shim in console) | C (minimal Unity host) |
|---|---|---|
| Time to first packet | Multi-day spike, dead-end risk | Hours |
| Code we write | Custom Unity engine adapter + bot loop | Tiny Unity scene + bot MonoBehaviour |
| NGO behaves as designed | Hopefully | Yes |
| Memory per bot | Maybe ~30MB | ~150-200MB |
| 12 bots in one process | Yes | Yes (one Unity process, 12 NGO clients) or 12 processes |
| Brittleness to game updates | Medium | Low — rebuild against new NGO |
| Debugging | Tracing into Unity native | Standard Unity debugger |

The 12 × ~200MB memory cost is fine on a dev machine for a stress harness.
The harness still meets the spirit of "protocol-level" — bots are scripted,
lightweight, and not running Puck — they just use Unity's runtime to host
the NGO client.

The `spike_load/` artifact stays in the repo as documented evidence of the
Path A investigation; it is not the chosen direction.

## Open questions to resolve before implementation

These are not blockers for the mission doc — they are the first design
decisions to make when we start building.

1. **Wire protocol**: which transport Puck uses (likely Unity Netcode /
   Mirror / custom over UDP), how messages are framed, and which messages are
   needed for the minimum join → spawn → input → leave loop.
2. **Identity fields**: the smallest set of fields a server requires to accept
   a connection once auth is bypassed (display name, Steam ID stand-in, etc.)
   and how to fabricate them safely per bot.
3. **Server orchestration**: how the test server is launched, which mod is
   loaded, the flag/config that disables ban checks, and how profiler output
   is captured per run.
4. **Determinism**: seedable RNG for bot decisions so runs are comparable.
5. **Telemetry**: what we measure server-side (tick time histogram, GC, alloc
   rate, network bytes/sec, dropped inputs) and how we aggregate it across
   runs.

## Working agreement

- Treat the decompile directories as **read-only reference**. All new code
  lives under this repository.
- Prefer driving the game through its existing input/networking surfaces over
  patching internals — mods that change those surfaces are exactly what we're
  profiling, so the bots must exercise them.
- Keep bot behavior dumb-but-busy until proven insufficient; smarter bots are
  only worth the complexity if a mod's hot path is gated behind realistic play.
