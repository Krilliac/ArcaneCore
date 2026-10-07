# Player stats and combat formulas

Lane `stats-combat-formulas` (branch `claude/vw-stats-combat-formulas`). Everything below is
reimplemented from the behaviour of the references; no reference code or data is copied into this
repository. `D:\refs\vmangos` is the primary reference (commit 4b3d241), `D:\refs\mangos-classic`
the cross-check, `D:\refs\classic-db` the base data.

Code: `src/ArcaneCore.Game/Stats/`, `src/ArcaneCore.Game/Combat/ICombatStatSource.cs`,
`src/ArcaneCore.Kernel/WorldData/PlayerStats/`, `src/ArcaneCore.Data/World/PlayerStats/`,
`src/ArcaneCore.World/Stats/`. Tests: `tests/ArcaneCore.Game.Tests/Stats/`,
`tests/ArcaneCore.Game.Tests/Spells/CombatAbilityEffectTests.cs`,
`tests/ArcaneCore.Data.Tests/PlayerStatsImportTests.cs`, `tests/ArcaneCore.World.Tests/Stats/`.

## What was wrong

A player's update fields for attack power, min/max damage (main hand, off hand, ranged), attack time,
crit, dodge, parry and block were never written, and the equipped weapon was ignored. The melee swing
reads `UNIT_FIELD_MINDAMAGE/MAXDAMAGE`, so every player swing hit the vmangos "max damage 0 becomes
5" fallback (`Unit::CalculateDamage`, Unit.cpp:2327-2328) and rolled 0-5 plus nothing. Equipping a
+stamina item changed the stat but not the health, and the base level data only existed if a developer
supplied a text file.

## Delivered

### 1. Pure formulas (`Stats/StatFormulas.cs`, `UnitMods.cs`, `AgilityRates.cs`, `SpellCritTable.cs`)

| Formula | vmangos reference |
|---|---|
| health from stamina (1 per point to 20, then 10), mana from intellect (then 15) | StatSystem.cpp:149-163 |
| armor 2 per agility, intellect-percent armor aura | StatSystem.cpp:128-147 |
| max health / max power composition `((base + create) * basePct + total + bonus) * totalPct` | StatSystem.cpp:165-192 |
| attack power by class, druid forms, Predatory Strikes | StatSystem.cpp:194-307 |
| total attack power (mods, multiplier, floor 0) | Unit.cpp:8037-8061 |
| AP multiplier 2.4 fist, 1.7 dagger, 3.3 two-hand, 2.8 ranged | SpellCaster.cpp:1826-1853 |
| min/max damage `((base + AP/14*speed + weapon) * basePct + total + physical) * totalPct`, shapeshift range, unusable weapon, extra damage entries, ammo DPS | StatSystem.cpp:354-455 |
| crit, dodge, parry, block percentages, class bases 0.9/3.2/0.7/3.0/1.7/2.0, 0.04 per skill point | StatSystem.cpp:514-640 |
| shield block value | Player.cpp:5137-5144 |
| agility rate tables: level 1 and last level mandatory, linear gap interpolation, last rate past the end | ObjectMgr.cpp:5072-5148, 5332-5358, Util.h:357-365 |
| combat skill gain chance (max = 5 * level, defense caps the mob at level + 5, weapon two-segment curve, +min(10, 0.02 * int)) | Player.cpp:5341-5400 |

The critic's correction is applied: the skill gain maximum is `5 * playerLevel`, not the skill's own
maximum (Player.cpp:5356). The skill gain chance is a pure function; nothing calls it yet (see limits).

Spell crit from intellect (`SpellCritTable`) is **not proven retail**: vmangos Unit.cpp:2604 and
mangos-classic Player.cpp:4988 both carry the comment "from mangos 3462 for 1.12 MUST BE CHECKED", and
mangos-classic adds "FIXME: Add base value and scaling for hunters" (Player.cpp:5009). The values are
exactly the references' table; `SpellCritTable.VerifiedRetail` is `false`. Nothing consumes it yet.

### 2. Player base data (`PlayerStatsDataModule`, `PlayerStatsDumpImporter`, `VMangosMigrationReader`)

