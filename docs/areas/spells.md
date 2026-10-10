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
  - Channels: MSG_CHANNEL_START/UPDATE, `UNIT_FIELD_CHANNEL_OBJECT` and `UNIT_CHANNEL_SPELL`. An interrupted channel clears the fields and
    sends the zero MSG_CHANNEL_UPDATE at once; a channel that ends normally (its timer, `FinishChannel`) keeps them for 1000 ms
    (vmangos Spell::SendChannelUpdate and ChannelResetEvent, Spell.cpp:4801-4823, 8341-8357), and a new channel in that second resets
    them first (Spell.cpp:3463-3469).
- Cooldowns: per spell, per category, and GCD by StartRecoveryCategory. Also SMSG_SPELL_COOLDOWN, SMSG_CLEAR_COOLDOWN, and running cooldowns in SMSG_INITIAL_SPELLS.
- Targets: `SpellCastTargets` reads and writes every 1.12 flag. Selection covers caster, explicit unit, self-cast and none.
- Effects: school damage, heal, apply aura, energize, teleport units (database position, home bind, caster destination), learn spell, trigger spell and dummy, each with its combat-log packet (SPELLNONMELEEDAMAGELOG, SPELLHEALLOG, SPELLENERGIZELOG). `RegisterEffect` lets other areas add more. Effect 86 activates script-selected game objects; see [the object-effect follow-up](../integration/activate-object-effect-20261009.md).
- Auras:
  - Holders with durations (minimum 300 ms), permanent and passive auras, stacking up to StackAmount, and replacement rules.
  - Visible slots (positive 0–31, negative 32–47) and the update fields UNIT_FIELD_AURA, AURAFLAGS (a nibble per slot), AURALEVELS and AURAAPPLICATIONS (`charges * stacks - 1`, clamped, rewritten on every stack or charge change).
  - SMSG_UPDATE_AURA_DURATION and periodic ticks with SMSG_PERIODICAURALOG.
  - `SpellSystem.AddAura(target, spellId, permanent, caster)` (vmangos Unit::AddAura): the spell's APPLY_AURA effects as a holder on
    the target without a cast (no checks, no cast packets, no other effects); `permanent` is ADD_AURA_PERMANENT, a holder that never runs
    out. Narrower than vmangos: area and persistent area aura effects are not built. Creature scripts reach it through
    `ICreatureSpellCaster.AddAura` and `CreatureAI.DoAddAura`.
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
| SpellCastTargets corpse GUID | cmangos and gtker read the corpse GUID after the string target. vmangos `ReadForCaster` reads it with the unit/object GUIDs. The server's outbound vmangos writer emits one unit/object/corpse GUID, while inbound combined flags require separate GUID fields. | Reading uses the cmangos/gtker order and writing uses the vmangos order. Client fixtures must serialize the inbound layout rather than reuse the server writer for combined unit/corpse targets. Corpse/string combinations also differ in ordering. |
| SMSG_CAST_RESULT | gtker writes the reason when `result != FAILURE`, which is inverted against both servers. | vmangos: status 0 = success with nothing after it; 2 = failure, followed by the reason and its argument. |
| SMSG_LEARNED_SPELL | vmangos writes u16 spell + i16 slot; gtker writes one u32. | One u32 (the same bytes for 1.12 spell ids). |
| AURAFLAGS bits | vmangos: CANCELABLE 0x01, EFF0 0x08, EFF1 0x04, EFF2 0x02. cmangos-classic uses different values. | vmangos. |
| Interrupt packets | cmangos also sends SMSG_SPELL_FAILURE on interrupt. vmangos sends SMSG_SPELL_FAILED_OTHER to the set (self included) plus CAST_RESULT to the caster. | vmangos. |
| SMSG_SPELL_COOLDOWN | vmangos `Player::AddCooldown` sends no SMSG_SPELL_COOLDOWN, because the client starts the timer itself for casts it requested. `Player::AddGCD(updateClient)` sends `(spell, 0)` only for server-forced GCDs. | Non-triggered casts send nothing, as in vmangos. Triggered player casts (server-initiated: `.cast`, triggered spells) send `(spell, cooldown ms)` so the client shows a cooldown it did not start. This is a deliberate addition. |
| IsPositive | vmangos `IsPositiveSpell`/`IsPositiveEffect` decide per effect. | Ported exactly (docs/areas/aura-engine.md); the holder is positive when all of its aura effects are. |
| Starting spells | cmangos grants `playercreateinfo_spell` in `Player::Create`. | Granted at creation through character hooks; login fills legacy missing books. |

