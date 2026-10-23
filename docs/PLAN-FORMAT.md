# Plan files: what raid-actions accepts and checks

Plans come from raid-extractor (`output/plans/*.json`, written by `SwapPlan` in `RaidExtractor.Core/Battle/SwapPlan.cs`).
raid-extractor's SCHEMA.md → *Plan files* is the authority (its "check before a plan runs" is the same check as below); this
file lists what the actioner **reads** and **checks**. `*.progress.json` files next to a plan hold the planner's ticks and are
not plans. Fields not listed
here (predictions, `champions`, `locks` other than its name, `origin`, `difficulty`, ...) are ignored. Unknown fields are
ignored too.

## Accepted versions

`schemaVersion` 1 only. A plan without `schemaVersion` (the older `swap-plan.json`) is refused: it records neither the game
version nor the state of the pieces, so it cannot be checked. A newer version is refused.

## Fields read

| Field | Required | Use |
|---|---|---|
| `schemaVersion` | yes | must be 1 |
| `planId` | yes | identity in logs, snapshots and the protocol |
| `name`, `created` | yes | display |
| `gameVersion` | yes | must equal the fresh read's game version |
| `exportedAt` | yes | the export the plan was made from; the fresh read must not be older |
| `snapshotId` | no, but a plan without one is refused at the account check | raid-extractor `output/history/<snapshotId>`: the export the plan was made from; must exist and be that export |
| `steps[]` | yes, non-empty | `number, artifactId, slot, piece, fromChampionId (null = inventory), fromName, toChampionId, toName, expectedOwnerAfter`; all present, `fromChampionId` may be null |
| `pieces[]` | yes | `artifactId, slot, owner, level, rank, ascension, expectedOwner` (`owner`/`expectedOwner` null = inventory), `fingerprint` (optional) |
| `ticked` | no | only in files from before ticks moved to the progress file; ignored, with a warning: what is done is read from the account |

`piece`, `fromName` and `toName` are display text. They are never used to find or check anything.

## Structural checks (no account)

Each one refuses the plan:

- steps numbered `1..n` in file order; known slots; valid ids; `fromChampionId ≠ toChampionId`
- `expectedOwnerAfter = toChampionId`
- no artifact moved twice; no champion slot filled twice
- no step takes a piece from a slot that an earlier step already refilled (by then the piece is in the inventory)
- every moved piece is in `pieces[]`, with the same slot and with `owner = fromChampionId`
- the end state in `pieces[].expectedOwner` follows from applying the steps the way the game does: the piece leaves its owner,
  and the recipient's old piece in that slot goes to the inventory

## Account check (fresh read), before starting and after every step

Each one refuses the plan:

- game version differs from `gameVersion`
- the read is older than `exportedAt`
- the plan's snapshot export is missing, or is not the export `exportedAt` names
- a piece is gone (sold or used), changed slot, or changed level, rank or ascension (against `pieces[]`)
- a piece's `fingerprint` (raid-extractor's `PlanPiece.FingerprintOf`, the formula in SCHEMA.md; a test checks the code against
  the documented formula) differs from the fresh read; for a piece without a fingerprint, its set, rarity, main stat, substats,
  rolls or glyphs differ from the snapshot export (rerolled or glyphed)
- a piece is not where the plan puts it once the steps already done are applied, or a recipient wears a piece in a plan slot
  that the plan does not know
- a champion is gone or in the vault; a faction accessory goes to a champion of another faction

Steps the account already shows as done (piece on its recipient) are skipped without input, in any order, provided every
other piece is exactly where those done steps put it. When every step is done there is nothing to do.

Warnings (shown, not refusing): ticks in the plan; a partial read (skipped items); a champion in the bathhouse.
