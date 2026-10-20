# Shipped content SQL

`sql/updates` carries the schema and nothing else ([sql/updates/README.md](../updates/README.md)). Content that ArcaneCore ships as
rows, so that it installs on a clean database and does not live only in someone's running database or a GM command session, lives
here:

```
sql/content/<component>/<provider>/NNNN_<name>.sql
```

`<component>` is `world` (the only one in use); `<provider>` is `sqlite`, `mariadb` or `postgresql`. `NNNN` is this folder's own
sequence, unrelated to the schema version; a file is never renumbered or reused.

| File | Needs world schema | Installs |
|---|---|---|
| `world/<provider>/0001_undercity_siege.sql` | 54 | game event 900 "Bot scenario: Undercity siege" and its `game_event_playerbot_*` rows ([bot event scenarios](../../docs/areas/playbots-scenarios-events.md)) |

## Rules every content file follows

- **Idempotent.** It deletes every row it owns, keyed on its own id (here the game event number), then inserts them, in one
  transaction. Running it twice leaves the same rows; rows of other ids are not touched. Re-running it is also how an edit to the
  content is applied.
- **One file per provider**, with the same rows. Identifiers are quoted the way the provider wants (`"x"` for SQLite and
  PostgreSQL, `` `x` `` for MariaDB); PostgreSQL boolean columns take `TRUE`/`FALSE`, the others `1`/`0`.
- **Needs its schema step first.** Apply the files of [`sql/updates`](../updates/README.md) up to the version in the table above (or run
  `arcane-db upgrade`), then the content file. A content file does not create tables.
- **A custom id range.** Game events 900 to 999 are reserved for shipped bot scenarios. The file deletes and recreates the `game_event` row
  of its id, so check an imported `game_event` table for a row of that number you want to keep before installing.
- **A test applies it** to a clean SQLite world database twice and compares the rows (`GameEventPlayerbotDataTests`), and the World.Tests
  project (`EventScenarioFeatureWiringTests`) loads the shipped SQLite file and validates the scenario it describes.

## Applying a file

Same clients and cautions as for the schema files ([sql/updates/README.md](../updates/README.md#applying-a-file)); take a backup first.

| Provider | POSIX shell / cmd.exe | PowerShell or any shell |
|---|---|---|
| SQLite | `sqlite3 -bail <db> < file.sql` | `sqlite3 -bail <db> ".read file.sql"` |
| PostgreSQL | `psql -v ON_ERROR_STOP=1 -d <db> -f file.sql` | same |
| MariaDB | `mariadb <db> < file.sql` | `mariadb --abort-source-on-error <db> -e "source file.sql"` |

The server reads the scenario tables when the playerbot pack attaches (server start or a playerbots hot reload), so restart or reload
after installing a file. Then `.event start 900` brings the scenario up.
