# Collision options

6 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:Collision`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `EnableHeight` | bool | `true` | true \| false | World | start | Use vmaps for floor heights (vmangos `vmap.enableHeight`, default on). |
| `EnableLineOfSight` | bool | `true` | true \| false | World | start | Use vmaps for line of sight (vmangos `vmap.enableLOS`, default on). |
| `EnablePathfinding` | bool | `true` | true \| false | World | start | Use navmeshes for paths (vmangos `mmap.enabled`, default on). |
| `MMapDirectory` | string | `(empty)` | text | World | start | Directory of the generated navmeshes (`NNN.mmap`, `NNNXXYY.mmtile`). Empty: `mmaps/` under `World:Maps:DataDirectory` (vmangos `DataDir/mmaps`). |
| `NavTileReadConcurrency` | int | `2` | a whole number, -2147483648 to 2147483647 | World | start | The most navigation tile files (`NNNXXYY.mmtile`) read at the same time by bots' path searches, in the background. Reads share the data disk with the map, so a low number keeps a cold start from starving it; a tile a search waits for is then ready a little later. At least 1. |
| `VMapDirectory` | string | `(empty)` | text | World | start | Directory of the extracted vmaps (`NNN.vmtree`, `NNN_YY_XX.vmtile`, `*.vmo`). Empty: `vmaps/` under `World:Maps:DataDirectory` (vmangos `DataDir/vmaps`). |
