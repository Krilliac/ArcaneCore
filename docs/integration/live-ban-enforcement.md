# Live ban enforcement: integration notes

Area doc: `docs/security/live-bans.md`. Lane branch `claude/vw4-live-ban-enforcement`.

## Schema

* Auth schema version for `account_banned` / `ip_banned` is the single constant `BanDataModule.Version`
  (`src/ArcaneCore.Data/Auth/BanDataModule.cs`), **3** on this base. The integrator renumbers it. Tests reference
  the constant or `AuthDbContext.Schema.CurrentVersion`, never a literal. `IntegratedSchemaTests` lists the module
  and `SchemaBootstrapGuardsTests` asserts `Schema.CurrentVersion == BanDataModule.Version` (the auth component has
  no other module on this base; if another lane adds one, those two assertions become `Max(...)` as the characters/
  world ones already are).
* Characters/World: untouched, so no `ICharacterDataCleanup` is needed (the ban tables are account-scoped).

## Shared files touched (all narrow and additive)

| File | Change |
|---|---|
| `src/ArcaneCore.World/Net/WorldSession.cs` | `RemoteAddress`; ban/IP check in `HandleAuthSessionAsync`; post-`Register` re-check; the header cipher and `CharacterSelect` state are now set after the re-check (a session is registered a moment before it is usable) |
| `src/ArcaneCore.World/Net/SessionRegistry.cs` | `Sessions` snapshot |
| `src/ArcaneCore.World/WorldServiceCollectionExtensions.cs` | one line: `Configure<BanOptions>("Bans")` |
| `src/ArcaneCore.Realm/Net/LogonSession.cs`, `LogonServer.cs` | optional trailing `IBanStore` constructor parameter; resolved from the connection scope |
| `src/ArcaneCore.Data/Stores/EfAccountStore.cs` | implements `IAccountAdmin`; optional `AccountStatusEvents` constructor parameter (`new EfAccountStore(db)` still compiles) |
| `src/ArcaneCore.Data/Auth/AuthDbInitializer.cs` | one `PurgeExpiredAsync` call after the schema is ensured |
| `tests/ArcaneCore.World.Tests/WorldTestHost.cs` | passes the real remote endpoint (was the literal `"test"`), registers `SessionRegistry`, `AccountStatusEvents`, `IBanStore`, `IAccountAdmin`, optional `BanOptions`; `ExpectSessionFaults` |
| `tests/ArcaneCore.World.Tests/InMemoryAccountStore.cs` | implements `IAccountAdmin` |

`IAccountStore` and its other fakes are **unchanged** on purpose. `BuiltinCommands.cs` is untouched (the
gm-commands lane owns `.kick`).

## Seams used

`IDataModule` (Auth), `IWorldFeature` (`BanEnforcementFeature`, `BanRecheckFeature`, both resolve their services
lazily and become inactive when the ban services are not registered), `ICommandGroup` (`BanCommands`).

## Merge hints

* If the wave-2 hardening/ops lanes add an Auth module, renumber `BanDataModule.Version` and the two assertions above.
* If the gm-commands lane's `CommandArgs` / `GmStrings` land, `BanCommandText` may be swapped to them; it
  deliberately has its own tokenizer so the lanes do not depend on each other.
* The wave-2 `.reload` coordinator could add `account_banned` / `ip_banned` reload targets; not done (not on this base).
