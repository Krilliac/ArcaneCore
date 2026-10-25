# Area: Atlas dev UI (browser side)

Branch `claude/atlas-devui-ui`. The Atlas browser UI gets a game-master layer on top of the live view: a right-click context menu on the map,
click and Alt+drag selection of live entities, bulk actions with one confirmation, an action log with Undo, AI debug overlays and a GM command
palette. It talks to the world daemon only through the [action channel](atlas-actions.md) (`caps` and `action` on the map feed); it adds no
other path to the server. See [atlas.md](atlas.md) for the host.

Code: `tools/ArcaneCore.Atlas/Live/` (`DevUiCatalog.cs`, `LiveActions.cs`, `LiveActionCommands.cs`, `ActionLog.cs`, `ActionOverlays.cs`,
`DevUiModule.cs`) and `tools/ArcaneCore.Atlas/Web/js/ui/devui*.js` with `Web/devui.css`. One hook in `main.js` (`installDevUi`).
Tests: `tests/ArcaneCore.Atlas.Tests/Live/LiveActionCommandsTests.cs`, `ActionLogTests.cs`, `Web/devui.test.mjs`, `Web/devui-syntax.test.mjs`.

## Every UI action is a command

The UI never talks to the daemon itself; it calls Atlas commands, so a script, the `ctl` tool or an agent can do whatever a click does.

| Command | Does |
|---|---|
| `action.catalog` | The menu entries that apply now: the built-in catalog narrowed by the daemon's `caps` (templates it has, gated items hidden when the daemon refuses them), plus the daemon's command list, the selection and the operator. |
| `action.run` | One raw GM command line (the palette's typed line). |
| `action.at` | A point action: a template id, a point, arguments. |
| `action.on` | An entity action: a template id, target guids (at most 50), arguments. |
| `action.confirm` | Confirms a pending bulk or gated action with its code. |
| `action.log`, `action.undo` | The last 500 actions; run the inverse of one. |
| `devui.select`, `devui.selection`, `devui.character` | The shared selection (also published as the `devui.selection` event) and the operator's character. |

Arguments are validated and printed by `DevUiCatalog.PrintArgs` (plain names and numbers only; a line break or a space in a value is refused),
so a menu argument cannot turn into a second command.

## Catalog

`DevUiCatalog.All` is the single list of 23 actions (teleport, place creature or object, spawns, kill, heal, respawn, revive, despawn, set
level, faction and speed, move to a picked point, bot orders, AI pause and resume, AI debug). The menu is built from the catalog the host
returns, not from a list in the page. `Web/support/devui-catalog.json` in the tests is the same catalog, and a C# test keeps the two equal.
The daemon has no `catalog` op yet, so the Atlas catalog is the built-in one filtered by `caps.templates`; line items are shown unless gated,
and a gated item needs `caps.catalog`, the command in the daemon's list and no live-server marker.

## Gestures and keys

* Right click without movement (under 4 px, under 300 ms) opens the menu at the cursor; a right drag of 4 px or more pans as before, so the
  two never both happen. Menu on the keyboard: Shift+F10 or the Menu key.
* Click selects one live entity; Alt+drag draws a box and selects everything inside (at most 50). Escape clears the selection.
* Keys (all listed in `ui/devui-keys.js`, a test compares them with `hotkeys.js` and the movement keys): Ctrl+Shift+P palette, Alt+K kill,
  Alt+H heal, Alt+T teleport me, Alt+F follow, Alt+L log, Delete despawn, Ctrl+Z undo. Alt-modified keys are ignored by `hotkeys.js` and
  `nav.js`, which is why they are free.

## Bulk actions and confirmation

An action over more than one target (the daemon's `BulkConfirmAbove`) asks once: the dialog names the targets (first 12, then a count) and the
summary the daemon gave. The code is the daemon's when it asked, otherwise Atlas's own (60 s, at most 16 pending). Nothing runs before the
operator agrees.

## Undo

Each logged action stores its inverse when one exists: place becomes `creature delete` of the new guid, teleport and move-to go back to the
previous position (across maps too), kill becomes respawn per target, bot take and release and AI pause and resume are each other's opposite.
Edits such as set level or faction record why they have no undo. Undo is itself logged and marks the original undone.

## Palette

Ctrl+Shift+P: catalog actions first, then every GM command from the daemon's list, filtered by words of the path. The help line
(`Syntax: ...`) is read by `devui-syntax.js` into a form (numbers, text, choices, flags, optional parts); a line the grammar cannot read
becomes a free-text field. The test reads docs/reference/gm-commands.md and requires at least 95% of its leaf help lines to be handled (it
prints the count). The typed line is always the last choice.

## AI overlays

"AI debug" runs `.atlas debug` on a creature and draws the aggro and leash rings, home point, route and threat lines through the existing
overlay layer. It refreshes on its own timer at no more than 2 Hz and stops when the daemon refuses or the creature is gone.

## Not done

* The transport: `LiveActions` sends through an `IFeedRequester` service. Binding it to the live feed (`LiveFeed.RequestAsync`) waits for the
  branch that adds request/reply to the feed; without it the actions report `unavailable` and the menu shows only the local helpers.
* A run against a development daemon with screenshots has not been done.
* Undo of a placed game object is not implemented (the reply format for its guid is unverified). Edits (set level, faction, speed) have no undo.
* "Bookmark here" moves the camera, because bookmarks are made from the camera.
