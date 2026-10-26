<p align="center"><b>ArcaneCore</b></p>

[![CI](https://github.com/Krilliac/ArcaneCore/actions/workflows/ci.yml/badge.svg)](https://github.com/Krilliac/ArcaneCore/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Krilliac/ArcaneCore/actions/workflows/codeql.yml/badge.svg)](https://github.com/Krilliac/ArcaneCore/actions/workflows/codeql.yml)
[![Docker smoke](https://github.com/Krilliac/ArcaneCore/actions/workflows/docker-smoke.yml/badge.svg)](https://github.com/Krilliac/ArcaneCore/actions/workflows/docker-smoke.yml)
[![Nightly (Debug)](https://img.shields.io/badge/nightly%20(Debug)-download-blue?style=flat-square)](https://github.com/Krilliac/ArcaneCore/releases/tag/nightly)
[![Last commit](https://img.shields.io/github/last-commit/Krilliac/ArcaneCore.svg?style=flat-square&logo=github&logoColor=white)](https://github.com/Krilliac/ArcaneCore/commits/main)
[![PRs welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg?style=flat)](docs/guide/contributing.md)

---

# ArcaneCore - Vanilla WoW 1.12.1 server

**ArcaneCore** is an open source World of Warcraft **1.12.1 (client build 5875)** server emulator, written from scratch in
**C# / .NET 11**. It stores game data in [MariaDB](https://mariadb.org/), MySQL, PostgreSQL or SQLite through EF Core.

If you liked the original incarnation of World of Warcraft and want to run it yourself, this is the project for you. It provides a
logon (realm) server that manages accounts and the realm list, and a world server that serves the game, the way the 1.12 retail
servers did. It targets build 5875 only: other client builds are not supported.

The binding design rules are in [`ARCANECORE_CHARTER.md`](ARCANECORE_CHARTER.md). Behaviour follows retail 1.12.1, checked against
the [mangos, cmangos and vmangos](#credits) families and other references; a deliberate deviation sits behind an option that defaults
to retail.

## Features and status

- **Two daemons**: `ArcaneCore.Realm` (logon, TCP 3724) and `ArcaneCore.World` (world, TCP 8085), plus an optional client-socket
  gateway (`ArcaneCore.Gateway`, [Gateway passthrough](docs/areas/gateway.md), not accepted with a real client).
- **Playerbots**: a bot pack that the world executable loads from its own directory.
- **Databases**: MariaDB (primary), MySQL, PostgreSQL 16 and SQLite, with schema creation, additive upgrades and an operator upgrade
  tool (`arcane-db`).
- **Operations**: `.conf` files in the mangos style, a `check-config` verb, Prometheus metrics ([metrics](docs/ops/metrics.md)),
  watchdog heartbeat, Docker images and a production compose file.
- **Tools**: account CLI (`arcane-account`), content importer (`arcane-content-importer`), `arcane-db`, a mock client
  (`arcane-mock`) and the Atlas map and collision visualizer (`arcane-atlas`, [Atlas](docs/areas/atlas.md)).

> Status: **M1-M6** implemented and automatically verified (M5: world-thread runtime, generated protocol tables, persistence,
> multi-engine schema management; M6: logout, chat, /who, account settings, action bars, GM commands); awaiting real-client
> acceptance. Scope and order of the next milestones: [`docs/ROADMAP.md`](docs/ROADMAP.md). Everything beyond M6 is likewise verified
> by automated tests only. The documentation index is [`docs/README.md`](docs/README.md); every configuration key and default is in
> the generated [configuration reference](docs/reference/configuration.md).

This integration candidate also combines creatures, grids/terrain/teleports, items, spells, social systems, and the partial
quests/NPC draft. Exact branch heads, schema allocations, integration repairs, validation, and remaining client acceptance work are in
[the fleet integration record](docs/integration/fleet-20261003.md). Later NPC, instance, AI, loot, progression, spell persistence and
economy work is in [the takeover ledger](docs/integration/takeover-20261003.md). Continue from
[the Claude handoff](docs/integration/claude-handoff-20261003.md).

The [M13a follow-up](MILESTONE_M13A.md) restores saved quest journals before login, serves quest/NPC text queries, and persists timed
expiry across relogs and map transfers. Full quest/NPC interaction and [real-client acceptance](docs/M13A_ACCEPTANCE.md) remain
pending. The [M13b slice](MILESTONE_M13B.md) adds guarded live-creature quest status, details, acceptance and abandonment for
ordinary non-repeatable quests; rewards, objective event adapters and broader NPC services remain subsequent work.

The native [mock client](MILESTONE_MOCK_CLIENT.md) exercises the real realm and world sessions with synthetic SQLite content and
independent client packets. [Mock checks](docs/MOCK_CLIENT_ACCEPTANCE.md) run in CI; actual build 5875 UI and content acceptance will
follow separately. After building Release, run its bounded authentication, character, journal and reconnect scenario with:

```bash
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

It prints assertion results as JSON and returns a failing exit code if a stage fails.

## Requirements

- **Run a download or a Docker image**: nothing but the operating system and a database server. The archives are self-contained (no
  .NET install) and the images carry their runtime. SQLite needs no server at all.
- **Build from source**: the .NET SDK named in [`global.json`](global.json) (.NET 11).
- **Database**: MariaDB (primary, and what the compose files use), MySQL, PostgreSQL 16 or SQLite.
- **Client**: a WoW 1.12.1 (build 5875) client. Client data (DBC files, extracted maps, vmaps, mmaps) and world content dumps are not
  part of this repository and may not be added to it; you supply them ([Client data](#client-data)).
- **Platforms**: Windows and Linux (x64 and arm64) on the download page; any Linux host with Docker for the images (`linux/amd64` and
  `linux/arm64`).

## Download

Prebuilt binaries are attached to [GitHub releases](https://github.com/Krilliac/ArcaneCore/releases). Asset names never change, so
these links are stable.

**Nightly is a Debug pre-release**, rebuilt on every push to `main` and replaced in place; the live development server runs the same
configuration. Tagged `v*` releases are built in Release configuration.

| Platform | Nightly (Debug) |
|---|---|
| Windows x64 | [arcanecore-win-x64.zip](https://github.com/Krilliac/ArcaneCore/releases/download/nightly/arcanecore-win-x64.zip) |
| Linux x64 | [arcanecore-linux-x64.tar.gz](https://github.com/Krilliac/ArcaneCore/releases/download/nightly/arcanecore-linux-x64.tar.gz) |
| Windows arm64 | [arcanecore-win-arm64.zip](https://github.com/Krilliac/ArcaneCore/releases/download/nightly/arcanecore-win-arm64.zip) |
| Linux arm64 | [arcanecore-linux-arm64.tar.gz](https://github.com/Krilliac/ArcaneCore/releases/download/nightly/arcanecore-linux-arm64.tar.gz) |
| Checksums | [SHA256SUMS](https://github.com/Krilliac/ArcaneCore/releases/download/nightly/SHA256SUMS) |

The same file names exist under every release tag, for example `.../releases/download/<tag>/arcanecore-linux-x64.tar.gz`. The arm64
archives are built on a best-effort basis: when an arm64 build fails, the release has no arm64 archive for that run (assets of earlier
runs are removed, never left behind next to newer x64 ones), so check `SHA256SUMS` for what the release carries.

Each archive unpacks to `arcanecore-<platform>/`, self-contained and laid out like the Docker images, plus the `*.conf.dist` files of every key
(the images do not carry those):

```
world/       ArcaneCore.World, the Playerbots pack beside it, world.conf.dist, appsettings.json
realm/       ArcaneCore.Realm, realm.conf.dist, appsettings.json
tools/       arcane-content-importer
dbupgrade/   arcane-db
account/     arcane-account
```

**Docker images** (`linux/amd64` and `linux/arm64`), published to `ghcr.io` by the [publish workflow](.github/workflows/docker-publish.yml).
GHCR packages start private: the images are public once the repository owner sets each package (`arcanecore-world`, `arcanecore-realm`,
`arcanecore-tools`) to public visibility, a one-time step after the first push ([Docker guide](docs/guide/docker.md#image-tags-and-the-registry)):

```bash
docker pull ghcr.io/krilliac/arcanecore-world:edge      # edge: every push to main, Debug configuration
docker pull ghcr.io/krilliac/arcanecore-realm:edge
docker pull ghcr.io/krilliac/arcanecore-tools:edge      # content importer, arcane-db, arcane-account
```

Version tags (`v*`) are Release builds (a tag run pushes only the version tag); `sha-<7 hex>` pins the commit of a push to `main` (Debug).
Running them: [Docker guide](docs/guide/docker.md).

## Build from source

```bash
dotnet build ArcaneCore.slnx -c Release
dotnet test  ArcaneCore.slnx -c Release
```

Use `-c Debug` for the Debug configuration. The solution build puts the Playerbots pack beside the world executable; building
`src/ArcaneCore.World` alone does not, and a world without it exits 78. Build the solution (or `src/ArcaneCore.Playerbots`) in the
configuration you run, and again after changing world code. The data-layer tests run against MariaDB and PostgreSQL when
`ARCANECORE_TEST_MARIADB` / `ARCANECORE_TEST_POSTGRES` hold a server connection string (CI provides both).

Contributors: [`docs/guide/contributing.md`](docs/guide/contributing.md), [`CLAUDE.md`](CLAUDE.md) (the repository's working guide) and
[`docs/conventions.md`](docs/conventions.md).

```
src/
  ArcaneCore.Kernel         domain models + data seams (clustering boundary)
  ArcaneCore.Cryptography   WoW-flavor SRP6 (verified against KAT vectors)
  ArcaneCore.Protocol       generated opcodes, packet reader/writer, MovementInfo, header cipher
  ArcaneCore.Game           world thread (WorldRuntime), maps, objects, generated update fields
  ArcaneCore.Data           EF Core stores, schema bootstrapper; MariaDB / MySQL / PostgreSQL / SQLite
  ArcaneCore.Realm          logon/realm daemon (TCP 3724)
  ArcaneCore.World          world daemon (TCP 8085): sessions, opcode table, handlers, chat commands, save queue
tools/
  ArcaneCore.AccountTool    account create / set-password / set-gmlevel / list CLI (`db ...` forwards to arcane-db)
  ArcaneCore.Atlas          arcane-atlas: 3D map, collision, navmesh, diff and live world visualizer (Web UI / CLI / MCP)
  ArcaneCore.DbUpgrade      arcane-db: database upgrade tool (status / plan / check / upgrade / backup-info)
  ArcaneCore.MockClient     owned-loopback realm/world protocol acceptance CLI
  codegen/                  generates WorldOpcode.g.cs + UpdateFields.g.cs from the references
tests/
  ArcaneCore.Atlas.Tests          Atlas visualizer, reference readers, diff, headless renderer and live feed tests
  ArcaneCore.Cryptography.Tests   SRP6 known-answer + round-trip tests
  ArcaneCore.Realm.Tests          logon loopback handshake tests
  ArcaneCore.Game.Tests           update pipeline, maps, world runtime (no sockets)
  ArcaneCore.Data.Tests           schema + stores on SQLite, MariaDB, PostgreSQL
  ArcaneCore.World.Tests          end-to-end world daemon over loopback
  ArcaneCore.MockClient.Tests     independent client vectors and realm/world lifecycle
```

## Docker

`docker compose up -d --build` builds and starts MariaDB, the realm and the world from this checkout. For a VPS, pull the published
images instead with [`docker-compose.prod.yml`](docker-compose.prod.yml) (log rotation, restart policies, healthchecks, a backup
script). Passwords, ports, the firewall, client data upload, moving an existing database, updating and Debug versus Release images are
in the [Docker guide](docs/guide/docker.md).

## Setup

The full walk-through is [`docs/guide/installation.md`](docs/guide/installation.md). In short, from a download or a build:

1. **Database**: provision a MariaDB/MySQL/PostgreSQL database (or use SQLite) and set the connection strings (below). Each
   component keeps a version row (`auth_schema`, `characters_schema`, `world_schema`). Startup creates missing schemas, adopts M1-M4
   databases, applies additive upgrades and **refuses to start** on anything else. World data (start positions, race appearance,
   class stats) is seeded into the world database until the content importer fills it. Upgrading a production database between
   versions is an operator procedure with a dry run, a backup gate and a drift check, `arcane-db` (see
   [docs/ops/database-upgrade.md](docs/ops/database-upgrade.md)). By default every start still creates and upgrades the schema;
   `Database:Upgrade:Policy` (`CreateOnly` or `Never`) makes a start refuse to upgrade an existing database instead, the way the
   retail servers do.
2. **Realm list**: on first start an empty realm list is filled from `Realms:Seed` in the realm configuration; the address is what
   the client is sent to for the world.
3. **Account**: `arcane-account create MYUSER` (from a download: `account/arcane-account`; from source:
   `dotnet run --project tools/ArcaneCore.AccountTool -- create MYUSER`). It prompts for the password without echo, or reads
   `--password-stdin`, or `ARCANE_ACCOUNT_PASSWORD`; a password on the command line still works but is visible in process listings
   and shell history, and prints a warning.
4. **Start**: the realm daemon, then the world daemon (`realm/ArcaneCore.Realm`, `world/ArcaneCore.World`, or
   `dotnet run --project src/ArcaneCore.Realm -c Release` and `dotnet run --project src/ArcaneCore.World -c Release`). Run the
   world's `check-config` verb first to validate the configuration without starting anything. The world daemon shares the auth
   database with the realm daemon (it reads the session key produced at logon), so its `Database:Auth` connection must point at the
   same database as the realm's.
5. **Client**: put `set realmlist 127.0.0.1` (or your host) in the client's `realmlist.wtf`, log in, pick the realm and create a
   character. Acceptance procedures: [`docs/M1_ACCEPTANCE.md`](docs/M1_ACCEPTANCE.md), `docs/M2_ACCEPTANCE.md` ...
   `docs/M6_ACCEPTANCE.md`.

The three logical databases each have their own provider and connection string under `Database:Auth`, `Database:Characters` and
`Database:World` (they may point at one server or even one database). A component without its own section falls back to the M1-M4
style `Database:Provider` / `Database:ConnectionString`.

| Provider | Value | Use |
|---|---|---|
| MariaDB | `MariaDb` | primary |
| MySQL | `MySql` | supported |
| PostgreSQL 16 | `PostgreSql` | supported |
| SQLite | `Sqlite` | zero-setup development (`Data Source=arcane.db`) |

**Auto-create on login** (WCell convention): `Auth:AutocreateAccounts` is **off** in the shipped
`src/ArcaneCore.Realm/appsettings.json`, and it is not retail behaviour (vmangos has no auto-create); it is a development
convenience you must switch on yourself. With it enabled, an unknown account is created on the first login **when the password equals
the username**, the only password the server can confirm for a brand-new account under SRP6. Change it afterward with
`arcane-account set-password`. Never enable it on an internet-facing realm.

**GM levels and commands**: account levels are `Player` (0), `Moderator` (1), `GameMaster` (2) and `Administrator` (3):

```bash
dotnet run --project tools/ArcaneCore.AccountTool -- set-gmlevel MYUSER gamemaster
```

The level is read when the account enters the world. Commands are typed in chat with a `.` or `!` prefix and may be abbreviated
(`.serv i`); `.help` lists what the account may use. Examples: `.help`, `.save` and `.server info` (Player); `.gps`, `.announce`,
`.gm on` and `.saveall` (Moderator); `.kick <name>` (GameMaster). The full list is in the
[GM command reference](docs/reference/gm-commands.md).

## Configuration

Each daemon reads a `.conf` file in the style of the mangos `mangosd.conf` and `realmd.conf`: copy `world.conf.dist` to `world.conf`
(and `realm.conf.dist` to `realm.conf`) next to the daemon; the `.dist` files ship in the source tree
(`src/ArcaneCore.World/world.conf.dist`, `src/ArcaneCore.Realm/realm.conf.dist`) and in every download. Every key is also an
environment variable with `__` for `:` (the Docker files rely on this), and the command line wins over both. Precedence, the
`check-config` and `write-config` verbs and exit codes are in [`docs/guide/configuration.md`](docs/guide/configuration.md); every key
with its type and default is in the generated [configuration reference](docs/reference/configuration.md). World tuning (tick,
autosave, chat ranges, message of the day, logout delay) lives in the `World` section, with defaults that follow vmangos/cmangos.

## Client data

Nothing from the game client ships with ArcaneCore. The world reads the client's `*.dbc` files (set `ClientData:DbcDirectory`) and the
terrain, collision and navigation data you extract (`maps/`, `vmaps/`, `mmaps/` under `World:Maps:DataDirectory`). Without them the
world starts and warns for each missing file; `ClientData:Strict` makes that fatal. World content (creatures, items, quests) is
imported from cmangos classic-db or vmangos dumps with `arcane-content-importer`. Details:
[client data](docs/areas/client-data.md), [content import](docs/areas/content-import.md).

## Community and contributing

- **Bugs and ideas**: [GitHub issues](https://github.com/Krilliac/ArcaneCore/issues).
- **Contributing**: [`docs/guide/contributing.md`](docs/guide/contributing.md) covers the layout, the build and test gate, seams, schema
  modules, tests and repository hygiene.
- **Security**: report vulnerabilities privately, see [`SECURITY.md`](SECURITY.md).
- **Documentation**: the index is [`docs/README.md`](docs/README.md); operations are in [`docs/guide/operations.md`](docs/guide/operations.md).

## License

ArcaneCore is licensed under the GNU General Public License v3.0 (see [`LICENSE`](LICENSE)). It remains an independent
implementation: the GPL references named below are read for behaviour and exact values and reimplemented, not copied (charter
section 4). Third-party code imported under GPL-3.0-compatible licenses is listed in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).

## Credits

Protocol details are reimplemented from the references listed in
[`docs/ROADMAP.md`](docs/ROADMAP.md#reference-set-extended-2026-10-02-and-2026-10-04-at-the-developers-request): vmangos,
cmangos-classic, mangoszero, AscEmu, gtker/wow_messages + wow_srp, WCell, MangosSharp and wowdev.wiki. Each is cited inline in the
code. The mangos, cmangos and vmangos projects and their contributors, who documented and re-created this game's server behaviour
over many years, are the behaviour references of this project, and their work is gratefully acknowledged.

World of Warcraft, and all related art, images, and lore are copyright Blizzard Entertainment, Inc. ArcaneCore is not affiliated with
or endorsed by Blizzard Entertainment.

Regenerating the protocol tables (`src/ArcaneCore.Protocol/WorldOpcode.g.cs`, `src/ArcaneCore.Game/UpdateFields.g.cs`) from vmangos and
gtker/wow_messages checkouts, which the script cross-checks and fails on any disagreement:

```bash
python3 tools/codegen/gen_wow_tables.py --vmangos <vmangos/core> --wow-messages <wow_messages>
```
