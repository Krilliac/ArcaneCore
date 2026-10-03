# Character creation and deletion rules

Lane `character-creation-rules` (branch `claude/vw4-character-creation-rules`, base `claude/vw-integration`
41babaf). Everything below is reimplemented from the behaviour of the references; no reference code or data
is copied into this repository. `D:\refs\vmangos` is the primary reference, `D:\refs\mangos-classic` the
cross-check, `D:\refs\wow_messages` (gtker) the wire values, `D:\refs\classic-db` the base data.

Code: `src/ArcaneCore.World/Characters/Creation/`, `src/ArcaneCore.World/Characters/CharacterNames.cs`,
`src/ArcaneCore.World/Handlers/CharacterHandlers.cs`, `src/ArcaneCore.Data/Content/Chr/`,
`src/ArcaneCore.Data/Content/Import/Mappers/PlayerCreateActionDumpImporter.cs`,
`src/ArcaneCore.Kernel/WorldData/StartActions.cs`, `src/ArcaneCore.Kernel/Characters/CharacterNameTakenException.cs`.
Tests: `tests/ArcaneCore.World.Tests/Characters/Creation/`, `tests/ArcaneCore.Data.Tests/CharacterNameUniqueTests.cs`,
`tests/ArcaneCore.Data.Tests/ContentImport/PlayerCreate/PlayerCreateActionImporterTests.cs`.

## What was wrong

The create handler checked only "a `playercreateinfo` row exists", gender, name, in-use and an unclamped
per-realm count. There was no `CharactersCreatingDisabled`, no NOT_PLAYABLE race answer, no PvP one-faction
rule, no start level or money, the name case mapping used .NET culture rules, an invalid UTF-8 name answered
MIXED_LANGUAGES, a creation hook that failed left the half-created character (and its name) behind, a lost
name race answered CHAR_CREATE_ERROR, the character list was unbounded, new characters had an empty action
bar and the codes 0x33, 0x35 and 0x4A-0x4F did not exist.

## Delivered

### 1. Wire codes and exact names (`CharacterNames`, `PacketReader.ReadCStringBytes`, `CharResult`)

| Behaviour | Reference |
|---|---|
| Case mapping is an exact port of `wcharToUpper`/`wcharToLower` (Basic and Extended Latin, sharp s to U+1E9E for the first letter and back in the tail, Cyrillic, Latin odd/even 0x100-0x12F); fullwidth forms and Greek are left alone | vmangos Util.h:268-316 |
| `normalizePlayerName`: empty, invalid UTF-8 or more than 15 code points fails and the handler answers CHAR_NAME_NO_NAME (the handler now reads the raw bytes) | ObjectMgr.cpp:64-80, CharacterHandler.cpp:249-254 |
| Name lengths count code points, a lone surrogate is CHAR_NAME_INVALID_CHARACTER | ObjectMgr.cpp:9580-9598 |
| Codes `CHAR_CREATE_PVP_TEAMS_VIOLATION` 0x33, `CHAR_CREATE_ACCOUNT_LIMIT` 0x35, `CHAR_NAME_PROFANE` 0x4A, `RESERVED` 0x4B, 0x4C-0x4F | gtker world_result.wowm:91-159 |

### 2. Pure rules (`CharacterCreationRules`, `CharacterNameRules`, `CharacterCreationOptions`, `RaceClassRules`)

The checks of `HandleCharCreateOpcode`, in its order (CharacterHandler.cpp:185-322):
disabled team mask for player security (0x32, :193-216) -> unknown race or class (0x30, :218-228) -> race
flagged NOT_PLAYABLE (0x32, :230-237) -> gender (0x30, :239-244) -> name (NO_NAME, TOO_SHORT/LONG,
MIXED_LANGUAGES per `MinPlayerName`, `StrictPlayerNames` and `RealmZone`, ObjectMgr.cpp:9507-9598,
RealmZone.h:23-61) -> name in use (0x31) -> per-realm count with `CharactersPerRealm` clamped to 1..10
(0x34, World.cpp:633) -> PvP realm one faction per account (0x33, :282-307) -> no start row (0x2F, Player.cpp:408-413).

