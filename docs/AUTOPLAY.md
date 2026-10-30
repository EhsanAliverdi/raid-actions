# Auto Play: technical design

The plan (goal, decisions, phases, status) is raid-extractor's [plan/autoplay.md](../../raid-extractor/plan/autoplay.md). This file
is how it is built and how to add an area.

## Layers

```
raid-extractor app (Auto Play page)          raid-actions (autoplay process)
  settings.json  ──── written by the page ───►  AutoplayEngine (scheduler)     RaidActions.Core/Autoplay
  stop.request   ──── written by Stop ──────►     ├─ IActivity: ClassicArenaActivity, (later: Inbox, Energy, Quests…)
  status.json    ◄─── written every pass ────     ├─ PlayContext: settings, clocks, events, human pauses
  events-<day>.jsonl ◄── one line per event ─     └─ IPlayGame ─┬─ LivePlayGame (clicks)  RaidActions.App/Play
                                                                 └─ FakeArenaGame (tests, --fake)
shared contract: raid-extractor src/RaidExtractor.Core/Autoplay (settings, status, ResourceClock, GameText, ArenaRules)
```

All files live in raid-extractor's `output/autoplay/` (never committed). The bot is its own process (the page starts it with
`RaidActions autoplay --dir <raid-extractor output>`, no redirected output), so it keeps playing when the app closes; the page finds it
again by `status.json` (fresh and its process alive).

## Engine rules

