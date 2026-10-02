# ArcaneCore

A from-scratch World of Warcraft **1.12.1 (build 5875)** server emulator in **C# /
.NET 10**. See [`ARCANECORE_CHARTER.md`](ARCANECORE_CHARTER.md) for the binding design
charter and prime directives.

> Status: **M1–M6** implemented and automatically verified (M5: world-thread runtime,
> generated protocol tables, persistence, multi-engine schema management; M6: logout,
> chat, /who, account settings, action bars, GM commands); awaiting real-client
> acceptance. Scope and order of the next milestones: [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Layout

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
  ArcaneCore.AccountTool    account create / set-password / set-gmlevel / list CLI
  codegen/                  generates WorldOpcode.g.cs + UpdateFields.g.cs from the references
tests/
  ArcaneCore.Cryptography.Tests   SRP6 known-answer + round-trip tests
  ArcaneCore.Realm.Tests          logon loopback handshake tests
  ArcaneCore.Game.Tests           update pipeline, maps, world runtime (no sockets)
  ArcaneCore.Data.Tests           schema + stores on SQLite, MariaDB, PostgreSQL
  ArcaneCore.World.Tests          end-to-end world daemon over loopback
```

## Databases

Three logical databases, each with its own provider and connection string under
`Database:Auth`, `Database:Characters` and `Database:World` (they may point at one server
or even one database). A component without its own section falls back to the M1–M4 style
`Database:Provider` / `Database:ConnectionString`.

| Provider | Value | Use |
|---|---|---|
| MariaDB | `MariaDb` | primary |
| MySQL | `MySql` | supported |
| PostgreSQL 16 | `PostgreSql` | supported |
| SQLite | `Sqlite` | zero-setup development (`Data Source=arcane.db`) |

Each component keeps a version row (`auth_schema`, `characters_schema`, `world_schema`).
Startup creates missing schemas, adopts M1–M4 databases, applies additive upgrades and
**refuses to start** on anything else. World data (start positions, race appearance,
class stats) is seeded into the world database until the content importer lands (M8).

The data-layer tests run against MariaDB and PostgreSQL when
`ARCANECORE_TEST_MARIADB` / `ARCANECORE_TEST_POSTGRES` hold a server connection string
(CI provides both).

## Build & test

Requires the .NET 10 SDK.

```bash
dotnet build ArcaneCore.slnx -c Release
dotnet test  ArcaneCore.slnx -c Release
```

## Running M1 (logon daemon)

1. Provision a MariaDB/MySQL/PostgreSQL database and set the connection string in
   `src/ArcaneCore.Realm/appsettings.json` (`Database` section; `Provider` is
   `MariaDb`, `MySql` or `PostgreSql`).
2. Create a test account (or enable `Auth:AutocreateAccounts`):
   ```bash
   dotnet run --project tools/ArcaneCore.AccountTool -- create MYUSER MYPASS
   ```
3. Start the daemon:
   ```bash
   dotnet run --project src/ArcaneCore.Realm
   ```
4. Point the client's `realmlist.wtf` at the daemon and log in. See
   [`docs/M1_ACCEPTANCE.md`](docs/M1_ACCEPTANCE.md) for the acceptance procedure.

### Auto-create-on-login (WCell convention)

With `Auth:AutocreateAccounts` enabled, an unknown account is created on the first
login **when the password equals the username** — the only password the server can
confirm for a brand-new account under SRP6. Change it afterward with
`arcane-account set-password`.

## Running the world daemon

The world daemon shares the auth database with the realm daemon (it reads the session
key produced at logon). Configure `src/ArcaneCore.World/appsettings.json` (the `Auth`
database must match the realm daemon's), then:

```bash
dotnet run --project src/ArcaneCore.World
```

After logging in and selecting the realm, the client reaches character select, can create
characters and enter the world. Acceptance procedures: `docs/M2_ACCEPTANCE.md` …
`docs/M6_ACCEPTANCE.md`.

World tuning lives in the `World` section (defaults follow vmangos/cmangos):

| Option | Default | Meaning |
|---|---|---|
| `TickIntervalMs` | 50 | world tick |
| `UpdateCompressionThreshold` | 128 | update packets above this many bytes are zlib-compressed |
| `AutosaveIntervalMs` | 900000 | periodic save of online characters (0 = off) |
| `MaxOutboundBytes` | 8 MiB | a client that stops reading is disconnected |
| `CharactersPerRealm` | 10 | characters per account on this realm |
| `Motd` | `Welcome to ArcaneCore.` | message of the day; `@` separates lines |
| `ListenRangeSay` / `ListenRangeYell` / `ListenRangeTextEmote` | 25 / 300 / 25 | chat ranges in yards (0 = whole map) |
| `AllowTwoSideChat` / `AllowTwoSideWhoList` | false | cross-faction whispers/emotes, and /who of the other faction |
| `LogoutDelayMs` | 20000 | logout countdown |
| `InstantLogoutSecurity` | `Moderator` | lowest account level that logs out instantly |
| `GmLevelInWhoList` | `Administrator` | highest staff level ordinary players see in /who |
| `PlayerCommands` | true | whether plain players may use `.help`, `.save`, … |

### GM levels and commands

Account levels are `Player` (0), `Moderator` (1), `GameMaster` (2) and `Administrator` (3):

```bash
dotnet run --project tools/ArcaneCore.AccountTool -- set-gmlevel MYUSER gamemaster
```

The level is read when the account enters the world. Commands are typed in chat with a `.`
or `!` prefix and may be abbreviated (`.serv i`); `.help` lists what the account may use.

| Command | Level |
|---|---|
| `.help [command]`, `.commands`, `.save`, `.server info`, `.server motd` | Player |
| `.gps`, `.announce <text>`, `.notify <text>`, `.gm [on\|off]`, `.gm chat [on\|off]`, `.saveall`, `.modify money <copper>` | Moderator |
| `.kick <name>` | GameMaster |

## Regenerating protocol tables

`src/ArcaneCore.Protocol/WorldOpcode.g.cs` and `src/ArcaneCore.Game/UpdateFields.g.cs` are
generated from vmangos and gtker/wow_messages checkouts (the script cross-checks them and
fails on any disagreement):

```bash
python3 tools/codegen/gen_wow_tables.py --vmangos <vmangos/core> --wow-messages <wow_messages>
```

## References

Protocol details are reimplemented (not copied) from the references listed in
[`docs/ROADMAP.md`](docs/ROADMAP.md#reference-set-extended-2026-10-02-at-the-developers-request)
— vmangos, cmangos-classic, mangoszero, AscEmu, gtker/wow_messages + wow_srp, WCell,
MangosSharp and wowdev.wiki — each cited inline in the code.
