# raid-actions

The **actioner** for [raid-extractor](../raid-extractor) (the **planner**): executes a gear swap plan in Raid: Shadow Legends
by mouse/keyboard input and screen recognition, step by step, verifying every step by reading the account.

> Automation is against Plarium's terms of service. This program is off by default, runs only after an explicit Start, and
> keeps all game input inside `RaidActions.exe`.

## Rules

- Executes exactly what a plan says, or refuses it. Never builds, changes, reorders or "improves" a plan; never applies locks.
- Reads game state only through raid-extractor's `RaidExtractor.Core` (read-only, `PROCESS_VM_READ`). Never writes game memory,
  never injects, never talks to Plarium's servers.
- Before the first input: a fresh read of the account must match the plan exactly ([docs/PLAN-FORMAT.md](docs/PLAN-FORMAT.md)),
  and a gear snapshot is saved ([docs/SNAPSHOT.md](docs/SNAPSHOT.md)).
- Nothing from the game is committed: no game files, screenshots, templates cut from the game, logs or output.

## Layout

| Project | What |
|---|---|
| `src/RaidActions.ExtractorCore` | raid-extractor's `RaidExtractor.Core`, compiled from its sources into this repo's `bin/obj` |
| `src/RaidActions.Core` | Plan reader and validator, the account check, plan-to-actions conversion, gear snapshot. No game input |
| `tests/RaidActions.Tests` | Everything above, without the game |

### Where raid-extractor is

`RaidExtractorRoot` (default `..\raid-extractor`, next to this repo). Override with `-p:RaidExtractorRoot=D:\path` or the
`RaidExtractorRoot` environment variable.

Core is compiled from source rather than through a `ProjectReference` on purpose: a ProjectReference builds inside the
raid-extractor tree (its `bin/obj`), which that repo's own builds use at the same time. Here nothing in raid-extractor is
written. It is the same code with the same read-only guarantee.

## Build and test

```
dotnet build
dotnet test
```

## Status

Phase 0: plan reader, validator, account check, action conversion, snapshot; tests. No executable yet.
Requests for the planner: [docs/requests-for-raid-extractor.md](docs/requests-for-raid-extractor.md).
