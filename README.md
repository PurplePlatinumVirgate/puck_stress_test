# Puck Stress Test

A load-testing and bot harness for [**Puck**](https://store.steampowered.com/app/2994020/Puck/),
the multiplayer hockey game. It spawns many synthetic clients ("bots") that
speak Puck's networking protocol (Unity Netcode for GameObjects / Unity
Transport) directly and connect to a Puck dedicated server you operate. Use it
to stress-test a server, profile the cost of server-side mods, or generate
realistic multiplayer load.

> ⚠️ **Use on servers you own/operate.** This tool includes an authentication
> bypass intended for *local and self-hosted test servers*. Running bots against
> servers you do not control may violate Puck's Terms of Service. This is an
> unofficial, third-party project and is not affiliated with or endorsed by the
> Puck developers.

> 🤖 **AI-authorship disclosure.** Most of this codebase was produced by an AI
> coding agent (Anthropic's Claude) working from high-level human direction —
> roughly **levels 6–8, and mostly tier 7, on the
> [VisiData AI-involvement scale](https://www.visidata.org/blog/2026/ai/)**
> (human specifies requirements at a high level; bots handle implementation).
> **It still requires review.** Read and test before relying on it.

## What's in here

| Component | What it is |
|-----------|------------|
| `BotHost/` | Unity 6000.0.44f1 project that runs the bots (built to `BotHost.exe`). |
| `BotAuthBypassMod/` | Server-side Puck plugin that lets unauthenticated bots join a test server. **Required** for bots to connect (or use the server config flag — see below). |
| `TelemetryMod/` | *Optional* server-side plugin: per-tick + per-event CSV metrics. |
| `ConfigCaptureMod/` | *Optional* one-shot server-side plugin: dumps the server's `NetworkConfig` hash + prefab list (useful after a Puck update). |
| `server-template/` | Sanitized server config + launch script to copy into your own Puck dedicated-server install. |
| `playbooks/` | JSON files describing bot behavior for a run. |
| `tests/` | Test-plan template + per-run checklists. |

## Quick start

1. **Install prerequisites** (Puck, the Puck dedicated server, Unity, .NET SDK)
   and **build** the bots + mods — see **[BUILD.md](BUILD.md)**.
2. **Set up and launch a test server** with the auth bypass — see
   **[SERVER_SETUP.md](SERVER_SETUP.md)**.
3. **Run a smoke test** (server in one console, bots in another):
   ```powershell
   & "<REPO>\BotHost\Build\BotHost\BotHost.exe" -batchmode -nographics `
       --playbook "<REPO>\playbooks\smoke_12bots_30s.json" `
       --server 127.0.0.1 --port 30609
   ```
   See [`launch_commands.txt`](launch_commands.txt) for more examples.

If something doesn't connect, start with **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)**.

## Documentation

- **[BUILD.md](BUILD.md)** — what to install and how to build everything.
- **[SERVER_SETUP.md](SERVER_SETUP.md)** — server-side requirements (the auth bypass, plugins, ports).
- **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** — common failures and fixes.
- **[UPDATING.md](UPDATING.md)** — how to re-sync the harness when Puck ships a new build (maintainers).
- **[MISSION.md](MISSION.md)** — why the harness is built the way it is.
- **[STATUS.md](STATUS.md)** — current capabilities.
- **[playbooks/README.md](playbooks/README.md)** — the run-config schema.
- **`docs/dev-notes/`** — deep reverse-engineering notes (historical reference).

## License

Released into the public domain under [The Unlicense](LICENSE). This covers the
original code here only — **not** the proprietary Puck/Unity assemblies, which
you supply yourself (see [BUILD.md](BUILD.md)).
