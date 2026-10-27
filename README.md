# raid-actions

The **actioner** for [raid-extractor](../raid-extractor) (the **planner**): executes a gear swap plan in Raid: Shadow Legends
by mouse/keyboard input and screen recognition, step by step, verifying every step by reading the account.

> Automation is against Plarium's terms of service. This program is off by default, runs only after an explicit Start, and
> keeps all game input inside `RaidActions.exe`. **Phase 1: there is no game input in the code at all; only the fake executor.**

## Rules

- Executes exactly what a plan says, or refuses it. Never builds, changes, reorders or "improves" a plan; never applies locks.
- Reads game state only through raid-extractor's `RaidExtractor.Core` (read-only, `PROCESS_VM_READ`). Never writes game memory,
  never injects, never talks to Plarium's servers.
- Before the first input: a fresh read of the account must match the plan exactly ([docs/PLAN-FORMAT.md](docs/PLAN-FORMAT.md)),
  and a gear snapshot is saved ([docs/SNAPSHOT.md](docs/SNAPSHOT.md)). After every step the account is read again. On anything
  unexpected the run stops, saves a screenshot and reports the step; it never retries.
- Nothing from the game is committed: no game files, screenshots, templates cut from the game, logs or output.

## Layout

| Project | What |
|---|---|
| `src/RaidActions.ExtractorCore` | raid-extractor's `RaidExtractor.Core`, compiled from its sources into this repo's `bin/obj`; `core-sources.lock` pins them |
| `src/RaidActions.Core` | Plan reader and validator, account check, plan-to-actions conversion, runner (pause/abort/verify), run log, gear snapshot, fake game |
| `src/RaidActions.Game` | Reading the game, never input: the window, captures, screen recognition (OpenCvSharp), UI state and live account reads through Core ([docs/READING-THE-GAME.md](docs/READING-THE-GAME.md)) |
| `src/RaidActions.Protocol` | Messages for the planner ([docs/PROTOCOL.md](docs/PROTOCOL.md)) |
| `src/RaidActions.App` | `RaidActions.exe`: the CLI and the window |
| `tests/RaidActions.Tests` | Everything above, without the game |

### Where raid-extractor is, and the Core lock

`RaidExtractorRoot` (default `..\raid-extractor`, next to this repo). Override with `-p:RaidExtractorRoot=D:\path` or the
`RaidExtractorRoot` environment variable.

Core is compiled from source rather than through a `ProjectReference` on purpose: a ProjectReference builds inside the
raid-extractor tree (its `bin/obj`), which that repo's own builds use at the same time.

**The build fails when any compiled Core file changed, appeared or disappeared** since `core-sources.lock` was accepted. The
error lists the files and shows a `git diff --no-index` command against a local copy of the accepted sources (in `obj`, from
the last accept on this machine). After reviewing:

```
dotnet build -p:AcceptCoreChanges=true     # rewrites core-sources.lock; commit it
```

## Use

```
RaidActions                              the window
RaidActions open <plan.json>             the window with a plan
RaidActions validate <plan.json>         check the plan against the account
RaidActions dry-run <plan.json>          navigate and highlight every step, never equip
RaidActions run <plan.json>              run it (asks for the plan's confirmation code, or --confirm <code>)
RaidActions sample                       a made-up account and plan to try the app
RaidActions restart-game [--wait 300]    a stuck game: close Raid (end it after 30 s), start it through Plarium Play, wait for the village
RaidActions go-home                      back to the village with back/close elements only (closes offers), then Champions

Read-only, against the running game:
RaidActions validate <plan.json> --live  check the plan against the game as it is now
RaidActions window | capture | ui-state | recognize | templates seed|recut
```

A plan in raid-extractor's `output\plans\` is checked against that output's `account.json` and `history\`. Phase 1: the fake
executor starts from that saved `account.json` and moves pieces in memory; nothing is sent to the game.

Abort: **Ctrl+Alt+Esc** anywhere (a global hotkey that works while the game has focus), the Abort button, or Ctrl+C in the CLI.
Logs, snapshots and screenshots go to `output\` (gitignored). Exit codes: 0 done, 1 refused, 2 failed, 3 aborted, 4 usage,
5 not confirmed.

## Build and test

```
dotnet build
dotnet test
```

## Status

- Phase 0: plan reader, validator, account check, action conversion, snapshot.
- Phase 1: protocol, runner, fake game, CLI, window.
- Phase 2: game window, DPI, capture, screen recognition, UI state and live account reads from memory (read-only).

Requests for the planner: [docs/requests-for-raid-extractor.md](docs/requests-for-raid-extractor.md).