## Acceptance steps

1. `dotnet build ArcaneCore.slnx -c Release -warnaserror`
2. `dotnet test ArcaneCore.slnx -c Release`. Set `ARCANECORE_TEST_MARIADB` and `ARCANECORE_TEST_POSTGRES` for the engine matrix. The spell tests are:
   - `tests/ArcaneCore.Game.Tests/Spells`: packets, formulas, pipeline, auras.
   - `tests/ArcaneCore.Data.Tests/Spells`: schema upgrade, stores, DBC importer on SQLite, MariaDB and PostgreSQL.
   - `tests/ArcaneCore.World.Tests/Spells`: loopback end to end.
3. With a client:
   - Run `arcane-spell-import <WoW 1.12.1>/Data/DBFilesClient` after extracting the DBCs the client actually uses: each file from `patch-2.MPQ` if it has it, else `patch.MPQ`, else `dbc.MPQ` (the base archive holds older copies of some files, e.g. `SpellShapeshiftForm.dbc`). `D:efsient-dbc-5875-effective` holds verified extractions.
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
cast on others retain their existing behavior, with the Hunter's Mark exception below.

The [death aura continuation](../integration/death-aura-lifecycle-20261004.md) removes the
dying caster's own Hunter's Mark holders on its map, preserving other casters and ordinary
foreign auras. Exact holder and caster ownership prevent GUID reuse from transferring cleanup.
Held participants defer this death cleanup until quest settlement releases them. Creature
corpse disposal now unapplies handler contributions before discarding spell state, and both
natural and forced respawn clear recreated old-life auras before field initialization and AI.

Deliberate limits:

- Hunter's Mark eligibility uses its known family/flag rather than the upstream database `Custom`
  single-target flag; the general custom flag and single-target registry are not imported.
- Natural corpse disposal still clears retained auras earlier than upstream respawn. The current
  lifecycle now runs their handlers at that existing disposal point.
- Player side effects of dying (shapeshift removal, pet, combo points) belong to other systems.
- vmangos also treats `Attributes == DO_NOT_DISPLAY && DurationIndex == 21` as passive (`SpellAuras.cpp:6666`);
  `SpellInfo` carries the resolved duration, not the DBC index, so that case is not covered.
- Passive auras may still be applied to a dead target (existing behaviour; vmangos only allows it while a player
  is loading).
- A death-only spell aimed at a living target is not rejected by the cast check (vmangos returns BAD_TARGETS).

## Handler modules and spell breadth

Effect and aura handlers are added as `ISpellHandlerModule` types (`src/ArcaneCore.Game/Spells/SpellHandlerModules.cs`)
instead of by editing `SpellSystem.Effects.cs` / `SpellSystem.Auras.cs`:

- A module is a concrete class with a parameterless constructor (public or internal) that registers handlers with
  `RegisterEffect` / `RegisterAura` from `Register(SpellSystem)`.
- `SpellHandlerModules.BuiltIn` finds every module of the Game assembly by reflection, ordered by full type name
  (ordinal). The `SpellSystem` constructor applies them after the built-in tables. Another assembly (or a test) uses
  `SpellSystem.RegisterModules(Assembly)` / `RegisterModules(IEnumerable<Type>)`; `SpellSystem.Modules` lists what ran.
- Fail closed: a module that replaces a handler installed before it ran (built-in, an earlier module, or a seam
  registration), is applied twice, is abstract or lacks the constructor makes startup throw naming the module and the
  effect or aura. Nothing is skipped silently. `RegisterEffect` / `RegisterAura` called directly still replace, as before.

Delivered handlers (reference: vmangos `src/game/Spells`; only the 1.12.1 branch of its `#if` blocks counts, build 5875):

