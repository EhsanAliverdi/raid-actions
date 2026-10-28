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

`config/ui-offsets.manual.json` holds what ConfigGen cannot resolve, worked out from the dump's C header: `IntProperty._value`
(0x24) and `BoolProperty._value` (0x21), fields of the generic base `Property<T>`. Both were **verified in-game on 2026-10-26**:
the gear panel's `_activeTabIndex` followed the tab toggle that was on (0 → 1 → 0), and exactly one tile's `_isSelected` was set,
the champion on screen.

At run time the UI offsets are merged with raid-extractor's `config/offsets.json` (read) into `output/runtime`. They must
come from the same game build, or nothing is read.

## Click targets from Unity's UI (memory, not templates)

Where to click comes from the game's own UI objects, read from memory (read-only), not from template matching.

```
open view (ViewMeta.View, a managed RaidView)
 → UnityEngine.Object.m_CachedPtr → native MonoBehaviour → +0x20 GameObject
 → GameObject components[0] = its RectTransform → +0x28 TransformHierarchy, +0x30 index
 → hierarchy: localTransforms (TRS), parentIndices, deepChildCount, transforms[], nextIndices
```

- **The subtree** of a view is a depth-first walk along `nextIndices` for `deepChildCount` nodes. Subtrees are not always
  contiguous index ranges: the Arena HUD's range had 84 empty slots.
- **Per element**: name, active self / in hierarchy, components, and the managed script on each component. A script counts
  only if its `m_CachedPtr` points back to that component. Also read: the bound view-model context (any `BaseView`'s `Context`)
  and `Selectable.m_Interactable`.
- **Screen rectangle**: the RectTransform's cached rect (local units) is transformed by the world matrix (TRS composed up
  the parents). Raid's UI canvases are **Screen Space – Overlay**: the canvas root sits at the client centre, scaled by the
  canvas scaler (1600/1920 = 0.8333 at 1600×900), so world coordinates are client pixels with the origin at the bottom left.
  Any other canvas mode, or a canvas that does not fit the current client size (e.g. a minimized game), gives **no
  rectangle**, and a step on it refuses.
- The native layouts are not in the IL2CPP dump. They were worked out on the running game with `RaidActions probe` and are
  listed with how each was established in `config/ui-native.json` (verified / unverified). The reader refuses if the game
  version differs from that file's. Managed fields (`m_CachedPtr`, `Selectable.m_Interactable`, `CanvasScaler`) come
  through ConfigGen as before.
- **Ids, not positions**: list items (champion tiles, inventory items, chat lines, …) each have their own context object,
  so a target is "the element whose context is a hero item with id X", never a position worked out from a sort order. The
  exact context classes and id fields of the champion screen are taken from its live tree (`ui-tree`) and added through
  ConfigGen.
- **Targets** (`UiTargets`), each with the id it stands for:

  | Target | Found by | Id |
  |---|---|---|
  | champion tile | the tile's `HeroSlotContext._heroId` (the list is virtual: 61 pooled tiles for 224 champions) | champion id |
  | inventory item | `InventoryArtifactContext.Avatar.Artifact.Id` | artifact id |
  | gear slot | `EquipmentArtifactSlot (n)` / jewelry slots, slot from `BindingMasterPath` (`WeaponSlot` … `ClothesSlot` = Chest, `CloakSlot` = Amulet) | slot id |
  | gear tab | toggles `Tabs/1..4` (`m_IsOn`) | tab number |
  | popup | `EquipButton_h`, `RemoveButton_h`, `HideButton` | — |
  | manage gear dialog | `Unequip_h` (Remove), `BTN_Close1` | — |

  Targets are named as the game's UI shows them (Ring / **Amulet** / Banner); the slot key is the game's data id
  (`ArtifactKindId.Cloak` = the amulet), which plan files use. A target may be clicked only when active, interactable, on screen
  and **fully inside its scroll viewport**. `UiTargets.Resolve` refuses when no element or **more than one** element claims the
  id (pooled list items keep stale ids). Descendants are found by tree position, never by path text: sibling tiles share names.
