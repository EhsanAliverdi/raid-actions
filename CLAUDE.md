# Working rules for raid-actions

## Read first: plans and docs
- **Before any work, read raid-extractor's [plan/](../raid-extractor/plan/README.md)** (features span both repos; the plans live
  there) and the plan of the feature you touch. **Update that plan as you work** (tick steps, move "Where we are"), so another agent
  knows exactly where things stand.
- **[docs/](docs/)** here holds the bot's own notes (protocol, plan format, reading the game, requests for raid-extractor); deferred
  work goes to raid-extractor's [docs/TODO.md](../raid-extractor/docs/TODO.md).
- raid-extractor's [CLAUDE.md](../raid-extractor/CLAUDE.md) has the shared hard rules (read-only memory, what is never committed,
  allow-lists, testing on the real game: dry run first, then a small real run after Ehsan's go-ahead).

## This repo
- raid-actions never modifies raid-extractor. RaidExtractor.Core is compiled from source; when it changes, review and accept the lock:
  `dotnet build -p:AcceptCoreChanges=true`, commit `src/RaidActions.ExtractorCore/core-sources.lock`.
- Output (runs, dry runs, screenshots, logs, ui-trees) stays in `output/` and is never committed.
