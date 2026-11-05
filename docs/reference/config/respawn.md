# Respawn options

9 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Respawn:Dynamic:CountBots` | bool | `true` | true \| false | World | live | `World:Respawn:Dynamic:CountBots`: count playerbots as players (default true). False counts only human players, so bots neither shorten nor take part in the scaling. |
| `Respawn:Dynamic:Enabled` | bool | `false` | true \| false | World | live | `World:Respawn:Dynamic:Enabled`: scale respawn delays with the players around the spawn. False (the default) leaves every delay as the database says, which is retail. When true this replaces `Creatures:Respawn:DynamicRate` (TrinityCore's zone-rate scaling, off by default) rather than compounding with it. |
| `Respawn:Dynamic:IncludeElitesAndRares` | bool | `false` | true \| false | World | live | `World:Respawn:Dynamic:IncludeElitesAndRares`: also scale elite, rare and rare-elite creatures (default false: only normal creatures scale). World bosses never scale, whatever this says. |
| `Respawn:Dynamic:IncludeGameObjects` | bool | `true` | true \| false | World | live | `World:Respawn:Dynamic:IncludeGameObjects`: scale the respawn delay of gathering nodes (herbs, mineral veins) and quest objects too (default true). Other game objects never scale. |
| `Respawn:Dynamic:MaxReduction` | float | `0.9` | a number from 0 to 1 ('.' is the decimal point); 0 never scales | World | live | `World:Respawn:Dynamic:MaxReduction`: the share of the base delay that scaling may take off, so the delay is never below `base * (1 - MaxReduction)` (0..1, default 0.9: at most a tenth of the base delay remains). 0 never scales. |
| `Respawn:Dynamic:MinimumSeconds` | uint | `30` | a whole number of seconds from 1 to 86400 | World | live | `World:Respawn:Dynamic:MinimumSeconds`: the delay is never scaled below this many seconds, and a delay already at or under it is left alone (1..86400, default 30). A corpse is removed when its respawn time is reached, so a value under the time players need to loot cuts loot short. The code has no separate corpse minimum, so this floor is it: the default 30 keeps a corpse lootable for at least 30 s. |
| `Respawn:Dynamic:PlayerThreshold` | int | `5` | a whole number from 0 to 10000 | World | live | `World:Respawn:Dynamic:PlayerThreshold`: players within `Radius` that are free; only the players above this many shorten the delay (0..10000, default 5). |
| `Respawn:Dynamic:Radius` | float | `100` | a number of yards from 5 to 500 ('.' is the decimal point) | World | live | `World:Respawn:Dynamic:Radius`: players are counted within this many yards (2D) of the spawn's home position (5..500, default 100). A radius is local to the contested spot, where a zone count is not, and is answered by the map's grid cells at the cost of the few cells it touches. |
| `Respawn:Dynamic:ScalePerPlayer` | float | `0.1` | a number from 0 to 10 ('.' is the decimal point); 0 never scales | World | live | `World:Respawn:Dynamic:ScalePerPlayer`: each player above `PlayerThreshold` adds this to the divisor, so the delay is `base / (1 + ScalePerPlayer * max(0, players - PlayerThreshold))` (0..10, default 0.1: 15 players over the threshold divide the delay by 2.5). 0 never scales. |