- **Checked live on 2026-10-26** (Kael's champion screen): the memory rectangles landed on every tile, gear slot and tab at
  **1600×900** and at **1280×720**. Every rectangle at 1280×720 is the 1600×900 one times 0.8 (canvas scale 0.833 → 0.667).
  Tile ids matched the champions shown (Kael = 1, selected), and the bottom rows were refused as outside the list's viewport.
  Placeholder tiles (id 0 or −1) are not targets.
- **Never clicked**: `Sell_h` (manage gear: Sell, which sells the piece at once), `SelectAll_h` (the sell panel's Select all),
  `QuickEquipButton` (Auto-Equip), `PowerUpButton` (Upgrade).
- **Sell mode** (2026-10-28, the `sell` command only; swap runs never have these clicks): the picker's `SellMode` toggle (binding
  `DisplayMode.SellArtifactsButton`) opens `SellArtifactsPanel_h` with `Cancel` (sell-cancel) and `Apply` (sell-apply, the panel's
  Sell). What is ticked is read from memory: `HeroesInfoOverlayContext._inventoryContext` (InventoryViewContext) → `_sellArtifacts`
  (SellArtifactsContext) → `_sellArtifactsCount` and `_markedForSell` (the items' InventoryArtifactContext → Avatar → Artifact.Id); each
  item also has `_withSellMark` (ticked) and `_hasOwner` (worn). Apply opens the game's `MessageBox` view: `MessageBoxContext._titleText`
  / `_mainMessage` ("Are you sure you want to sell 3 Artifacts? (0 equipped)") and two buttons with `MessageBoxButtonContext` (their
  `ButtonContextBase.Text`: "Cancel", "Sell"); its X is `BTN_Close1_h`. The confirmation's Sell is clicked only once per list, after a
  one-time unlock, when the text names the planned count and "(0 equipped)" and the ticked list is exactly the plan's pieces.
- **The picker's lists**: all six gear slots are one list, grouped by set (a helmet shows while the weapon slot's list is scrolled), so a
  sale opens it from the Weapon slot and sweeps it once from the top; accessories (ring, amulet, banner) are the other list, opened from
  the Ring slot. The list keeps only the items near the viewport in memory (pooled, some with old rectangles), so the order of the pieces
  is found while sweeping, the top is where a turn no longer changes the pieces in view, and the list is read again once it stops gliding
  after a wheel turn. A single wheel event carrying many notches counts as one notch in the game: long turns are many notches 8 ms apart.
- **Overlay check**: `RaidActions overlay` captures the window and draws the memory rectangles on it (green clickable, red
  not interactable, cyan bound context) in `output/overlays`.
- **Templates are only a cross-check**: a template that matched somewhere else than the memory rectangle (overlap under
  50%) makes the step refuse; a template that did not match at all does not.

```
RaidActions ui-tree [View] [--watch --for <min>]   element trees with rectangles (output/ui-trees), snapshot on every change
RaidActions ui-rects [View]                        clickable elements: rectangle, interactable, context
RaidActions overlay [View]                        memory rectangles on a capture (output/overlays)
RaidActions probe views | dump <hex> [len] | class <hex>
```

## Live account read

`LiveGame.Account` uses Core's exporter on the running game, writing to `output/live` (never to raid-extractor), so
`RaidActions validate <plan> --live` checks a plan against the game as it is now.

## The window and captures

- `GameWindow.Find`: the single `Raid.exe` process and its `UnityWndClass` window; client area in physical pixels (the calls
  run per-monitor DPI aware), the window's DPI (96 = 100%, 144 = 150%), minimized, visible, cloaked, foreground.
- `WindowCapture.Take`: `PrintWindow` with full content, which works while the window is covered. A blank image is refused.
  Copying from the screen is only a fallback when the game is the foreground window. A minimized window cannot be captured.

## Window size

**Any size works.** Click targets come from memory rectangles, which scale with the window (checked live at 1600×900 and
1280×720: every rectangle ×0.8). A click is refused only when the game window is **minimized** (or hidden) or **smaller than the
minimum client area** in `config/window.json`. In that case the run restores or enlarges the window **itself** (no y/n) to the
minimum, the smallest size that works, **in place** (its top-left corner stays; only pulled back on screen if needed), and **leaves it at that size** after the run (2026-10-28: no putting back, so the next run needs no resize). Before 2026-10-28 it was put back: same size, position and state, minimized again if it
was (`GetWindowPlacement` / `SetWindowPlacement`, without activating it).

The minimum keeps the smallest click target (a champion tile or a picker item) at least **40 px** at the window's aspect ratio,
and is never below the floor of **1280×720**. `RaidActions window minimum` measures it from the champion screen's targets in
memory. Below the floor it fits the window to the floor to measure precisely, then puts the window back. Measured on
2026-10-26: the smallest target was 61 px at 1280×720 (picker items; champion tiles 59 px), so 40 px needs only about
870×490 and **the floor decides: 1280×720**.

`RaidActions window fit [--size WxH]` restores and sizes the window to the minimum, or to the given size, centred in the work
area. Captures, recognition and overlays work at any usable size.

## Screen recognition

Templates scale to the current window. Re-cut templates search a band around the capture's height divided by the height they
were cut at; seed templates search all scales. The template cross-check is optional: a template that does not match is only
noted and **never blocks a step on its own**. Only a template that matched *somewhere else* than the memory rectangle refuses.


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
RaidActions window                 the game window: size, DPI, state, usable for clicks
RaidActions window fit [--size WxH] restore / size it to the minimum (or the given size)
RaidActions window minimum         measure the minimum size from the champion screen
RaidActions capture [--out f]      a capture of the client area (output/captures)
RaidActions ui-state [--watch]     the UI from memory
RaidActions recognize [--image f]  anchors and gear slots, with an annotated image
RaidActions templates seed|recut
RaidActions validate <plan> --live
```
