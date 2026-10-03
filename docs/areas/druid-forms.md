# Druid shapeshift forms (vw5 lane `druid-forms`)

Branch `claude/vw5-druid-forms`, based on `claude/vw4-integration` 7313b9e. Code: `src/ArcaneCore.Game/Spells/Stances/`
(the form engine), `src/ArcaneCore.Game/Spells/Druid/` (tables, scripts), `src/ArcaneCore.Game/Spells/Scripts/`,
`src/ArcaneCore.Game/Spells/Checks/FormMountCastChecks.cs`, `src/ArcaneCore.Game/Stats/FormStatListener.cs`, and the world
features in `src/ArcaneCore.World/Spells/` and `src/ArcaneCore.World/Stats/`. Tests: `tests/ArcaneCore.Game.Tests/Druid/`,
`tests/ArcaneCore.World.Tests/Spells/`, `tests/ArcaneCore.Data.Tests/Spells/ShapeshiftFormDbcTests.cs`.

Directive: as close to vanilla 1.12.1 as possible, verified against the references. Primary reference is vmangos
(`D:\refs\vmangos`); mangos-classic differs (no model scale, a different druid attack power) and is not followed. Nothing
was copied from the references; only numbers and behaviour with a `file:line` citation in the source comment. Companion
document: `docs/areas/druid.md` (the wave-2 pure rules; its "not wired" and "not delivered" lists are superseded by this one).

## What changed for a player

Before this lane aura 36 (SPELL_AURA_MOD_SHAPESHIFT) was handled for the three warrior stances only: every druid form,
Ghost Wolf, Shadowform and rogue Stealth was an inert buff. Now:

