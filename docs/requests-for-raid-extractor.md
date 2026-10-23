# Requests for raid-extractor

From raid-actions (the actioner). raid-actions does not change raid-extractor; these are requests for whoever works there.

## Status (2026-10-23)

Checked against raid-extractor's working copy (plan v1, partly uncommitted). raid-actions pins the Core sources it compiles
(`core-sources.lock`), so every Core change is reviewed here before it is built.

| # | Request | Status |
|---|---|---|
| 1 | Document plan files in SCHEMA.md | **Done**: SCHEMA.md → *Plan files* (raid-actions follows it) |
| 2 | Record more than level, rank and ascension per piece | **Done**: `pieces[].fingerprint` (SHA-256, formula in SCHEMA.md). raid-actions checks it, using the code's `FingerprintOf`, and a test recomputes it from the documented formula |
| 3 | Always set `snapshotId` | **Done**: every saved plan has one; SCHEMA.md says to refuse without it, and raid-actions does |
| 4 | Game version mismatch is a refusal, not a warning | Agreed by the user; SCHEMA.md now lists it as a refusal. `PlanValidator.Check` should follow |
| 5 | A champion that gives and receives in the same slot | **Done in the planner**: the giving step comes first. raid-actions still refuses the wrong order (`DonorSlotReplacedEarlier`) |
| 6 | Don't rewrite the plan file during a run | **Done**: plan files are written once; ticks go to `<plan>.progress.json` |

## Open

### 7. "Apply automatically" (phase 6)

The planner's button and its use of the protocol ([PROTOCOL.md](PROTOCOL.md)) will be described here in phase 6.