- Every pass: reload settings; stop request or master switch off → stop; outside active hours → rest; break due → rest; the person
  used the PC (GetLastInputInfo newer than the bot's own last input, `MouseInput.LastSentUtc`) → hands off for N minutes.
- Activities are asked in priority order (`Decide`: quick, no game reads). The first that wants to act runs one turn (`RunTurnAsync`:
  one battle, one refresh, one inbox visit). Otherwise the engine waits until the earliest time an activity named (re-checking
  every 5 s, and never a busy loop: an idle step always waits at least 1 s).
- An exception in a turn: event `error`, game sent home (`GoHomeAsync`), back-off 30 s, 60 s, 120 s…; `MaxErrorsInARow` (3) stops.
- Priority (Ehsan, 2026-10-30): Classic Arena, Energy, Faction Wars, Cursed City. The arena refreshes for free whenever nobody under
  the power limit is on the board (even with no token, so an opponent waits for the next one) and never while someone is.
- Energy (raid-extractor plan/energy.md): one activity for every place that spends energy; its settings are a priority list of rows
  (place, difficulty, stage or highest beaten, runs per day or unlimited, Super Raid) and sources per resource (keep, inbox, gem
  refills off unless switched on, with per-day / price / gems-to-keep limits). `EnergyPlanner` (raid-extractor Core) is pure and
  tested; the live dungeon route (`LivePlayGame.Energy.cs`) is built from the dump and **not run yet**.
- `--once`: stops when nothing is to be done now (dry runs, tests). `--dry-run`: the game shows (overlay picture) and does not press
  anything that spends or changes the account (Start, Refresh, Collect).

## Finding things on screen (PlayTargets)

Same principle as the gear targets: never a position. An element is found by the **view-model context the game binds to it**, read
from memory with the offsets in `config/ui-offsets.json` (names in `config/ui-names.json`, generated offline from the dump by
`scripts/gen-ui-offsets.ps1`).

| Kind | View | Found by |
| --- | --- | --- |
| `village-battle`, `village-inbox` | VillageHUD | BattleMapButtonContext, InboxButtonContext |
| `mode-arena` | BattleModeSelectionDialog | ArenaModesContainerContext |
| `arena-classic` | ArenaMap | the ArenaMapItemContext that ArenaMapContext keeps as `_arena1x1` |
| `arena-opponent` (Key = user id) | ArenaDialog | CurrentOpponentContext; `_opponent` → ArenaOpponent (UserId, Name, TotalPower, Status); the row's button named Battle/Start/Fight |
| `arena-refresh` | ArenaDialog | a button named Refresh, in ArenaBattleTabContext (`_freeRefreshAvailable`, `_freeRefreshTimeLeft`) |
| `arena-tab` (Id = order) | ArenaDialog | toggles under a "Tab" path; `ArenaDialogContext._tabIndex` 0 = battle |
| `team-start` | HeroesSelectionDialogToArena | the button named Start/Battle (rule to confirm live) |
| `battle-auto` | BattleHUD | a button named Auto; `BattleHUDContext._isAutoEnabled` |
| result | BattleFinishArenaDialog | `_battleResultType` (1 victory, 2 defeat, 3 draw), `_enemyCombatPower`; left by the existing `back` kind (never Replay/Next) |
| `inbox-collect` | InboxDialog | InboxItemContext (`_title`, `_message`, `_iconUrl`), its Collect/Claim button; tokens by text (`IsArenaTokens`) |
| HUD counters | any | UserResourceContext (`_resource`, `_value` "7/10", `_incrementStatusText` "05:12") |

Seen live 2026-10-29 (the dump's guesses corrected): many buttons have no context of their own and are bound **by path** instead:
the village's Battle (`BattleMap`) and Inbox (`Inbox`), the battle menu's Arena card (`Arenas`; its clickable `Panel` below) and Faction
Wars card (a binding with "Fraction"), the arena map's Classic Arena (`Arena1x1`: a label over the 3D scene, under a canvas inside the
view, which `UnityUi` now reads). The opponent's team power is the row's text ("198.04K", `_teamPower`); `ArenaOpponent.TotalPower` is the
whole account. The free refresh is `RefreshBlock/FreeResfreshAvailable_h/BTN_1` (the paid one under `FreeResfreshNotAvailable_h` is
forbidden). The team screens' Start is `Default_h`; our slots are `HeroSlotContext` under `MySquad`. The arena result closes with its
RETURN TO ARENA bar (`CloseBlock/Content`, no button component). A target that finds nothing writes a screen dump (every active
element: name, binding, context, rectangle) next to the run's overlays.

| Faction Wars | View | Found by |
| --- | --- | --- |
| `mode-fw` | BattleModeSelectionDialog | binding containing "Fraction" |
| `fw-crypt` (Id = HeroFraction) | FractionWarsMap | FractionWarsRegionItemContext (`_id`, `_name`, `_resourceCount`) |
| `fw-stage` (Id = stage id) | FractionWarsDialog | FactionWarsStageContext (StageContext `_id`, `_name` "Stage 11"); the row's Battle button |
| team | HeroesSelectionDialogToFractionWars | slots `HeroSlotContext._heroId` under `MySquad`; Start `Default_h` |
| result | BattleFinishFractionWarsDialog | BattleFinishPveDialogContext (`_battleResultType`, `_stars`) |

Where a context holds several buttons, the one whose name or binding says what it does is taken; when that is not exactly one the
target is left unclickable with a note listing the candidates. **`RaidActions autoplay-explore`** (read only) prints every target,
whether it may be clicked and why, the board, the HUD counters and the inbox: run it on each screen in the first live session and
fix the rules in `PlayTargets.cs`.

## Safety (in code)

- Allow-list: `LivePlayGame` clicks only `PlayTargets.Kinds` and the ways back (`back`, `dialog-close`, `screen-close`); a purchase
  view on top (Bank, Offer, Shop, Refill, Purchase) refuses every click but its close.
- No gems: refresh only when `_freeRefreshAvailable` is true; out of tokens the game's refill window is closed and the turn stops.
  The one exception is Energy's gem refills, only when switched on in its sources and within their limits; the live game does not
  buy yet (the refill window is to be explored first) and answers "not explored".
- Spending clicks (Start, Refresh, Collect) are shown, not pressed, in a dry run.
- One bot at a time: Auto Play holds the mutex `Local\RaidActions.Autoplay`; the clicking commands refuse while it is held.
- Ctrl+Alt+Esc aborts (the kill switch is registered for `autoplay`).

## Adding an area

1. Contract: a settings record in `AutoplaySettings` (a new property: older files get its defaults) and, if needed, pure rules.
2. Engine: an `IActivity` (Decide quick; one short turn per RunTurnAsync; counters and timers in `Status`), methods on `IPlayGame`
   in game terms, a fake for tests (virtual time: `FakeHost`).
3. Live: targets in `PlayTargets` (contexts from the dump: `dump/redux/cs/il2cpp.cs`; add fields to `config/ui-names.json` and run
   `scripts/gen-ui-offsets.ps1`), the flow in `LivePlayGame`, kinds added to the allow-list.
4. App: a card on the Auto Play page (move it out of "Coming later").
5. Live session with Ehsan: `autoplay-explore` on each screen, `autoplay --dry-run --once --enable`, then one real turn.

## Commands

```powershell
RaidActions autoplay-explore                        # read only: what Auto Play sees now
RaidActions autoplay --dry-run --once --enable      # navigate, show the battle and refresh clicks, press none, stop
RaidActions autoplay --once --enable                # play until nothing is to be done now
RaidActions autoplay --fake                         # the made-up arena (no game), for the page
```
