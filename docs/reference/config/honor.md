# Honor options

12 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:Honor`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `CityProtector` | bool | `false` | true \| false | World | start | Assign the City Protector titles (vmangos default off). |
| `DishonorableKills` | bool | `true` | true \| false | World | start | Civilian kills below gray cost honor (vmangos CONFIG_BOOL_ENABLE_DK). |
| `Enabled` | bool | `true` | true \| false | World | start | Master switch of the honor system (kills, ranks, the weekly calculation). |
| `MaintenanceDay` | uint | `3` | a whole number, 0 to 4294967295 | World | start | Weekday of the weekly calculation (Sunday 0, clamped to 6). |
| `MaintenanceMode` | HonorMaintenanceMode | `Startup` | Live \| Startup | World | start | When the weekly calculation runs: Startup (default) or Live (an opt-in in-process weekly job). Values: `Live`, `Startup`. |
| `MinHonorKills` | uint | `0` | a whole number, 0 to 4294967295 | World | start | Honorable kills a week to be ranked; 0 selects 15 (MIN_HONOR_KILLS_POST_1_10). |
| `PoolSizePerFaction` | uint | `0` | a whole number, 0 to 4294967295 | World | start | Standing pool size per faction; 0 uses the number of ranked players. |
| `RacialLeaderExcludedEntries` | uint[] | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is a whole number, 0 to 4294967295 | World | start | Creature entries that are never racial leaders. |
| `Rate` | float | `1` | a number, '.' is the decimal point | World | start | Non-retail when not 1 (default 1): multiplies earned honor (kills, battleground bonuses, quests); Rate.Honor of the MaNGOS Zero fork and the cmangos/TrinityCore line (vmangos has none). A negative or non-finite value is 1. |
| `ReportDirectory` | string | `(empty)` | text | World | start | Directory that receives the vmangos HCR calculation report; empty writes none. |
| `RpDecay` | float | `0.2` | a number, '.' is the decimal point | World | start | Weekly rank point decay, clamped to 0..1. |
| `TimeZoneOffsetHours` | int | `0` | a whole number, -2147483648 to 2147483647 | World | start | Hours added to UTC for the game day and the weekday of the weekly calculation. |
