# Area: Spell system core

Branch `feat/spells`. The cross-area contract (schema versions, shared-file edits, seams and
requests) is in [docs/integration/spells.md](../integration/spells.md).

References: **vmangos** (`Spell.cpp`, `SpellHandler.cpp`, `PacketsSpell.cpp`, `SpellAuras.cpp`,
`SpellEntry.cpp`, `SpellCaster.cpp`, `Player.cpp`, `Unit.cpp`), **cmangos-classic** (`Spell.cpp`,
`SpellAuras.cpp`, `DBCStructure.h`, `DBCfmt.h`, `ObjectMgr.cpp`, and the `spell_template` DDL), and
**gtker wow_messages** (`.wowm` packet definitions). Each piece of code cites its source inline.
When a server and the docs disagree, the server wins and the conflict is listed below.

## Implemented

**Data** (`src/ArcaneCore.Data/Content/Spells`, `.../Characters/Spells`)
- World schema v2 adds:
  - `spell_template`: every cmangos-classic column, with its name and signedness.
  - The DBC-derived tables `spell_cast_times`, `spell_duration`, `spell_range` and `spell_radius`.
  - `playercreateinfo_spell` and `spell_target_position`, using cmangos table and column names.
- Characters schema v3 adds `character_spell (CharacterId, Spell)`.
- `ISpellContentStore` (load, plus a transactional replace of the DBC tables) and `ICharacterSpellStore`, with EF implementations on all three engines.
- `DbcFile` is a WDBC reader. `SpellDbcImporter` maps Spell.dbc (173 fields, build 5875, using cmangos `SpellEntry` indices) and the four auxiliary DBCs to rows.
- `tools/spell-import` (`arcane-spell-import <DBFilesClient>`) writes those rows into the world database.

**Engine** (`src/ArcaneCore.Game/Spells`; world thread)
- `SpellInfo`, built from rows by `SpellStoreFactory`. It resolves the cast time, duration, range and radius indices, and computes:
  - cast time (level scaling, minimum, cast speed, +500 ms for ranged-slot spells)
  - duration
  - power cost (flat, per level, percent of base mana or max power, use-all, school cost modifier)
  - effect value (vmangos `CalculateSpellEffectValue`)
- Cast pipeline (vmangos `Spell::prepare / cast / update / cancel`):
  - Prepare. A cast already in progress gives SPELL_IN_PROGRESS, and a new cast interrupts a running channel. CheckCast covers: caster alive, cooldowns, stun, moving, explicit target present and alive, range (vmangos `CheckRange`, melee reach and player leeway), and power.
  - SMSG_SPELL_START, then the GCD (vmangos `Player::AddGCD`).
  - After the cast time: re-check, cooldowns, power, SMSG_CAST_RESULT, SMSG_SPELL_GO, effects.
  - Movement interrupts per vmangos.
  - CMSG_CANCEL_CAST, CMSG_CANCEL_CHANNELLING and CMSG_CANCEL_AURA follow vmangos rules. On cancel: SMSG_SPELL_FAILED_OTHER, CAST_RESULT INTERRUPTED, and the GCD is reset.
  - Channels: MSG_CHANNEL_START/UPDATE, `UNIT_FIELD_CHANNEL_OBJECT` and `UNIT_CHANNEL_SPELL`.
- Cooldowns: per spell, per category, and GCD by StartRecoveryCategory. Also SMSG_SPELL_COOLDOWN, SMSG_CLEAR_COOLDOWN, and running cooldowns in SMSG_INITIAL_SPELLS.
- Targets: `SpellCastTargets` reads and writes every 1.12 flag. Selection covers caster, explicit unit, self-cast and none.
- Effects: school damage, heal, apply aura, energize, teleport units (database position, home bind, caster destination), learn spell, trigger spell and dummy, each with its combat-log packet (SPELLNONMELEEDAMAGELOG, SPELLHEALLOG, SPELLENERGIZELOG). `RegisterEffect` lets other areas add more.
- Auras:
  - Holders with durations (minimum 300 ms), permanent and passive auras, stacking up to StackAmount, and replacement rules.
  - Visible slots (positive 0–31, negative 32–47) and the update fields UNIT_FIELD_AURA, AURAFLAGS (a nibble per slot), AURALEVELS and AURAAPPLICATIONS.
  - SMSG_UPDATE_AURA_DURATION and periodic ticks with SMSG_PERIODICAURALOG.
  - Handlers: periodic damage (flat and percent), periodic heal and OBS_MOD_HEALTH, periodic energize and OBS_MOD_MANA, periodic trigger spell, root, stun and dummy. `RegisterAura` lets other areas add more.

**World daemon** (`src/ArcaneCore.World/Spells`)
- `SpellFeature`:
  - Loads the tables and spellbooks.
  - Ticks the engine on the world thread.
  - Builds SMSG_INITIAL_SPELLS. A character with no spellbook first gets its `playercreateinfo_spell` defaults.
  - Casts known passives on login and drops spell state on logout.
