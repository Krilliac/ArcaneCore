# ArcaneCore

A from-scratch World of Warcraft **1.12.1 (build 5875)** server emulator in **C# /
.NET 10**. See [`ARCANECORE_CHARTER.md`](ARCANECORE_CHARTER.md) for the binding design
charter and prime directives.

> Status: **M1 (Logon + SRP6)**, **M2 (World handshake)** and **M3 (Character lifecycle)**
> implemented and automatically verified; awaiting real-client acceptance. Milestones are
> strictly gated.

## Layout

```
src/
  ArcaneCore.Kernel         domain models + data seams (clustering boundary)
  ArcaneCore.Cryptography   WoW-flavor SRP6 (verified against KAT vectors)
  ArcaneCore.Protocol       world opcodes, header read/write, vanilla header cipher
  ArcaneCore.Game           entities, object GUIDs, UpdateFields, object updates
  ArcaneCore.Data           EF Core stores + DB-driven world data; MariaDB / MySQL / PostgreSQL
  ArcaneCore.Realm          logon/realm daemon (TCP 3724)
  ArcaneCore.World          world daemon (TCP 8085)
tools/
  ArcaneCore.AccountTool    account create / set-password / list CLI
tests/
  ArcaneCore.Cryptography.Tests   SRP6 known-answer + round-trip tests
  ArcaneCore.Realm.Tests          logon loopback handshake tests
  ArcaneCore.World.Tests          world handshake, header-cipher, character lifecycle tests
```

## World data is loaded from the database

Per design, the DBC-equivalent data (start positions, race appearance/faction, class base
stats) lives in seeded, tunable DB tables — `player_create_info`, `race_info`,
`class_info` — with **no client extraction required**. The world daemon seeds defaults on
first run and reads everything from these tables; tune them in SQL.

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

## Running M2 (world daemon)

The world daemon shares the auth database with the realm daemon (it reads the session
key produced at logon). Configure `src/ArcaneCore.World/appsettings.json` against the
same database, then:

```bash
dotnet run --project src/ArcaneCore.World
```

After logging in (M1) and selecting the realm, the client performs the world handshake
and reaches the (empty) character-select screen. See
[`docs/M2_ACCEPTANCE.md`](docs/M2_ACCEPTANCE.md).

## References

Protocol details are reimplemented (not copied) from vmangos, cmangos-classic,
gtker/wow_messages + wow_srp, WCell and wowdev.wiki — each cited inline in the code.
