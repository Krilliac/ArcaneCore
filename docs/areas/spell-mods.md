# Area: Spell modifiers

Branch `claude/vw5-spell-modifier-engine`. The vanilla 1.12.1 (build 5875) spell-modifier engine: auras 107 (ADD_FLAT_MODIFIER)
and 108 (ADD_PCT_MODIFIER) become live modifiers a player holds, every place vmangos reads `Player::ApplySpellMod` asks the engine,
charged modifiers (Clearcasting, Nature's Grace, Nature's Swiftness, Shadow Trance shapes) are spent and given back exactly like
vmangos, and the client is told. Talents become **data**: a talent rank spell is a passive with aura 107 or 108, so learning it
through the talents area (`docs/areas/talents.md`) is all it takes; there is no per-talent code.

References (read-only, never copied): **vmangos** `D:\refs\vmangos` (primary), **mangos-classic** `D:\refs\mangos-classic` (where it
differs), **wow_messages** `D:\refs\wow_messages` (`wowm/world/spell/smsg_set_flat_spell_modifier.wowm`,
`smsg_set_pct_spell_modifier.wowm`), **classic-db** `D:\refs\classic-db` (`spell_affect`). Every formula below cites its source line.
No retail data is copied: every spell in the tests is a synthetic talent shape.

## Delivered

| Layer | Files (`src/ArcaneCore.*`) | What it does |
|---|---|---|
| Engine | `Game/Spells/Mods/SpellMod.cs`, `SpellModMath.cs`, `SpellModEngine.cs`, `ISpellModEngine.cs`, `SpellModScope.cs`, `SpellModOptions.cs`, `SpellModExtensions.cs` (`spells.Mods`) | The per-player mod lists, the vmangos formula, the charge scope (`m_appliedMods`). The engine is an `ISpellModifiers`, so every consumer of that seam reads it. |
| Auras | `SpellModModule.cs`, `SpellModAuraHandlers.cs`, `PassiveReapply.cs`, `HardcodedMods.cs`, `SpellModValueAdapter.cs`, `SpellModCastObserver.cs` | One discovered `ISpellHandlerModule`: handlers for aura 107/108, the engine installed as `SpellSystem.SpellModifiers`, the cast-time and duration value hooks, the Frost Warding / Improved Fire Ward mods, the cast observer that ends charged mods. |
| Client | `SpellModPackets.cs`, `SpellModClientSync.cs` | SMSG_SET_FLAT_SPELL_MODIFIER (0x266) and SMSG_SET_PCT_SPELL_MODIFIER (0x267). |
| Owners | `ISpellModOwnerResolver.cs`, `PetTotemModOwner.cs` | `GetSpellModOwner`: a pet or totem reads its owner's mods. |
| Masks | `IClassMaskSource.cs`, `World/Spells/Mods/FileClassMaskSource.cs`, `Data/Content/Import/Mappers/SpellAffectDumpImporter.cs`, `arcane-content-importer class-masks` | 64-bit class masks from an overlay file. |
| Report | `TalentModCoverage.cs` | How many talent rank spells carry a modifier, which cannot work, and which use an operation nothing reads (`Readers`). |
| World | `World/Spells/SpellModFeature.cs` | Binds `Spells:Mods`, loads the overlay, logs the mask report. |
| Pipeline sites | `SpellSystem.Mods.cs` and one-line calls in `SpellSystem.cs`, `.Seams.cs`, `.Targeting.cs`, `.AreaAuras.cs`, `.Effects.cs`, `.Combat.cs`, `SpellCombatRules.cs`, `Effects/SpellThreat.cs`, `Casters/Bonus/SpellBonusModule.cs`, `Casters/Drain/DrainAuras.cs`, `SpellCast.cs` | See the site table below. |

### The formula (`SpellModMath.Evaluate`, vmangos `Player::ApplySpellMod`, `Player.cpp:22417-22466`)

