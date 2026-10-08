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
| Seal bookkeeping | A seal on a unit sets AURA_STATE_JUDGEMENT (Judgement's CasterAuraState); the last seal leaving clears it. Both go through `SpellSystem.ModifyAuraState`, so the world's `AuraStateService` adds vmangos' side effects (an aura that needs the state goes with it). | `Paladin/PaladinAuraRules.cs` | SpellAuras.cpp:6815-6893, Unit.cpp:4682-4745 |
| Spell specific stacking | Seal: one per unit from any caster. Blessing, paladin aura, judgement: one per unit per caster; one rank of a chain per unit; a weaker rank never replaces a stronger one: it is refused before it is added (`SpellSystem.HolderAddRefusals`), and nothing it would have replaced goes. | `Paladin/PaladinAuraRules.cs`, `PaladinSpells.cs` | Unit.cpp:3216-3224, :3355-3560, SpellEntry.cpp:100-122, :177-195 |
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

## Talent scripts (lane `tb-t3-talent-class-scripts-pets`)

Talents that vmangos implements as per-spell class scripts. Each is a new file on an existing seam; each test was RED-proved by switching its script off
(`D:/ArcaneCore-lanes/_logs/tb-t3-talent-class-scripts-pets/red-*.log`).

| Talent | What it does | Code | vmangos |
|---|---|---|---|
| Ignite (11119, 11120, 12846, 12847, 12848) | Proc script on the talent's DUMMY aura (a fire crit, spell_proc_event school 4 / procEx CRITICAL_HIT): 4/8/12/16/20% of the crit's original amount is the tick damage of 12654 (PERIODIC_DAMAGE every 2 s for 4 s) cast by the mage on the victim. A live Ignite (anyone's) that still has ticks to deal gains the share and a stack (at 5 stacks only the duration refreshes), and its ticks start over, so the accumulated tick rolls into a fresh 4 s; one whose ticks are all dealt is replaced. | `Mage/IgniteScript.cs` | spell_mage.cpp:50-130 |
| Combustion (11129, 28682) | Proc script on the invisible DUMMY aura (3 charges, fire spells): every fire hit adds a stack of the +10% crit buff 28682; only crits spend charges; the last crit removes the buff and the last charge. No buff (dispelled) ends the proc aura; cancelling the buff (AURA_REMOVE_BY_CANCEL) removes the proc aura. | `Mage/CombustionScript.cs` | spell_mage.cpp:144-205 |
| Swiftmend (18562) | Cast check: the target needs a druid PERIODIC_HEAL of Rejuvenation (0x10) or Regrowth (0x40), else TARGET_AURASTATE. Effect: the one with the shortest remaining duration (the first on a tie) is consumed and 4 (Rejuvenation) or 6 (Regrowth) times its tick is added to the heal before the heal bonus (`SwiftmendHealModifier`). | `Druid/SwiftmendScript.cs` | spell_druid.cpp:101-160 |
| Mana Tide (16191) | Periodic trigger script: a PERIODIC_TRIGGER_SPELL Mana Tide aura makes its target cast the trigger on itself with the aura's amount (dithered). The build 5875 row is APPLY_AREA_AURA_PARTY of PERIODIC_ENERGIZE (170 mana every 3 s, 20 yd) and never reaches the script; it restores mana through the area aura and the energize tick (pinned by a test). | `Shaman/ManaTideScript.cs` | spell_shaman.cpp:49-67, mangos-classic SpellAuras.cpp:1209-1213 |
| Reckoning (20178) | ADD_EXTRA_ATTACKS script: one more extra attack on top of those pending, up to 4 (the plain effect only queues on a unit with none pending). The talent aura 20177 procs it on crits taken; the proc engine already lets 20178 proc while extra attacks are pending. | `Paladin/ReckoningScript.cs` | spell_paladin.cpp:178-206 |
| Counterattack (19306, 20909, 20910) | Cast check: the target must be the attacker whose attack the hunter parried (the parry's combo-point marker, which moves with the reactive window's target); the CasterAuraState HUNTER_PARRY of the data gates the window. | `Hunter/CounterattackScript.cs` | spell_hunter.cpp:121-136, Unit.cpp ProcSkillsAndReactives |

## Owner-to-pet talent auras (`Pets/PetAuras`)

vmangos `spell_pet_auras` (SpellMgr::LoadSpellPetAuras, SpellMgr.cpp:2222): an owner spell whose DUMMY aura or DUMMY effect gives the owner's permanent pet an
aura chosen by the pet's creature entry. `PetAuraTable` holds the 24 vanilla rows as code (the classic-db z2815 table, also azerothcore's vanilla ids):
Soul Link 19028 -> 25228; Spirit Bond 19578 / 20895 -> 19579 / 24529; Master Demonologist 23785, 23822-23825 -> imp 416 / felhunter 417 / voidwalker 1860 /
succubus 1863 variants; Stalker's Ally 28757 -> 28758. No table or importer.

- `PetAuraService` keeps the owner's set (vmangos `m_petAuras`) per spell system. The owner's DUMMY aura applied / removed calls AddPetAura / RemovePetAura
  (SpellAuras.cpp:2201-2208) through the per-spell DUMMY aura dispatch; Soul Link's DUMMY effect calls AddPetAura (SpellEffects.cpp:1497-1502,
  `PetAuraDummyEffectScript`); unlearning a table spell drops it (Player.cpp:3850-3852, a learn observer). AddPetAura casts the pet's aura on the current pet
  at once; RemovePetAura removes it from the pet.
