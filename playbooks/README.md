# Playbooks

Machine-readable companions to the human-readable test plans in
`tests/`. A playbook tells the bot harness what every bot should *do*
during a profiling run: which baseline behaviors are on, which scripted
actions to fire (chat, slash commands, quick-chats), how to assign
teams/positions, and any mod-specific extras.

The bot harness reads the playbook with `--playbook <path>` and applies
it to all spawned bots.

## Why split human + machine

- `tests/<run>.md` — the operator's checklist + post-run notes.
  Human-only.
- `playbooks/<run>.json` — the bot harness's input. Machine-only.

A run typically references both: the test plan checklist verifies that
the chosen playbook covers the mod's hot path; the playbook itself
locks down what the bots actually did so the run is reproducible.

## File layout

```
playbooks/
  vanilla_baseline.json       Reference baseline — all default behaviors,
                              no scripted actions. Use this for any
                              "no-mod" comparison run.
  example_chat_heavy.json     Demonstrates scripted_actions for a chat /
                              command-heavy mod.
  README.md                   This file.
```

Naming: keep playbooks matched to mod shortnames where possible
(`examplemod.json`, etc.). The harness doesn't care; humans do.

## Schema

See `vanilla_baseline.json` for an annotated reference. Top-level
fields:

| field | type | meaning |
|---|---|---|
| `name` | string | Free-form label shown in logs |
| `description` | string | Free-form |
| `schema_version` | int | Bump if the bot's parser changes shape |
| `duration_seconds` | number | How long the run lasts before bots disconnect |
| `seed` | int | Deterministic PRNG seed for bot decisions |
| `bot_count` | int | 1..12 |
| `team_assignment` | string | `alternate` / `all_red` / `all_blue` / explicit list |
| `position_distribution` | object | `{skater: N, goalie: M}` summing to bot_count |
| `behavior` | object | Toggle map (see below) |
| `scripted_actions` | array | Time-ordered actions (see below) |
| `mod_specific` | object | Free-form payload the mod author defines |

### `behavior` toggles

These map 1:1 to the **Baseline behaviors** checklist in
`tests/TEMPLATE.md`. Default = `true` for all baseline behaviors;
playbooks override only the ones they want off.

```
skate_to_puck            move toward the puck when not carrying
rotate_stick_to_puck     orient stick toward puck
rotate_head_to_puck      orient look angle toward puck
attempt_poke             use poke/slap when in range
push_to_opposing_goal    bias movement toward goal when carrying
respect_faceoff          stand still during faceoff phase
pass_to_teammate         occasional pass attempts
line_change              pretend to swap lines on whistle
```

### `scripted_actions`

Each entry has:

```json
{
  "at_seconds": 30,
  "bot_indices": "all" | [0, 3],
  "action": "chat" | "command" | "quick_chat" | "team_change" | "position_change",
  "args": { ... }                  // action-specific
}
```

Examples by action type:

```json
{ "action": "chat", "args": {"text": "gg"} }
{ "action": "command", "args": {"command": "/restart"} }
{ "action": "quick_chat", "args": {"id": 5} }
{ "action": "team_change", "args": {"team": "Red"} }
{ "action": "position_change", "args": {"position": "Goalie"} }
```

Actions fire in `at_seconds` order. Ties resolve by array order.

### `mod_specific`

Anything the mod under test wants to drive. The harness deserializes
this as an opaque dict and exposes it to per-mod behavior plugins. A
plugin author writing a custom behavior can add a line in their
playbook like:

```json
"mod_specific": {
  "examplemod": {
    "some_flag": true,
    "trigger_event_every_seconds": 15
  }
}
```

This is intentionally lax — the harness validates the top-level shape
only.

## Lifecycle

1. **Author writes a playbook** alongside the test plan
   (`tests/<run>.md`).
2. **Harness loads it** at startup: `BotHost.exe --playbook <path>`.
3. **Each bot reads its own slice** of `bot_indices` for scripted
   actions, runs `behavior` toggles each tick.
4. **Run completes** at `duration_seconds`; bots disconnect cleanly.
5. **Operator** archives the playbook with the run's profile traces in
   `tests/<run>.md`.

## Validation

The harness logs a summary at startup:
- How many actions parsed
- Any unknown keys (warning, not error)
- Resolved `bot_count` → `team_assignment` → `position_distribution`
  consistency

Unknown keys are tolerated so older harness versions don't break newer
playbooks; bumping `schema_version` is the operator's way of signaling
intent if a structural change happens.