`result = value + ((value + flat) * pct / 100 + flat)` in single precision (`diff`, `:22463`), flat and percent each summed over every
mod that affects the spell. An integer base is truncated toward zero (`T(float(base) + diff)`), which `ISpellModEngine.Apply(int)`
does and `SpellSystem.ModInt` returns unchanged when nothing modified it.

- **Affected** (`SpellModifier::IsAffectedOnSpell`, `SpellModifier.cpp:34-41`, `SpellEntry::IsFitToFamilyMask`, `SpellEntry.h:698-701`):
  the spell has the same `SpellFamilyName` as the spell that carries the aura and `(SpellFamilyFlags & mask) != 0`. A zero mask
  affects nothing. The mask is the aura effect's `EffectItemType` (`SpellMgr.h:394-398`).
- A percent mod is skipped when the base is 0 (`:22431-22434`, "most important for spell mods with charges"); CASTING_TIME with a
  base of 10 s or more skips a percent mod of -100 or less (`:22436-22438`); a CASTING_TIME percent mod of exactly -100 forces -100%,
  discards the flat total and **stops reading further mods** (`:22455-22460`, the Barkskin plus Nature's Swiftness fix).
- A spell with `SPELL_ATTR_EX3_IGNORE_CASTER_MODIFIERS` (`SpellDefines.h:978`) is never modified (`:22420`).
- Percent below -100 is not clamped (vmangos); mangos-classic floors the multiplier at 0 (`Player.h:2577-2619`). Not offered as an option.

### Aura handler (`Aura::HandleAddModifier`, `SpellAuras.cpp:1081-1111`)

Only a player holds a mod; an operation at or above `SpellModOp.Max` (29) is ignored. The mod's family name is the **aura spell's**
`SpellFamilyName`, its mask the effect's. Charges are `0` for a spell with `StackAmount > 1`, otherwise the holder's `procCharges`;
Shadow Trance (17941) and Netherwind Focus (22008) start with one (`:1090-1099`). Removing the aura removes the same mod. After a mod
is added or removed, every permanent passive self-cast aura it affects is removed and cast again (`ReapplyAffectedPassiveAuras`,
`:1005-1075`), except for the operations DURATION, CHARGES, NOT_LOSE_CASTING_TIME, CASTING_TIME, COOLDOWN, COST, ACTIVATION_TIME,
GLOBAL_COOLDOWN, SPEED, HASTE and ATTACK_POWER and for a mod spell with `procCharges`. Frost Warding (11189, 28332, mask `0x100`) and
Improved Fire Ward (11094, 13043, mask `0x8`) are flat RESIST_MISS_CHANCE mods built in code (`:2117-2155`).

### Call sites (each cites the vmangos line it follows)

| Op | ArcaneCore site | vmangos |
|---|---|---|
| COST | `SpellSystem.CalculatePowerCost(modifyCost)`, between the school flat modifier and the creature scaling / school multiplier | `Spell.cpp:7008-7040` |
| CASTING_TIME | `SpellModValueAdapter` (the cast-time value seam; never asked for 0) | `SpellEntry.cpp:486-494` |
| DURATION | `SpellModValueAdapter` | `SpellEntry.cpp:723-751` |
| GLOBAL_COOLDOWN | `AddGlobalCooldown`, before the haste scaling | `Player.cpp:22101-22111` |
| COOLDOWN | `AddCooldown`: the spell time when it has one, else the category time | `Player.cpp:22200-22206` |
| RANGE | `CheckRange` / `CheckDestRange` on the maximum range before the leeway; melee: `range_mod = 1.0 + diff` of ATTACK_DISTANCE (a vmangos quirk, reproduced) | `Spell.cpp:6890-6917` |
| RADIUS, JUMP_TARGETS | `AreaRadius`, `RandomNearCaster`, `Chain`, area auras | `Spell.cpp:2058-2062`, `SpellAuras.cpp:420` |
| EFFECT_PAST_FIRST | the chain multiplier in `Chain` | `Spell.cpp:1766`, `:1920` |
| ALL_EFFECTS | after the registered value modifiers (combo points) | `SpellCaster.cpp:1197-1199` |
| DAMAGE / DOT | the finished done amount in `SpellBonusModule.Done` (direct damage and healing use DAMAGE, over-time snapshots DOT), melee and ranged class spells, `EffectWeaponDamage`; all before the target side | `SpellCaster.cpp:1443-1452`, `:1520-1525`, `:1696-1700` |
| SPELL_BONUS_DAMAGE | the coefficient times 100, only when there is a benefit | `SpellCaster.cpp:1760-1766` |
| CRITICAL_CHANCE, CRIT_DAMAGE_BONUS, RESIST_MISS_CHANCE (magic), RESIST_DISPEL_CHANCE, MULTIPLE_VALUE (mana shield), NOT_LOSE_CASTING_TIME | the sites the `ISpellModifiers` seam already had | `Unit.cpp:5311-5314`, `SpellCaster.cpp:958-980`, `:841`, `SpellEffects.cpp:2548`, `Unit.cpp:2062`, `Spell.cpp:7482` |
| RESIST_MISS_CHANCE (melee) | `MeleeSpellHitResult`: a hit-chance bonus that lowers the miss chance | `SpellCaster.cpp:381-388` |
| THREAT | `SpellThreat.Add`, before the MOD_THREAT auras | `ThreatManager.cpp:41-44` |
| MULTIPLE_VALUE | health leech effect, leech aura tick, mana leech tick | `SpellEffects.cpp:1868`, `SpellAuras.cpp:6008`, `:6171-6175` |
| ACTIVATION_TIME | `SpellSystem.ModifiedAmplitude` on the amplitude of the periodic aura types (`PeriodicTiming.TakesActivationTimeMod`), at creation and again on an in-place refresh (the fresh value, not compounded as vmangos does). Not on a restore: an aura loaded from `character_aura` takes the spell's raw amplitude, because the table keeps no period; vmangos saves `periodic_time0-2` and loads them back (`Player::_LoadAuras`, `Aura::SetLoadedState`) | `SpellAuras.cpp:8078-8083`, `:293`, `:319`; `Player.cpp:15318`, `:15409` |

A unit standing in for a game object (`SpellSystem.CastForGameObject`, a wild trap or spell caster casting by itself) gets no mods:
`ModInt` and `ModFloat` return the value unchanged for it while the object's cast runs, as a game object has no mod owner
(`SpellCaster.cpp:1195-1200`, `GetSpellModOwner` is a unit method). The magic hit roll reads the seam directly and is not covered.

### Charges (`Player::DropModCharge`, `RestoreSpellMods`, `RemoveSpellMods`, `Player.cpp:17617-17783`)

A cast gets a `SpellModScope` (`Spell::m_appliedMods`) when it is prepared. The engine spends charges only inside a **consume window** that
`SpellSystem` opens around two phases: the cast-time read at prepare (after the first `CheckCast`, so a failed check spends nothing,
`Spell.cpp:3436`) and the whole of `Cast()` from the re-check onwards, where the cost is read again with charges (`:3646-3658`,
"in case of mana reduction buff proc while casting") and the effects and crit rolls run. Outside a window (checks, power checks) the
engine only reads. Operations vmangos reads without a spell (duration, both cooldowns, threat, charges, activation time, chance of
success, haste, attack power) never spend a charge. A spent mod with charges `> 0` goes to 0 -> `-1`: it is "depleted but pinned" and applies only
to the casts whose scope holds it. A successful cast removes the aura of every pinned mod (`Spell::finish`, `:4399`); a channel does it
when the channel starts (`:3834`); a cancelled or failed cast gives the charges back (-1 becomes 1; `:3534`, `:4364`). A percent mod on a zero
value spends nothing; a flat CASTING_TIME mod is not spent when an instant-cast percent mod covers the spell (Patch 1.11, Nature's
Grace after Nature's Swiftness, `Player.cpp:22444-22453`; option `InstantCastKeepsFlatCastTimeCharge`, default true = retail).

### Client packets (`Player::SendSpellMod`, `Player.cpp:17650-17672`)

`u8 bit, u8 op, i32 value`, one packet per set bit of the mod's mask (0-63, ascending) carrying the sum of the player's mods of that
op and type that have the bit, on add and on remove (0 when none remain). wow_messages marks the pct layout with an unresolved
"CORRECT_LAYOUT" note; vmangos writes both identically and the tests pin vmangos.

## Configuration (`Spells:Mods`, `SpellModOptions`, restart-only: `.reload config` does not re-read it)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Kill switch: false makes aura 107/108 inert again (no mod registered, no value changed). |
| `HardcodedWardMods` | true | The built-in Frost Warding / Improved Fire Ward mods (`SpellAuras.cpp:2117-2155`). |
| `CustomCharges` | true | The single charge of Shadow Trance and Netherwind Focus (`:1090-1099`). |
| `ReapplyPassives` | true | `ReapplyAffectedPassiveAuras` after a mod changes. |
| `InstantCastKeepsFlatCastTimeCharge` | true | The Patch 1.11 Nature's Grace rule (build 5875 > 1.10.2 is retail). |
| `SendClientModifiers` | true | SMSG_SET_FLAT/PCT_SPELL_MODIFIER. |
| `OwnerModsForPetsAndTotems` | true | `GetSpellModOwner` for pets and totems. |
| `ClassMaskFile` | unset | The 64-bit class-mask overlay (below); unset reads the DBC's 32-bit masks only. |

## Class masks (the 64-bit problem)

vmangos holds the class mask in a 64-bit `EffectItemType` (`SpellEntry.h:656`); ArcaneCore's spell table reads one 32-bit word
(`SpellEffectInfo.ItemType`, `SpellDbcImporter.cs`). In classic-db `spell_affect` 15 of 132 rows (11%) have a mask above bit 31
(bits 32-42 and 52 occur), so with DBC masks only those talents would affect the wrong spells or none. The overlay fixes that without a
schema change: `arcane-content-importer class-masks <world.sql[.gz]> --class-mask-file <file>` writes `spell effect 0xMASK` lines (outside
the repository, GPL data), `Spells:Mods:ClassMaskFile` points at it, and the engine asks the overlay first and the DBC second.
A malformed, duplicated or missing file fails startup; an unset file with modifier effects that have no mask logs a warning with the
count. `SpellModFeature` and `TalentModCoverage` print the numbers, which is the operator's counter (this repository has no
`Spell.dbc`, so per-class counts could not be measured here).

## Limits and open questions

- **Operations not wired** (nothing in the base reads them yet): SPEED and ATTACK_POWER on an aura's own amount (`SpellAuras.cpp:3982`,
  `:5086-5219`) and CHARGES at holder creation (`:6693`) (the holder constructor is another lane's file). `TalentModCoverage` reports the
  talent modifier effects that use them ("nothing reads it"); its `Readers` list names the reading site of every other operation and a test
  compares that list with the reads in the source, so wiring one of these three updates the list in the same change. The operations this list
  once named are wired: HASTE (`AttackSpeedAuras`, `:4016`), CHANCE_OF_SUCCESS (the proc chance, `SpellSystem.Procs.cs` and
  `SpellSystem.ItemCombatProcs.cs`, `UnitAuraProcHandler.cpp:490`), the aura-level RESIST_MISS_CHANCE (`SpellSystem.Auras.cs`
  `ApplyReflectSchoolMods`, `:5430`) and MULTIPLE_VALUE (`DrainAuras`, `SpellSystem.PowerBurn.cs`, `SpellSystem.Combat.cs`,
  `SpellSystem.Mitigation.cs`). The engine already answers every operation, so wiring is a one-line call at the site.
- **Radius** is not applied to caster-relative destination points (`SelectCasterRelativeLocation`) or persistent area auras
  (dynamic objects do not exist yet).
- **Charges**: a charge is only spent by the cast-time read, the cost and what runs inside `Cast()`. vmangos also spends at the first
  `CheckCast` (range, jump targets); here `CheckCast` only reads. A delayed (projectile) hit runs after the window. A pet's or totem's
  cast reads its owner's mods but never spends the owner's charges (vmangos guards the restore and remove calls with `IsPlayer`, so a pet
  would leave the mod stuck at -1). A saved charged aura comes back with the holder's charges, as in vmangos, which does not mirror
  consumption to the holder.
- **Unswitched deviations, awaiting a developer decision** (no `Spells:Mods` option exists for either; the standing directive wants a default-retail switch, and neither retail behaviour is a sensible default):
  (1) a pet's or totem's cast reads its owner's mods but never spends the owner's charges (`SpellModEngine.CreateScope` returns no scope for a non-player). In vmangos the spell's `m_appliedMods` does record the spend (`DropModCharge`) but `Spell::finish` and `cancel` only call `RemoveSpellMods`/`RestoreSpellMods` when `m_caster->IsPlayer()` (`Spell.cpp:3534`, `:3564`, `:4399`), so retail leaves the owner's mod stuck at -1; a switch would reproduce that bug.
  (2) `PassiveReapply` stops re-entering after depth 2 (a recast passive that is itself a modifier). vmangos has no limit; the depth is a safety guard against a cycle of mutually affecting passives, and the claim that two levels cover the retail talent chains is **unverified** (no `Spell.dbc` here).
- **Pets and totems** are not refreshed by `ReapplyAffectedPassiveAuras` when the owner's mod changes (`CallForAllControlledUnits`,
  `:1067-1073`).
