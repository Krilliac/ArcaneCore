# Area: Rogue mechanics

Branch `claude/vw2-class-rogue` (wave 2). Cross-area edits and conflict notes are in
[rogue-aura-interrupt.md](../integration/rogue-aura-interrupt.md), [rogue-stealth-core.md](../integration/rogue-stealth-core.md) and
[rogue-creature-stealth.md](../integration/rogue-creature-stealth.md). No schema change in this lane.

References, in order of authority: **vmangos** (primary; every rule below cites `Unit.cpp`, `Spell.cpp`, `SpellAuras.cpp`, `Player.cpp`,
`SpellEntry.h`), **mangos-classic** (cross-check), **wow_messages** (update field layout: `PLAYER_FIELD_BYTES2` 0x04ec). Nothing was copied from the
references. The retail standard is "as close to vmangos as possible"; the one deliberate deviation sits behind a config option that defaults to
the talent text (below).

## Delivered

**Aura-interrupt dispatch (RG-01)** `Game/Spells/AuraInterrupt/*`. The 23 `AuraInterruptFlags` bits as masks; `SpellSystem.RemoveAurasWithInterruptFlags`
(except spell, skip stealth by Dispel 5, skip invisibility by Dispel 6); `StealthBreakRules.ShouldRemoveStealthAuras` (triggered casts,
EX_ALLOW_WHILE_STEALTHED, the Camouflage/Shadowmeld/Vanish icons, Improved Sap 30/60/90 percent). Hooked at: cast preparation, before the cast bar runs (ACTION, +LOOTING for a game
object target, and the stealth break; `Spell.cpp:3443-3456`) and cast completion (ACTION_LATE, +ATTACKING for a non-positive first target), a hostile spell hit or miss on the target (HOSTILE_ACTION_RECEIVED,
target stealth and invisibility strip), the end of a white swing (ATTACKING), and self damage (breaks auras at build 5875). Stealth, Vanish and Shadowmeld
carry 0x3C07 in Spell.dbc, so all of them break exactly as their data says.

