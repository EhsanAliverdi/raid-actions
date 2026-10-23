# Requests for raid-extractor

From raid-actions (the actioner). raid-actions does not change raid-extractor; these are requests for whoever works there.
Status as of 2026-10-23, against the plan v1 work that is in raid-extractor's working copy but not committed yet.

## 1. Document plan files in SCHEMA.md

`SwapPlan.cs` says the schema is in "SCHEMA.md → Plan files", but SCHEMA.md has no such section yet. raid-actions reads the
v1 shape from the `SwapPlan` / `SwapStep` / `PlanPiece` records ([PLAN-FORMAT.md](PLAN-FORMAT.md)). It has a test that
serialises raid-extractor's own `SwapPlan` record and reads it back, so a format change on your side shows up there.

## 2. Record more than level, rank and ascension per piece

`pieces[]` would not catch a reroll, a glyph or a substat change at the same level. raid-actions compares against the
snapshot export (`snapshotId` in `output/history`), and refuses the plan when that file is missing. Please add one of these
to `PlanPiece`:

- `set`, `rarity`, `primary` and `substats` (with `rolls` and `glyph`), in the same shape as `artifacts[]`, or
- a `fingerprint` string over the same fields, with its formula documented.

With that, a plan can be checked even after the history file is gone.

## 3. Always set `snapshotId`

A plan saved through `Save` (not `SaveNew`) has `snapshotId: null`, and raid-actions refuses it. Please make every saved plan
carry one.

## 4. Game version: a refusal, not a warning

`PlanValidator.Check` treats a different game version as a warning. raid-actions refuses it, because the screens, the offsets
and the game's rules may have changed. Please show it as a blocker for "Apply automatically", so your app never offers a
plan that the actioner will then refuse.

## 5. Step order when a champion gives and receives in the same slot

Steps go inventory first, then donor by donor. When a champion gives away its piece in slot S and receives another piece in
S, the receiving step can come first. By the time the giving step runs, its piece is already in the inventory, so
`fromChampionId` is wrong. raid-actions refuses such a plan (`DonorSlotReplacedEarlier`). Please order the giving step first,
or refuse to make the plan.

## 6. Don't rewrite the plan file during a run

Ticks are saved into the plan file itself. raid-actions reads the plan once and logs its SHA-256 and `planId`. A rewrite
during a run is harmless, but it makes the logged hash differ from the file. Better: don't write ticks while a run is in
progress, which the protocol will report. The "Apply automatically" button comes in phase 6.