- `SpellHandlers`: CMSG_CAST_SPELL, CMSG_CANCEL_CAST, CMSG_CANCEL_AURA and CMSG_CANCEL_CHANNELLING.
- `SpellbookCache`: an in-memory spellbook with ordered write-through to `character_spell`.
- `SpellCommands`: `.learn`, `.unlearn`, `.unaura` (GameMaster), `.cast` and `.cooldown` (Administrator).

## Discrepancies (servers win)

| Topic | Finding | Choice |
|---|---|---|
| SpellCastTargets corpse GUID | cmangos and gtker read the corpse GUID after the string target. vmangos `ReadForCaster` reads it with the unit/object GUIDs. | Reading uses the cmangos/gtker order and writing uses the vmangos order. The two orders only differ when the corpse and string flags are both set, which no 1.12 client packet does. |
| SMSG_CAST_RESULT | gtker writes the reason when `result != FAILURE`, which is inverted against both servers. | vmangos: status 0 = success with nothing after it; 2 = failure, followed by the reason and its argument. |
| SMSG_LEARNED_SPELL | vmangos writes u16 spell + i16 slot; gtker writes one u32. | One u32 (the same bytes for 1.12 spell ids). |
| AURAFLAGS bits | vmangos: CANCELABLE 0x01, EFF0 0x08, EFF1 0x04, EFF2 0x02. cmangos-classic uses different values. | vmangos. |
| Interrupt packets | cmangos also sends SMSG_SPELL_FAILURE on interrupt. vmangos sends SMSG_SPELL_FAILED_OTHER to the set (self included) plus CAST_RESULT to the caster. | vmangos. |
| SMSG_SPELL_COOLDOWN | vmangos `Player::AddCooldown` sends no SMSG_SPELL_COOLDOWN, because the client starts the timer itself for casts it requested. `Player::AddGCD(updateClient)` sends `(spell, 0)` only for server-forced GCDs. | Non-triggered casts send nothing, as in vmangos. Triggered player casts (server-initiated: `.cast`, triggered spells) send `(spell, cooldown ms)` so the client shows a cooldown it did not start. This is a deliberate addition. |
| IsPositive | vmangos `IsPositiveSpell` inspects a large table, including triggered spells. | A simplified heuristic: the debuff attribute, enemy targets, and damage or harmful auras. |
| Starting spells | cmangos grants `playercreateinfo_spell` in `Player::Create`. | Granted at creation through character hooks; login fills legacy missing books. |

## Acceptance steps

1. `dotnet build ArcaneCore.slnx -c Release -warnaserror`
2. `dotnet test ArcaneCore.slnx -c Release`. Set `ARCANECORE_TEST_MARIADB` and `ARCANECORE_TEST_POSTGRES` for the engine matrix. The spell tests are:
   - `tests/ArcaneCore.Game.Tests/Spells`: packets, formulas, pipeline, auras.
   - `tests/ArcaneCore.Data.Tests/Spells`: schema upgrade, stores, DBC importer on SQLite, MariaDB and PostgreSQL.
   - `tests/ArcaneCore.World.Tests/Spells`: loopback end to end.
3. With a client:
   - Run `arcane-spell-import <WoW 1.12.1>/Data/DBFilesClient` after extracting the DBCs from `dbc.MPQ`.
   - Fill `playercreateinfo_spell` from classic-db (M8 importer) or by hand.
   - Log in: the spellbook shows the starting spells.
   - Cast a spell with a cast time and watch the cast bar, the GCD and the cooldown.
   - Use `.learn <id>` and `.cast <id>` as a GM.
   - Apply a damage-over-time spell and check the aura icon, its timer and the ticks.

## Death

`SpellFeature` subscribes `MapCombat.UnitKilled` and calls `SpellSystem.OnUnitDied`, which removes every
non-passive aura of the dead unit (through the normal removal path, so stun/root handlers, aura slots and
area children are cleaned up). A dead player keeps the root `MapCombat` set on JUST_DIED. Cooldowns, the
cast in progress and passive auras are untouched, so a death-then-logout save holds no auras. Not done:
a death-persistent aura exemption (ghost, resurrection sickness) and cancelling the cast in progress. The
vmangos `RemoveAllAurasOnDeath` rule was not checked against a reference clone (none was available).

## What's left

- Area, chain and AoE target selection, and TargetB-based unit selection.
- Reagents, item casts, totems, spell focus, shapeshift and stance checks, facing, line of sight, area restrictions.
- Talents, ranks, spell modifiers, crits, resists and misses (SPELL_GO misses are supported by the packet, but nothing produces them yet), proc system, diminishing returns, immunities, dispel.
- Aura persistence (`character_aura`) and cooldown persistence. Both are dropped on logout.
- Complete spell combat modifiers. Integrated spell damage now uses map combat death/threat,
  and effective healing adds base distributed threat and enters combat.
- Non-player far teleports. Player far teleports and shared creature lookup are connected in
  the integration candidate; non-player transfers remain unsupported.
- Trainers and the trainer spell list (the NPC area, through `SpellSystem.LearnSpell`).
- The remaining effect and aura types, which are logged once as unsupported.
