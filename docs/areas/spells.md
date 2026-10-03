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

`SpellFeature` subscribes `MapCombat.UnitKilled` and calls `SpellSystem.OnUnitDied`, which follows vmangos
`Unit::SetDeathState(JUST_DIED)` (`src/game/Objects/Unit.cpp:7322-7323`, `InterruptNonMeleeSpells(false)`): the
cast or channel in progress is cancelled (a channel also drops its auras), then `Unit::RemoveAllAurasOnDeath`
(`Unit.cpp:3969-4004`) rule: every aura that is neither passive nor death persistent is removed through the
normal removal path (stun/root handlers, aura slots and area children are cleaned up). Death persistent is
`SPELL_ATTR_EX3_ALLOW_AURA_WHILE_DEAD` for a 1.12.1 build (`SpellEntry.h:952-959`, `SpellDefines.h:967`), exposed
as `SpellInfo.IsDeathPersistent`. Applying an aura to a dead target needs a passive, death-persistent or
dead-target spell (`Unit.cpp:3110`, `SpellEffects.cpp:1672`; `SpellInfo.CanTargetDead` = Ex2 ALLOW_DEAD_TARGET or
death-only, `SpellEntry.h:931-942`), and the cast check accepts a dead explicit target for such a spell
(`Spell.cpp:5572`). A dead player keeps the root `MapCombat` set on JUST_DIED. Cooldowns and auras the dead unit
cast on others are untouched, so a death-then-logout save holds no auras.

Deliberate limits:

- The Hunter's Mark (`SPELL_AURA_MOD_STALKED`) carve-out at the top of `RemoveAllAurasOnDeath` has no handler
  yet (vmangos-only; cmangos-classic lacks it).
- The creature-respawn clear of death-persistent auras (`Creature.cpp:827`) is not implemented.
- Player side effects of dying (shapeshift removal, pet, combo points) belong to other systems.
- vmangos also treats `Attributes == DO_NOT_DISPLAY && DurationIndex == 21` as passive (`SpellAuras.cpp:6666`);
  `SpellInfo` carries the resolved duration, not the DBC index, so that case is not covered.
- Passive auras may still be applied to a dead target (existing behaviour; vmangos only allows it while a player
  is loading).
- A death-only spell aimed at a living target is not rejected by the cast check (vmangos returns BAD_TARGETS).

## Combat spell data model (warrior-mechanics S02a)

Additive data only; nothing reads these fields at cast time yet (the cast-time consumers are the later
warrior-mechanics slices). Reference rule: only the `SUPPORTED_CLIENT_BUILD = 1.12.1` branch of vmangos
(`src/shared/Progression.h:36`) counts.

- `SpellInfo` now carries `Stances`, `StancesNot`, `CasterAuraState`, `TargetAuraState`, `ProcFlags`,
  `ProcChance` and `EquippedItemClass` / `SubClassMask` / `InventoryTypeMask` (`SpellStoreFactory` copies them from
  `spell_template`; `ProcCharges` already existed). `EquippedItemClass` defaults to -1 (Spell.dbc "none").
- `SpellEnums.Combat.cs`: `ShapeshiftForm` (vmangos `SharedDefines.h:1421-1441`), `ShapeshiftFlags` (`:1467-1476`),
  `AuraState` (`SpellDefines.h:642-656`; 9-11 are vmangos custom states), `ProcFlags` (`:1043-1081`), `ProcFlagsEx`
  (`:1099-1120`), `SpellModOp` (`:602-631`, no value 13), and the combat attribute bits as `SpellAttributes*Combat`
  enums (`:830-975`). `SpellDefines.cs` is deliberately untouched, so the legacy enums keep their (partial) members.
- `SpellInfo.Combat.cs` helpers: `IsNextMeleeSwing` (`SpellEntry.h:887-890`: bit 0x4 **or** 0x400 - Heroic Strike and
  Cleave use 0x4, so the legacy `SpellAttributes.OnNextSwing` alone misses them), `NeedsComboPoints`
  (`SpellEntry.h:1082-1085`), `IsRemovedOnShapeLost` (`:1180-1186`, including the hard-coded spell 24864) and
  `GetErrorAtShapeshiftedCast` (`SpellEntry.cpp:1032-1074`).
- Limits: `GetErrorAtShapeshiftedCast` takes the form's `SpellShapeshiftForm.dbc` flags1 and the "talent that learns a
  spell" exemption (`GetTalentSpellCost`) from the caller, because neither the shapeshift-form table nor the talent
  tree is loaded by this core yet; an unknown form returns `CastOk` like vmangos (`SpellEntry.cpp:1051-1055`).

## What's left

- Area, chain and cone target selection are implemented (`SpellSystem.Targeting.cs`, with a line-of-sight filter on area lists); only the remaining TargetB-based selections are missing.
- Reagents, item casts, totems, spell focus, shapeshift and stance checks (no shapeshift or stance data is read at cast time), facing, area restrictions.
- Talents, ranks, spell modifiers, proc system (aura holders carry `procCharges`, but nothing consumes them), diminishing returns, immunities. Hit, crit and resist rules (`SpellCombatRules`) and the dispel effect exist.
- Complete spell combat modifiers. Integrated spell damage now uses map combat death/threat,
  and effective healing adds base distributed threat and enters combat.
- Non-player far teleports. Player far teleports and shared creature lookup are connected in
  the integration candidate; non-player transfers remain unsupported.
- Trainers and the trainer spell list (the NPC area, through `SpellSystem.LearnSpell`).
- The remaining effect and aura types, which are logged once as unsupported.
