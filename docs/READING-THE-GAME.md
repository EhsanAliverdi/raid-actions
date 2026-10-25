# Reading the game (phase 2)

Everything here is read-only: no input, no focus change, no writes to the game. Game input will only ever come from
RaidActions.exe, after an explicit Start.

## UI state from memory

The game's UI is readable from memory through RaidExtractor.Core's session (`PROCESS_VM_READ`, with the game binary's hash
checked against the offsets). The path, with every hop checked by class name:

```
AppViewModel (static, SingleInstance<AppViewModel>._instance)
 → _overlayManager (OverlayManager) → _viewMaster (RaidViewMaster) → _views : List<ViewMeta>
     ViewMeta { Key : ViewKey, State : ViewState, View : RaidView, Visibility }
 → the HeroesInfoOverlay view (the champion screen) → BaseView.Context : HeroesInfoOverlayContext
     _heroId                                   the champion shown
     _selectedHeroContext._heroId              the selected champion
     _heroDetailsContext._heroId               the champion of the gear panel
     _heroDetailsContext._inventoryArtifactId  the artifact selected in the inventory picker
     _heroDetailsContext._activeTabIndex       the gear panel's tab (IntProperty)
 → _blockUi._blockingEventsCounter             > 0 while the UI is blocked (loading, transitions)
```

So before every Equip click, a read can confirm "the right champion, the right piece". `ChampionScreenState.Champion` is only
set when the screen, the selection and the gear panel all name the same champion.

**Battle in progress**: the battle's views (`BattleHUD`, `BattleLoadingDialog`, `BattlePauseDialog`, …, and every
`BattleFinish…` result screen) are open views. Names are matched from an explicit list, because many unrelated views contain
"Battle" (the always-open `BattleTextNotificationsView`, the Battle Pass). Not covered: a background (auto) battle running
while you are in menus has no view of its own.

**Popups**: overlays are views too, so the gear-related ones are visible by name: `SwapArtifactsOverlay`,
`RemoveArtifactOverlay`, `UndressArtifactOverlay`, `ConfirmArtifactsSwapOverlay`, `ArtifactEquipWarningOverlay` (faction,
grade or ascension warnings). Which one is the "take it off the other champion" prompt is still to be seen in-game.

### Offsets

`config/ui-names.json` lists the members (raid-extractor's `names.json` format). `scripts/gen-ui-offsets.ps1` runs
raid-extractor's **existing ConfigGen build** on a staging root in `output/offsets-staging`. That root holds junctions to
raid-extractor's `dump/`, `work/` and `upstream/` (read-only use; ConfigGen writes only `<root>/config/offsets.json`), and the
script writes the result to `config/ui-offsets.json`. Nothing in raid-extractor is written. Re-run it after a game update,
once raid-extractor has a new dump.

`config/ui-offsets.manual.json` holds what ConfigGen cannot resolve: `IntProperty._value` (a field of the generic base
`Property<int>`), 0x24 from the dump's C header. It stays **unverified** until checked in-game.

At run time the UI offsets are merged with raid-extractor's `config/offsets.json` (read) into `output/runtime`. They must
come from the same game build, or nothing is read.

## Live account read

`LiveGame.Account` uses Core's exporter on the running game, writing to `output/live` (never to raid-extractor), so
`RaidActions validate <plan> --live` checks a plan against the game as it is now.

## The window and captures

- `GameWindow.Find`: the single `Raid.exe` process and its `UnityWndClass` window; client area in physical pixels (the calls
  run per-monitor DPI aware), the window's DPI (96 = 100%, 144 = 150%), minimized, visible, cloaked, foreground.
- `WindowCapture.Take`: `PrintWindow` with full content, which works while the window is covered. A blank image is refused.
  Copying from the screen is only a fallback when the game is the foreground window. A minimized window cannot be captured.

## Screen recognition

- Anchors (OpenCvSharp, normalized cross-correlation, multi-scale): `gear.tabs`, `gear.total-stats` (the champion's gear
  panel), `champion-list.sort` (the "By Rank" sort box: it matches only with that sort), `picker.auto-equip` (the artifact
  picker), `popup.equip` / `popup.upgrade` (a piece's popup).
- Templates are cut from raid-extractor's `output/ui-reference` screenshots (`templates seed`) into `output/templates`. They
  are Plarium's images, so they stay local and are never committed. `templates recut` re-cuts them from a live capture and
  pins them to that window height, which then searches a narrow scale band.
- Gear slots: `config/ui-layout.json`, nine boxes relative to `gear.tabs` (measured on the reference screenshot, within 2 px).
  Status **unverified** until checked on the live window.
- The roster: matching champion portraits against tiles does **not** work. Tested against all 1,129 portraits, the best hits
  landed on empty tiles with scores around 0.6. Which tile is which champion will come from memory instead
  (`HeroesInfoOverlayContext._heroes`, the list as displayed), with only the tile geometry from the screen (phase 3).

## Commands

```
RaidActions window                 the game window: size, DPI, state
RaidActions capture [--out f]      a capture of the client area (output/captures)
RaidActions ui-state [--watch]     the UI from memory
RaidActions recognize [--image f]  anchors and gear slots, with an annotated image
RaidActions templates seed|recut
RaidActions validate <plan> --live
```
