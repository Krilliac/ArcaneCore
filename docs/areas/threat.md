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
- The second-choice selector predicate (`isLowPriority`) is supported by `SelectVictim`; the creature host passes feared and
  confused targets only (damage-immune, breakable-CC and totem second choices need aura-holder data and a spell catalog).
- `ThreatContext` carries only the list-side flags. School, crit and spell-threat scaling (vmangos `ThreatCalcHelper::CalcThreat`)
  are not implemented: damage threat stays raw damage, MOD_THREAT and spell_threat are unimplemented.

### victim-selection (Creatures/CreatureMapSystem.Combat.cs `SelectHostileTarget`)

Creature victim selection now follows vmangos `Unit::SelectHostileTarget` (Objects/Unit.cpp:7544-7612):

1. Dead, evading or unknown creature: false. A creature in its 5 s respawn pacify chooses nothing, does not switch and does not
   evade (:7559-7561).
2. A taunt target (latest taunter that is still a valid target) beats the threat list (:7563); then the list picks (two-pass,
   110%/130%); a NO_THREAT_LIST creature sticks to its current victim (:7569-7571).
3. A chosen target is attacked (and chased) unless the creature is stunned, confused or fleeing (:7573-7581).
4. No target: NO_THREAT_LIST returns false; not in combat, taunted or charmed returns false; a creature that is not chasing but
   still has a targetable attacker returns false (a pet sent at a far target, :7592-7603); anything else evades.

Limits: stun/fear/confuse are read from `UnitFlags` (no aura-holder query), the "prevents fleeing" and pending-stun states are
not modelled, second-choice targets are only feared or confused units (damage-immune, breakable-CC and the totem rule of
`Unit::IsSecondaryThreatTarget`, Objects/Unit.cpp:9644-9676, need the aura engine and a spell catalog the host does not have).
The unreachable-target timers (Creature.cpp:1017-1040) are not delivered: nothing in the repository reports a chase as
unreachable (`TargetNotReachableEvent` has no producer), so there is nothing honest to time.

### taunt and threat auras (Spells/Effects/ThreatEffects.cs, Spells/Auras/ThreatAuras.cs, Combat/Threat/Taunt.cs)

| Piece | Behaviour | Reference |
|---|---|---|
| SPELL_EFFECT_ATTACK_ME (114) | Skipped when the (non-player) target already attacks the caster; otherwise the caster's threat is set to the current victim's and the caster becomes the current victim at once | SpellEffects.cpp:3356-3395 |
| SPELL_AURA_MOD_TAUNT (11) | Caster joins the target's taunt list (GUID list, latest first); a living target that can hold a list attacks the caster (not while confused/fleeing) and lifts the taunter's threat via the temp-threat rule; removal fades the temp threat out, evades on an empty list | SpellAuras.cpp:3939-3967, Unit.cpp:7443-7509 |
| SPELL_AURA_MOD_TOTAL_THREAT (103) | Fade: on a living player with a living caster the value is folded once into every list entry and taken out on removal | SpellAuras.cpp:3920-3937, HostileRefManager.cpp:39-55 |
| SPELL_EFFECT_MODIFY_THREAT_PERCENT (125) | The caster's entry changes by the value percent (below -100 removes) | SpellEffects.cpp:5638-5646 |
| EventAI 13 THREAT_SINGLE, 14 THREAT_ALL_PCT | Direct add or percent on the chosen target; percent on every entry | mangos-classic CreatureEventAI.cpp ProcessAction |

Immunity: unit immunity auras (EffectImmunity ATTACK_ME, StateImmunity MOD_TAUNT) stop taunts through the existing immunity rules
(vmangos applies exactly these two for CREATURE_IMMUNITY_TAUNT, Creature.cpp:444-448). The static creature taunt flag is not
read: `ICreatureImmunityProvider` carries mechanic and school masks only, so a creature template with that flag still
gets taunted until the creature data lane exposes it.

Limits: SMSG_CAST_RESULT with DONT_REPORT for "already attacking you" is not sent; the taunted creature is not turned to face
the taunter; after the taunt fades the victim is re-selected at the creature's next update (vmangos selects at once).