The PvP rule keeps the vmangos quirks on purpose: only the account's first character (the lowest id, a
`std::map` keyed by guid, ObjectMgr.h:464) is compared, a first character of race 0 always violates, and
Game Masters, `AllowTwoSide.Accounts` and non-PvP realms bypass it. `GameType` PvP, RP-PvP and FFA all count
as PvP realms (World.h:802). Start level, GM start level and start money are clamped as World.cpp:670-679
(`MAX_MONEY_AMOUNT` Player.h:656).

### 3. The create handler

* Records the start level and money; the home bind equals the start position (unchanged).
* A hook that throws after the row was inserted rolls the character back through `CharacterDeletion.TryDeleteAsync`,
  the whole delete contract (hooks, per-module cleanup, ledger, finalizers, directory), so no orphan row, name
  or directory entry stays (vmangos saves a new character in one transaction, Player.cpp:16193-16315).
* A unique-name violation of the characters index surfaces as `CharacterNameTakenException` on every engine
  (SQLite extended code 2067/1555, MariaDB 1062, PostgreSQL 23505) and is answered NAME_IN_USE (0x31).
* `CMSG_CHAR_ENUM` lists at most 10 characters, oldest first, without rows whose race/class pair has no create
  info (CharacterHandler.cpp:168-183, Player.cpp:1632-1650; gtker smsg_char_enum.wowm: the client cannot handle more than 10).

### 4. Starting action bar

World schema step `StartActionWorldModule.Version` (15) creates `playercreateinfo_action`
(race, class, button, action, type; classic-db has 215 rows over the 40 pairs). `PlayerCreateActionDumpImporter`
(wired into the content importer CLI and table specs) applies the load checks of vmangos (playable race and
class, button < 120, action < 0x01000000; ObjectMgr.cpp:4737-4796, Player.cpp:5900-5933). `StartActionsFeature`
(an `ICharacterHooks`) writes the validated bar with the new character, dropping spell and item rows whose
spell or item is not in the loaded templates (an unloaded template store is not judged against). The first
login then sends it in SMSG_ACTION_BUTTONS (MasterPlayer.cpp:30-39).

## Configuration (`CharacterCreation` section; every default is the retail value)

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `Retail` | `Legacy` restores the earlier permissive behaviour (no disabled mask, PvP rule, NOT_PLAYABLE, start level/money, rollback, list cap or start bar) |
| `CharactersCreatingDisabled` | 0 | bit 0 stops Alliance, bit 1 Horde, for player accounts |
| `GameType` | `Normal` | `Normal`, `PvP`, `Rp`, `RpPvP`, `FfaPvP`, `Normal2` |
| `AllowTwoSideAccounts` | false | bypasses the PvP faction rule |
| `RealmZone` | 1 | name alphabet with `StrictPlayerNames` bit 2 |
| `StrictPlayerNames` | 0 | 0 any one script, 1 basic Latin, 2 realm zone script (3 = both) |
| `MinPlayerName` | 2 | clamped 2..12 |
| `StartPlayerLevel`, `GmStartLevel`, `StartPlayerMoney` | 1, 1, 0 | clamped as vmangos |
| `StartActions` | true | give new characters their starting bar |

`World:CharactersPerRealm` keeps living in the world options and is clamped to 1..10 by the rules.

## Deliberate differences from retail (all documented, none silent)

* `CHAR_DELETE`: an unknown guid, a foreign account and a character in the world are answered
  CHAR_DELETE_FAILED (vmangos sends nothing for the first two and for a loaded-not-in-world player; a player
  in the world is force-logged-out and deleted, CharacterHandler.cpp:332-363). The answer is part of the delete
  lifecycle contract and its recovery tests (`docs/integration/character-delete.md`) and stays. The guild-leader
  refusal (0x3A) matches vmangos (CharacterHandler.cpp:340-344).