| Module (`Spells/Effects`, `Spells/Auras`) | Handlers | vmangos source | Behaviour and limits |
|---|---|---|---|
| `DirectCombatEffects` | `INSTAKILL` | `SpellEffects.cpp:268` | SMSG_SPELLINSTAKILLLOG (victim guid, spell; wow_messages `smsg_spellinstakilllog.wowm` 1.12), then the target's whole health as direct damage through `IDamageSink`. |
| | `HEAL_MAX_HEALTH` | `:3516` | Heal = the caster's maximum health times the caster's `MOD_HEALING_DONE_PERCENT` auras and the target's strongest negative and positive `MOD_HEALING_PCT`; dithered; shares the crit/heal/log tail of `HEAL` (`SpellSystem.DeliverHeal`). |
| | `THREAT` | `:3503`, `Unit.cpp:7409-7432`, `ThreatManager.cpp:391-447` | Creatures only (no pet or totem model yet); both units alive and in one map; scaled by the caster's `MOD_THREAT` auras of the spell's school; `EX4_NO_HARMFUL_THREAT` adds nothing, `EX_NO_THREAT` never creates an entry. No `SPELLMOD_THREAT` (talents). |
| | `DISPEL_MECHANIC` | `:5507`, `SpellAuras.cpp:7435` | Removes every holder whose spell, or an aura-carrying effect, has the mechanic in `EffectMiscValue`, whatever its polarity or caster. |
| `StatAuras` | `MOD_STAT`, `MOD_RESISTANCE`, `MOD_ATTACK_POWER`, `MOD_RANGED_ATTACK_POWER` | `SpellAuras.cpp:4641`, `:4551`, `:5169`, `:5181` | Flat amounts applied as deltas to the same update fields items and level-ups write (stats + `PLAYER_FIELD_POS/NEGSTAT`, resistances + the resistance buff fields, the two int16 halves of the attack power mods; the polarity of the spell picks the half). Stamina and intellect move max health and mana by the difference of the stat bonus curve (`StatSystem.cpp:134-190`). Wand users take no ranged AP. The applied amount is remembered per aura. |
| `VisualAuras` | `MOD_SCALE`, `TRACK_CREATURES`, `TRACK_RESOURCES` | `SpellAuras.cpp:2948`, `:2909`, `:2923` | Scale, bounding radius and combat reach by the same factor; track bit `misc - 1`; a tracking spell (`EX_NO_AUTOCAST_AI` or `ALLOW_WHILE_MOUNTED`) removes other trackers (`SPELL_TRACKER`, `SpellEntry.cpp:152-157`). |
| `LeechAuras` (+ `SpellSystem.Leech.cs`) | `PERIODIC_LEECH`, `PERIODIC_MANA_LEECH` | `SpellAuras.cpp:5927-6014`, `:6116-6190` | Drain Life, Siphon Life, Drain Mana: damage log with the periodic flag, heal by damage times `EffectMultipleValue`, channel stops when the target dies; mana drain with the periodic aura log (power, amount, float multiplier), caster gain, half the gain as threat, damage-cancel auras removed. No spell power, absorbs, immunities, procs, Mark of Kazzak or Improved Drain Mana. |
| `ResurrectionEffects` | `RESURRECT` (18), `RESURRECT_NEW` (113) | `SpellEffects.cpp:5228-5248,209-263`; `Player.cpp:20065-20120` | Offer percentage or flat health/mana to a dead player, including an online corpse owner on another map. Acceptance validates the caster and uses the teleport ACK before restoration and persistence. Effect 113 also restores an existing current summoned pet, with fresh AI and Demonic Sacrifice cleanup; persistent hunter-pet recovery and other summon kinds remain separate. See [player resurrection](../integration/player-resurrection-20261004.md) and [pet revival](../integration/pet-revival-20261004.md). |
| `SelfResurrectionEffects` | `SELF_RESURRECT` (94) | `SpellEffects.cpp:5334-5375` | Immediately restores a dead player at its current position with percentage or negative-value flat health/mana, fractional rounding, body cleanup and a normal World save. Release-dialog availability and reagents remain separate. See [self-resurrection](../integration/self-resurrection-20261004.md). |
| `GhostAuras` | `GHOST` (95) | `SpellAuras.cpp:5639-5660`; `Player.cpp:4561-4578` | Sets the unit ghost visibility bit and player ghost flag. Production death hooks cast imported 8326 and known Wisp Spirit 20584, with content-free movement fallback. See [ghost form](../integration/ghost-form-20261004.md). |
| `DurabilityEffects` | `DURABILITY_DAMAGE` (111), `DURABILITY_DAMAGE_PCT` (115) | `SpellEffects.cpp:5576-5640` | Signed points or percentage loss for a selected equipment/bag slot, all equipment, or equipment and carried contents. Uses existing durability options, stat transitions and item persistence. See [durability spells](../integration/durability-spells-20261004.md). |
| `SpellMagnetAuras` (+ `Spells/Magnet`) | `SPELL_MAGNET` (96) | `SpellCaster.cpp:31-68`; `Spell.cpp:2227,249` | Eligible hostile magic spells select the live magnet caster, spend a protection charge, and report the redirected target. Mixed effects and channels share that selection. See [Grounding evidence and limits](../integration/grounding-totem-20261004.md). |