- A pet arrives (Pet::CastPetAuras, Pet.cpp:2302-2330): a new summon (`PetInitializer.InitCreateSpells`, Pet.cpp:2101, `current = false`) first ends the
  owner's spells whose DUMMY targets TARGET_UNIT_CASTER_PET (Soul Link: a new demon ends it) and gets the others; a loaded hunter pet gets them all (current for
  the login / teleport restore and the revive, not for Call Pet). Only a permanent pet takes them (Pet::IsPermanentPetFor: a hunter's pet, a warlock's demon).
- SPELL_EFFECT_APPLY_AREA_AURA_PET (119), which every pet aura is, is now handled: the pet carries the source and its owner within the effect radius gets the
  copy (AreaAura::Update, SpellAuras.cpp:690-706); the copy goes with the source.
- Talent hooks (`TalentPetHooks`, World `Pets/TalentPetFeature.cs`, attached on the world thread because the talent feature attaches after the pets
  namespace): `TalentService.TalentsReset` removes the pet (vmangos Player.cpp:4144-4146 `RemovePet(PET_SAVE_REAGENTS)`; a hunter's pet is saved out of slot
  first, as Dismiss Pet saves it); `TalentLearned` re-casts the owner's talent auras the pet is missing (mangos-classic HandleLearnTalentOpcode).

## Shaman

| Piece | What it does | Code | vmangos |
|---|---|---|---|
| Flametongue Weapon proc (10 ids) | `(value + 3.85 * fire spell damage) * 0.01 * weapon speed` of the item that procced, dithered, dealt by Flametongue Attack 10444 cast with that item as its cast item. | `Shaman/ShamanWeaponScripts.cs` | spell_shaman.cpp:19-44 |
| Rockbiter Weapon proc (6 ids) | `value * main-hand attack time / 1000` threat (whole numbers, added raw with no school: no SPELLMOD_THREAT and no MOD_THREAT multiplier, ThreatManager.h:192 and Unit.cpp:7414-7415) where the shaman is already on the list. | `ShamanWeaponScripts.cs` | SpellEffects.cpp:4544-4561 |

