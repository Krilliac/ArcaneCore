# Rogue lane: aura-interrupt dispatch (RG-01)

Branch `claude/vw2-class-rogue`. Area doc: [docs/areas/rogue.md](../areas/rogue.md). No schema change.

## What landed

New files (no merge risk):

- `src/ArcaneCore.Game/Spells/AuraInterrupt/AuraInterruptMask.cs`: all 23 `AuraInterruptFlags` bits as raw masks
  (vmangos `SpellDefines.h:577-599`). The existing `SpellAuraInterruptFlags` enum is left alone.
- `src/ArcaneCore.Game/Spells/AuraInterrupt/StealthBreakRules.cs`: `ShouldRemoveStealthAuras`
  (vmangos `Spell.cpp:8301-8331`) and `IsPositiveTarget` (vmangos `SpellEntry.h:218-236`).
- `src/ArcaneCore.Game/Spells/AuraInterrupt/SpellSystem.AuraInterrupt.cs` (partial `SpellSystem`):
  `RemoveAurasWithInterruptFlags` (vmangos `Unit.cpp:3735-3751`), `RemoveSpellsCausingAura`, and the
  private cast and hostile-hit helpers the hooks below call.

## Shared-file edits (each one line or a few, all commented "rogue lane")

| File | Change | Why / vmangos |
|---|---|---|
| `Game/Spells/SpellSystem.cs` `Cast` | `InterruptForCast(cast)` after the re-check passes, before the cooldown/power | `Spell.cpp:3440-3456` (ACTION, +LOOTING for a game object target), `3697-3714` (ACTION_LATE, +ATTACKING for a non-positive first target) |
| `Game/Spells/SpellSystem.cs` `Cast` miss branch | `InterruptTargetOfHostileSpell(.., hit: false ..)` | `Spell.cpp:1893-1897` |
| `Game/Spells/SpellSystem.Effects.cs` `ApplyEffects` | tracks "has a positive-value damage effect" and calls `InterruptTargetOfHostileSpell(.., hit: true, dealsDamage)` after the aura holder is added | `Spell.cpp:1622-1626`, `1645-1650` |
| `Game/Spells/SpellSystem.Combat.cs` `OnDamageTaken` | the self-damage guard no longer returns before the aura break. Cast pushback/interrupt still ignores self damage | `Unit.cpp:660-670`: `SKIP_STEALTH` is false above client build 1.6.1, so self damage breaks stealth |
| `Game/Combat/MapCombat.DamageEvents.cs`, `MapCombat.Melee.cs` | new event `MeleeSwingResolved(attacker, victim)` raised at the end of `AttackerStateUpdate` | `Unit.cpp:2285` |
| `World/Spells/SpellFeature.cs` | subscribes `MeleeSwingResolved`, removing ATTACKING auras from the attacker | same |

Conflict notes for the integrator: warrior-mechanics S02 (cast-check/observer seams) and spell-breadth S1/S2 touch the same
`Cast` and `RemoveHolder` regions. Re-apply the one-line `InterruptForCast` call or move it to an
`ISpellCastObserver` when that seam lands.

## Not done here (documented limits)

- INTERACTING / LOOTING / ITEM_USE from client opcodes (gossip hello, trainer list, list inventory, quest hello,
  tabard, binder, stable, repair, battlemaster hello, game object use, item use). vmangos calls
  `RemoveAurasWithInterruptFlags(INTERACTING)` inside each handler **after** it validated the NPC
  (`NPCHandler.cpp:58,167,319,354,376,425,491,517,623,708,821,852,1012`, `QuestHandler.cpp:98`, `BattleGroundHandler.cpp:67`,
  `ItemHandler.cpp:721`, `SpellHandler.cpp:257`). A pre-handler opcode table would also strip stealth on refused
  interactions, which retail does not, so it was not built. The public `SpellSystem.RemoveAurasWithInterruptFlags(player, AuraInterruptMask.Interacting)`
  is the call each owning lane adds after its own validation.
- Proc-flag skip: the damage break in vmangos passes `checkProcFlags = true` and skips auras whose spell has `procFlags`.
  `SpellInfo` has no proc flags until the proc engine lands (warrior-mechanics S02/S12). Until then every aura with the
  Damage bit breaks.
- The damage break does not pass the damaging spell id as `except` (vmangos passes `spellProto->Id`), because `OnDamageTaken` has no spell parameter.
- Caster-side half of `Spell.cpp:1652-1668` (a visible caster of a hostile non-Sap spell loses stealth even when the cast-time
  rule exempted it) needs `IsVisibleForOrDetect`, i.e. the stealth visibility model (RG-02, not delivered).
- "Hit that deals damage" is a proxy for vmangos `m_damage != 0`: a SCHOOL_DAMAGE / HEALTH_LEECH / weapon damage effect with a positive computed value.

## Tests

`tests/ArcaneCore.Game.Tests/Rogue/AuraInterruptTests.cs` (every negative paired with a flipping positive in the same fixture),
`tests/ArcaneCore.World.Tests/Rogue/MeleeSwingInterruptTests.cs` (production path: map combat tick -> SpellFeature).
Proven load-bearing by reverting the swing subscriber: the world test times out without it.
