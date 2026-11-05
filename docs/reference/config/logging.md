# Logging options

17 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Logging:ArcaneCore`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Console:Mode` | ConsoleMode | `Color` | Color \| Plain \| Off | Both, Gateway | start | `Color` (default), `Plain` or `Off`. Live at reload; colour is still disabled when stdout is not a terminal or `NO_COLOR` is set. Values: `Color`, `Plain`, `Off`. |
| `Console:QueueCapacity` | int | `4096` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Lines the console writer thread may hold before new lines are dropped (and the drop counted and reported). Restart-only. |
| `File:Enabled` | bool | `false` | true \| false | Both, Gateway | start | `true` writes the one log file, in the chosen `Format`, to `Path`. Default off. |
| `File:Path` | string | `(empty)` | text | Both, Gateway | start | The active log file; relative paths resolve against the working directory. Rolled segments sit next to it as `name-yyyyMMdd-NNN.ext`. Not set means `logs/arcanecore.jsonl` for a Json file and `logs/arcanecore.log` for a Text file. |
| `File:QueueCapacity` | int | `8192` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Lines the file writer thread may hold before new lines are dropped (counted and reported in the file itself). The world thread never waits on the disk. |
| `File:Retain` | int | `14` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Rolled segments kept next to the active file; the oldest beyond this count are deleted after each roll. 0 keeps all. |
| `File:RollDaily` | bool | `true` | true \| false | Both, Gateway | start | `true` rolls at the first line of a new day (in the `Timestamps` clock). |
| `File:RollSizeMb` | int | `64` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Roll when the active file would exceed this many MiB; 0 never rolls by size. |
| `Format` | LogFileFormat? | `(empty)` | Text \| Json | Both, Gateway | start | The content format of the one log file: `Json` (one JSON object per line, structured properties kept) or `Text` (the plain console line format). Not set means the legacy `File`/`Json` switches decide; with nothing configured the default is `Json`: JSON keeps the structured state properties for ingestion, and the console already gives a text view. The file is configured under `File`. Restart-only. Values: `Text`, `Json`. |
| `IncludeScopes` | bool | `true` | true \| false | Both, Gateway | start | `true` appends the active logger scopes to each line (`=&gt; scope`). Live at reload. |
| `Json:Enabled` | bool | `false` | true \| false | Both, Gateway | start | Deprecated: use `File:Enabled` with `Format=Json`. `true` writes one JSON object per log event to `Path`. Default off. |
| `Json:Path` | string | `(empty)` | text | Both, Gateway | start | Deprecated: use `File:Path`. The active JSON-lines file; relative paths resolve against the working directory. Not set means `logs/arcanecore.jsonl`. |
| `Json:QueueCapacity` | int | `8192` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Deprecated: use `File:QueueCapacity`. Lines the JSON writer thread may hold before new lines are dropped (counted and reported as a JSON line). |
| `Json:Retain` | int | `14` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Deprecated: use `File:Retain`. Rolled segments kept; the oldest beyond this count are deleted after each roll. 0 keeps all. |
| `Json:RollDaily` | bool | `true` | true \| false | Both, Gateway | start | Deprecated: use `File:RollDaily`. `true` rolls at the first line of a new day (in the `Timestamps` clock). |
| `Json:RollSizeMb` | int | `64` | a whole number, -2147483648 to 2147483647 | Both, Gateway | start | Deprecated: use `File:RollSizeMb`. Roll when the active file would exceed this many MiB; 0 never rolls by size. |
| `Timestamps` | TimestampKind | `Utc` | Utc \| Local | Both, Gateway | start | The clock timestamps are taken from: `Utc` (default) or `Local`. Live at reload. Values: `Utc`, `Local`. |