- **Mask data**: vmangos corrects 99 masks from the classic SpellEffect.db2 in `sql/migrations/20240926142033_world.sql` (23 of them above
  32 bits) with `UPDATE ... WHERE` guard values that only equal the spell data after the `spell_affect` fold-in; evaluating them needs the
  imported spell data, so the importer does not apply them, and vmangos' `spell_effect_mod` table is not read either. Which list is
  retail is genuinely contested (the corrected and the classic-db values agree on only 32 of the 99 guarded spells). Open question:
  does the real 1.12.1 `Spell.dbc` `EffectItemType` hold 32 or 64 bits? (`SpellFamilyFlags` is read as 64.) Needs a DBC.
- **Login**: SMSG_SET_*_SPELL_MODIFIER is sent when the passive is cast on login, not after SMSG_INITIAL_SPELLS in a dedicated resync;
  whether a real client accepts it at that moment is not verified (no real client acceptance was run for the client packets,
  the cost display, the Clearcasting-shaped zero cost or the respec zeroing).
- Aura 112 (OVERRIDE_CLASS_SCRIPTS) and 109 (ADD_TARGET_TRIGGER) are not part of this area (proc and class-script work); the mask
  overlay serves them too, with the same 32-bit limit when it is not configured.
- **Stores**: no store or schema is touched, so MariaDB and PostgreSQL provider theories are not applicable (state: SQLite-only,
  nothing to exercise). The overlay is a text file, not a table.

## Tests

`tests/ArcaneCore.Game.Tests/SpellMods/` (math, aura handlers, cast pipeline, amounts and combat, charges, packets, pets and totems,
talent shapes, the talent coverage report, the TalentService end-to-end round trips), `tests/ArcaneCore.Data.Tests/SpellMods/`
(importer and `class-masks` command), `tests/ArcaneCore.World.Tests/SpellMods/` (overlay file, feature). All are deterministic: the
spell clock is stepped by the test and `SpellSystem.Random` is seeded. `ModTestSupport` builds synthetic talent-shaped spells.
