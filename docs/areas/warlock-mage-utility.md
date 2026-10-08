# Warlock, mage and utility spells (wave 4 lane "warlock-mage-utility")

Status: delivered on branch `claude/vw5-warlock-mage-utility` (base `claude/vw4-integration` 7313b9e). Each section below is one
delivered slice with its scope, limits, provenance and the file list. Nothing is copied from the reference servers; every formula
cites the vmangos source it was checked against (`D:\refs\vmangos`, GPL, read-only).

Slices the design listed and this lane did not deliver are in "Not delivered" at the end, with the reason.

## wlm-02 Spell scripts (`Game/Spells/Scripts`, `World/Spells/Utility/SpellScriptFeature.cs`)

vmangos keeps class logic in `SpellScript` objects keyed by spell id (`src/scripts/spells/spell_warlock.cpp`, `spell_mage.cpp`).
`ISpellScript` + `[SpellScript(ids...)]` + `SpellScriptRegistry` (reflection discovery, one script per spell id, a duplicate id,
a missing attribute or an empty id list throws) + `SpellScriptDispatcher` mirror it without editing `SpellSystem`:

| Hook | Where it runs | vmangos |
|---|---|---|
| `OnCheckCast` | `ISpellCastCheck`, phase `Final`, order `int.MaxValue` (after every other check, also for triggered casts) | Spell.cpp:6480-6481 |
| `OnCast` | `ISpellCastObserver.OnCast`: power and ammo are taken, targets and effects not yet run | Spell.cpp:3716-3724 |
| `OnEffectExecute` | chained DUMMY (3) and SCRIPT_EFFECT (77) handlers; runs before the previously installed handler | Spell.cpp:5254-5257 |
| `OnSuccessfulDispel` | chained DISPEL wrapper: only when the target lost at least one aura stack | scripts/spells |

SCRIPT_EFFECT has no behaviour of its own in vmangos (SpellEffects.cpp:3685-3700, :4560-4570 fall through to the database script
table, which is empty here), so the dispatcher installs a no-op for it: the mage Teleport spells and the Create Healthstone ranks
no longer log "effect 77 is not implemented". Chaining keeps any handler another feature installed before the dispatcher; a feature
that replaces DUMMY, SCRIPT_EFFECT or DISPEL after it without chaining would drop the scripts (the world feature attaches after
`SpellFeature`, alphabetically).

Limits: `OnEffectExecute` is raised for DUMMY, SCRIPT_EFFECT and the effects a script declares with `ExecuteEffects` (see wlm-08; vmangos raises it before every effect; no script of this lane
needs the others). vmangos `OnSummon` is not provided because nothing here raises it yet. When the crafting lane's cost hook
(`ISpellCostTaker`) merges, script `OnCast` must stay after it (a reagent failure must never fire a script); the integrator checks
the observer order. No script ships in this slice: the scripts of later slices register their ids with the attribute.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/SpellScriptTests.cs` (12 tests: hook order, the late-check ordering, power already
taken at `OnCast`, no unsupported-effect log, dispel only on removal, duplicate/unknown/unmarked scripts, double install, chaining
before and after).

## wlm-03 Pet and minion targets (`Game/Spells/Utility/Targets`, `World/Spells/Utility/PetTargetFeature.cs`)

Three implicit targets the built-in switch did not serve (they were logged as "implicit target N is not implemented" and hit nothing):

| Target | Meaning | vmangos |
|---|---|---|
| 5 `UNIT_CASTER_PET` | the caster's pet (`UNIT_FIELD_SUMMON`), else the creature it charms (`UNIT_FIELD_CHARM`) | Spell.cpp:2212-2222 |
| 27 `UNIT_CASTER_MASTER` | the charmer or owner of the caster (Sacrifice) | Spell.cpp:2770-2772 |
| 32 `LOCATION_UNIT_MINION_POSITION` | caster position + (effect radius, orientation + 0.25 pi); radius 0 when the effect has no radius index; location-only, the caster carries the effect (every Summon Pet spell) | Spell.cpp:2975-3022 |

`CasterPetCastCheck` (phase `Items`, before the equipment check) is the "check pet presents" block (Spell.cpp:5545-5568): no pet is
`NO_PET`, a dead pet `TARGETS_DEAD`, and a triggered cast is `DONT_REPORT`. Limits: vmangos answers `DONT_REPORT` only for a cast triggered
by an aura (`m_triggeredByAuraSpell`); the check context carries only "triggered", so every triggered cast is silent. The pet line-of-sight
check (:5567) is not made. The point of target 32 is the unclamped offset at the caster's Z (vmangos `GetFirstCollisionPosition`; the same
limit as the existing caster-relative locations 41-47). Installing twice throws (a second selector for one target id fails closed).

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/PetTargetTests.cs` (9 tests, run RED against an empty `Install` first: 7 failed).