**Stealth core (RG-02a/b/c)** `Game/Stealth/*`, `World/Stealth/StealthFeature.cs`. MOD_STEALTH apply/remove (client flag bytes, NO_DETECT then STEALTH,
hostile casts at the unit cancelled), MOD_STEALTH_LEVEL and MOD_STEALTH_DETECT as data auras, a generic `IVisibilityRule` seam in `Map.UpdateVisibilityOf`,
the stealth rule in vmangos order (self, GM, Hunter's Mark caster, non-hostile group, NO_DETECT, keep-if-listed, distance formula, line of sight), and the
player detection pass (first after 1000 ms, then every 2000 ms). The distance formula is `Unit::CanDetectStealthOf` with the sniffed measurements of its
comment as test cases: 9 yd same-level player vs player, 21 vs a creature, 1.5 yd per level (doubled above 3 levels of difference), cap 30, minus 9 yd behind the detector,
always detected under 1.5 yd, never by a stunned detector, 5/6 yd base and per level for creatures, alert band +5 yd. Positional helpers: `IsBehindTarget` with the strict
creature-facing rule, `IsFromBehindOnly`, the Gouge facing shape.

**Creature detection (RG-03)** `StealthServices.CanCreatureSee` (per map). The call into `CanAggroOnSight` belongs to the creature-ai lane
(one line documented in `rogue-creature-stealth.md`).

**Energy (RG-05a)** The pool and tick already followed vmangos (100 max and start; +20 per 2000 ms; regen in combat; resurrection and level-up refill).
`EnergyTests` and `RogueCreationTests` pin that behaviour so the stat/aura/rate lanes cannot change it unnoticed.

## Config

`World:Stealth:MaxPlayerDetectRange` (30), `MaxCreatureDetectRange` (30), `ImprovedSapRollPerPhase` (false), `GroupVisibilityMode` (`SameGroup`; vmangos `Visibility.GroupMode` 0 same sub-group, 1 `SameRaid`, 2 `SameTeam`).
`ImprovedSapRollPerPhase`: vmangos calls `ShouldRemoveStealthAuras` once at cast start and once at completion, so Improved Sap rolls twice and a 30/60/90 percent
talent keeps stealth 9/36/81 percent of the time (Spell.cpp:3455, 3713, 8301-8331). mangos-classic implements Improved Sap differently (a proc that recasts the highest
Stealth rank, `ClassScripts/Rogue.cpp:103-110`). By default one roll decides, which gives the 30/60/90 of the talent text; `true` reproduces the literal vmangos code.

## Reference disagreements and open questions

1. Combo points are cleared when a rogue or druid selects another unit in vmangos (`MiscHandler.cpp:410-414`), never in mangos-classic. No slice here (combo storage belongs to
   warrior-mechanics S09 or spell-breadth S5); the choice must be made when that lane lands (config `Combo:ClearOnSelectionChange`, default vmangos).
2. Stealth detection: vmangos' sniffed linear model is implemented; mangos-classic uses `0.3 x (30 + 5 x (level - 1) + modifiers - strength)`, front-only (`ObjectVisibility.cpp:152-178`). Not implemented.
3. Neither reference clears combo points on leaving combat, although the lane brief lists it.
4. Garrote scales with attack power only in vmangos (`SpellAuras.cpp:4341-4396`); not part of this lane's delivered scope.
5. `SpellItemEnchantment.dbc`, `SpellShapeshiftForm.dbc` and `SpellDuration.dbc` are not in the references: poison proc chances, form-30 flags and finisher durations cannot be verified without them.

## Limits (not stubs: absent, documented)

- Shapeshift form 30 and the movement slow of Stealth (aura 36 misc 30, aura 33): need the stance and speed-aura lanes. A stealthed unit is not in form 30 until then.
- No Vanish/Sanctuary effect, no Vanish script (remove roots/snares/Hunter's Mark, recast highest Stealth rank), no Preparation, no Distract, no Pick Pocket, no poisons, no rogue talent
  consumers, no combo points, no finisher scaling, no energy modifiers (Adrenaline Rush, Vigor, Rate.Energy) and no 82 percent energy refund on a miss. Each is in the lane's
  `slices_not_done` with its blocker.
- No COOLDOWN_ON_EVENT start for Stealth (10 s cooldown after the aura fades) and no cancel-removes-Vanish: both need the fade hook that spell-breadth owns.
- Interaction breaks (gossip, trainer, vendor, quest NPC, game object use, item use): the dispatcher is public; each owning lane calls it after its own validation (list in `rogue-aura-interrupt.md`).
- No proc-flag skip in the damage break (SpellInfo has no proc flags yet); the damaging spell is not excluded from its own damage break.
- No GM-invisibility guard, no invisibility masks (potions, gnome devices), no Silithus flag drop; creatures that stealth are not re-evaluated until their next regular update.
- Creature stealth alert behaviour (face the player, distract 5 s, 10 s cooldown, `SMSG_AI_REACTION`).

## Real-client acceptance (not automatable here)

Rogue level 10 (`.learn 1752 2098 1784 53 921 5171`): (1) Stealth toggles; others see you fade; relog while stealthed; (2) a mob at 9 yd front and back: aggro distances
and the alert exclamation; (3) the stealth icon and minimap tint; (4) Sap a humanoid, then damage it; (5) Improved Sap rank 3 keeps stealth about 90 percent; (6) energy bar
ticks of 20 every 2 seconds. Combo, Backstab and Pick Pocket steps wait for their slices.

## Provenance

Every formula cites its vmangos `file:line` in the code comments; the test classes carry the same citations
(`AuraInterruptTests`, `StealthDetectionFormulaTests`, `PositionalRulesTests`, `StealthVisibilityTests`, `CreatureStealthTests`, `EnergyTests`).
Citation corrections made while reviewing the lane design: the cancel-Stealth-removes-Vanish rule is `SpellAuras.cpp:3675-3682`; the rogue-only pickpocket loot window is
`LootHandler.cpp:110` and `285-287`; pickpocketing a creature in combat has no 1.12 check (`Spell.cpp:5466-5471` is `#if <= 1.11.2`).

## Wave-2 integration note
The aura-interrupt machinery (`AuraInterruptMask`, `SpellSystem.RemoveAurasWithInterruptFlags`) is the single implementation; the druid
lane's duplicate constants/extension were removed. The rogue swing event is named `MapCombat.MeleeSwingFinished` (the warrior lane
already owns `MeleeSwingResolved(MeleeDamageInfo)`). Combo points: this lane delivers none; the single combo implementation is
the wave-1 `Combat/Combo/ComboPointService` (warrior lane), which the druid and rogue abilities should use.
