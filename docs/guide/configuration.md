# Configuration files (world.conf and realm.conf)

Each daemon reads its settings from a `.conf` file, in the style of MaNGOS `mangosd.conf` and `realmd.conf`: one `Key = Value` line per
option, a comment block above it with the description, type, default, allowed values and whether `.reload config` applies it. The shipped
templates list every key the daemon reads, bots, weather, Warden and everything else:

- `src/ArcaneCore.World/world.conf.dist` for the world daemon (`world.conf`),
- `src/ArcaneCore.Realm/realm.conf.dist` for the realm (logon) daemon (`realm.conf`).

Both templates are generated from the options classes, never maintained by hand, and a test fails until they are regenerated when a key is
added (see "Adding a key" below). The reference of every key with its type and default is [docs/reference/configuration.md](../reference/configuration.md).

## Using the files

1. Copy `world.conf.dist` to `world.conf` (and `realm.conf.dist` to `realm.conf`) next to the daemon, in the directory it starts in.
2. Keep the lines you want to change and delete the rest. A key that is absent keeps the default shown in the template. Copying the whole
   template unchanged sets every key to its default and therefore overrides every value of `appsettings.json` with the default.
3. Start the daemon. A `.conf` file that does not exist is simply skipped; the appsettings files keep working exactly as before.

Where the file is looked for, first match wins:

| Source | Meaning |
|---|---|
| `--config <file>` | this file; a file that does not exist is an error (exit code 78) |
| `ARCANECORE_CONFIG` | an environment variable naming a directory that holds `world.conf` and `realm.conf` |
| the content root | the daemon's working directory, or the directory given with `--contentRoot` |

## Precedence

Lowest to highest: appsettings.json < appsettings.{Environment}.json < world.conf (realm.conf for the realm) < user secrets (Development only) < environment variables < command line.
A key set in a higher source wins. Editing the `.conf` file while the daemon runs changes nothing by itself, as in vmangos: `.reload config` (or a restart) applies it, and which keys then take effect without a restart is the Reload line of each key (see "Reloading" below).

## Syntax

- `Key = Value`, one per line. A dot separates the levels: `World.Port` is the configuration path `World:Port`; the daemon also accepts the colon form. Keys are not case-sensitive.
- A line that starts with `#` or `;` is a comment. There are no trailing comments, because `#` and `;` can occur in values (connection strings).
- Text may be quoted; inside quotes only `\"` and `\\` are escapes. Booleans are `true` or `false`; numbers use `.` as the decimal point; time spans are `[d.]hh:mm:ss`.
- Lists use numbered keys (`.0`, `.1`, ...). Entries you write are appended to the default list, they do not replace it. Dictionaries use the entry name as the last part of the key.
- A key the daemon does not read is a start-up warning that names the closest known key. A malformed line stops the daemon with exit code 78.

## Command line

Every key can also be set on the command line with `--Section:Key=value`, for example `--World:Port=8085` for the world daemon or `--Auth:Port=3724` for the realm.
The daemons read their command line before anything starts, so a mistake cannot start a live server by accident:

- `--help`, `-h`, `-?` or `/?` prints the usage and exits with code 64; nothing is started.
- An unknown command, an unknown key, a key that belongs to the other daemon, or an option without a value is a usage error: the message is printed and the daemon exits with code 64.
- `--config <file>` names the `.conf` file, `--environment <name>` the host environment, `--contentRoot <dir>` the directory the files are read from.

## Commands

The first argument may be a command; a command ends with an exit code (0 success, 64 usage error, 78 invalid configuration).

| Command | What it does |
|---|---|
| `check-config` | Loads the whole configuration (appsettings, `.conf`, environment, command line), validates it and exits: 0 when it is valid, 78 when it is not. Nothing is started and no database is touched. |
| `write-config --defaults <file>` | Writes the documented defaults of every key, the same text as the `.conf.dist` template. `-` as the file writes to standard output. |
| `write-config --current <file>` | Writes the effective configuration of the profile: every key explicit, a value that differs from the default marked `# changed from default`. Use it to migrate an existing appsettings.json profile to a `.conf` file. Secrets are masked: the `Password=`/`Pwd=` part of every connection string and the whole value of any key named Password, Secret, Token or ApiKey are written as `********`, a masked key is never marked as changed, and an unknown key is masked by the same rule. Replace the placeholders by hand. |
| `write-config --current <file> --include-secrets` | Writes the secrets unmasked, for a lossless migration. Refused for `-` (standard output), so a secret never reaches a terminal or a log; the file contains real passwords: keep it out of version control. |
| `write-config ... --force` | Overwrites a file that already exists; without it an existing file is an error. |

Examples (build the solution first in the same configuration: the world executable needs the Playerbots pack beside it, which
building `src/ArcaneCore.World` alone does not produce, and exits 78 without it):

```
dotnet build ArcaneCore.slnx -c Release
dotnet run --project src/ArcaneCore.World -c Release -- check-config
dotnet run --project src/ArcaneCore.World -c Release -- write-config --current world.conf
dotnet run --project src/ArcaneCore.Realm -- write-config --defaults realm.conf
```

## Reloading

`.reload config` in the world console re-reads the configuration. The Reload line of a key says what happens: **Live** keys are applied to the running server,
**Restart** keys keep their old value and the server reports that a restart is needed, **Start-up** keys are only read when the daemon starts.

With `HotReload:ApplyConfigFileChanges = true` (default `false`) the world daemon does the same by itself when `world.conf` or an `appsettings*.json`
file is saved; it needs the host's file watching, `hostBuilder:reloadConfigOnChange`, which is on by default. Once the file has been quiet for half a second,
the configuration goes through the `.reload config` pipeline, whether or not `HotReload:Commands` is on: live keys change on the world thread,
restart keys keep their value, and one log line lists the changed keys that took effect and those that need a restart. Like `.reload config`, an
applied change ends the `.rates set` and `.server config set` overrides; a save that changes no value applies nothing. A file with a mistake is not
applied: the error is logged, the running values stay, and the next save is tried again. vmangos has no such watcher (an edit applies only through
`.reload config`), which is why the switch is off by default. The realm daemon reads its keys at start only.

## Adding a key

An options class property is a key as soon as it has a `///` summary, and its `[ConfigDaemons]` attribute (on the class, or on one property) says which daemon reads it.
After adding or changing a key, regenerate the catalog, the two templates and the reference page, then review the diff:

```
ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs
```

CI fails with this command in its message while the committed files differ from the generated ones, so a key can never reach a release without its documentation.