## wlm-07 Soul Shards (`Game/Spells/Warlock/ChannelDeathItemAura.cs`, `SoulShardRules.cs`)

`SPELL_AURA_CHANNEL_DEATH_ITEM` (86) had no handler, so Drain Soul and Shadowburn (14 DB spells, 9 trainer ranks) never made a Soul Shard.
`ChannelDeathItemAura` is an `ISpellHandlerModule` (discovered, no world feature needed) implementing vmangos
`Aura::HandleChannelDeathItem` (SpellAuras.cpp:2833-2880):

* only when the holder was removed because its target died: `SpellAuraHolder.RemovedByDeath` (new internal flag, set by
  `SpellSystem.RemoveAurasOnDeath` before the remove handlers run; two shared-file edits, `SpellAuraHolder.cs` and `SpellSystem.Death.cs`,
  one line and one property each) is the vmangos `AURA_REMOVE_BY_DEATH` remove mode. Expiry, dispel and cancel make nothing;
* the caster must be a player still in the world; the effect value is the count and the effect's `ItemType` the item;
* warlock family spells make one item per warlock and target: a second channel-death-item aura of the same caster still on the victim
  postpones it (`HasAuraTypeByCaster`), so Shadowburn plus Drain Soul give one shard and two warlocks one each;
* a Soul Shard (6265) needs `Player::IsHonorOrXPTarget` (victim above the caster's gray level via `ExperienceFormulas.GrayLevel`, not a
  totem or pet; Player.cpp:19943-19957) and, for a creature victim, a tap by the caster;
* no room: `SendEquipError` is sent whenever the store is not fully ok (also for a partial fit), the part that fits is stored, nothing when
  nothing fits; the item push (`created`) goes to the caster.