Intrinsic [totem immunities](../integration/totem-immunity-20261004.md) use the installed immunity
rules before hit reporting. Immune effects are removed individually from the target mask;
an empty mask produces `IMMUNE2` in `SMSG_SPELL_GO`, while eligible mixed effects still land.

Shared-file changes made for these handlers (all small and additive, each with a test):

- `SpellSystem.cs`: the constructor applies `SpellHandlerModules.BuiltIn`.
- `SpellSystem.Effects.cs`: `EffectHeal` delegates its tail to the new internal `DeliverHeal`.
- `SpellSystem.Auras.cs`: when a stack count changes an aura's amount, the handler is un-applied with the old amount and
  applied with the new one (`SpellAuraHolder::SetStackAmount`, `SpellAuras.cpp:6987-6991`). Without it a stacking stat
  aura (Sunder Armor shape) would leave its old contribution behind.
- `SpellCombatRules.ApplyArmor`, `MapCombat.Melee.cs` (`CalculateMeleeDamage`) and `SpellSystem.Combat.WeaponDamageRoll`:
  armor is read as signed (`MeleeHitTable.ApplyArmor` already clamps negative armor to zero, but the unsigned read turned
  a negative armor into about 4.29e9) and the negative attack power half is added, not subtracted
  (`Unit::GetTotalAttackPowerValue`, `Unit.cpp:8037`). These were latent: nothing wrote negative values before.
- `SpellPackets.BuildSpellNonMeleeDamageLog`: optional `periodic` flag.

Data-driven ranking (classic-db `ClassicDB_1_12_1_z2815`, read-only, parsed with a local script; nothing copied into
the repo): player spells = `playercreateinfo_spell` plus trainer spells (`npc_trainer`, `npc_trainer_template`) with a
required level up to 20, with learn wrappers and trigger chains resolved (943 spells; the profession-heavy closure is
larger than a class-only view). The share whose every effect and aura type has a handler, by handler presence only (not
by correctness), went from 152 to 204 of 943. After this work the biggest remaining gaps by distinct spells, over the
player set plus creature spell lists, are: `CREATE_ITEM` (376, mostly tradeskills, unsafe without reagent and skill
checks, which would make crafting free), `ENCHANT_ITEM`, `KNOCK_BACK` (51), `SKILL_STEP`/`SKILL`/`PROFICIENCY`/`WEAPON`/
`LANGUAGE`/`TRADE_SKILL` (skills area), `PERSISTENT_AREA_AURA` (38, Blizzard and Flamestrike), summons, `ADD_COMBO_POINTS`,
`OPEN_LOCK`, and the auras `MOD_DECREASE_SPEED` (116), `MOD_MELEE_HASTE` (94), `PROC_TRIGGER_SPELL` (61), `MOD_DAMAGE_DONE`
(43), `MOD_DAMAGE_TAKEN` (35), `MOD_FEAR` (29), `MOD_INCREASE_SPEED` (23), `MOD_SILENCE` (20), `DAMAGE_SHIELD` (20),
`SCHOOL_ABSORB` (18).

