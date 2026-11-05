# Skills options

20 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Skills`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AlwaysMaxSkillForLevel` | bool | `false` | true \| false | World | start | vmangos AlwaysMaxSkillForLevel (default false). |
| `ChanceGreen` | uint | `25` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.Green (default 25). |
| `ChanceGrey` | uint | `0` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.Grey (default 0). |
| `ChanceOrange` | uint | `100` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.Orange (default 100). |
| `ChanceYellow` | uint | `75` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.Yellow (default 75). |
| `CraftingChanceCurve` | bool | `false` | true \| false | World | start | Opt-in, off by default: crafting skill-ups between a recipe's yellow and grey levels use the player-measured sliding chance `(grey - skill) / (grey - yellow)` instead of the yellow and green chances (not a datamined retail rule). |
| `DbcPaths` | string[] | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is text | World | start | The four DBC paths, in the order SkillLine, SkillRaceClassInfo, SkillTiers, SkillLineAbility. |
| `FlushDebounceMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | World | start | How long a changed skill table waits before it is written (ms); logout, shutdown and deletion flush at once. |
| `GainCrafting` | uint | `1` | a whole number, 0 to 4294967295 | World | start | vmangos SkillGain.Crafting (default 1). |
| `GainDefense` | uint | `1` | a whole number, 0 to 4294967295 | World | start | vmangos SkillGain.Defense (default 1). |
| `GainGathering` | uint | `1` | a whole number, 0 to 4294967295 | World | start | vmangos SkillGain.Gathering (default 1). |
| `GainWeapon` | uint | `1` | a whole number, 0 to 4294967295 | World | start | vmangos SkillGain.Weapon (default 1). |
| `MaxPrimaryTradeSkill` | int | `2` | a whole number, -2147483648 to 2147483647 | World | start | vmangos MaxPrimaryTradeSkill (default 2). |
| `MiningSteps` | uint | `75` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.MiningSteps (code default 75; the sample configuration says 0 = no decay). |
| `Mode` | SkillsMode | `Retail` | Retail \| Legacy | World | start | Which skill implementation the daemon runs. `Retail` (default) is the vmangos skill system and needs the build-5875 skill DBC paths below (without them the daemon logs an error banner and falls back to `Legacy`); `Legacy` is the pre-skill stand-ins for development hosts only. Values: `Retail`, `Legacy`. |
| `SkillLineAbilityDbcPath` | string | `(empty)` | text | World | start | Build-5875 SkillLineAbility.dbc (15 fields; 14 is also read). |
| `SkillLineDbcPath` | string | `(empty)` | text | World | start | Build-5875 SkillLine.dbc (22 fields). |
| `SkillRaceClassInfoDbcPath` | string | `(empty)` | text | World | start | Build-5875 SkillRaceClassInfo.dbc (8 fields). |
| `SkillTiersDbcPath` | string | `(empty)` | text | World | start | Build-5875 SkillTiers.dbc (33 fields). |
| `SkinningSteps` | uint | `75` | a whole number, 0 to 4294967295 | World | start | vmangos SkillChance.SkinningSteps (code default 75). |