Limits, recorded: (1) there is no tap list in the combat system (the threat-and-aggro lane owns it), so `ISoulShardTapSource` is a seam
(`SoulShardRules.UseTapSource`) with no shipped implementation, and without one every kill counts as tapped; the loot recipients cannot stand
in because the loot bag only exists after the death; (2) the creature template's xp multiplier of 0 and `UNIT_STATE_NO_KILL_REWARD` are not
modelled; (3) no `Spells:Warlock:SoulShards:RequireTap` switch was added: the design's switch would only have let a host skip a retail rule, and
nothing needs it until a tap source exists.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/SoulShardTests.cs` (10 tests; RED first: 6 failed, the 4 negative cases passed by design).

## wlm-16 Absorb shield spell power (`SpellAmountStage.AbsorbShield`, `SpellBonusModule`)

Absorb shields got no spell power: `SnapshotAuraAmount` skipped `SPELL_AURA_SCHOOL_ABSORB`. Now the shield amount goes through a new
`SpellAmountStage.AbsorbShield` (caster side, stored in the aura at creation; a login-restored aura is built from its saved amount in
`SpellSystem.Persistence.cs` and never re-snapshotted, so there is no double bonus). `SpellBonusModule` implements vmangos
`Aura::HandleSchoolAbsorb` (SpellAuras.cpp:5750-5810):

* Fire Ward and Frost Ward (mage family flags 3 and 8, `SpellClassMask.h`) and Shadow Ward (icon 207, category 56; the real classic-db z2815 rows 6229, 11739, 11740 and 28610 carry family 0 although vmangos's own database has 5, so family 0 and 5 are both accepted): 10 percent of
  `SpellBaseDamageBonusDone` for the spell's school (ModDamageDone for the school plus the per-aura truncated spirit part, players only);
* Power Word: Shield (priest family flag 0) in the same function: 10 percent of `SpellBaseHealingBonusDone` (the design listed only the three
  wards; retail does the same for Power Word: Shield and no other lane owns it);
* the bonus is multiplied by `CalculateLevelPenalty` (`SpellCoefficients.LevelPenalty`), added to the data amount and truncated to int
  (`rand_dither` of an integer is the integer). Ice Barrier, Mana Shield, Spellstone, Sacrifice and every other shield keep the data amount.

Shared edits: `ISpellAmountModifier.cs` (enum member), `SpellSystem.Amounts.cs` (one switch arm), `SpellBonusModule.cs` (one branch plus the two
base-bonus helpers). Existing limits of the module stay (equipped-item restricted +damage auras, talent spell mods).

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/AbsorbShieldTests.cs` (10 tests; RED first: 7 failed, 3 characterization tests pass today:
unchanged non-ward shield, module not installed, other-school shield). The absorb path itself (shield break, school mask) is exercised as acceptance.

## wlm-04 Pet power spells (`PowerDrainEffect`, `SpellSystem.PerSecondCosts.cs`, `LifeTapScript`)

* **POWER_DRAIN (8)** (`Game/Spells/Utility/PowerDrainEffect.cs`, discovered module), vmangos `EffectPowerDrain` (SpellEffects.cpp:1696-1760): Dark Pact
  and Viper Sting. The value goes through the direct damage bonus (`AmountModifier`, truncated), is capped at the target's current power, a target
  that does not use the drained power type is left alone (happiness only hits pets), and for mana the caster gains the drained amount times
  `EffectMultipleValue` (0 counts as 1), dithered; a self drain gives nothing back. Limits: the SPELLMOD_MULTIPLE_VALUE talent modifier (spell-modifier
  lane) and the SMSG_SPELLLOGEXECUTE power-drain entry are missing.
* **Per-second power cost** (`Spells/SpellSystem.PerSecondCosts.cs`, hook in `UpdateAuras`, `SpellAuraHolder.PerSecondTimer`): `ManaPerSecond` was carried
  and never charged. After the duration step of a running holder, once a second the caster pays `manaPerSecond + perLevel * level` of the spell's
  power type (health for Health Funnel); the "no target per second costs" attribute (0x800) makes only one holder pay (vmangos tests `GetCasterGuid() == target->GetTargetGuid()`, which never holds for the real Health Funnel here because `Unit.Target` is only a melee victim or a player selection, so the holder on the caster itself, aura 88, pays when the spell has one; a spell without such a holder keeps the vmangos test); a caster that
  cannot pay loses the aura and the channel and a player gets FIZZLE (vmangos SpellAuras.cpp:7296-7330). Deliberate limit: for a health cost at or below
  the amount vmangos falls into `GetPower(POWER_HEALTH)` (an unrelated update field); the evident intent, a fizzle, is implemented. The Health Funnel heal
  tick needs no exception here: the base periodic heal never damages the caster (the vmangos damage-the-caster branch for visual 163 is not ported).
