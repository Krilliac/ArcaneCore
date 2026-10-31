# Installation and first run

This guide takes a clean machine to a running realm and world daemon that a 1.12.1 client can log in to. It is checked against the
code by tests (every key, command, flag, path and default named here must exist), but the whole walk-through has **not** been run
end to end on a clean machine by an automated check, and no real 1.12.1 client has been recorded against these daemons yet
(see the [acceptance pages](../README.md#acceptance-procedures-real-client-or-mock-client)). Report anything that does not work.

Related pages: [configuration reference](../reference/configuration.md) (every key and default),
[database upgrade runbook](../ops/database-upgrade.md), [security hardening](../security/hardening.md),
[exit codes](../reference/exit-codes.md), [GM commands](../reference/gm-commands.md).

## 1. Prerequisites

- The .NET SDK named in `global.json` (11.0.100-rc.2; a newer 11.0 feature band is accepted).
- A database server for the three databases below, or nothing at all if you use SQLite (zero setup, development and tests).
  MariaDB is the primary engine; `MySql`, `PostgreSql` and `Sqlite` are the other `Database:*:Provider` values.
- A WoW 1.12.1 (build 5875) client on a machine that can reach the daemons. No client data is part of this repository, and none
  may be added to it; terrain and collision data, DBCs and world content dumps are supplied by you.

## 2. Build

```
dotnet build ArcaneCore.slnx -c Release
```

The executables are `src/ArcaneCore.Realm` (the logon daemon, port 3724) and `src/ArcaneCore.World` (the world daemon, port 8085), and the
tools in `tools/`: `arcane-account`, `arcane-db`, `arcane-content-importer`, `arcane-spell-import` and `arcane-mock`. Run any of them with
`dotnet run --project <path> -c Release -- <arguments>`.

## 3. The databases

ArcaneCore uses three logical databases: **auth** (accounts and the realm list, shared by every realm), **characters** (one per realm) and
**world** (static content). Each has its own `Database:<Component>` section with a `Provider` and a `ConnectionString`:

```json
{
  "Database": {
    "Auth":       { "Provider": "MariaDb", "ConnectionString": "Server=127.0.0.1;Port=3306;Database=arcanecore_auth;User=arcane;Password=arcane;" },
    "Characters": { "Provider": "MariaDb", "ConnectionString": "Server=127.0.0.1;Port=3306;Database=arcanecore_characters;User=arcane;Password=arcane;" },
    "World":      { "Provider": "MariaDb", "ConnectionString": "Server=127.0.0.1;Port=3306;Database=arcanecore_world;User=arcane;Password=arcane;" }
  }
}
```

The user `arcane` with password `arcane` is a development placeholder shipped in the sample files. Create your own user and put real credentials
in environment variables (for example `Database__Auth__ConnectionString`), not in a file you commit.

| `Provider` value | Connection string shape |
|---|---|
| `MariaDb` | `Server=host;Port=3306;Database=name;User=user;Password=...;` |
| `MySql` | the same MySQL-style string |
| `PostgreSql` | `Host=host;Port=5432;Database=name;Username=user;Password=...;` |
| `Sqlite` | `Data Source=path/to/file.db` |

The realm daemon only needs the auth database. The shipped `src/ArcaneCore.Realm/appsettings.json` uses the older single-database layout
(`Database:Provider` and `Database:ConnectionString` directly); a component without its own sub-section falls back to those two keys.

**Schema.** On first start each daemon creates the missing schema and upgrades an older one, because `Database:Upgrade:Policy` defaults to `Always`.
For production, switch to `CreateOnly` (create an empty database, refuse to upgrade an existing one) or `Never` (verify only) once the databases
exist, and apply upgrades deliberately with `arcane-db`; see the [upgrade runbook](../ops/database-upgrade.md). To move an existing SQLite install to PostgreSQL or MariaDB, use `arcane-db copy` ([runbook](../ops/database-upgrade.md#moving-from-sqlite-to-postgresql-or-mariadb)). The version table is in the
[schema reference](../reference/schema.md).

## 4. Configure the realm list

The world daemon is found through the realm list. On first start an empty realm list is filled from `Realms:Seed` in the Realm configuration
(name, `Address` as `ip:port` of the world daemon, type, flags, population, category). The shipped seed points at `127.0.0.1:8085`; change the
address to the one clients will use. Ports and bind addresses are `Auth:Port`, `Auth:BindAddress`, `World:Port` and `World:BindAddress`.

The world daemon keeps its own row current, as vmangos mangosd does: `World:Realm:Id` (default 1, vmangos `RealmID`) names the row. The row says
offline while the world is starting and until it accepts connections, online while its loop is ticking, and offline again when it stops cleanly.
A world that dies or hangs without saying so is shown offline once its heartbeat (`World:Realm:HeartbeatIntervalSeconds`, default 10) is older than
`Realm:Liveness:HeartbeatStaleSeconds` (default 60), and a realm shows red while a `.server restart` countdown runs; the realm list also carries
the account's character count per realm, which the client shows as a green realm name ([realm list liveness](../ops/realm-liveness.md)). Each new
session writes the population hint, 2 x sessions / `World:Realm:PlayerLimit` (default 100, vmangos `PlayerLimit`); the count is every session in the session registry, bot sessions included. A world with no matching row logs
a warning and runs on; `World:Realm:Id = 0` turns the writes off. A gateway worker (`Cluster:Worker:Enabled`) does not write the row; in that
topology the operator owns the row ([gateway](../areas/gateway.md)). If the world and logon daemons run on different hosts, keep their clocks
synchronised (NTP): the heartbeat is compared across them.

## 5. Create an account

Accounts are not created automatically: the shipped `Auth:AutocreateAccounts` is `false`, and the feature is a development convenience, not retail
behaviour. Create one with the account tool:

```
dotnet run --project tools/ArcaneCore.AccountTool -- create MYUSER
dotnet run --project tools/ArcaneCore.AccountTool -- set-gmlevel MYUSER administrator
```

The password comes from a no-echo prompt, from stdin with `--password-stdin`, or from the `ARCANE_ACCOUNT_PASSWORD` environment variable. A password
on the command line works but is visible in process listings and shell history. The tool reads the `Database` configuration of its own
`appsettings.json` (or `Database__...` environment variables), so point it at the same auth database as the realm daemon.

## 6. Import world content (optional but needed for a populated world)

World content is not shipped. `arcane-content-importer` reads cmangos classic-db or vmangos dumps (plain or gzip) and client DBCs into the world database:

```
dotnet run --project tools/ArcaneCore.ContentImporter -- plan classic-db-dump.sql
dotnet run --project tools/ArcaneCore.ContentImporter -- import classic-db-dump.sql --provider mariadb --connection-string "Server=127.0.0.1;Database=arcanecore_world;User=arcane;Password=..."
dotnet run --project tools/ArcaneCore.ContentImporter -- import-dbc path/to/DBFilesClient --provider mariadb --connection-string "Server=127.0.0.1;Database=arcanecore_world;User=arcane;Password=..."
dotnet run --project tools/ArcaneCore.ContentImporter -- verify --provider mariadb --connection-string "Server=127.0.0.1;Database=arcanecore_world;User=arcane;Password=..."
```

`import-dbc` also reads `Map.dbc` and `AreaTable.dbc` (and `WMOAreaTable.dbc`) from the same directory and fills `map_template`, `area_template` and `wmo_area_template` when they are empty (the dungeon columns from a dump, such as player limit, reset delay and ghost entrance, are not filled this way; `refresh --dbc-dir` or `import-map-dbc --replace --dump` brings them in); without them the World skips every instance portal. It likewise fills `areatrigger_template`, `taxi_nodes` and `taxi_path` from `AreaTrigger.dbc`, `TaxiNodes.dbc` and `TaxiPath.dbc` when those tables are empty, so the sequence above ends with a clean `verify` unless the dump has other missing references. Tables that already have rows are left as they are; `refresh <dump> --dbc-dir path/to/DBFilesClient` rewrites them. `verify` fails (exit 6) while any of those five tables is empty, and names the command that fills it.

`plan` reads the dumps and reports without writing anything. Every verb except `plan` needs a target: `--database <file>` for SQLite, or `--provider` (`sqlite`, `mariadb`, `mysql`, `postgresql`) with a connection string. The connection string can come from `ARCANECORE_CONTENT_CONNECTION` (keeps the password off
the command line), but the variable supplies only the connection string and still needs `--provider`. The importer refuses a database or report path that is inside a git work tree and not
ignored. Details and limits: [content import](../areas/content-import.md).

## 7. Check the configuration, then start the daemons

```
dotnet run --project src/ArcaneCore.World -c Release -- check-config
```

Use the configuration of the build in section 2: the world executable loads the Playerbots pack (`ArcaneCore.Playerbots.dll`) from its
own directory, the solution build puts it there, and a world without it exits 78. Building `src/ArcaneCore.World` alone does not
refresh the pack, so build the solution again after changing world code.

`check-config` validates the world configuration without starting it and exits with 78 when it is invalid; the daemon runs the same check at every
start, and `Startup:Strict=true` makes warnings fatal too. Then start the realm daemon, then the world daemon, in two terminals:

```
dotnet run --project src/ArcaneCore.Realm -c Release
dotnet run --project src/ArcaneCore.World -c Release
```

Supervisors should restart on exit code 2 (a restart request) and must not restart on 78; see [exit codes](../reference/exit-codes.md).

## 8. Connect a client

Put `set realmlist 127.0.0.1` (or the host that runs the realm daemon) in the client's `realmlist.wtf`, log in with the account from step 5, pick the
realm and create a character.

## Development runner

For local development, `scripts/dev-runner.ps1` starts a disposable realm and world pair on fresh SQLite databases with a generated Administrator
account, under `dotnet watch` hot reload:

```
powershell -File scripts/dev-runner.ps1
powershell -File scripts/dev-runner.ps1 -Status
powershell -File scripts/dev-runner.ps1 -Stop
```

Other flags: `-Name`, `-Reuse`, `-ContentDir`, `-Account`, `-RealmPort`, `-WorldPort`, `-NoBuild`, `-NoModules` and `-RestartOnRudeEdit`. It refuses to run in a
Production environment. The details are in [code hot reload](../areas/code-hot-reload.md).
