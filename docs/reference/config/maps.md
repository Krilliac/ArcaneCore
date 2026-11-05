# Maps options

7 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Maps:DataDirectory` | string | `(empty)` | text | World | restart | Directory holding the extractor output (a `maps/` folder of `.map` files, as written by the vmangos/cmangos-classic map extractor — vmangos `DataDir`). Empty, or a directory without `maps/`, means no terrain: height, area and liquid lookups then report "no data" and nothing fails. |
| `Maps:GridActivationDistance` | float | `100` | a number, '.' is the decimal point | World | live | Radius around players whose grids are loaded (an active non-player object loads only its own grid; a grid stays active while something active is within the visibility range of it) (vmangos `Map::m_gridActivationDistance`, initialised from `Visibility.Distance.Continents` = 100). |
| `Maps:GridCleanUpDelayMs` | int | `300000` | a whole number, -2147483648 to 2147483647 | World | live | How long an idle grid stays loaded (vmangos `GridCleanUpDelay`, default 5 minutes, at least `MinGridDelayMs` — World.cpp `setConfigMin`). |
| `Maps:GridPrefetchDistance` | float | `200` | a number, '.' is the decimal point | World | live | How far beyond `GridActivationDistance` a player looks for grids that do not exist yet, so their terrain, vmap and navmesh tiles are read and parsed on the thread pool before the grid is created (`GridContainer.GridApproaching`); other objects, active ones included, look half this distance around themselves. 0 turns the prefetch off. An ArcaneCore addition (vmangos reads the tiles inside the update): it changes when the files are read, not what is loaded or when. It also turns off the arrival prefetch (the tiles of a teleport's destination are read when the teleport starts), which has a budget of its own so it never evicts the approach prefetches. |
| `Maps:GridUnload` | bool | `true` | true \| false | World | live | Whether idle grids are unloaded at all (vmangos `GridUnload`, default on). |
| `Maps:UpdateActiveCellsOnly` | bool | `true` | true \| false | World | live | On continents, update creatures only in the cells within `GridActivationDistance` of a player or active object (vmangos Map::UpdateCells → UpdateActiveCellsSynch, Maps/Map.cpp:815-857): a creature elsewhere in a loaded grid stands still, its AI and movement paused, until a player comes near; its respawn time is wall-clock and its corpse decay catches up on the first update (WorldObject::UpdateHelper::UpdateRealTime, Objects/Object.h:477-481). False updates every creature of the loaded grids each tick (the behaviour before 2026-10-11). |
| `Maps:VisibilityRelocationLowerLimit` | float | `10` | a number, '.' is the decimal point | World | live | How far (yards, 3D) a unit (player or creature) must move from where its last movement-triggered visibility pass was scheduled before the next one is (vmangos `Visibility.RelocationLowerLimit`, default 10, World.cpp:830; Unit::OnRelocated). Moves within it keep the visible sets of the last pass; the grey distance (`Map.VisibilityGreyDistance`) and the next pass cover the difference. Game objects get a pass on every move. 0 runs a pass on every move. |
