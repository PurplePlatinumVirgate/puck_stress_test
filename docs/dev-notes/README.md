# Dev notes (historical reference)

These are working notes kept from the harness's development — the reverse-engineering
journey of getting protocol-level bots to connect to Puck across builds B202 → B323 →
B897. They're preserved because the *reasoning* is useful (why the NGO patch exists, how
the scene-sync was solved, how prefab/NV layouts were derived).

**Read them as history, not current instructions.** They predate the open-source cleanup
and will contain:

- **Old build facts** — counts, hashes, enum orders, and protocol details from B202/B323
  that have since changed. The current build is B897.
- **References to paths that aren't in this repo** — e.g. `testserver/`, `ml/`,
  `live_streamer`, `spike_load/`, and run-artifact logs are local-only / gitignored and
  not published.
- **The old gameplay port `7777`** — stale; Puck uses UDP **30609** (see `SERVER_SETUP.md`).

For the **current**, authoritative procedure to update the harness for a new Puck build,
see **[../../UPDATING.md](../../UPDATING.md)**. For current architecture and status, see
`../../MISSION.md` and `../../STATUS.md`.
