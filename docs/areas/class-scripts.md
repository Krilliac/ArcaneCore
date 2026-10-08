# Class scripts

Status: wave 2, lane `class-scripts` (branch `claude/w2-class-scripts`, built on the proc-engine tip `988c27a1`). Reference: D:\refs\vmangos
(primary; scripts/spells/*.cpp, Spells/SpellEffects.cpp, Spells/SpellAuras.cpp, UnitAuraProcHandler.cpp, Objects/DynamicObject.cpp), mangos-classic
where the build 5875 data needs it (Holy Light), the build 5875 `spell_template` rows of the world database for ids, families, flags and effects.
Every formula cites its vmangos file and line in the code.

The scripts use the existing seams: `ISpellScript` (`SpellScriptDispatcher`, per spell id, for DUMMY / SCRIPT_EFFECT / declared effects and cast
checks), the proc engine's `RegisterProcScript` (docs/areas/procs.md), value modifiers (`ISpellValueModifier`), cast observers and handler modules
(`ISpellHandlerModule`, discovered, so every spell system, the unit-test ones included, has them). Two seams are new: the periodic damage script
(`IPeriodicDamageScript`) and persistent area auras.

## Paladin

| Piece | What it does | Code | vmangos |
|---|---|---|---|
| Seal bookkeeping | A seal on a unit sets AURA_STATE_JUDGEMENT (Judgement's CasterAuraState); the last seal leaving clears it. | `Paladin/PaladinAuraRules.cs` | SpellAuras.cpp:6815-6893 |
| Spell specific stacking | Seal: one per unit from any caster. Blessing, paladin aura, judgement: one per unit per caster; one rank of a chain per unit; a weaker rank never replaces a stronger one (and then nothing it would have replaced goes). | `Paladin/PaladinAuraRules.cs`, `PaladinSpells.cs` | Unit.cpp:3355-3560, SpellEntry.cpp:100-122, :177-195 |
| Seal of Righteousness | The dummy aura's melee proc: weapon-speed scaled damage between `amount/87` (1.5 s) and `amount/25` (4.0 s), Improved SoR percent mods on the base, the seal's spell bonuses, the rank's damage spell (25742 ... 25713) dithered, and one more weapon-enchant proc. | `Paladin/PaladinProcScripts.cs` | UnitAuraProcHandler.cpp:979-1053 |
| Judgement (20271) | Cancels the caster's seal and casts the judgement its third effect names (simple value). | `Paladin/PaladinSpellScripts.cs` | SpellEffects.cpp:4502-4529 |
| Judgement of Light / Wisdom | The judged unit's PROC_TRIGGER_SPELL debuff: whoever strikes it casts the rank's heal (20267 ...) or mana (20268 ...) on itself. | `PaladinProcScripts.cs` | UnitAuraProcHandler.cpp:1428-1466 |
| Judgement of Command | The dummy casts the damage its base points name; the damage is halved unless the target is stunned and takes the spell bonuses. | `PaladinSpellScripts.cs` | spell_paladin.cpp:37-75 |
| Holy Shock | Heal on a friend, damage on anything else; a hostile target must be in front (1.4 yd needs no facing). | `PaladinSpellScripts.cs` | spell_paladin.cpp:77-125 |
| Hammer of Wrath | The ranged spell's damage takes the spell bonuses. | `PaladinSpellScripts.cs` | spell_paladin.cpp:19-35 |
| Holy Light / Flash of Light | SCRIPT_EFFECT in the build 5875 data: the rank's value heals through 19968 / 19993 (Flash of Light dithers and adds the libram bonuses). | `PaladinSpellScripts.cs` | mangos-classic SpellEffects.cpp:4151-4171, vmangos :4485-4500 |
| Blessing of Light | The blessing's dummy amounts join the flat healing-taken bonus of Holy Light (effect 0) and Flash of Light (effect 1), through the heal's coefficient. | `Paladin/BlessingOfLightRules.cs` (called from `SpellBonusModule.Taken`) | Unit.cpp:5364-5378 |
| Forbearance | A bubble (Divine Shield, Divine Protection, Blessing of Protection) puts 25771 on its target after it hit; a positive spell on a target immune to it fails TARGET_AURASTATE, so Forbearance (mechanic immunity to INVULNERABILITY) refuses the next bubble. | `PaladinSpellScripts.cs` (`ForbearanceObserver`, `PositiveSpellImmunityCheck`) | spell_paladin.cpp:210-225, Spell.cpp:5634-5636 |
| Paladin resistance auras | SPELL_AURA_MOD_RESISTANCE_EXCLUSIVE: only the strongest bonus and malus per school count. | `Auras/ResistanceExclusiveAuras.cs` | SpellAuras.cpp:4503-4549 |
| Consecration | Ticks recalculated from the base points with the caster's current spell power. | `Paladin/ConsecrationScript.cs` | SpellAuras.cpp:5872-5874 |

The other seals (Command, Justice, Light, Wisdom, the Crusader), the blessings (Might, Wisdom, Kings, Salvation, Freedom, Sanctuary) and the auras
(Devotion, Retribution, Concentration, Sanctity) are data-driven auras the aura engine and the proc engine already run; Seal of Command's PPM, Blessing of
Sanctuary's block-only proc and the 50% judgement procs need their `spell_proc_event` rows (procs.md).

## Shaman

| Piece | What it does | Code | vmangos |
|---|---|---|---|
| Flametongue Weapon proc (10 ids) | `(value + 3.85 * fire spell damage) * 0.01 * weapon speed` of the item that procced, dithered, dealt by Flametongue Attack 10444. | `Shaman/ShamanWeaponScripts.cs` | spell_shaman.cpp:19-44 |
| Rockbiter Weapon proc (6 ids) | `value * main-hand attack time / 1000` threat where the shaman is already on the list. | `ShamanWeaponScripts.cs` | SpellEffects.cpp:4544-4561 |

The imbues themselves are temporary weapon enchantments (SPELL_EFFECT_ENCHANT_ITEM_TEMPORARY, crafting lane) whose COMBAT_SPELL enchantment effect is
procced by `SpellSystem.HandleItemCombatProc` (Rockbiter Weapon's TOTEM effect adds weapon damage in `PlayerEnchantments`); Windfury Weapon and Frostbrand
Weapon are data-driven there. The enchantment rows come from the client's SpellItemEnchantment.dbc and the PPM rows from `spell_proc_item_enchant`.

Totems: the totem is rooted (`MovementFlags.Root` at summon and on every active update, `TotemSystem.cs:277`, `TotemSystem.Active.cs:21`;
`ActiveTotemTests` asserts it) and Searing Totem casts: the build 5875 `totem_spell` maps Searing Totem 2523 to 22048 (2.2 s cast, 20 yd, fire), a
cast-time spell, so `TotemSystem.Active` drives it (ActiveTotemTests, TotemLoopbackTests).

## Rogue

Rupture adds `attack power * min(combo points, 3) / 100` and Garrote `attack power * 0.03` to the tick amount, once, when the aura is created
(SpellAuras.cpp:4366-4384), through `Rogue/RogueBleedScripts.cs` (`RogueScriptFeature` installs it after the combo point feature).

## Warlock

Curse of Doom (603): a tick that kills casts Curse of Doom Effect (18662) on the caster one time in ten (SpellAuras.cpp:5921-5924); players and units
a player owns are not targets (TARGET_IS_PLAYER / BAD_TARGETS, Spell.cpp:7584-7592). SPELL_EFFECT_SUMMON_DEMON (112, `Pets/SummonService.SummonDemon.cs`,
SpellEffects.cpp:5796-5819) summons the Doomguard at the destination with the caster's level for the spell's duration. Conflagrate needs and consumes the
caster's Immolate (spell_warlock.cpp:58-110).

## Common DUMMY / SCRIPT_EFFECT scripts (`Spells/ClassScripts`)

Last Stand (12976 for 30% health), Execute (dummy: base + rage * multiplier through 20647; damage: rage to 0), Bloodrage (combat on its rage),
Deep Wounds (weapon average share over 12721), Bloodthirst (value percent of attack power), troll Berserking (haste from missing health through 26635,
AURA_STATE_BERSERKING), Preparation, Cold Snap and Readiness (cooldown resets). vmangos SpellEffects.cpp:742-850, :1440-1458 and
scripts/spells/spell_warrior.cpp, spell_mage.cpp, spell_hunter.cpp.

## Persistent area auras (`Spells/PersistentAreaAuras`)

SPELL_EFFECT_PERSISTENT_AREA_AURA had no handler (no dynamic object entity existed); Consecration needed it.

- The effect runs once per cast on the ground (Spell.cpp:3971-3976): a `DynamicObject` (TYPEID_DYNAMICOBJECT, `DYNAMICOBJECT_*` fields as
  DynamicObject::Create, :76-135) at the destination (a unit target's position, else the caster's), with the effect radius through SPELLMOD_RADIUS and
  the cast's duration, is added to the map, so clients get its create block and draw the ground visual. The units the effect's selector lists appear in
  SMSG_SPELL_GO but never run the effect (Spell.cpp:1129-1137).
- Each spell update (DynamicObject::Update and DynamicObjectUpdater::VisitHelper): the object goes when its caster left the world or the map, and when
  its time ran out unless it is the running channel's object; units in its radius get the spell's aura (alive, not a GM, a valid attack target for a
  negative effect or a helpable one for a positive effect, LOS from the object for a player caster, at most once per 2 s, the patch 1.7 rule that a
  non-PvP-flagged player's negative area spell does not hit players outside a duel, combat unless NO_THREAT-like attributes, immunity). An existing
  holder of the spell from the caster gets the object's duration; otherwise a holder with the spell's duration (a channel's remaining time).
- The aura leaves a unit outside the radius or when its object is gone (PersistentAreaAura::Update, SpellAuras.cpp:892-919), unless the spell has
  SPELL_ATTR_EX3_NO_AVOIDANCE; a channel's end removes its objects (Spell.cpp:3595, 4794) and a channel whose first effect is the ground aura makes the
  object its channel object (Spell.cpp:4830-4833).

## Seams

- `SpellSystem.RegisterPeriodicDamageScript(spellId, IPeriodicDamageScript)`: `CalculateTick` replaces the tick's caster-side amount before the ramp and
  the target side (vmangos OnPeriodicCalculateAmount and the hard-coded spells of Aura::PeriodicTick); `AfterTick` runs after the damage.
- `SpellSystem.DynamicObjects`, `GetDynamicObjects(caster)`, `FindDynamicObject(holder)`, `RemoveDynamicObjects(caster, spellId)`.
- `SpellSystem.CastItemCombatSpell` (internal): a script's extra weapon proc.

## Deviations and limits

- Stacking: vmangos refuses a weaker holder before adding it; here it is added and removed in the same call. `spell_group` stack rules (Greater
  Blessing versus Blessing) are not modelled; a rank chain is the spell family plus the spell name.
- Judgement of Light's heal script (the Tier 3 bonus through `m_triggeredByAuraBasePoints`), Illumination, Seal of the Crusader's damage reduction and
  Blessing of Sacrifice (SPLIT_DAMAGE_PCT, aura engine) are not part of this lane. Hammer of Wrath crits with the spell crit chance, not the melee one.
- Bloodthirst does not add MOD_MELEE_ATTACK_POWER_VERSUS; Execute reads the rage at the dummy effect (after the cost, as vmangos' OnCast does).
- The Doomguard is a wild summon (killed out of combat when its time is up, TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN) where vmangos uses
  TEMPSUMMON_TIMED_COMBAT_OR_DEAD_DESPAWN; a summoning ritual destination and Inferno's Enslave Demon are not modelled.
- Ground objects never move; the 2 s refresh window starts at the first visit; an existing holder that lacks the ground effect's aura is not given it.
- Real data: Spell.dbc / `spell_template` (every id above), SpellItemEnchantment.dbc for the imbues, `spell_proc_event` and
  `spell_proc_item_enchant` rows for PPM procs, and creature 11859 (Doomguard) in `creature_template`. The tests use synthetic rows with the real ids,
  families, flags and effect kinds.

## Tests

- `tests/ArcaneCore.Game.Tests/ClassSpells`: `PaladinScriptTests` (11), `PaladinHealingAndAuraTests` (4), `ShamanWeaponScriptTests` (5),
  `ClassDummyScriptTests` (13), `CurseOfDoomTests` (5); `Rogue/RogueBleedScriptTests` (7); `Spells/PersistentAreaAuraTests` (11).
- `tests/ArcaneCore.World.Tests/Spells/ClassScriptWiringTests` (the production world spell system carries the scripts) and the playerbot scenarios
  `Playerbots/Scenarios/ClassScriptScenarioTests` (two bots duel: Seal of Righteousness and Judgement; Consecration's ground object reaches the
  opponent's client and ticks on it), with `ScenarioClassDecoders` (SMSG_SPELLNONMELEEDAMAGELOG, SMSG_PERIODICAURALOG).
- RED evidence: `D:/ArcaneCore-lanes/_logs/w2-class-scripts/red-*.log` (each run with the lane's implementation switched off).