* `CharactersPerAccount` is not enforced: vmangos reads it but never enforces it (World.cpp:631); mangos-classic
  enforces it over the realm character counts, which need a cross-realm table that this lane did not deliver.
  Code 0x35 exists on the wire enum only.
* A pair with a valid race and class but no start row answers CHAR_CREATE_ERROR (vmangos), not CHAR_CREATE_FAILED
  as before; two existing tests were updated for that.

## Not delivered (limits)

Each of these needs data or a primitive this lane does not have; none is stubbed.

* Appearance validation (`Player::ValidateAppearance`): needs CharSections.dbc and CharacterFacialHairStyles.dbc
  readers and a client DBC. Only the gender is checked. (Design slice cc05.)
* Reserved and profane names: the `reserved_name` table, NamesProfanity.dbc and NamesReserved.dbc
  (codes 0x4A/0x4B exist; nothing sends them). (cc06.)
* ChrRaces.dbc/ChrClasses.dbc: race/class existence and NOT_PLAYABLE come from the vanilla masks
  (SharedDefines.h:60-110, `RaceClassRules`), `race_taxi_start` still has no writer so new characters get no
  starting flight paths. (cc04.)
* Starting outfit: classic-db has 0 `playercreateinfo_item` rows and the outfit lives in CharStartOutfit.dbc
  (mangos-classic Player.cpp:857-911); a classic-db realm still creates characters without starting items until
  that reader lands. (cc08; it also edits `ItemsFeature`, which the wave-2 items lane owns.)
* First-login cinematic (SMSG_TRIGGER_CINEMATIC 0xFA, ids Undead 2, Orc 21, Dwarf 41, Night Elf 61, Human 81,
  Gnome 101, Troll 121, Tauren 141, gtker smsg_trigger_cinematic.wowm): default-on it would break every
  strict login-sequence expectation (`WorldTestClient.LoginAsync`, MockClient `ScenarioConnection.LoginAsync`,
  raw sequences in other lanes' tests), so it needs the integrator to land a tolerance for the optional packet
  first. It belongs after SMSG_LOGIN_SETTIMESPEED, before the map add. (cc10.)
* Realm character counts (`realm_characters`, realm list per-account count), live `.reload` of the creation
  options, `CMSG_CHAR_RENAME` (opcode exists, no handler), petition cleanup on delete, soft delete, guild id /
  flags / pet data in SMSG_CHAR_ENUM, FFA PvP combat flag.
* Dev seeds: the three start positions of `WorldDbInitializer` that differ from classic-db (Night Elf, Undead,
  Gnome orientation) were left alone; an import with `--replace` supplies the real rows.

## Verification

* No client DBC exists on the verification machine, so nothing here was proven against a real 1.12.1 client.
* MariaDB and PostgreSQL are not available locally. `CharacterNameUniqueTests` and
  `PlayerCreateActionImporterTests` are theories over `TestDatabases.AvailableProviders` written against the
  real provider semantics (error codes above; the `playercreateinfo_action` table is a plain `CreateTableChange`,
  and an importer run is one `ImportTransaction`), but only SQLite ran locally. The hosted CI run is the first
  MariaDB/PostgreSQL proof. Index collation differs per engine (MariaDB `general_ci` also rejects a name that
  differs by case or accent, SQLite and PostgreSQL compare bytes), so the handler's lower-case `IsNameTakenAsync`
  stays the authority.

## Provenance

vmangos CharacterHandler.cpp:142-368, Player.cpp:326-357, 401-529, 5900-5933, 16170-16330, MasterPlayer.cpp:30-39,
ObjectMgr.cpp:64-80, 4520-4796, 9507-9598, Util.h:106-316, RealmZone.h, World.cpp:597-682, World.h:629-634,802,
SharedDefines.h:60-110; mangos-classic Entities/CharacterHandler.cpp:422-445, Player.cpp:857-911;
gtker world_result.wowm, smsg_char_enum.wowm, smsg_trigger_cinematic.wowm; classic-db `playercreateinfo*` counts
(40 / 215 / 0 / 1497 rows).
