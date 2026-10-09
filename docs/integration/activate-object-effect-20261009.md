# Spell effect 86: activate object (2026-10-09)

The imported world 46 spell table has 150 `ACTIVATE_OBJECT` effects across 137 spells and uses action values 1-8, 10, 12, 15 and 16. ArcaneCore now resolves game objects in the caster's map and applies these actions through `GameObjectMapSystem`. A missing or despawned target does nothing, as in the reference `Spell::EffectActivateObject`. This module is discovered with the other spell effect handlers; it does not replace an existing handler.

For implicit target 40, the selected object is the explicitly named eligible object or the nearest object matching an imported `spell_script_target` game-object row. Targets 51 and 52 select all matching objects in the source or destination area; each row's inverse effect mask is honored. The cast keeps the selected object GUIDs per effect, runs the action once per object, and lists those GUIDs as spell-go hits. Its internal caster carrier is not a unit hit for an object-only effect. The imported content has 440 game-object script-target rows for effect-86 spells; without a matching row or explicit object target, the spell does not guess one.

The behavior follows the action switch in mangos-classic `src/game/Spells/SpellEffects.cpp::EffectActivateObject` and the Classic client's `GameObjectActions` values in `Entities/GameObject.h`; vmangos `SpellEffects.cpp` was checked for the same state transitions. No source code was copied.

| Action | Current behavior |
|---|---|
| 1-4 | Send the named custom animation, 0-3. |
| 5, 8 | Use the object as the casting unit (Onyxia Eruption 17731 sends animation 0). Doors, buttons, traps, spell focuses and goobers use their existing state machines. A goober accepts a creature caster without trying player-only gossip or quest credit. |
| 6, 7 | Clear or set the locked flag. |
| 9, 10, 12, 13, 18 | Open and unlock, close, use the alternative destroyed door state, rebuild, or close and lock. Values 9, 13 and 18 are not used by the imported spell table, but use the same object state machine. |
| 15 | Despawn the object through its existing respawn lifecycle. |
| 16, 17 | Set or clear no-interact. For the Silithus templar, duke and royal spells named in the mangos-classic switch, action 16 also summons the mapped creature at the stone for a 60-second out-of-combat-or-dead lifetime. |

The object AI may handle an activation before the generic action (`OnActivateBySpell`). Unrecognized action values are reported once per spell and action. `ToggleOpen` (11) and `Creation` (14) remain unsupported because the reference gives no behavior and the imported 1.12.1 spell table uses neither. Creature users of a goober do not yet start every game-object script event that the reference `GameObject::Use` can start; the unit path activates the object and linked trap.

`ActivateObjectEffectTests` proved RED before the handler (lock, door state and custom animation did not change), then GREEN after. Further RED/GREEN tests cover the goober's just-deactivated pass, implicit target 40, area targets 51/52, inverse effect masks, spell-go object hits, object-AI interception, and the 60-second Silithus summon clock. The existing Blackwing Lair targeting and relay-command classes still pass. Imported-world and real-client behavior remains to be exercised beyond those focused tests.

Release solution build: 0 warnings, 0 errors. The final combined focused filter passed 46/46; the full Game suite passed 7,944 with 15 skips, mock-client protocol passed 69/69, and the full World suite with real terrain passed 3,321 with 24 skips. The first World run had the previously recorded `PlayerbotAreaTriggerTests.ObservingWithoutAnEntry_AllocatesNothing` allocation flake (4,088 bytes), which passed alone; one bounded full rerun passed. No GameObject or Spell code in this slice calls that tracker. No imported-world or real-client cast of these 1.12.1 spells has been recorded.