World schema step `PlayerStatsDataModule.Version` (World 11 in the integrated tree). Tables, named like the source tables: `player_classlevelstats`, `player_levelstats`,
`player_xp_for_level`, `player_crit_per_agility`, `player_dodge_per_agility`. `PlayerStatsContent`
(Kernel) is the read model; it applies the reference's gap fill (a level without a row uses the nearest
lower one, ObjectMgr.cpp:4884-4892, 4996-5004) and `Validate` reports what the reference exits on
(level 1 rows, XP rows below the maximum level, rate rows for level 1 and the last level).

The crit/dodge per agility rates exist only in vmangos migrations (`20260703210621`, `20260711022757`,
`20260711025640`); classic-db has only the first three tables, and vmangos corrected many of those rows
afterwards (`20210515110157`, `20220917141705`, `20221022144513`, `20230212062229`, `20231111002042`).
`PlayerStatsDumpImporter.Read` takes a classic-db or vmangos dump (by column name),
`ApplyMigration` / `ApplyMigrationDirectory` replays the `UPDATE` and `INSERT` statements of
`sql/migrations/*_world.sql` on those rows in timestamp order. Any other statement on a tracked table
(`DELETE`, `TRUNCATE`, `ALTER`, an `UPDATE` without `WHERE` or with an expression) throws
`NotSupportedException` rather than being skipped. An `UPDATE` that matches no row is reported as a
warning. `WriteAsync` is atomic with the same transaction contract as `CreatureDumpImporter`.

There is still no importer CLI (same state as the creature and game object importers); the library entry
points are the importer methods:

```csharp
var importer = new PlayerStatsDumpImporter();
importer.Read(textReaderOverTheClassicDbDump);          // classic-db player_* tables
importer.ApplyMigrationDirectory(@"D:\refs\vmangos\sql\migrations");
PlayerStatsImportReport report = await importer.WriteAsync(worldDb, replace: true);
```

Verified against the real references (a throwaway program, not committed): the classic-db dump gives
540 class/level rows, 2400 race/class/level rows and 60 XP rows; all eight migrations contain tracked
statements (1248 statements; 368 row updates, 0 unmatched); the migrations change 260 level-stats rows and
107 class/level rows relative to the dump and add 440 crit and 440 dodge rate rows (class 1:59, 2:32,
3:58/57, 4:58, 5:26/27, 7:28, 8:60, 9:60, 11:59 sniffed levels, the rest interpolated); `Validate` over
every playable race/class pair at maximum level 60 reports 0 problems.

### 3. The player stat system (`PlayerStatSystem`, `PlayerStatState`, `StatBonuses`)

`PlayerStatSystem.Attach(player)` (done by `StatsFeature` at login, after the items and the level base
values are loaded) routes the inventory's item hook and its dual wield rule through the system, rebuilds
the weapon state from the equipment already worn and writes every derived value; afterwards every item
equip/unequip, level-up and learned ability (Parry, Block and Dual Wield) recomputes. Every update is a recompute from the stat fields
and the equipment, so calling it twice changes nothing.

Written fields: `UNIT_FIELD_ATTACK_POWER`, `UNIT_FIELD_RANGED_ATTACK_POWER`, min/max damage of the main
hand, off hand and ranged slot, `UNIT_FIELD_BASEATTACKTIME` (main, off hand) and `RANGEDATTACKTIME` from the
weapon's delay (restarting the swing timer when in combat, Player.cpp:6988-7001), `PLAYER_CRIT_PERCENTAGE`,
`PLAYER_RANGED_CRIT_PERCENTAGE`, `PLAYER_DODGE/PARRY/BLOCK_PERCENTAGE`, the agility part of armor (as the
difference to what the system added before, so item armor stays), and the stamina/intellect bonus of the
maximum health and mana (below).

The weapon part of `Player::_ApplyItemMods` is ported faithfully (Player.cpp:6826-7008): per hand a list of
up to five damage entries (min, max, school) and an entry count, set from the item prototype and zeroed
again when the item leaves, a weapon in the ranged slot only counts as ranged for bow/gun/crossbow/thrown/wand
types, broken items count for nothing. **A vmangos behaviour is reproduced:** unequipping a weapon zeroes the
entries the item used (Player.cpp:6954-6976), so a hand that held a weapon and lost it deals only the attack
power part until a weapon is equipped again, while a hand that never held one deals the 1-2 fist range
(Unit.cpp:129-139). That is vmangos' behaviour; whether retail differs is not verified here (open question).

