# Puck game DLLs

This folder holds the game's managed assemblies, copied from your Puck
install's managed-DLL directory:

    <your Puck install>\Puck_Data\Managed\

Set the `PUCK_MANAGED` environment variable to that path and run
`copy_dlls.ps1` (see ../../../../BUILD.md). These DLLs are gitignored and
are never committed — see "NOT shipping" below.

## What gets copied

`copy_dlls.ps1` copies the **Harmony stack** that ships with Puck — the bot
uses Harmony to patch NGO at runtime, and `BotHost.asmdef` lists `0Harmony.dll`
as a precompiled reference:

- `0Harmony.dll`
- `Mono.Cecil.dll`
- `MonoMod.RuntimeDetour.dll`
- `MonoMod.Utils.dll`

Run `copy_dlls.ps1` **before opening the project in Unity** (the asmdef can't
resolve `0Harmony.dll` otherwise), and re-run it after every Puck update.

## Why not Puck.dll itself

The bot does **not** reference `Puck.dll`. The Puck types it needs (Player,
PlayerInput, NetworkVariable layouts, RPC method-IDs, enums) are transcribed by
hand in `Assets/Scripts/Mirror/` to avoid a dependency on the proprietary game
assembly (which also caused NGO version-resolution conflicts during plugin
import). So `Puck.dll` is neither a build nor a runtime dependency, and we don't
copy it.

## Why we don't reference Unity.Netcode.Runtime.dll directly

NGO is brought in through Unity Package Manager
(`com.unity.netcode.gameobjects`) instead of as a copied DLL because the
package install also registers Unity Editor extensions, code-gen hooks, and
default settings. Bringing it in via DLL works but skips that setup.

We pin the Package Manager version to match the version Puck ships
(Unity 6000.0.44f1's NGO line). Wire format is stable across NGO patch
releases.

## NOT shipping

Do not commit the DLLs to source control if this turns into a multi-machine
project — they are copyrighted game assets. Local-only.
