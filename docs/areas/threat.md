# Area: threat, aggro and combat AI rules

Status: wave 4 lane `threat-and-aggro` (branch `claude/vw5-threat-and-aggro`). Everything below is checked against the
vmangos sources in `D:\refs\vmangos` (primary). Nothing here was run against a retail client or a vmangos server: the
evidence is the reference code plus this repository's tests.

## Delivered scope

### threat-list-core (Game/Combat/ThreatList.cs, Game/Combat/Threat/*)

`ThreatList` is the one ThreatManager of a creature (vmangos `ThreatManager` + `ThreatContainer` + `HostileReference`).
The mirror set `UnitCombat.ThreatenedBy` is the HostileRefManager. Behaviour that follows the reference:

| Rule | Reference |
|---|---|
| Who may hold a list: living creature, not a totem, not a pet of a player, not charmed by a player, not NO_THREAT_LIST; players never | Objects/Unit.cpp:7377-7405 (`ThreatRules.CanHaveThreatList`) |
| Ignored: the owner itself, dead target, GM target; threat never below 0 | Threat/ThreatManager.cpp:399-407, HostileReference::addThreat :101-106 |
| A non-negative change on a pet's entry also creates a 0-threat entry for its owner | ThreatManager.cpp:117-122 |
| Assist threat is 0 while the owner is confused or fleeing (the entry still exists) | ThreatManager.cpp:414-421 |
| `NoNewEntry`: raise an existing entry, never create one (EX_NO_THREAT) | ThreatManager.cpp:424, addThreatDirectly :427-447 |
| Offline list: a GM target's entry keeps its threat but is never selected; it comes back when the GM flag goes | ThreatManager.cpp:128-149, :526-543 |
| Percent modification: below -100 removes, -100 zeroes, otherwise scales | ThreatManager.cpp:250-262, ThreatManager.h addThreatPercent |
| Temp threat (taunt, Fade): folded into the stored threat and remembered; applied once, no stacking | ThreatManager.cpp:481-500, ThreatManager.h setTempThreat/resetTempThreat |
| Taunt caster list: latest valid taunter first | Objects/Unit.cpp:7513-7542 |
| Victim selection: two passes (second-choice targets only when nobody else), 110% in melee reach / 130% otherwise, out-of-area target abandons the selection | ThreatManager.cpp:286-370 |
| `HostileRefs`: add temp threat / scale / delete references over every list holding a target | Threat/HostileRefManager.cpp:39-55, :124-134 |

The list re-sorts lazily (only after a change), with a stable insertion sort, as vmangos does (ThreatManager.cpp:275-280).

## Limits (not delivered here, by design)

- No threat packets exist in vanilla 1.12 (`wow_messages` threat packets are 3.3.5 only), so nothing is sent.
- Assist threat zeroing for a stunned owner whose stun breaks on damage and for UNIT_STATE_ISOLATED needs aura-holder data
  that the spell lane does not publish; only confused and fleeing are applied.
- "Taxi flying" targets going offline wait for a taxi primitive on `Player`; the GM case is real.
- The second-choice selector predicate (`isLowPriority`) is supported by `SelectVictim`; the creature host does not pass one
  yet (damage-immune and secondary-threat targets need the aura engine lane's holder data).
- `ThreatContext` carries only the list-side flags. School, crit and spell-threat scaling (vmangos `ThreatCalcHelper::CalcThreat`)
  are not implemented: damage threat stays raw damage, MOD_THREAT and spell_threat are unimplemented.
- Taunt (SPELL_EFFECT_ATTACK_ME, SPELL_AURA_MOD_TAUNT), MOD_TOTAL_THREAT, MODIFY_THREAT_PERCENT have list primitives
  (`TauntApply`, `ApplyTempThreatModifier`, `ModifyThreatPercent`) but no spell handler yet.