`ICombatStatSource` (implemented by `PlayerStatSystem`, installed on every map's `MapCombat.Stats`) answers the
hit table's equipment questions: has an off-hand weapon (usable, unbroken, Unit.cpp:499-509), can parry (the
Parry ability and a main-hand, else off-hand weapon, Unit.cpp:2496-2499 / Player.cpp:8518-8539), can block
(the Block ability and an unbroken item with a block value in the off-hand slot, Unit.cpp:2531-2539), the
shield block value (Player.cpp:5137-5144). A unit it does not know answers null and the `CombatHooks`
defaults apply, so every existing combat test keeps its behaviour. The seven call sites in
`MapCombat.Melee.cs` now go through `MapCombat` helpers; an off-hand swing's weapon skill is the level
maximum when the source sees an off-hand weapon (the hooks only know about it through `HasOffhandWeapon`).

Abilities: the Parry, Block and Dual Wield spell effects (SpellEffects.cpp:5280, 5286, 2620) set
`PlayerStatState.CanParry/CanBlock/CanDualWield`; learned passive spells are already cast on learn and at login
(`SpellSystem.CastLearnedPassive`), so the abilities come back after every login. A player attached to the
system can equip an off-hand weapon only with Dual Wield (`StatStateItemRequirements`).
Unlike the reference, where `SetCanDualWield` is a plain flag, it recomputes the attack power and damage fields: the passive is cast after the items are attached at login, when the off-hand damage was written without attack power. Partial agility rate rows (a class missing level 1 or 60, a rate of 0) leave out the agility terms with one warning unless `Stats:RequireImportedData` is set, which refuses the login.

Health from stamina and mana from intellect (`StatBonuses`, StatSystem.cpp:165-192) follow the **total** stat,
items included: equipping +10 stamina raises the maximum health by 100 once the stamina is above 20, +10
intellect raises the maximum mana by 150 for a class with a mana pool (create mana above 0); the current value
is clamped when the maximum drops (Unit::SetMaxHealth). The level application
(`PlayerProgression.ApplyBase`) now moves only the class base health and mana; the bonuses are applied by
`StatBonuses` through a ledger on `PlayerStatState`, so a host without the stat system behaves exactly as before.

### 4. The daemon feature (`StatsFeature`)

Loads `IPlayerStatsContentStore` once at the first login (the progression hook awaits it first). When
`Progression:LevelStatsPath` is empty and the import holds level rows, the level base values come from the
imported rows joined with the class health/mana (instead of the developer text file). Builds the
`AgilityRates` from the imported rate rows. Without a content store, or with an empty one, the feature still
attaches players: level stats stay as `Progression` configures them and crit/dodge get no agility term, with
one warning. A failed load is not cached and fails the login (fail closed, like the items feature).

## Deviations from retail (config switch, default)

| Deviation | Switch | Default |
|---|---|---|
| An incomplete player base data import does not refuse the login; the agility terms of crit/dodge are left out and the level stats may be absent (vmangos exits at startup, ObjectMgr.cpp:4876-4882, 4988-4994, 5117-5129) | `Stats:RequireImportedData` | `false` (set `true` on a host that imported the data to get the retail refusal) |

Everything else follows vmangos. Unverified-retail items inherited from vmangos: spell crit table (not
consumed), the weapon-entry zeroing above.

## Limits (not done)

- No aura modifiers. There is no `SPELL_AURA_MOD_*` stat system in the tree, so +AP/+crit/+dodge/+% auras,
  weapon-specific parry talents, haste, shapeshift forms (druid AP, weaponless damage) and the disarm
  *trigger* are not modelled (disarm is honoured when the system recomputes, nothing recomputes on disarm).
  `UnitMods` / the modifier-group model of the design was deliberately not built: it would have no producer.
