# Rogue lane: stealth core (RG-02a, RG-02b, RG-02c)

Branch `claude/vw2-class-rogue`. Area doc: [docs/areas/rogue.md](../areas/rogue.md). No schema change: stealth is volatile, exactly as
in vmangos. It survives logout through the existing `character_aura` rows (a stealth spell carries no LEAVE_WORLD interrupt flag) and
the aura handler rebuilds the visibility state when the aura is restored (`RestoreAuras` runs the apply handler).

## New files

- `Game/Stealth/StealthDetection.cs`, `StealthOptions.cs`: the vmangos detection formula (`Unit::CanDetectStealthOf`, `Unit.cpp:6543-6616`),
  config section `World:Stealth` (`MaxPlayerDetectRange`, `MaxCreatureDetectRange`, both 30, `World.cpp:566-567`).
- `Game/Combat/Positional/PositionalRules.cs`: `IsBehindTarget` (`Unit.cpp:2806-2821`, strict creature-facing rule), `IsFromBehindOnly`
  (`SpellEntry.h:909-912`), the Gouge facing shape (`Spell.cpp:5646`). Wraps the existing `MapCombat.HasInArc`.
- `Game/Stealth/StealthState.cs`: `StealthRegistry` (visibility group per unit: On / Stealth / NoDetect, and the set of stealthed units).
- `Game/Stealth/StealthAuras.cs`: the MOD_STEALTH apply/remove handler (`SpellAuras.cpp:3631-3693`): flag bytes
  (`UNIT_FIELD_BYTES_1` byte 3 = 0x02, `PLAYER_FIELD_BYTES2` byte 1 |= 0x20), STEALTH_INVIS_CANCELS removal, NO_DETECT then STEALTH with a
  visibility refresh after each, hostile casts in progress cancelled (`InterruptSpellsCastedOnMe`, `Unit.cpp:10181-10215`).
- `Game/Stealth/StealthVisibilityRule.cs`: the stealth part of `IsVisibleForOrDetect` (`Unit.cpp:6321-6461`) in vmangos order.
- `Game/Stealth/StealthDetectionUpdater.cs`: `HandleStealthedUnitsDetection` (`Player.cpp:22007-22052`) on the player timer
  (first pass after 1000 ms, then every 2000 ms, `Player.cpp:272, 1141-1151`); idle while no unit is stealthed.
- `Game/Stealth/SpellSystem.Stealth.cs`: `HasAuraType`, `InterruptSpellsCastedOnMe`.
- `Game/Maps/VisibilityRules.cs`: the generic `IVisibilityRule` seam (no rogue content; gm-commands, Prowl, Shadowmeld, Feign Death and
  invisibility can use it).
- `World/Stealth/StealthFeature.cs` (discovered `IWorldFeature`): registers the three aura handlers on the world spell system and attaches
  the rule and the updater to every map (`MapCreated` plus existing maps).

## Shared-file edit

`Game/Maps/Map.cs`, additive:
- `AddVisibilityRule(IVisibilityRule)`, `RefreshVisibility(WorldObject)` (vmangos `UpdateObjectVisibility` / `UpdateVisibilityAndView`) and
  `UpdateVisibilityWithDetection(Player, WorldObject)` (detect mode, used by the pass).
- `UpdateVisibilityOf` consults the rules after the range check: `inVisibleList && (!inRange || !allowed)` removes (out-of-range block),
  `!inVisibleList && inRange && allowed` creates. With no rule attached nothing changes.
Conflict note: gm-commands (GM invisibility) edits the same method; they should add an `IVisibilityRule` instead of editing it.

## Behaviour (all from vmangos)

- A hidden stealthed unit is revealed only by the detection pass, never by a movement-driven update; a unit the viewer already sees stays
  visible through movement updates and is dropped by the next pass that no longer detects it (`Unit.cpp:6438-6451`).
- Always allowed: the viewer itself, a game master, the Hunter's Mark caster, a non-hostile party/raid member. A dead viewer never detects.
- Out-of-range blocks are used to hide a player (vmangos `BuildOutOfRangeUpdateBlock`), the existing `UpdateVisibilityOf` path.

## Limits (documented, not stubbed)

- The shapeshift half of every Stealth rank (aura 36 misc 30, form 30) and the movement slow (aura 33) need the stance and speed-aura lanes.
  A unit gets the stealth auras and flags but is not in form 30, so stance-gated openers cannot be checked yet.
- No GM-invisibility guard (VISIBILITY_OFF), no invisibility masks (potions, gnome devices), no Silithus flag drop, no cancel-removes-Vanish,
  no COOLDOWN_ON_EVENT start (RG-04 needs spell-breadth S1/S2 hooks), no Vanish 1 s "cannot be detected" window (RG-08).
- The mangos-classic detection model is not implemented; vmangos (sniff-verified) is the only model.
- A creature that stealths is not re-evaluated for viewers until the next regular update (creatures have no stealth source yet).
- `Cell::VisitAllObjects`-granular scanning is approximated by evaluating every stealthed unit of the map; the formula's 30 yd cap applies.
- Registering the same aura type twice is last-writer-wins today. If spell-breadth S1 (registry) lands first this lane's three
  `RegisterAura` calls must move to an `ISpellHandlerModule`; spell-breadth S6b also lists a stealth handler: keep exactly one owner.

## Tests

`tests/ArcaneCore.Game.Tests/Rogue/StealthDetectionFormulaTests.cs` (the sniffed measurements), `PositionalRulesTests.cs`,
`StealthVisibilityTests.cs` (flags, hiding, group/GM/Hunter's Mark, 2000 ms cadence with the fake clock, stay-until-pass, idle pass,
hostile cast cancel, persisted-aura restore, pruning), `tests/ArcaneCore.World.Tests/Rogue/StealthEndToEndTests.cs` (discovered feature on the real loop).
Mutation checked: removing the refresh, the cast cancel or the keep-if-listed rule fails 4 tests.
Real-client checks the automated suite cannot give: transparency/minimap tint of a stealthed rogue, others seeing it fade.