The imbues themselves are temporary weapon enchantments (SPELL_EFFECT_ENCHANT_ITEM_TEMPORARY, crafting lane) whose COMBAT_SPELL enchantment effect is
procced by `SpellSystem.HandleItemCombatProc` (Rockbiter Weapon's TOTEM effect adds weapon damage in `PlayerEnchantments`); Windfury Weapon and Frostbrand
Weapon are data-driven there. The enchantment rows come from the client's SpellItemEnchantment.dbc and the PPM rows from `spell_proc_item_enchant`.

Totems: the totem is rooted (`MovementFlags.Root` at summon and on every active update, `TotemSystem.cs:277`, `TotemSystem.Active.cs:21`;
`ActiveTotemTests` and `TotemRootTests` assert it) and Searing Totem casts: the build 5875 `totem_spell` maps Searing Totem 2523 to 22048 (2.2 s cast, 20 yd, fire), a
cast-time spell, so `TotemSystem.Active` drives it (ActiveTotemTests, TotemLoopbackTests).

## Rogue

Rupture adds `attack power * min(combo points, 3) / 100` and Garrote `attack power * 0.03` to the tick amount, once, when the aura is created
(SpellAuras.cpp:4366-4384), through `Rogue/RogueBleedScripts.cs` (`RogueScriptFeature` installs it after the combo point feature).

## Warlock

Curse of Doom (603): a tick that kills casts Curse of Doom Effect (18662) on the caster one time in ten (SpellAuras.cpp:5921-5924); players and units
a player owns are not targets (TARGET_IS_PLAYER / BAD_TARGETS, Spell.cpp:7584-7592). SPELL_EFFECT_SUMMON_DEMON (112, `Pets/SummonService.SummonDemon.cs`,
SpellEffects.cpp:5796-5819) summons the Doomguard at the destination with the caster's level; once out of combat at the end of the spell's duration it
is unsummoned (TEMPSUMMON_TIMED_COMBAT_OR_DEAD_DESPAWN), and it has no owner, so its summoner leaving does not take it away. Conflagrate needs and consumes the
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
  non-PvP-flagged player's negative area spell does not hit players outside a duel unless both are free-for-all PvP (GridNotifiersImpl.h:170), combat unless NO_THREAT-like attributes, immunity). An existing
  holder of the spell from the caster gets the object's duration; otherwise a holder with the spell's duration (a channel's remaining time).
- The aura leaves a unit outside the radius or when its object is gone (PersistentAreaAura::Update, SpellAuras.cpp:892-919), unless the spell has
  SPELL_ATTR_EX3_NO_AVOIDANCE; a channel's end removes its objects (Spell.cpp:3595, 4794) and a channel whose first effect is the ground aura makes the
  object its channel object (Spell.cpp:4830-4833).

## Seams

- `SpellSystem.RegisterPeriodicDamageScript(spellId, IPeriodicDamageScript)`: `CalculateTick` replaces the tick's caster-side amount before the ramp and
  the target side (vmangos OnPeriodicCalculateAmount and the hard-coded spells of Aura::PeriodicTick); `AfterTick` runs after the damage.
- `SpellSystem.DynamicObjects`, `GetDynamicObjects(caster)`, `FindDynamicObject(holder)`, `RemoveDynamicObjects(caster, spellId)`.
- `SpellSystem.CastItemCombatSpell` (internal): a script's extra weapon proc.
- `SpellSystem.RegisterDummyAuraHandler(spellId, apply)`: a case of vmangos Aura::HandleAuraDummy's switch on the spell id. The DUMMY aura type's one handler
  slot dispatches here; one case per spell (a second is a startup error). The pet auras own the table's ids.
- Internal helpers for scripts in `SpellSystem.Auras.cs`: `ApplyAuraEffect` (EffectApplyAura for an area aura effect), `SetAuraAmount` (ApplyModifier off,
  amount, on), `ModAuraStackAmount` (SpellAuraHolder::ModStackAmount), `RestartHolderTicks` (a holder refreshed with itself: ticks start over).
- SPELL_EFFECT_APPLY_AREA_AURA_PET (119) is claimed by `PetAuraModule` (only when nothing else handles it).

## Deviations and limits

- Stacking: `spell_group` stack rules (Greater Blessing versus Blessing) are not modelled; a rank chain is the spell family plus the spell name. A
  refused weaker party-aura child is built again on the next area update (no handler, slot or packet; vmangos does the same refusal each pulse).
- Judgement of Light's heal script (the Tier 3 bonus through `m_triggeredByAuraBasePoints`), Illumination, Seal of the Crusader's damage reduction and
  Blessing of Sacrifice (SPLIT_DAMAGE_PCT, aura engine) are not part of this lane. Hammer of Wrath crits with the spell crit chance, not the melee one.
- Bloodthirst does not add MOD_MELEE_ATTACK_POWER_VERSUS; Execute reads the rage at the dummy effect (after the cost, as vmangos' OnCast does).
- A dead Doomguard is left to its corpse decay (vmangos restarts its timer for the corpse); a summoning ritual destination and Inferno's Enslave
  Demon are not modelled.
- Ground objects never move; the 2 s refresh window starts at the first visit; an existing holder that lacks the ground effect's aura is not given it.
- Reckoning: this server's extra-attack queue (`UnitCombat`, combat area) makes a queued batch ready on the next unit update, so Reckoning's stacks build while
  the paladin is not swinging and are released on its next melee update; vmangos releases them after the paladin's next own swing (`AddExtraAttackOnUpdate`).
- Counterattack reads the parried attacker from the hunter's combo target; a non-player caster is not checked (the reactive service is not reachable
  from a script).
- Pet auras: the owner's copy of an APPLY_AREA_AURA_PET aura is decided when the source is applied (vmangos re-checks the radius every update) and leaves
  with the source within one spell update when the pet is unsummoned. A revived pet does not get the auras back until the next summon or learned talent
  (vmangos Pet.cpp:658 casts them when the pet comes alive; the revive path is the creatures area's). A warlock demon removed by a respec gives no soul shard
  back (the summon does not charge it either). Mana Tide's build 5875 row does not use the script (above).
- Real data: Spell.dbc / `spell_template` (every id above), SpellItemEnchantment.dbc for the imbues, `spell_proc_event` and
  `spell_proc_item_enchant` rows for PPM procs, and creature 11859 (Doomguard) in `creature_template`. The tests use synthetic rows with the real ids,
  families, flags and effect kinds.

## Tests

- `tests/ArcaneCore.Game.Tests/ClassSpells`: `PaladinScriptTests` (13), `PaladinHealingAndAuraTests` (4), `ShamanWeaponScriptTests` (7),
  `ClassDummyScriptTests` (14), `CurseOfDoomTests` (7); `Rogue/RogueBleedScriptTests` (7); `Spells/PersistentAreaAuraTests` (13).
- Talent scripts: `ClassSpells/MageTalentScriptTests` (8: Ignite tick, rank share, fire-crit only, rollover on a second crit, the 5-stack refresh;
  Combustion stacks and charges, cancel, dispelled buff), `SwiftmendScriptTests` (5), `ManaTideScriptTests` (2), `ReckoningScriptTests` (3),
  `CounterattackScriptTests` (4); `Pets/TalentPetAuraTests` (8: Soul Link on the imp and the warlock and its unlearn, Soul Link ending with a pet change,
  Master Demonologist's variant by entry at summon and its unlearn, the permanent-pet rule, Spirit Bond on a called hunter pet, the respec removing the
  hunter's pet, a learned talent re-casting the pet aura). The scenario `ClassScriptScenarioTests.Swiftmend_*` runs Swiftmend between two bots.
- RED evidence for the talent scripts: `D:/ArcaneCore-lanes/_logs/tb-t3-talent-class-scripts-pets/red-*.log` (one per script or hook, each failing on
  assertions with the piece switched off).
- `tests/ArcaneCore.World.Tests/Spells/ClassScriptWiringTests` (the production world spell system carries the scripts) and the playerbot scenarios
  `Playerbots/Scenarios/ClassScriptScenarioTests` (two bots duel: Seal of Righteousness and Judgement; Consecration's ground object reaches the
  opponent's client and ticks on it), with `ScenarioClassDecoders` (SMSG_SPELLNONMELEEDAMAGELOG, SMSG_PERIODICAURALOG).
- RED evidence: `D:/ArcaneCore-lanes/_logs/w2-class-scripts/red-*.log` (each run with the lane's implementation switched off).
