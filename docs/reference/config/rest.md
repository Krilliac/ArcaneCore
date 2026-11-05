# Rest options

5 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Rest`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AccrualIntervalSeconds` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Seconds of resting between two gains of rested experience (the reference adds the gain once at least 10 seconds have passed since the last one). The pool grows by the same amount per second whatever the interval; a longer one only makes the steps larger. Values below 1 count as 1. |
| `RateInGame` | float | `1` | a number, '.' is the decimal point | World | start | Rate.Rest.InGame: multiplier of the rested experience gained while resting in an inn or a capital city. 0 turns the gain off. |
| `RateOfflineInTavernOrCity` | float | `1` | a number, '.' is the decimal point | World | start | Rate.Rest.Offline.InTavernOrCity: multiplier of the rested experience gained while logged out, when the character logged out resting. |
| `RateOfflineInWilderness` | float | `1` | a number, '.' is the decimal point | World | start | Rate.Rest.Offline.InWilderness: multiplier of the rested experience gained while logged out, when the character did not log out resting. The gain is a quarter of the resting one at rate 1 (the reference divides it by four). |
| `SaveIntervalSeconds` | uint | `300` | a whole number, 0 to 4294967295 | World | start | Seconds between the writes of the rested state of every online character (the pool, the time and the resting flag; the time is what offline accrual counts from after a crash). A logout and a shutdown always write. 0 writes only then, so after a crash the stored time is that of the last logout and the whole session counts as offline time. |