| Form | id | what happens |
|---|---|---|
| Cat | 1 | display 892 (Alliance) or 8571 (Horde), scale 0.8, energy (max 100, starts at 0), boost 3025, attack time 1.0 s |
| Tree of Life | 2 | display 864, boost 5420 |
| Travel | 3 | display 632, scale 0.8, boost 5419 |
| Aquatic | 4 | display 2428, scale 0.8, boost 5421 |
| Bear / Dire Bear | 5 / 8 | display 2281 or 2289, rage (max 1000, the rage the druid had is kept), boosts 1178 + 21178 / 9635 + 21178, attack time 2.5 s |
| Ghost Wolf | 16 | display 4613, scale 0.8 |
| Warrior stances | 17-19 | as before (rage kept up to Tactical Mastery's amount, stance boost) |
| Shadowform | 28 | form byte only: Holy spells (StancesNot) and NOT_SHAPESHIFT spells are blocked |
| Stealth | 30 | form byte only: the 21 rogue stealth-form spells (Ambush, Garrote, Cheap Shot, Sap, Pick Pocket, Vanish, Premeditation) become castable |
| Moonkin | 31 | display 15374 / 15375, boost 24905 |

Every druid animal form casts the Shapeshift Form Effect (spell 9033: removes roots and slows) when it is applied; Ghost
Wolf, Shadowform, Stealth and the stances do not (`SpellAuras.cpp:2436-2445`).

## Delivered (slice commits, in order)

1. **A1 retail form table and flag-based predicates** (`ShapeshiftFormCatalog.Retail`, `FormQueries`). The 32 rows of the
   client `SpellShapeshiftForm.dbc` (flags1 and creatureType). `IsShapeShifted` is `form != 0 && !(flags1 & Stance)`
   (`Unit.cpp:5843-5852`), so Cat, Tree, Travel, Aquatic, Bear, Dire Bear, Ghost Wolf and Shadowform count, warrior stances,
   Stealth and Moonkin do not. Weapon-skill gain follows it (`Player.cpp:5351`): it was "form byte != 0" before, which
   would have stopped warriors and rogues from gaining weapon skill the moment stances and stealth became real. Player
   creature type is a Beast in the animal forms (`Unit.cpp:7722-7735`). `CombatEnvironment.ShapeshiftForms` links the table
   (DBC or built-in) for combat code. `Combat:RequireShapeshiftFormDbc` (default false) refuses startup without
   `Combat:ShapeshiftFormDbcPath`.
2. **A2 form-bound passive gate** (`PassiveFormCastCheck`, `PassiveFormGateFeature`). Learned and login-time passives bound
   to a form by `Stances` (Feline Swiftness 17002, Sharpened Claws ...) apply only in that form
   (`Player::IsNeedCastPassiveLikeSpellAtLearn`, `Player.cpp:3748-3763`). Feline Swiftness used to give +15 percent run
   speed in humanoid form.
3. **A3 script effect registry and spell 9033** (`ScriptEffectModule`, `ScriptEffectRegistry`, `ShapeshiftFormEffectModule`).
   One module owns effect 77 and dispatches by spell id; other areas add an entry (`ScriptEffectRegistry.For(system).Add`)
   instead of registering the effect again (a second owner makes startup fail, by design). 9033 removes roots with a
   mechanic and slows that are neither crowd control nor a daze (`SpellEffects.cpp:4442-4480`, `Unit.cpp:543-556`).
4. **A4 the form engine** (`ShapeshiftService`, `TransformScale`, `FormPowerRules`, `IFormChangeListener`). Follows
   `HandleAuraModShapeshift` (`SpellAuras.cpp:2420-2625`), `HandleShapeshiftBoosts` (`:5433-5597`) and the power part of
   `InitDataForForm` (`Player.cpp:18271-18312`): 9033, display and a transform-scale ledger (the native scale is restored
   exactly, `Unit.cpp:10967-10991`), the power switch, Furor (the icon-238 dummy aura holds the chance; 17099 in Cat, 17057 in
   the bears), the form byte, the linked boosts, every known passive that needs the form, Leader of the Pack (17007 known and
   24932 bound to the form), Heart of the Wild (A7). Leaving a form returns a druid to mana with rage 0, removes the boosts and
   everything bound to the form (`IsRemovedOnShapeLost`) and interrupts shape-bound casts. A form change raises
   `IFormChangeListener.OnFormChanged` so the stat area never needs a spell-lane edit.
5. **A5 Ghost Wolf, Shadowform and Stealth** set their form through the same handler (rogue energy and priest/shaman mana
   are untouched).
6. **A6 feral stat wiring** (`PlayerStatSystem.OnFormChanged`, `FormStatListener`, `FormStatFeature`). Attack power with the
   cat agility term and Predatory Strikes (`StatSystem.cpp:194-296`), ranged attack power 0, the level based weapon-less
   damage range (`StatSystem.cpp:354-445`), no weapon use in Cat/Bear/Dire Bear for any hand (`Unit.h:963-978`, both
   `CanUseEquippedWeapon` copies), attack times 1.0 / 2.5 s, the weapon delays restored when the form ends
   (`Player::SetRegularAttackTime`, `Player.cpp:5158-5172`), recomputation when Predatory Strikes (icon 1563, a Dummy
   aura; `SpellAuras.cpp:2164-2171`) comes or goes.
7. **A7 custom base points and druid scripts** (`SpellSystem.CastCustomSpell`, `RegisterPeriodicTriggerScript`,
   `DruidScriptsModule`). `CastCustomSpell` follows vmangos `CastCustomSpell` (`SpellCaster.cpp:2279-2330`): an explicit base
   point replaces the effect's own (`CalculateSpellEffectValue`, `SpellCaster.cpp:1147-1215`; a die is still rolled), applied
   before the spell modifiers, for the synchronous cast only. Scripts: Frenzied Regeneration (a tick spends up to 100 stored
   rage and casts the heal 22845 with `rage * amount / 10`, `SpellAuras.cpp:1417-1433`), Enrage (aura 25503 with -27, or -16
   in Dire Bear, `spell_druid.cpp:75-100`), Heart of the Wild (24900 / 24899 cast with the talent's amount, `SpellAuras.cpp:5505-5530`).
8. **A8 Ferocious Bite and Rip** (`DruidFinisherScripts`, `DruidScriptFeature`). Ferocious Bite effect 0 gets
   `AP * combo * 0.03` and the left-over energy times the effect's damage multiplier, a hit spends the energy
   (`spell_druid.cpp:23-72`); the Rip tick amount gets `AP * min(combo, 4) / 100` once, when the aura is created
   (`SpellAuras.cpp:4341-4363`, `:4420-4438`). Both read the combo points before the finisher spends them.
9. **A9 login restore.** A saved form is restored by `SpellSystem.RestoreAuras`, which raises `IsRestoringAuras`; the form
   handler then takes the form's power type and maximum but leaves the loaded powers alone and rolls no Furor. vmangos
   restores the saved health and powers after the auras are loaded (`Player.cpp:15057-15070`), so the end result is the same;
   without it a cat druid would lose its energy at every login.
10. **A10 mount and shapeshift gates** (`FormMountCastChecks.cs`, `FormMountCheckFeature`). A mount spell fails NOT_SHAPESHIFT
    in a disallowed form (`Spell.cpp:6370-6391`); a non-passive, non-triggered player cast while mounted dismounts first unless
    ALLOW_WHILE_MOUNTED (`Spell.cpp:5680-5692`); an aura spell that shapeshifting or mounting cancels is refused on a shifted
    or mounted target with BAD_TARGETS (`Spell.cpp:5446-5451`).
11. **A11 this document and the guards** (`FormRegistrationGuardTests`): aura 36 has exactly one owner, effect 77 exactly one
    module, every stance bit of the player spell data has a form row and a handler.

## Data provenance (read this before trusting a number)

The 32 `SpellShapeshiftForm` rows, the class power types (Warrior 1, Rogue 3, others 0; `FormPowerRules.ClassPowerType`) and
the race creature type (7, humanoid, for all nine races) were read from the developer's own build-5875 client
(`Data\dbc.MPQ`, `DBFilesClient\*.dbc`), not from the GPL references and not downloaded. Only the meaningful columns are in
the repository. `ShapeshiftFormDbcTests.Retail_MatchesTheDevelopersClientDbc_RowForRow` checks the built-in table against a real
file when `ARCANECORE_TEST_DBC_DIR` points at a directory with `SpellShapeshiftForm.dbc`; without it the test is reported as
Skipped (never a silent pass). Spell data in the tests are classic-db 1.12.1 `spell_template` values.

Row facts: flags1 is 0x7 (Stance, NotToggleable, PersistOnDeath) for the stances 17-19, 0x8 (CanInteractNpc) for Shadowform
28, 0x1 for Stealth 30 and for the Moonkin form 31 (the client row is named zzOLDStealth 2) and for the unused row 32, and 0
for every other row, the druid forms included. creatureType is 1 (beast) for 1, 3, 4, 5, 8, 14, 16, -1 for 17 and 28.
vmangos' `SHAPESHIFT_FLAG_DONT_USE_WEAPON` (0x10) and `AGILITY_ATTACK_BONUS` (0x20) are in no 1.12.1 row, which is why the
weapon-less forms are decided by the hard-coded `IsAttackSpeedOverridenForm` (1, 5, 8; `SharedDefines.h:1456-1466`).

## Configuration

| Key | Default | Effect |
|---|---|---|
| `Combat:ShapeshiftFormDbcPath` | empty | client `SpellShapeshiftForm.dbc`; wins over the built-in table |
| `Combat:RequireShapeshiftFormDbc` | false | refuse startup when no DBC path is set |
| `Combat:StanceShiftKeepsSelfBuffs` | false | existing option; now explicitly warrior-stance only (a druid form always takes its own buffs along) |
| `Forms:ResetFistAttackTimeOnFormLoss` | true | a hand without a weapon gets the 2.0 s base attack time when Cat or Bear ends. **false is vmangos literal**: `SetRegularAttackTime` only rewrites hands that hold a weapon, so an unarmed druid keeps 1.0 / 2.5 s after the form. Default is the plausible retail behaviour, not the vmangos quirk (open question 3) |

## Deliberate differences from vmangos

- **The previous form is removed before the new one is applied.** In vmangos the aura stacking rules remove it when the new
  spell is added, before the handler runs; the handler's own "remove other shapeshift" call then finds nothing. In this
  spell system aura stacking is per spell id, so the handler removes the old form first, which gives the same order
  (display native, mana, rage 0, then the new form). Without this the old form's removal would undo the new form's display.
- The `Combat:StanceShiftKeepsSelfBuffs` option governs warrior stances only.
- **Login restore** (A9) skips the power reset and Furor roll instead of overwriting them afterwards; same end state.
- Custom values (A7) apply only to instant casts; vmangos allows a cast time (every script that uses it is instant).
- Ghost Wolf's `ShapeshiftService` entry carries no boost spell (none exists in vmangos either).

## Limits and not delivered (each needs another lane's primitive, data, or a decision)

- **Aura effects of the form passives**: armor percent (bear, Moonkin, Enrage's 25503), bear health (21178), threat (Cat 3025,
  Bear), stat percent (Heart of the Wild's 24899 / 24900 effects), crit percent (Leader of the Pack, Sharpened Claws), Mod
  Skill (Tree). The handlers `ModBaseResistancePct`, `ModIncreaseHealth`, `ModThreat`, `ModTotalStatPercentage`,
  `ModCritPercent` and `ModSkill` are the aura-engine-completeness and threat lanes' job; the holders and amounts exist and are
  asserted, their numeric effect is not. **Threat in forms (and Cower) is therefore not modelled.**
- Moonkin Form (24858): no 1.12.1 talent, trainer or LearnSpell row teaches it (the survey of `Talent.dbc` found Hurricane at
  Balance tier 6). The engine supports it fully (GM `.learn`, tests cover the generic path); it is unobtainable in normal play.
- Rebirth (resurrection requests and the reagent: graveyards-resurrection lane), Feral Charge (no Charge effect), Natural
  Shapeshifter, Improved Enrage, Blood Frenzy / Primal Fury procs and Brutal Impact (spell-modifier-engine and proc engine), Innervate
  and Reflection mana regeneration (aura-engine-completeness; slice S9 of the design was dropped from this lane), battleground
  shapeshift removal (battlegrounds lane), item-use interlock and equip-spell recast on a form change (no CMSG_USE_ITEM handler
  and no equip-spell application on this base), the weapon-dependent crit refresh of `HandleShapeshiftBoosts` (Elemental
  Sharpening Stone in cat), polymorph-over-form display priority (needs a Transform handler; `HasTransform` is already honoured),
  taxi spell branch (no SendTaxi effect), creature bear and cat displays (CreatureDisplayInfo.dbc), the display half of
  `IsInDisallowedMountForm` and the `NOT_ON_TAXI` branch of the dismount-on-cast rule (no flight state).
- Druid trainer spells still blocked by other primitives: Thorns (damage shield), Soothe Animal, Challenging Roar (taunt),
  Nature's Grasp (proc), Hurricane (persistent area aura), Barkskin, Insect Swarm, Prowl openers (ACTION_CANCELS removal).
- Forms 14 and 15 (creature bear and cat) and 32 (Spirit of Redemption: display 16031, boosts 27792 / 27795, 39 spells carry its\n  Stances bit) have rows in the form table but no handler: no player spell reaches 14 and 15, and Spirit of Redemption needs the\n  lethal-damage trigger of the priest talent (not on this base). An aura with such a form leaves the form byte alone and is\n  reported once.\n- Mana regeneration while shapeshifted is unconditional (vmangos `Regenerate(POWER_MANA)`); not verified against retail.
- Persistence: forms ride the existing permanent `character_aura` rows; no schema or store change. Nothing in this lane touches
  the Characters or World schema, so no MariaDB/PostgreSQL provider test applies; the login restore is exercised through
  `SpellSystem.RestoreAuras` in memory only (no world-level login test, no database).

## Cross-lane behaviour changes the integrator should know

- **Rogue Stealth now sets form 30.** Tests of the rogue lane that assumed no form byte pass on this branch (the full suite is
  green), but `docs/areas/rogue.md` scenarios should be re-read: the stealth-form spells are really castable now.
- **Shadowform and Ghost Wolf now block normal casts** (4,956 spells carry NOT_SHAPESHIFT, the priest Holy spells carry
  StancesNot bit 28), as in retail.
- **Casting while mounted dismounts** (A10): any lane that casts a non-passive spell from a mounted player without
  ALLOW_WHILE_MOUNTED now dismounts it.
- `PlayerCombatSkills.CanUseEquippedWeapon` and `PlayerStatSystem.CanUseEquippedWeapon` are false in Cat/Bear/Dire Bear for every
  hand; a ranged-combat lane edit of either copy must keep that predicate.
- `ScriptEffectModule` owns effect 77, `ShapeshiftService` owns aura 36 and `SpellSystem.RegisterPeriodicTriggerScript` owns the
  per-spell periodic trigger scripts: aura-engine-completeness, quests-advanced and warlock-mage-utility must add to these, not
  register the same aura or effect again.
- Features attach in full-name order: `ArcaneCore.World.Combat.ComboFeature` before `ArcaneCore.World.Spells.DruidScriptFeature`
  (the latter throws a clear error otherwise); `StanceFeature.AddFormChangeListener` works in either order.

## Open questions for the developer

1. Model scale 0.80 for Cat, Travel, Aquatic and Ghost Wolf is the vmangos value (mangos-classic does not scale). One look at a
   real client (set the form, read the update fields, compare the model size of a Tauren cat) would settle which is retail.
2. Embedding the 32 form rows as `ShapeshiftFormCatalog.Retail` (read from the developer's client) is what makes rogue stealth and
   every druid form work out of the box. The alternative is `Combat:RequireShapeshiftFormDbc=true` with the DBC path set.
3. `Forms:ResetFistAttackTimeOnFormLoss` defaults to the plausible retail behaviour; confirm or flip.
4. Furor can in principle roll at login in vmangos (the apply path runs for restored auras) but the saved power overwrites it, so
   there is no option for it.
5. Bear Form (5487) and Aquatic Form (1066) are quest rewards, not trainer spells; the form engine is the prerequisite for
   testing those quests.

## Citations

vmangos: `SpellAuras.cpp:2317-2411` (display), `:2420-2625` (HandleAuraModShapeshift), `:5433-5597` (HandleShapeshiftBoosts),
`:1417-1433` (Frenzied Regeneration), `:2164-2171` (Predatory Strikes), `:4341-4363` and `:4420-4438` (Rip);
`Player.cpp:18271-18312` (InitDataForForm), `:5158-5172` (SetRegularAttackTime), `:3748-3763` (passive gate), `:5345-5352` (weapon
skill gain), `:15057-15070` (restore saved powers after auras); `Unit.cpp:5843-5880`, `:7722-7735`, `:10967-10991`, `:543-556`;
`Unit.h:963-978`; `StatSystem.cpp:194-296`, `:354-445`; `SpellCaster.cpp:116-135`, `:1147-1215`, `:2279-2330`;
`SpellEntry.cpp:1032-1074`; `SpellEntry.h:1141-1150`; `Spell.cpp:5446-5451`, `:5680-5692`, `:6370-6391`;
`SpellEffects.cpp:4442-4480`; `scripts/spells/spell_druid.cpp:20-100`; `SharedDefines.h:1418-1476`. Client files: see
"Data provenance". classic-db: spell ids 768, 783, 1066, 5487, 9634, 24858, 9033, 3025, 1178, 21178, 9635, 5419, 5420, 5421, 24905,
17002, 16972, 17007, 24932, 17057, 17099, 5229, 25503, 22842, 22895, 22896, 22845, 17003, 24899, 24900, 22568-31018, 1079-9896.
