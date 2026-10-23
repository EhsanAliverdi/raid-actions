# Gear snapshot

Saved before the first input of every run (dry runs too), in `output/snapshots/` (gitignored):

| File | Content |
|---|---|
| `<yyyyMMdd-HHmmss>-<planId>.json` | The gear snapshot (below) |
| `<yyyyMMdd-HHmmss>-<planId>.account.json.gz` | The full fresh read, in raid-extractor's history format (gzip of `account.json`); `History.Load` opens it |

## `.json`

```
{
  "kind": "raid-actions.gear-snapshot",
  "schemaVersion": 1,
  "takenAt": "2026-10-23T13:05:00+11:00",
  "gameVersion": "11.85.5",
  "accountExportedAt": "...",                 // meta.exportedAt of the read it was taken from
  "plan": { "planId", "name", "sha256", "path" },
  "champions": [ { "id", "name", "gear": { "<slot>": artifactId } } ],
  "pieces": [ { "artifactId", "slot", "owner", "ownerName", "description" } ],
  "restore": [ "Aria (#1): Weapon #202, Gloves #201", ..., "Pieces in the inventory: Helmet #100" ]
}
```

- `champions`: every champion a step names, plus every champion wearing a plan piece at the time, with **all** their gear.
- `pieces`: every plan piece and every piece those champions wear, with the owner at the time (`null` = inventory).
- `restore`: the same as plain lines, so the gear can be put back by hand without any tool.