- ~~Ammo DPS is 0~~ Fixed by the ranged lane (wave 4): the ammo DPS is part of the ranged damage fields and is recomputed when the ammo changes (`docs/areas/ranged/damage-inputs.md`).
- With retail skill content, `StatsFeature` uses `PlayerSkillStatSource` to read effective weapon and
  defense skills, including temporary and permanent aura bonuses. Skill changes recompute the crit,
  dodge, parry and block fields (vmangos `0e3ff01e`, `StatSystem.cpp:514-640`). Hosts without skill
  content retain `LevelMaximumSkills`. Skill gain and persistence are implemented by the skills area.
  `SkillAuraWorldTests` checks live recomputation and restoration across logout/login without storing
  bonuses as pure skill values.
- Not persisted: XP, rest, health and powers still reset on relog (design slice `character-progress-persistence`
  was not done: Characters schema, cleanup registration).
- Melee fidelity beyond the stat inputs is untouched: one sub-damage per swing, one armor value for all
  schools, crush/glancing details, threat, rage rates (`melee-fidelity`, `threat-fidelity`).
- Creature stats still come from the template rows (`creature-stats` not done); XP/rest/tap rules
  (`xp-rest`, `tap-and-damage-origin`) and spell-side combat math (`spell-combat-fidelity`) are untouched.
  `PlayerXpTable` still returns 0 for the next-level XP at level 60 (vmangos GetXPForLevel returns the table
  row, ObjectMgr.cpp:8522-8528); changing it was left to the XP slice because an existing test pins 0.
- The base data importer has no CLI.

## Tests

