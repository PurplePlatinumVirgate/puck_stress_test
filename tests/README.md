# Test plans

One Markdown file per stress-test run. Use `TEMPLATE.md` as the starting
point.

## Naming
`<mod-shortname>_<YYYY-MM-DD>[_<NN>].md` — `_NN` only when there are
multiple runs against the same mod on the same day.

Examples:
- `vanilla_2026-04-27.md` — vanilla baseline
- `examplemod_2026-04-27_01.md` — first run of the day against the
  mod under test
- `examplemod_2026-04-27_02.md` — second run after a fix

## Lifecycle
1. **Before**: copy `TEMPLATE.md`, fill out *Run identity*, *Hypothesis*,
   *Server config*, *Pre-flight*, *Baseline behaviors*, *Mod-specific
   behaviors*, *Measurements*, *Comparison baseline*. Tick the pre-flight
   boxes as you do them.
2. **During**: keep the file open; jot timestamps next to surprising
   events.
3. **After**: fill out *Run results* and *Follow-up actions*. Commit (or
   archive) alongside the run's profile traces.

## Why a fixed template
- Keeps comparisons honest run-to-run.
- The pre-flight checklist exists because we've already had to throw away
  one run when we found out the wrong mod list was loaded.
- Mod-specific extras live in the same file as the baseline so a future
  reader doesn't miss why the run touched a particular code path.

## Future: machine-readable playbooks
The bots themselves don't read these files. Eventually we want a parallel
JSON playbook (`playbooks/<mod>.json`) that the bots load to drive
mod-specific scripted actions (commands, chat phrases, etc.). For now,
the human-readable template is the source of truth and any mod-specific
scripted action is wired in code per run. See task #12 for the playbook
work.