Why those were not done here (each needs a system that is another area's or a design decision):

- Speed auras need the pending-movement-change and ack flow (`MovementPacketSender`, `HandleForceSpeedChangeAck`) and the
  creature spline speed (`Creature.CreatureRunSpeed` is template-only).
- Melee haste needs a base attack time that weapon changes also respect.
- Proc auras need the proc system; damage, absorb, reflect and immunity auras need the damage and hit-result
  pipeline to read them; silence, pacify, disarm, fear and confuse need the cast and movement gates.
- Spell power and healing bonuses do not exist, so `MOD_DAMAGE_DONE` and friends would be write-only.
- `POWER_DRAIN` stays unregistered: `QuestRewardEffectCapabilityTests` uses it as the "no handler" sentinel.

Conflicts to resolve at integration: the stats area (`stats-combat-formulas`) may introduce a modifier ledger that owns
the stat and resistance fields; `StatAuras` then moves onto it (it is the only writer of those fields from auras here).
The skills area owns `PROFICIENCY`, `WEAPON`, `LANGUAGE` and the passive skill effects; `SpellInfo` carries no
equipped-item class fields yet, which `PROFICIENCY` needs.
`n## Combat spell data model (warrior-mechanics S02a)

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

## Combat spell seams (warrior-mechanics S02b)

Registration points on `SpellSystem` for the stance, equipment, combo, aura-state, proc and spell-mod features.
They are registered by calling `Register*`, not discovered. Callbacks run on the world thread; a throwing callback
is not caught (same rule as `SpellHit`). Types are in `SpellCombatSeams.cs`, the registries in `SpellSystem.Seams.cs`,
the melee slot in `SpellSystem.NextSwing.cs`.

- `ISpellCastCheck` (`RegisterCastCheck`): may veto a cast with any `SpellCastResult`. Checks run by
  `SpellCheckPhase` then `Order` (`SpellCastCheckOrder`), the line order of vmangos `Spell::CheckCast`: shapeshift
  (`Spell.cpp:5349`) < caster aura state (`:5392`) < `CheckItems` (`:5698`) < the built-in range (`:5707`) and power (`:5721`) checks. Phases: Caster (after the cooldown check,
  before stun), Target (once an explicit unit target exists and is alive; skipped for spells without one), Items
  (before range). The context says whether the check is the strict cast-start one or the landing re-check.
  `GetErrorAtShapeshiftedCast` is strict-only in vmangos (`:5349`); the check itself must honour `Strict`.
  Two later phases cover the rest of `CheckCast`: Power (after range, line of sight and the target rules, before the
  built-in power check: the combo point requirement, `Spell.cpp:7035-7038`) and Final (after power: the 20% target aura
  state, `:5733-5742`; also run for triggered casts). Order values follow the source: shapeshift < caster aura state <
  equipment < combo points < target aura state (`SpellCastCheckOrder`). The context's target is the explicit unit
  target (null without one); in the Target and Items phases a self cast of a unit-target spell passes the caster.
- `ISpellCastObserver` (`RegisterObserver`): `OnPrepared`, `OnCast` (power taken, before targets and effects),
  `OnTargetOutcome` (miss reason, damage dealt, healing done, crit, effect mask; also for misses) and
  `OnFinished(completed)`. Damage and healing are credited to the outcome of the cast and target being applied;
  a nested triggered cast gets its own outcome.
- `ISpellValueModifier` (`RegisterValueModifier`; the context carries the effect's target): adjusts the effect value (before chain multipliers), the
  aura/channel duration (vmangos `CalculateDuration`, `SpellEntry.cpp:723-751`: never for permanent -1, floored at
  0), the power cost, and the cast time (`SpellEntry.cpp:487-494`: after the minimum, only when not 0, before haste).
  The duration is computed once per cast (`SpellCast.Duration`, like `m_duration`). Modifiers must be pure.
- Next-swing spells (`SpellInfo.IsNextMeleeSwing`, non-triggered) wait in `UnitSpellState.MeleeCast` instead of
  casting: SMSG_SPELL_START and the global cooldown at press, nothing else (vmangos `GetCurrentContainer`
  `Spell.cpp:7607`, `SetCurrentCastedSpell`). A second next-swing spell interrupts the queued one; generic casts and
  channels neither block nor cancel it; triggered next-swing spells cast at once. `CastQueuedMeleeSpell(caster,
  victim)` is what the melee swing calls (`Unit::AttackerStateUpdate` `Unit.cpp:2250-2254`): it re-checks, takes the
  power and applies the effects. `CancelCast` cancels the queued spell whatever spell id the client names
  (`SpellHandler.cpp:329-330`); `CancelQueuedMeleeSpell` is the same for the combat area (target lost, `Unit.cpp:4604`).
- `ISpellChainRangeProvider` (`RegisterChainRangeProvider`): replaces the fixed chain jump distance
  (`SpellConstants.ChainJumpRadius`); the melee-chain rule (`Spell.cpp:2256-2265`) is its first consumer (later slice).
- The melee swing calls `CastQueuedMeleeSpell` and `CancelQueuedMeleeSpell` through `IMeleeSpellHooks` (see combat.md,
  "Melee spells and the swing").
- Queued next-swing spells now send their own original5875 SMSG_ATTACKERSTATEUPDATE with spell ID and NO_ACTION,
  aggregated school/damage fields before the non-melee logs. White swing identity remains zero. Available zero/miss/lethal
  outcomes are covered; complete spell block/melee-hit physics and original-client acceptance remain pending.
  See [the local follow-up](../integration/helpful-next-swing-20261005.md).

## Warrior stances (warrior-mechanics S06)

`ShapeshiftService` (`Spells/Stances/`), installed by the world's `StanceFeature`, follows vmangos
`HandleAuraModShapeshift` (`SpellAuras.cpp:2420-2575`) and `HandleShapeshiftBoosts` (`:5433-5597`) at the 1.12.1 build.

- **Handler** for `SPELL_AURA_MOD_SHAPESHIFT` with forms 17-19 (Battle, Defensive, Berserker): the previous
  shapeshift aura is removed first; non-stance forms would also end `SHAPESHIFTING_CANCELS` auras; rage becomes
  the Tactical Mastery cap (class-script auras 831-835 = 50/100/150/200/250 raw, else 0; creatures 0,
  `:2528-2569`); `UNIT_FIELD_BYTES_1` byte 2 takes the form; the boost passive is added (21156 / 7376 / 7381,
  `:5468-5476`) and every known passive bound to the form is cast again (`SpellInfo.IsNeedCastSpellAtFormApply`,
  `SpellEntry.h:1141-1150`). Losing the form clears the byte, drops the boost, removes the self-cast auras bound
  to a form (`SpellAuraHolder::m_isRemovedOnShapeLost`, `SpellAuras.cpp:6672`: caster is the target and
  `IsRemovedOnShapeLost`) and interrupts a queued next-swing, preparing or channelled spell that is bound to it.
- **Gate**: `StanceCastCheck` (phase Caster, order Shapeshift) runs `GetErrorAtShapeshiftedCast` for the caster's
  form on the strict check of non-triggered casts only (`Spell.cpp:5349-5351`); the landing re-check and triggered
  casts skip it.
- **Persistence**: the stance aura is a normal permanent aura, so `character_aura` saves it and the restore
  re-runs the handler (form byte, boost). Passives are not saved and come back from the handler.
- **Death**: the real stance spells carry `ALLOW_AURA_WHILE_DEAD` (AttributesEx3 0x100000), and vmangos
  `RemoveAuraTypeOnDeath(MOD_SHAPESHIFT)` (`Player.cpp:1525`, `Unit.cpp:3955-3967`) spares death-persistent holders,
  so the stance outlives death; a stance without the bit is removed and the form cleared (both tested).
- **Form table**: `ShapeshiftFormCatalog`. `Combat:ShapeshiftFormDbcPath` points at the client's
  SpellShapeshiftForm.dbc (`ShapeshiftFormDbcReader`, 14 fields; a missing or malformed file stops the daemon).
  Without it only forms 17-19 are known, with `flags1 = 1` inferred from vmangos' definition of the stance flag
  (`SharedDefines.h:1471`), not read from a DBC; the result of the gate for warrior spells does not depend on it.
- **Config** (`Combat:StanceShiftKeepsSelfBuffs`, default false): vmangos removes Retaliation, Recklessness and Shield
  Wall with the old stance (the code above). vmangos also quotes patch 1.7.0 as saying they are no longer cancelled
  (`SpellAuras.cpp:5537-5539`), but the code under that comment is compiled only for builds up to 1.6.1, so the code
  is followed. With the option on, switching stances keeps them; cancelling a stance still removes them.
- **Historical warrior tranche limits**: forms other than17-19 were left unhandled here. Later local work adds
  cataloged Druid forms and [Ghost Wolf lifecycle](../integration/ghost-wolf-school-threat-20261005.md); unsupported
  forms and original-client acceptance remain separate. The original tranche had no model/display, speed or
  rage/energy swap; the "talent that learns a spell" exemption of the gate needs the talent tree (not loaded);
  Tactical Mastery only matters once talents exist (the aura is read, nothing grants it yet); the stance-change
  cooldown and the quest-granted stance spells are spell data / quest rewards, not code.

## Generic cast rules (warrior-mechanics S16)
`GeneralCastChecks` (installed by `CastCheckFeature`) registers four of vmangos `Spell::CheckCast`'s generic rules as cast checks:

- **Standing** (`Spell.cpp:5308-5309`, phase Start, before the cooldown check): a non-triggered cast needs a standing caster
  unless the spell has ALLOW_WHILE_SITTING (`NOT_STANDING`); the dead stand state counts as standing (`Unit::IsStandingUp`).
- **Combat-forbidden spells** (`:5343-5344`): `NOT_IN_COMBAT_ONLY_PEACEFUL` (Charge) fails with `AFFECTING_COMBAT` in combat; strict,
  non-triggered casts only, ahead of the shapeshift gate.
- **Stealth-only spells** (`:5353-5354`): `ONLY_STEALTHED` needs a `MOD_STEALTH` aura; strict, non-triggered, after the shapeshift gate.
- **Facing** (`:5639-5649`, phase Target): a behind-only spell (`AttributesEx2 == 0x100000` and `AttributesEx & 0x200`; vmangos' database
  custom flag is not available) fails with `NOT_BEHIND` unless the caster is behind the target, and a creature that fights the
  caster and is not incapacitated always faces it on the strict check; a spell whose Attributes are exactly `0x150010` needs the
  target to face the caster (`NOT_INFRONT`). vmangos also sends an interrupt packet with these two; that packet is not sent.
- **Not covered**: indoor and outdoor (terrain), underwater and above-water, battleground, mounted and taxi, target level limits,
  the other target flags, and the explicit-target mask check.

## What's left

`spell_script_target` rows now drive implicit unit targets 38 (nearest listed living creature/player or corpse) and 7 (source-area
units filtered by entry and effect mask), following vmangos `Spell::CheckScriptTargeting` and `Spell::SetTargetMap`. World schema 45
stores the rows. Game object script targets and the other script target modes remain outside this slice.

- Area, chain and cone target selection are implemented (`SpellSystem.Targeting.cs`, with a line-of-sight filter on area lists); only the remaining TargetB-based selections are missing.
- Spell focus, non-warrior shapeshift forms, remaining facing and area restrictions.
  Eight-slot reagent costs and the build-5875 item-use path are now implemented
  in the local continuation; item cooldown packet metadata and remaining
  consumable target fidelity are still pending. See [item/pet integration](../integration/server-item-pets-20261004.md).
- Talent spell effects (point accounting, learning, respec and persistence exist: [talents](talents.md)), ranks, the proc system (aura holders carry `procCharges`, but nothing consumes them). Hit, crit, resist, diminishing returns, immunities, dispel, crowd-control state, pushback and lockout, absorb and the caster-state gate exist: see [spell-rules.md](spell-rules.md) for scope, configuration and limits.
- Complete spell combat modifiers. Integrated spell damage now uses map combat death/threat,
  and effective healing adds base distributed threat and enters combat.
- Non-player far teleports. Player far teleports and shared creature lookup are connected in
  the integration candidate; non-player transfers remain unsupported.
- Trainers and the trainer spell list (the NPC area, through `SpellSystem.LearnSpell`).
- The remaining effect and aura types, which are logged once as unsupported (see the ranking in "Handler modules and spell breadth").
