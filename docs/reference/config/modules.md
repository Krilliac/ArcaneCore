# Modules options

6 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Modules:DuelReset`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Areas` | string | `12;14;809` | text | World | start | `DuelReset.Areas`: area ids separated by ';' (default Elwynn Forest, Durotar, Gates of Ironforge); "" or "0" means none. |
| `CooldownAge` | uint | `30` | a whole number, 0 to 4294967295 | World | start | `DuelReset.CooldownAge`: seconds a cooldown must have run before the start resets it. |
| `Cooldowns` | bool | `true` | true \| false | World | start | `DuelReset.Cooldowns`: reset cooldowns when the duel starts and restore them when it is won. |
| `Enabled` | bool | `false` | true \| false | World | start | Load the module at all. Off by default: it changes duels in Elwynn Forest, Durotar and the Gates of Ironforge. |
| `HealthMana` | bool | `true` | true \| false | World | start | `DuelReset.HealthMana`: fill health and power when the duel starts and restore them when it is won. |
| `Zones` | string | `0` | text | World | start | `DuelReset.Zones`: zone ids separated by ';'. "" means any zone (areas then do not matter); "0" means none. |