* **Life Tap** (`Game/Spells/Warlock/LifeTapScript.cs`, a wlm-02 script for 1454, 1455, 1456, 11687, 11688, 11689, vmangos spell_warlock.cpp:112-159):
  the check fizzles at health at or below the rounded-up bonus amount of the first effect's simple value (base points plus base dice, vmangos `m_currentBasePoints`, Spell.cpp:77); the effect trades the rolled value (bonus,
  dithered) of health for as much mana, scaled by each Improved Life Tap aura (warlock family, icon 208: `(amount + 100) * mana / 100`), no combat log, and
  fizzles after the cast result when health is not above the value. The mana is added with an energize log of spell 31818 (vmangos casts it with custom
  points; the spell itself is not cast). Limit: the SPELLMOD_COST talent modifier belongs to the spell-modifier engine lane.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/PetPowerTests.cs` (14 tests; RED first with the effect and the script moved away and no per-second hook:
12 failed, the two "does nothing" cases passed by design). Targets 5 and 27 (wlm-03) and the script dispatcher (wlm-02) are exercised end to end.

## wlm-15 Regeneration auras (`Combat/Power/RegenModifiers.cs`, `MapCombat.Regen.cs`, `IPowerAuraSource`)

Player regeneration ignored every aura but two. It now follows vmangos `Player::UpdateManaRegen` (StatSystem.cpp:642-661) and
`RegenerateAll` / `Regenerate` / `RegenerateHealth` (Player.cpp:2269-2402); the formulas are pure functions in `RegenModifiers`:

| Aura | Effect | Examples (classic-db z2815 values, base points + 1) |
|---|---|---|
| `MOD_POWER_REGEN_PERCENT` (110), mana | multiplies the spirit regen by (amount + 100) / 100, also inside the five second window | Evocation 1500 (x16) |
| `MOD_POWER_REGEN` (85), mana | amount / 5 per second, always | Drink 42 |
| `MOD_MANA_REGEN_INTERRUPT` (134) | inside the window the spirit part counts min(100, total) percent | Evocation 100, Mage Armor 30 |
| `MOD_REGEN` (84, Food) | out of combat, health + amount * (2000 / interval) per tick | no row quoted, test shape |
| `MOD_HEALTH_REGEN_IN_COMBAT` (161) | 2 * (total / 5) per tick, also in combat (health regenerates in combat with it) | Demon Armor 7 |
| `MOD_HEALTH_REGEN_PERCENT` (88) | out of combat x (100 + amount) / 100 | Health Funnel -100 |
| `MOD_REGEN_DURING_COMBAT` (116) | health regenerates in combat, x total / 100 | none yet |
| polymorph | health regenerates in combat, a tenth of the maximum per tick | Polymorph |

`IPowerAuraSource` (the seam combat uses to read auras; implemented by `SpellSystemPowerAuras`) gained four default-bodied members
(`GetTotalAuraModifier`, `GetTotalAuraModifierByMisc`, `GetRegenAuras`, `IsPolymorphed`), so a source that implements only the old three keeps
compiling and sees no such aura. The shared edits are `MapCombat.Regen.cs` (the health gate, `RegenerateHealth`, the mana tick) and
`CombatOptions.cs` (interface and implementation). The five second rule itself is unchanged: no spirit regen inside the window unless an aura says so.

Limits: (1) `IsPolymorphed` stands in for vmangos' `GetTransForm()` (the Transform aura has no handler, so no transform is tracked): a live holder with a
Transform aura and the mage polymorph classification (mage family, first effect confuse, silence prevention) counts, whichever transform is newest;
(2) `Rate.Health` does not exist in `CombatOptions` (the health regen never had it) and is not added; (3) the heal-on-tick regen of creatures and pets is untouched.

Tests: `tests/ArcaneCore.Game.Tests/CombatMechanics/RegenAuraTests.cs` (11 tests; RED first: 9 failed, the two baseline tests proving the harness passed).

## wlm-22 Invisibility and Detect Invisibility (superseded by the stealth lane at integration)

This lane built its own aura 18 / 19 handlers and an InvisibilityVisibilityRule. The reviewed Codex stealth lane (claude/cx-stealth-detection,
docs/areas/stealth.md) implements the same vmangos code (HandleInvisibility, HandleInvisibilityDetect, CanDetectInvisibilityOf) inside the stealth registry and
`StealthVisibilityRule`. Both register auras 18 and 19, which SpellSystem.RegisterModules rejects as a duplicate, so the integration keeps the stealth lane's
implementation (InvisibilityAuras, InvisibilityTests) and drops this lane's InvisibilityVisibilityRule, InvisibilityFeature and its 12 tests. See
docs/integration/wave4-integration.md.
## wlm-08 (reduced) Warlock demons and Demonic Sacrifice (`Pets/SummonService.Demons.cs`, `Spells/Warlock/DemonicSacrificeScript.cs`)

`SPELL_EFFECT_SUMMON_PET` (56) was unregistered (docs/integration/pets.md), so Summon Imp, Voidwalker, Succubus and Felhunter did nothing.
`SummonService.InstallDemons(SpellSystem)` (a new partial of the pets lane's service; `WarlockDemonFeature` calls it at world start, and a second
claim of the effect throws) registers vmangos `Spell::EffectSummonPet` / `Unit::EffectSummonPet` (SpellEffects.cpp:3171-3327), the part that does not need
a database:

* level: the caster's; for a non-player the caster's plus `EffectMultipleValue` (at least 1);
* `UnsummonOldPetBeforeNewSummon` (Unit.cpp:5116-5145): a player's old pet (alive or dead) is dismissed first, even when the new entry then turns out unknown;
  a non-player keeps a living pet and the summon is refused, a dead pet of the same entry is replaced;
* the pet appears at the owner's close point 2 yards at pi/2 (`PET_FOLLOW_DIST`, `PET_FOLLOW_ANGLE`; the spell's own target 32 destination is not used, as in
  vmangos), facing minus the owner's orientation, with the owner's faction, the spell as `UNIT_CREATED_BY_SPELL`, defensive react state for a player owner,
  aggressive otherwise, `pet_levelstats` stats, the `petcreateinfo_spell` spells and SMSG_PET_SPELLS (the existing pet initialisation);
* the Demonic Sacrifice buffs of the owner (`SPELL_AURA_OVERRIDE_CLASS_SCRIPTS` misc 2228: Burning Wish, Fel Stamina, Touch of Shadow, Fel Energy) end when a
  new pet appears.

Demonic Sacrifice (18788, vmangos spell_warlock.cpp:19-56): the INSTAKILL on the pet is preceded by a script (`DemonicSacrificeScript`) that casts the buff of the
pet's entry on the caster: Imp 416 -> 18789, Felhunter 417 -> 18792, Voidwalker 1860 -> 18790, Succubus 1863 -> 18791; any other entry only dies. Doing that needed a small
extension of the wlm-02 dispatcher: `[SpellScript(ids, ExecuteEffects = new[] { SpellEffectName.X })]` makes the dispatcher chain the installed handler of
that effect so the script's `OnEffectExecute` runs before it (vmangos raises it before every effect). Declared effects the world does not handle are not
chained, so they stay "not implemented".

Limits, recorded: (1) no persistence: the design's `IWarlockPetStore` and the `character_pet` table (wlm-10) are not delivered, so a demon is a fresh pet of the
caster's level each time (vmangos reloads the saved demon, with its name, from `character_pet`); (2) the random demon name (`pet_name_generation`, wlm-11) is not
generated, the pet keeps its creature name; (3) the Soul Shard reagent of Summon Voidwalker/Succubus/Felhunter and every other reagent is a cross-lane
primitive owned by crafting-professions and is not charged here; (4) entry 0 (hunter Call Pet) is not served; (5) Enslave Demon (charm) and the Inferno /
Ritual of Doom summons (SUMMON_DEMON) are not built.

Tests: `tests/ArcaneCore.Game.Tests/Pets/WarlockDemonTests.cs` (13 tests; RED first with an empty `InstallDemons` and no script: 12 failed, the "without the install"
control passed) and two dispatcher tests for `ExecuteEffects` in `SpellScriptTests.cs`.

## Not delivered (and why)

| Design slice | Reason |
|---|---|
| wlm-01 reagents and tools | already built by the crafting-professions lane (`SpellInfo.Items`, `ReagentCastCheck`, `ISpellCostTaker`); not duplicated. Review note for the integrator (from the design review, not re-read here): that lane check answers `ITEM_NOT_READY` (vmangos Spell.cpp:7279); retail and mangos-classic answer `SPELL_FAILED_REAGENTS` 0x5C (wow_messages smsg_cast_result.wowm:194) |
| wlm-05 CREATE_ITEM and conjure, wlm-06 item use | owned by crafting-professions (CreateItem, `CastItem`); the lane brief marks both as cross-lane primitives. The Soul Shard code here already stores items through `PlayerInventory` directly |
| wlm-09 SUMMON_DEMON, Curse of Doom | delivered by the class-scripts lane ([class-scripts](class-scripts.md)): SUMMON_DEMON and Curse of Doom's Doomguard; the ritual object destination and Inferno's Enslave Demon remain |
| wlm-10 pet store, wlm-11 pet names | schema slices (they need the next free numbers, Characters 26 / World 29 at wave-4 integration; 21 was taken by the wave-4 modules, see [wave4-integration](../integration/wave4-integration.md)); not started, so no provider theories were written and no store was exercised on any provider. Everything delivered here is Game-layer code with no schema change |
| wlm-12 rituals and player summon, wlm-14 mage portals | both need the reagent check and one shared TRANS_DOOR dispatcher (with fishing); blocked on crafting-professions |
| wlm-13 soulstone and self resurrection | needs the item-use path (wlm-06) and the graveyards lane |
| wlm-17 ward reflect | in vmangos the reflect is only half the work (the reflected cast back at the caster) and the Frost/Fire Warding chance needs the spell-modifier engine; low payoff before that lane lands |
| wlm-18 polymorph, wlm-21 stacking, wlm-19 persistent area auras | overlap aura-engine-completeness (Transform, stacking, persistent area auras); the regen and Health Funnel pieces they feed (wlm-15, wlm-04) are delivered |
| wlm-20 Blink | needs a navmesh raycast primitive (vmangos `Map::GetWalkHitPosition`) that `IPathfinder` does not offer on this base; designing it as a seam without an implementation was not worth a half slice |
| wlm-23 charm / Enslave Demon | delivered later by the unit-control lane (`docs/areas/unit-control.md`): SPELL_AURA_MOD_CHARM with the warlock demon's pet number and bar |
| wlm-24 acceptance | the classic-db count assertions were replaced by oracle values quoted from the dump in each slice's tests (Drink, Evocation, Mage Armor, Demon Armor, Health Funnel, Life Tap, Lesser/Greater Invisibility, the Detect ranks, the demon spells); no opt-in dump test or mock-client scenario was added |

Integration notes: the shared files touched are `SpellAuraHolder.cs` (two internal members), `SpellSystem.Death.cs` (one line), `SpellSystem.Auras.cs` (the per-second cost call),
`SpellSystem.Amounts.cs` and `ISpellAmountModifier.cs` (the absorb stage), `SpellBonusModule.cs`, `MapCombat.Regen.cs`, `Combat/Power/CombatOptions.cs`. Everything else is new files.
New startup claims (a duplicate fails the world at start): spell effects 8 (POWER_DRAIN) and 56 (SUMMON_PET), aura types 18, 19 and 86, implicit targets 5, 27 and 32,
and the spell script dispatcher's chains on DUMMY, SCRIPT_EFFECT, DISPEL and INSTAKILL.
