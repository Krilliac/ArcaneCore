# HotReload options

6 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `HotReload`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ApplyConfigFileChanges` | bool | `false` | true \| false | World | start | Apply an edit of world.conf or appsettings*.json while the world runs: once the edits settle, the configuration is re-read through the same pipeline as `.reload config` (live keys change on the world thread, restart keys keep their value and are logged), whether or not `Commands` is on. Default false, the retail behaviour: vmangos has no file watcher, a mangosd.conf edit applies only through `.reload config` (ServerCommands.cpp:1016). True is an opt-in ArcaneCore convenience. Needs the host's file watching (`hostBuilder:reloadConfigOnChange`, default true). Read once when the world starts. |
| `BuildTimeoutMs` | int | `60000` | a whole number, -2147483648 to 2147483647 | World | start | How long a candidate may take to build (database read plus indexing) before the reload is abandoned. 0 = no limit. |
| `Commands` | bool | `false` | true \| false | World | start | The one switch for live reload. Default false: a production server does not carry the reload machinery (vmangos registers the `reload` root, Chat.cpp:1212, but ArcaneCore's operator-initiated hot reload is a development tool, so it is opt-in). When false the reload coordinator is not built, no reloadable is registered and the `.reload` root does not exist (the chat reply is "There is no such command"); `ApplyConfigFileChanges` then runs the config reload on a coordinator of its own that holds only `config`. A development server turns it on with `HotReload:Commands=true`. Read once when the world starts. |
| `CommitTimeoutMs` | int | `30000` | a whole number, -2147483648 to 2147483647 | World | start | How long to wait for the world thread to run the swap before abandoning the reload. A swap that has not started by then is cancelled and never runs later. 0 = no limit. |
| `EmptyTables` | EmptyTablePolicy | `Retail` | Retail \| KeepLoaded | World | start | What a reload does when item_template, game_tele or areatrigger_teleport comes back empty. Default Retail: vmangos clears the loaded rows first (ObjectMgr.cpp:3817, 10468, 7708), so the table ends up empty. Values: `Retail`, `KeepLoaded`. |
| `NegativeNumbers` | InvalidNumberPolicy | `Retail` | Retail \| Reject | World | start | What `.reload config` does with a negative interval or range. Default Retail: vmangos logs an error and uses the default (World.cpp:2949-2977 setConfigPos/setConfigMin). Values: `Retail`, `Reject`. |
