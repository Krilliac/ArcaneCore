# Instances options

9 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:Instances`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `HomebindTimerMs` | int | `60000` | a whole number, -2147483648 to 2147483647 | World | start | Time a player who lost the right to be in an instance (left the group, global reset) has before being sent to its bind point (vmangos `Player::UpdateHomebindTime`: 60 s). |
| `IgnoreRaidGroup` | bool | `false` | true \| false | World | start | Let players enter raids without a raid group (vmangos `Instance.IgnoreRaid`, default off). |
| `NormalDungeonResetSeconds` | int | `7200` | a whole number, -2147483648 to 2147483647 | World | start | A normal (non-raid) dungeon resets this long after it was created, but only while nobody is inside (vmangos `MapPersistentStateManager::AddPersistentState`: "if no creatures are killed the instance will reset in two hours"; the schedule is cancelled while players are in). |
| `PerHourLimit` | int | `5` | a whole number, -2147483648 to 2147483647 | World | start | New instances one account may enter per hour (vmangos `Instance.PerHourLimit`, default 5; `MAX_INSTANCE_PER_ACCOUNT_PER_HOUR` Player.h:669). 0 turns the limit off (a ArcaneCore convention: vmangos would refuse every new instance at 0). Game masters are exempt. |
| `RateResetTime` | float | `1` | a number, '.' is the decimal point | World | start | vmangos `Rate.InstanceResetTime` (World.cpp:543, setConfigPos, default 1): multiplies the reset period in days of every raid with a `reset_delay`, truncated and at least one day (ObjectMgr::LoadMapTemplate, ObjectMgr.cpp:6809-6811). A delay of 0 (no global reset) stays 0. Read when the raid schedules are built and at each global reset; a negative value is 1. |
| `ResetExtendsOnKills` | bool | `true` | true \| false | World | start | A creature kill in a normal dungeon moves the reset time to respawn + 2 h when later (vmangos Map::BindToInstanceOrRaid, Map.cpp:3536-3544). Default on (retail). |
| `ResetRefusedNoticeSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | Seconds between two "Please leave the instance so it can be reset." notices to the players inside an instance that a refused personal reset asks to leave (an ArcaneCore limit: the refusal itself is an ArcaneCore choice, see docs/integration/instances.md, and the requester can repeat CMSG_RESET_INSTANCES at will). The requester still gets SMSG_INSTANCE_RESET_FAILED every time. 0 sends the notice on every refusal. |
| `ResetTimeHour` | int | `4` | a whole number, -2147483648 to 2147483647 | World | start | Hour of the day (UTC, 0–23) of global raid resets (vmangos `Instance.ResetTimeHour`, default 4). |
| `UnloadDelayMs` | int | `1800000` | a whole number, -2147483648 to 2147483647 | World | start | How long an empty instance map stays loaded before it is unloaded (vmangos `Instance.UnloadDelay`, default 30 minutes). Its save and binds survive the unload. |