- `StatFormulaTests`, `AgilityRateAndSpellCritTests` (103 cases): hand-worked vmangos values.
- `PlayerStatSystemTests`, `StaminaIntellectBonusTests`, `CombatAbilityEffectTests`: the production write path
  (equip through the inventory, abilities through the spell effects, fields inspected, the hit table's inputs).
- `PlayerStatsImportTests`: dump layouts, migration statements in the real wrapper shape, fail-closed cases,
  gap fill, validation, atomic write on every available provider.
- `StatsFeatureTests`: login through the real daemon test host with imported rows and a starting outfit.

## Percent and flat stat auras (lane `L2-percent-stat-auras`)

Code: `Spells/Auras/PercentStatAuras.cs`, `Stats/UnitMods.cs` (`PercentFactor`, `UnitModLedger`), `PlayerStatState.cs`, `PlayerStatSystem.cs`,
`Spells/Auras/StatAuras.cs`. Tests: `tests/ArcaneCore.Game.Tests/Stats/PercentStatAurasTests.cs`. Reference: mangos zero
(`SpellAuraPeriodic.cpp` `HandleModPercentStat`, `HandleModTotalPercentStat`, `HandleAuraModIncreaseHealth`, `HandleAuraModIncreaseEnergy`,
`HandleModBaseResistance`, `HandleAuraModBaseResistancePCT`, `HandleModResistancePercent`, `HandleAuraModAttackPowerPercent`, `HandleAuraModParryPercent`;
`SpellAuras.cpp:1454-1530` block and shield block value; `UnitStatModifier.cpp` `HandleStatModifier`; `StatSystem.cpp` `UpdateMaxHealth`, `UpdateArmor`,
`UpdateAttackPowerAndDamage`).

### Implemented

| Aura | Effect |
|---|---|
| `MOD_PERCENT_STAT` (80) | BASE_PCT of the stat, players only; scales the level base, not what items and flat auras added (those sit in `PLAYER_FIELD_POSSTAT/NEGSTAT`). |
| `MOD_TOTAL_STAT_PERCENTAGE` (137) | TOTAL_PCT of the stat, every unit. Stamina of a spell with the ability attribute keeps the health ratio. Heart of the Wild now takes effect. |
| `MOD_INCREASE_HEALTH` (34) / `_PERCENT` (133) | Flat maximum health, and TOTAL_PCT of it. Last Stand (12976) also adds to current health; Bear and Dire Bear Form passives (1178, 9635) keep the health percentage. |
| `MOD_INCREASE_ENERGY` (35) / `_PERCENT` (132) | Flat / TOTAL_PCT of the maximum of the unit's own power type (misc = power type, else ignored). Removal uses the group the apply recorded. |
| `MOD_BASE_RESISTANCE` (83) | Flat per school of the mask, players only, no buff counter. |
| `MOD_BASE_RESISTANCE_PCT` (142) / `MOD_RESISTANCE_PCT` (101) | BASE_PCT (players, scales the worn items' armor or resistance) / TOTAL_PCT (every unit). Holy has no field. |
| `MOD_ATTACK_POWER_PCT` (166) / `MOD_RANGED_ATTACK_POWER_PCT` (167) | TOTAL_PCT, written as `TotalPct - 1` to the multiplier field `StatFormulas.TotalAttackPower` reads; the damage fields are recomputed. Wand users take no ranged AP. |
| `MOD_PARRY/DODGE/BLOCK_PERCENT` (47/49/51) | Players: the sum of the amounts is the aura term of `ParryPercentage` / `DodgePercentage` / `BlockPercentage`. |
| `MOD_SHIELD_BLOCKVALUE` (158) / `_PCT` (150) | Players: flat term and multiplier of `ShieldBlockValue`. |

All sixteen rows of the aura support baseline are `Handler` now.

### Design

Items, level-ups and flat auras still write the update fields as deltas, so a field holds the flat sum. The percent slots live in a
`UnitModLedger` (`PlayerStatState.Mods` for a player, a `ConditionalWeakTable` entry created by the first percent aura for any other unit) together with
`Applied`, the amount the percentages currently add to that field. A refresh recomputes `Applied` from `pre = field - Applied`:
`((base * BasePct) + (pre - base)) * TotalPct`, truncated like the reference. That gives the reference's order (flat, then percent) whichever aura
was applied first. The flat handlers (`StatAuras`, the flat health, energy and base resistance auras) refresh after their delta, and
`PlayerStatSystem.UpdateAll` refreshes stats, then (after `StatBonuses.Update`) health and powers, then (after the agility armor) armor and resistances, so an
item put on or a level gained while a percent is active is scaled too. `PercentFactor` keeps a count of active modifiers and resets the product to exactly 1 when the
last goes, so repeated apply and remove cannot drift. `UnitMods` gained `AttackPower` and `AttackPowerRanged` after the existing values.
Threading: world thread only; nothing runs per tick, an aura change allocates one record per aura (and one ledger on a non-player unit's first percent aura).

Base values: the stat base of BASE_PCT is the field minus the player's buff counters; the armor and resistance base is the worn items' value, kept in
`PlayerStatState.ItemResistance` from the item hook calls that move the field. A worn item that breaks (durability 0) is removed through the same hook
after its durability was already written, so the stat system's broken-item check guards the apply only (the reference gets the same effect by calling
`_ApplyItemMods(false)` before writing the 0: mangos PlayerDurability.cpp:230-236, azerothcore Player.cpp:4898-4902; the vmangos lines were not checked
here); otherwise the broken item's armor, resistances, block value and weapon damage would
stay counted and a repair would add them a second time. Tests `BrokenItem_UnderABasePercentAura_...` and `BreakAndRepairWithoutAnAura_...` cover
break and repair with and without an active BASE_PCT aura.

### Known gaps

- `PLAYER_FIELD_POSSTAT/NEGSTAT` and the resistance buff mods are not scaled by the percent auras (the reference does, for the client UI). UNVERIFIED how the 1.12.1 client
  draws a percent buffed stat without it; the stat value itself is correct.
- Pets get no base armor modifiers (`MOD_BASE_RESISTANCE`, `_PCT` on a pet's armor); other creatures never have them in the reference.
- The `SPELLMOD_ATTACK_POWER` caster modifier on the attack power percent auras does not exist (no such spell mod class here).
- Mana regeneration and spell power after a stat change are not recomputed (no system for them yet); parry from weapon-specific talents is not modelled.
- A druid who changes power type keeps the percent on the old power's group until the aura ends (the reference re-evaluates per power type).
- The support matrix table in `aura-engine.md` still lists these rows as Unsupported; it is regenerated at integration (`AuraSupportBaseline.cs` is the source).
