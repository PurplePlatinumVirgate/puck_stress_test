# Puck's Unity version

Puck (build B202, current Steam install) is built with:

**Unity 6000.0.44f1** — Unity 6 LTS, patch 44f1.

Sources:
- `<PUCK_INSTALL>\UnityPlayer.dll` ProductVersion:
  `6000.0.44f1 (101c91f3a8fb)`
- `<PUCK_INSTALL>\Puck_Data\globalgamemanagers` header
  string: `6000.0.44f1`

Unity Hub installer link: <https://unity.com/download>
Direct Editor install (once Hub is installed):
`unityhub://6000.0.44f1/101c91f3a8fb`

## Why this version, exactly

For Path C the bot Unity project should match Puck's Unity version so:
- The NGO and UTP package versions ship by Unity 6000.0.44 line up exactly
  with the assemblies in `Puck_Data\Managed\`. Mismatches risk RPC method-ID
  hash differences, NetworkVariable layout drift, or transport handshake
  incompatibility.
- We can directly reference `Puck.dll` if we end up doing that for the
  Player-prefab strategy (task #10).

A close patch version (e.g. 6000.0.x where x ≠ 44) is *probably* fine since
NGO is wire-stable across patch releases, but exact-match is the safer
default.

## State on this machine

Unity Hub: **not installed**.
Unity Editor 6000.0.44f1: **not installed**.

Action for the user: install Unity Hub, then add Editor 6000.0.44f1.
The bot project work (task #4) is gated on this.

## Modules / build-support to add

For a headless bot host on Windows we only need:
- **Windows Build Support (IL2CPP)** — optional; Mono backend works for our
  purposes and matches what Puck ships.
- **Linux Build Support (Mono)** — only if we ever want to run bots on a
  Linux profiling machine.

Skip Android/iOS/WebGL/Visual Studio integration unless desired.
