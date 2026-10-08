# Duels: integration notes

Area document: [areas/duels.md](../areas/duels.md). Branch `claude/vw4-duels`, slices S0-S6 (S7 and S9 are not delivered, see the area doc).

## Seams used

* `IMapUpdater` / `[DefaultMapUpdater(Order = 10)]`: `MapDuel` (after combat, order 0).
* `IWorldFeature`: `ArcaneCore.World.Combat.Duel.DuelFeature` (binds `World:Duel`, creates `DuelService`, subscribes `PlayerLoggingOut`).
* `IOpcodeHandlerGroup`: `DuelHandlers` (`CMSG_DUEL_ACCEPTED`, `CMSG_DUEL_CANCELLED`).
* `ISpellHandlerModule`: `DuelSpellEffects` (effect 83), plus one `ISpellCastCheck` (`DuelCastCheck`, target phase) registered by `DuelService.Install`.
* No new opcodes, no appsettings edit (defaults live in `DuelOptions`), **no schema**: Characters and World versions are untouched.

## Edits in shared files (all small and commented)

| File | Change |
|---|---|
| `Game/Combat/CombatHooks.cs` | `IsFriendly`: `!DuelRules.IsOpponentHostile(a, b) &&` in front of the team rule. `CanAttack`: the PvP-flag gate also tests `!DuelRules.IsInDuelWith(attacker, victim)` |
| `Game/Combat/MapCombat.Death.cs` | `TogglePlayerPvpFlagOnAttackVictim`: early return for duel opponents |
| `Game/Combat/MapCombat.Melee.cs` | `SetInCombatWithAggressor`: `&& !DuelRules.IsInDuelWith(pv, pa)`. `DealDamage`: `ApplyDuelClamp` after the zero-damage return, `AfterLethalDuelDamage` after `Kill`, `AfterClampedDuelDamage` before the final return (logic in the new `MapCombat.Duel.cs`) |
| `Game/Combat/FactionCombatHooks.cs` | remark only |
| `Game/Spells/SpellAuraHolder.cs` | `AppliedAtUnixSeconds` |
| `Game/Spells/SpellSystem.cs` | `UnixSecondsClock` |
| `Game/Spells/SpellSystem.Auras.cs` | one stamping line in `AddAuraHolder` |
| `docs/integration/combat.md`, `docs/areas/combat.md` | the duel paragraphs |

Wave-2 spell combat rules, pets and CC lanes edit the same `DealDamage` and hook lines: re-apply the three `DealDamage` call sites and the two hook lines
mechanically; everything else is in new files.

## Contract for the pets / charm lane

Implement `IPlayerControlledUnit.ControllingPlayer` on the pet, guardian, totem and charmed-unit class (the owner or charmer player, null when none). Then:
the clamp attributes a pet's lethal hit to its owner (vmangos `Unit.cpp:762-779`), `DuelRules.IsOpponentHostile` makes a duelist's pet hostile to the opponent
(`Object.cpp:3640-3652`) and the PvP pulse exemption covers the pet. Still to add by that lane: `DuelService.CombatStopWithPets` over controlled units, combo points
aimed at the opponent's pet in `Complete` (`Player.cpp:6798-6804`), and the `PLAYER_DUEL_TEAM` copy for charmed players (mangos-classic only).

## Contract for other lanes

* Reflected spells: when the spell combat rules lane has a reflected flag on damage and holders, add the `pVictim == this && reflected` clause in `ApplyDuelClamp`
  and the `IsReflected` clause in `DuelService.RemoveHostileAuras`.
* Transports: delivered by the transport lane (`DuelInfo.TransportGuid`, `NOT_ON_TRANSPORT` in `CheckChallenge`, the ship as the duel area in
  `CheckDistance`; docs/areas/transports.md).
* `SpellSystem.UnixSecondsClock` is set by `DuelFeature` to the duel clock; any other consumer of `AppliedAtUnixSeconds` should read the same clock.

## Tests

`tests/ArcaneCore.Game.Tests/Duel/*` (state, packets, hostility, lifecycle on a stepping clock, distance, completion, lethal damage, challenge) and
`Spells/AuraAppliedTimeTests.cs`; `tests/ArcaneCore.World.Tests/Duel/*` (feature, loopback end to end with a stepped `TimeProvider`).
