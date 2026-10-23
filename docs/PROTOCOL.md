# Protocol: planner ↔ RaidActions.exe

Version **1**. Code: `src/RaidActions.Protocol` (`net10.0`, no dependencies, so the planner can reference it).
Transport: the local named pipe **`raid-actions-v1`**, served by RaidActions.exe (phase 6). Local machine only.

## Framing

- One message per line: UTF-8 JSON, ending with `\n` (`\r\n` accepted). Blank lines are skipped.
- Longest line: 1 MiB. A longer line is refused (`badMessage`), and reading continues with the next line.
- Every message has `type` (anywhere in the object), `v` (protocol version, required), an optional `id` chosen by the sender,
  and `replyTo` = the `id` of the request a reply answers.
- Property names are camelCase, enums are camelCase strings (`"dry"`, `"real"`), and absent optional fields are left out.
- A message with another `v` gets `error` `versionMismatch`, and the connection is closed. An unknown `type` or bad JSON gets
  `error` `badMessage`.

## Planner → actioner

| type | Fields | Reply |
|---|---|---|
| `hello` | `client`, `clientVersion` | `welcome` |
| `loadPlan` | `path`: absolute path of a plan file | `planLoaded`, or `error` |
| `validate` | | `validated`: a fresh read of the account and the full check |
| `start` | `mode` (`dry` / `real`), `planId`, `sha256` | `started`, then `progress`… and `result`; or `error` (`refused`, `planMismatch`, `busy`) |
| `pause` | | `paused` (takes effect before the next action) |
| `resume` | | `resumed` |
| `abort` | `reason?` | `result` with outcome `aborted` (immediate) |

`start` carries the `planId` and `sha256` that the user confirmed in the planner (after seeing the steps and donors). The
actioner refuses with `planMismatch` when they are not the loaded plan's. It always reads the account again and refuses
(`refused`, with the reasons) when the check fails.

## Actioner → planner

| type | Fields |
|---|---|
| `welcome` | `server`, `serverVersion`, `executor` (`fake` / `game`), `gameInputEnabled` (off by default) |
| `planLoaded` | `planId`, `name`, `sha256`, `gameVersion`, `steps[]` (`number, artifactId, slot, piece, fromChampionId, fromName, toChampionId, toName`), `issues[]` (from reading the file) |
| `validated` | `canStart`, `issues[]` (`severity` `error`/`warning`, `code`, `message`, `step?`), `steps[]` (`number`, `state` `pending`/`done`, `displaced?`) |
| `started` | `mode`, `runId`, `logPath` |
| `progress` | `step` (0 = around the steps), `action` (e.g. `OpenChampion`, `Equip`, `VerifyStep`), `text`, `state` (`started`/`ok`/`failed`/`aborted`), `detail?`, `time` |
| `paused`, `resumed` | |
| `result` | `outcome` (`completed`/`aborted`/`failed`/`refused`), `doneSteps[]` (read from the account after the run), `failedStep?`, `message`, `logPath?`, `snapshotPath?`, `screenshotPath?` |
| `error` | `code` (`badMessage`, `versionMismatch`, `noPlan`, `planMismatch`, `busy`, `refused`, `internal`), `message` |

Issue `code`s are the actioner's (`ArtifactUpgraded`, `PieceMoved`, `GameVersionMismatch`, …; see `IssueCode`). They are
stable, so programs can use them.

## Example

```
→ {"type":"hello","v":1,"id":"1","client":"raid-extractor","clientVersion":"1.4.0"}
← {"type":"welcome","v":1,"replyTo":"1","server":"RaidActions","serverVersion":"0.1.0","executor":"fake","gameInputEnabled":false}
→ {"type":"loadPlan","v":1,"id":"2","path":"F:\\MyGithub\\raid-extractor\\output\\plans\\x.json"}
← {"type":"planLoaded","v":1,"replyTo":"2","planId":"5163…","name":"Suggestions 1 + 3 together","sha256":"8a5f…",…}
→ {"type":"start","v":1,"id":"3","mode":"dry","planId":"5163…","sha256":"8a5f…"}
← {"type":"started","v":1,"replyTo":"3","mode":"dry","runId":"20261023-141500-51638773","logPath":"…"}
← {"type":"progress","v":1,"step":1,"action":"OpenChampion","text":"Open Pelops the Victor","state":"ok","time":"…"}
…
← {"type":"result","v":1,"outcome":"completed","doneSteps":[],"message":"Dry run done: …","logPath":"…","snapshotPath":"…"}
```

## Notes for implementers

- Pipe writes are not flushed: flushing a pipe (`FlushFileBuffers`) blocks until the peer has read the data.
  `MessageChannel` skips the flush for `PipeStream`.
- Create the server pipe with in/out buffers (e.g. 64 KiB). With buffer size 0, a write waits for the peer's read.
- Read and write on separate tasks: `progress` arrives while the planner may be sending `pause` or `abort`.
