# Playerbot risk against reward, and retreat

How an autonomous managed playerbot decides whether a fight is worth taking, gives up one it is losing, and remembers where it
went wrong. Goals, movement and the rest of the bot are in `docs/areas/playbots.md`; the class rotations in
`docs/areas/playbots-combat.md`. Configuration: `World:Playerbots:Risk` (every key live through `.reload config`).

References: vmangos `Creature::IsOutOfThreatArea` (Objects/Creature.cpp:2796-2815), `CreatureAI::EnterEvadeMode`,
`Creature::GetAttackDistance` (:2193-2240), `Creature::CallAssistance` (:2520-2545), `BasicAI::MoveInLineOfSight`;
vmangos `PlayerBots/PartyBotAI.cpp` for the class escapes (Feign Death :1638, Frost Nova and Blink :1786, Vanish then
`RunAwayFromTarget` :2777, Fade :2016, Psychic Scream :2116, Fear :2323, Hamstring and Intimidating Shout :2507-2530, Sprint
:2926); the mangoszero playerbot module's `FleeStrategy` ("critical health", "panic"), `FleeFromAddsStrategy` ("has nearest
adds") and `FleeManager` (flee point away from the creatures) for the design. Neither reference weighs a pull before taking it;
that part is ArcaneCore's own.

## Pieces

| File (`src/ArcaneCore.Playerbots/Risk/`) | What it does |
|---|---|
| `PlayerbotRiskOptions.cs` | `World:Playerbots:Risk`: `Enabled`, `Tolerance`, `RetreatHealthPct`, `NearlyWonHealthPct`, `RecoverHealthPct`, `DangerMemorySeconds`, `DeathLoopMemorySeconds`, `PartyRetreatOnWipe`. |
| `PlayerbotRiskModel.cs` | The pure arithmetic: the pre-engagement verdict (`Assess`) and the in-combat verdict (`Judge`). Unit tested on snapshots. |
| `PlayerbotRisk.cs` | The world side, one per bot: gathers the facts of each candidate, chooses the target, watches the fight, starts and runs the retreat, the recovery wait. |
| `PlayerbotFightTracker.cs` | The observed damage rates of the current fight over a rolling 8 seconds of world time, where each enemy joined, and the bot's damage per second across fights. |
| `PlayerbotRetreat.cs` | The retreat itself and the trail of places the bot walked through (`PlayerbotBreadcrumbs`). |
| `PlayerbotEscapes.cs` | The 1.12 class escapes, in order (pure). |
| `PlayerbotDangerMemory.cs` | The creatures and places of retreats and deaths, for `DangerMemorySeconds`. |
| `PlayerbotCreatureSpells.cs` | A creature's own spells: the CAST actions of its EventAI rows and their damage from `spell_template`, and whether one kills outright. |
| `PlayerbotHazards.cs` | The places a living bot's walks keep out of (lethal creatures, creatures it fled from or died to, places of deaths and retreats). |

The creatures that would attack the bot are the camped-body rule's `PlayerbotRecovery.Threats` (each with its own aggro radius
against the bot from `CreatureMapSystem.GetAttackDistance`, the creature's `CanInitiateAttack` and `IsProximityAggroAllowedFor`),
so a corpse run and a pull judge aggro the same way.

## Before a pull

When the brain needs a new target it asks `PlayerbotRisk.ChooseTarget` instead of taking the nearest. Candidates are those
`PlayerbotBrain.FindTargets` returns (the quest objective first, then the nearest; non-objectives up to three levels above the
bot, the named objective any level), at most five per decision, each verdict cached for 5 seconds.

For each candidate the predicted fight is the target plus:

* **Assist**: every creature the target's assistance call would bring (`CreatureMapSystem.CanAssist` within
  `AssistanceRadiusOf(template)`: vmangos calls idle same-faction creatures within `CreatureFamilyAssistanceRadius` when a
  creature enters combat).
* **Fight spot**: every creature whose aggro radius covers where the bot will stand to fight (its preferred range from the
  target along the approach).
* **Path**: every creature whose aggro radius covers the planned route there (sampled every 3 yards).

**Risk** is the damage the bot is predicted to take killing them one at a time, quickest first (each hits until it dies: its
template melee damage per second, or level x 1, x 2.5 for an elite, when the template has none, plus its spells: each EventAI
CAST action (cmangos `creature_ai_scripts` action 11) deals its `spell_template` damage (school, weapon, health leech, every tick
of a periodic damage aura, dice at their top, scaled by `creature_classlevelstats` class-1 `melee_damage` at the creature's `MaxLevel` when `SpellAttributes.ScalesWithCreatureLevel` `0x00080000` is set, vmangos `SpellCaster.cpp:1215-1225`) every `(Param3 + Param4) / 2` ms of its event, 10 s when the row gives none), as a share of the bot's current
health. The bot's damage per second is `2 + 1.8 x level` before its first fight, and after each fight 70% of the estimate so far
plus 30% of what it dealt in that fight's last 8 seconds (a moving average that starts from the level estimate: a window of a few
swings moves by a third with misses and dodges, and one unlucky one taken alone put same-level creatures out of reach for good,
since only fights refresh the estimate; claude/w19-risk-flake). A
mage, priest or warlock deals less as its mana runs out (to 35% of its damage at none: the wand); a hybrid less so. Remembered
danger near the target or against its kind multiplies the risk by `1 + 0.5 x hits`; a ready class escape takes 10% off. A lone
same-level creature is about 0.4 to 0.5; three of them about 2.5.

**Reward** is the experience of the kills relative to a same-level kill (`XP::Gain`: gray gives nothing, an elite twice), plus 2
for a quest objective and the loot value (a humanoid 0.2, a beast 0.1, or 0.3 for a skinner).

The **decision**:

| Decision | When | What the bot does |
|---|---|---|
| `engage` | risk at most `Tolerance x min(0.9, 0.4 + 0.2 x reward)` | Pulls it. |
| `avoid` (`lethal`) | a creature that would be in the fight has a spell that kills the bot outright: `SPELL_EFFECT_INSTAKILL`, or one hit of at least the bot's maximum health (the Scourge invasion's Skeletal Soldier and Spectral Apparition, Scourge Strike 28265) | Next candidate, quest objective or not. |
| `avoid` (`elite-above`) | an elite three or more levels above the bot that is not a quest objective | Next candidate. |
| `avoid` (`remembered`) | the bot fled from or died to this very creature within `DangerMemorySeconds` | Next candidate (it is not even listed). |
| `rest` (`low-health`, `low-mana`) | it would be an `engage` at full health and mana | When nothing else is worth it, the bot waits (eating or drinking when it carries food or water) until `RecoverHealthPct`, at most 90 s. |
| `detour` (`path-adds-N`) | the fight is acceptable without the creatures on the way | Walks via a waypoint 15, 25 or 35 yards to either side of the straight line, chosen so the route and the last leg stay 2 yards outside the reach of every visible creature that would attack the bot and is not part of the fight anyway (those on the way and any other, e.g. one standing beside the waypoint), and out of its hazards; `avoid` when none does. |
| `avoid` (`pack-of-N`, `too-strong`, `elite`) | otherwise | Next candidate; with none, the brain's destinations or exploring as before. |

The last verdict is reported: `risk=2.47 reward=3.30 decision=avoid reason=pack-of-3 target=6`.

A quest objective avoided for strength that a group of 2 to 5 would take (`PlayerbotRiskModel.NeededGroupSize`, with
`PlayerbotEngagementFacts.GroupSize`: N times the damage, the damage taken over N players' health, an elite up to three more levels)
is reported as `reason=group-of-N` and becomes a group goal (`docs/areas/playbots-groups.md`).

## In a fight

Each think in combat the brain asks `PlayerbotRisk.ObserveFight`. The tracker samples the bot's health and every enemy's
health (the target, its attackers and anything with the bot on its threat list, not evading). Over the window:
damage taken per second (net of heals) and damage dealt per second (an enemy counts only while seen in two samples, so an add
that just joined is not "damage"; one that died took its remaining health). Under 2 seconds of samples the estimate's priors
stand. Then `time to kill = enemies' health / damage dealt`, `time to die = bot health / damage taken`.

* An enemy with a spell that kills outright: retreat at once (`lethal`) unless the fight ends within 2 seconds.
* A single enemy at or below `NearlyWonHealthPct` (20%) is finished unless the bot would die in less than half the time it needs.
* Winning (`time to die x Tolerance >= time to kill`): fight on.
* Losing: retreat at `RetreatHealthPct` (35%), against more than one enemy when one more round would take the bot to or below that
  line (`PlayerbotFightFacts.RoundDamage`: the non-crit max hit, `UnitFieldMaxdamage`, of each live, non-evading melee creature
  attacking it, added up, so the mean damage rate cannot hide a burst of aligned blows that leaves the bot too weak to get away;
  the retreat reason is `losing-burst-N`; a lone enemy keeps the plain line), or when the bot would die
  within 4 seconds; until then fight on (`behind`). Whether the retreat can succeed is judged at the current health.
  A retreat that would not break contact before it kills the bot is no way out, and the bot fights on (`cornered-no-escape`,
  `PlayerbotRiskModel.RetreatSucceeds`): the damage predicted on the run must stay under 90% of the bot's health. A pursuer at
  least as fast as the bot (speed_run 1.14286 is 8 yards a second, an unbuffed bot 7) keeps hitting for the whole run and on
  until its leash clock is more than `LeashExtensionSeconds` (12) whole seconds old; that clock starts only once both are out of
  its threat area (vmangos `IsOutOfThreatArea`), so a bot that turns to run in contact is counted hit for the run plus 13 seconds
  (`PlayerbotRetreat.Assess`). A slower pursuer hits until the gap passes its reach (about 5 yards). Blows from behind land
  `1 / (1 - avoidance)` times as often (no dodge, parry or block); a ready root, stun, fear or stealth takes the seconds it holds
  off, a snare or a speed buff changes the speeds compared, and an escape's cast time is spent under the blows
  (`PlayerbotRetreatFeasibilityTests`, `PlayerbotRetreatFeasibilityWorldTests`).

The report then reads `fight ttk=9.4s ttd=28.2s decision=fight reason=winning`.

## Retreat

`PlayerbotRetreat` stops the attack (CMSG_ATTACKSTOP) and, each think:

1. Uses the first ready class escape not used yet in this retreat, through the ordinary CMSG_CAST_SPELL (`PlayerbotEscapes`):
   mage Frost Nova (an attacker within 10 yards), Blink (facing away first); rogue Vanish, Gouge, Sprint; hunter Feign Death
   (it then holds still until the creatures turn away), Wing Clip, Concussive Shot, and Aspect of the Cheetah out of combat;
   priest Psychic Scream (within 8), Power Word: Shield, Fade; warlock Howl of Terror (two within 10), Fear, Death Coil; warrior
   Intimidating Shout (within 8), Hamstring; druid Entangling Roots, Travel Form, Cat Form then Dash; paladin Divine Shield
   (below 20%), Hammer of Justice; shaman Frost Shock. The ordinary emergency potion and bandage of the rotations stay as they
   were.
2. Runs back the way it came: to the newest place on its trail (a point every 4 yards, laid while it walks, a teleport starts a
   new trail) that is outside every enemy's leash and farther from them than the bot is. An enemy's leash is
   `max(1.5 x aggro radius, ThreatRadius) + 5` around where it stood when it joined and around its home (vmangos
   `IsOutOfThreatArea`), or its template's hard leash. The route is the navigation's; without one, the trail itself. With no such
   place, straight away from the enemies past the farthest leash. A route that ends with the bot still pursued is extended 30
   yards further, up to 8 times. Each further run (and the first, when its own route was refused) asks for straight away from
   the enemies first, then 45 and 90 degrees to either side (never back toward them), each goal on the ground there, one exact
   path query a turn; with none, the walkable point nearest the straight one (`PlayerbotRetreat.PlanAway`). A think that finds no
   way asks up to five plans and the nearest-point search; each of the five that the danger memory refuses (rather than the mesh)
   also runs the hazard detour search, held to the think's search budget, and counts as one refusal toward the boxed-in escape
   (`PlayerbotRisk.Refused`), so such a think can add up to five. It once asked for the
   straight one only, every think: where that was a cliff edge, water or a wall, all 8 replans were refused alike within a few thinks
   and the bot gave up cornered where it stood (live 2026-10-16, about 200 `bot-risk/retreat-cornered` an hour, most after no
   escape; `PlayerbotRetreatReplanTests`).
3. Ends `safe` when nothing threatens the bot (the creatures evaded), `cornered` after 60 seconds, 8 extensions or 8 thinks in a
   row with no way out in any of those directions (it then fights that fight out), or `died`.

After a safe retreat the bot waits to `RecoverHealthPct` (eating or drinking when it can) before it pulls again; the creatures
it fled from and the place are remembered for `DangerMemorySeconds`. The report: `retreat reason=losing-to-3 escapes=Frost_Nova+Blink`.

A death also goes into the memory (the creatures attacking the bot at its last living update, the enemies of the fight being
watched, and the place), and a bot killed within 30 seconds of walking an errand
(a trainer, vendor or quest destination) sets that errand aside for 10 minutes (`PlayerbotSuspensions`) instead of walking the
same way into the same creatures after its revive.

## Hazards on every walk

`PlayerbotHazards` holds, for `DangerMemorySeconds`: each creature with a spell that kills the bot outright (its aggro reach plus 8
yards, following it while seen), each creature the bot fled from or died to (its reach plus 3), each creature beyond the bot's
strength alone or as part of an overlapping pack (`strong`: judged at full health and mana; its reach plus 3,
dropped at the next look once the bot can take it), the place of each death (25 yards) and of each retreat (20). The brain looks the
visible creatures over every 2 seconds, alive or a ghost, and the stealthed ones it cannot see within 50 yards
(`PlayerbotRecovery.StealthedHazardYards`). A creature fled from or died to is a hazard from the moment it is remembered, at its
home (where it evades to) with its reach plus 3; a look that sees it moves the hazard to where it stands. A stealthed ambusher
back in hiding at its spawn is out of sight and, from farther than 50 yards, out of the stealthed look too: before, its hazard was
only ever written by a look that saw it, and a walk planned from afar went through its lair (live 2026-10-16, Nightsaber Stalkers
2043 on Teldrassil killing bots that already remembered them; `PlayerbotStealthedHazardTests`). The `strong` kind is for a bot on its own: a bot-led group's leader and a
party bot do not keep it, and the ghost's revive spot does not count it (the recovery judges strength itself). Strong hazards are
kept apart (their own cap of 64, so they never push a death or retreat place out), updated in place at each look, dropped when a look
no longer sees the creature alive and strong, and lapse 6 seconds after the last look (a ghost, or a bot whose brain a bot-led
group drives, keeps none).

A creature also becomes a `strong` hazard when the creatures whose aggro reaches overlap at its position have a combined
minimum melee exposure beyond the same full-health strength limit. This uses the ordinary hostile-threat and aggro-radius
rules (vmangos `BasicAI::MoveInLineOfSight`), and a lower bound on damage, so two manageable creatures standing together
cannot be treated as a safe walk. At a corpse or spirit-healer landing, recovery judges all threats whose reaches cover the
landing as one fight, including their known spells; it still checks each nearby creature separately for a camped body.

A bot ringed by them (a low-level bot revived among higher creatures, their reaches overlapping round it) walks out by a bounded
escape (`PlayerbotRisk.Refused`, ring-escape spec of 2026-10-11):
* **Gate.** Only a goal's walk refused for a strong hazard counts; the trial plans of the pull estimate and of detours do not. The bot
  must have stayed within 10 yards of where the clock started for 6 seconds of world time (`BoxedMs`; any number of refusals in one
  tick count for nothing), and no straight line round it may be clear for its first 40 yards. Two creatures guarding a destination
  leave open lines: no escape, the goal backs off and the stall watch sets it aside as before. Each bot waits a further 0-2 seconds
  by its guid, so bots revived together do not all run the fan on one tick. At most one escape per 6 seconds, and at most 3 per
  5 minutes (a bot that never gets clear leaves its goal to the stall watch).
* **Exit.** `PlayerbotHazards.TryFindGap`: a fan of 48 lines (7.5 degrees), each scored by how deep its 2-yard samples lie inside the
  creatures' own aggro reach up to 5 yards past the last strong hazard on it; a line touching a death, retreat, lethal or fled
  hazard on the way is unusable; the least exposed wins (the gap between two creatures), ties to the nearer exit. One fan gives
  the best 3 lines; each is planned on the navigation, checked again against the non-strong hazards, and held to its line
  (`RouteExposure`): a route the navigation bent deeper into the reaches than its straight line is taken only when no route keeps
  to its line. No allocation; one attempt's fan costs about 0.2 ms at 64 strong and 10 other hazards (1.3-1.45 ms when it ran the
  fan once per planned line, `PlayerbotStrongHazardCostTests`).
* **Walk.** The route is pinned (`EscapeRoute`, goal `Escape`), walked by the brain before any goal, for at most 20 seconds
  (`EscapeLifeMs`); `TryAdvance` exempts that one route from strong hazards only. Combat or a death ends it, and a death resets
  the clock.
* **Invariants.** Death, retreat, lethal and fled hazards always block, the escape route included. A completed quest return
  can cross a pack-only strong hazard if each mob is individually manageable and within the brain's three-level non-objective
  range; other ordinary routes still guard all strong hazards. One look at 64 strong creatures costs about 32 us and
allocates nothing (`PlayerbotStrongHazardCostTests`; 272-300 us and 47 KB before). Live w23 2026-10-11:
five level 1-3 bots without a home, revived at the Goldshire spirit healer, gave 58 bodies up in 38 minutes among level 5-6 Defias
Cutpurses and Mangy Wolves that the pull estimate rightly avoided (`reason=too-strong`), attacked on sight on their walks past them.

Every route a living bot is given (`PlayerbotNavigation.TryPlan` / `TryPlanToward`: quest givers and objectives, trainers, vendors,
quest travel, exploring, approaches) is checked, sampled every 2 yards, against the hazards it does not already stand in. One that
passes through a hazard is replaced by a way round it: a corner before it and one past it, its radius plus 5, 15 or 28 yards off to
either side, each leg on the navigation mesh (or stepped over the terrain where the mesh has none); with none, the route is refused.
Each leg of such a route keeps its own proof (`PlayerbotRoute.LegsNavigated`): the motion skips the floor and line-of-sight checks only
on the mesh's legs, never on a stepped one that follows a mesh leg (it used to take the first leg's proof for the whole route), nor on
a walk that starts more than `PlayerbotMotion.OffLegYards` (5) off the leg it heads along. The goal then does what it does with any route it
cannot get: a trainer destination tries the next trainer (`PlayerbotTrainerDestinations` blocks that spawn), a vendor, quest giver
or travel goal another one or nothing, and a goal that keeps failing is given up by the stall watch. A route being walked is
checked again at each step and given up when a hazard turned up on the rest of it. An errand route that starts and ends inside a
single death or retreat place mark is refused so a revived bot leaves the spot, whereas a combat chase (`PlayerbotRoute.Chase`)
to a chosen or engaged creature inside that single mark is not refused unless the place is a three-death loop zone (`CoversDeathLoop`;
`PlayerbotPlaceHazardRouteTests`, `PlayerbotDefensiveTargetTests`). A retreat walks where it must; a party bot
follows its master and has no hazards.

A ghost's revive spot keeps out of the creature hazards too (`PlayerbotRecovery.Hazards`): reclaiming at the body inside a lethal
creature's reach (plus 8) or a remembered killer's counts as camped, so the ghost revives at a clear spot inside the reclaim radius
or, with none, takes the spirit healer after the usual 60 seconds. The remembered places themselves are not held against the body
(it lies at the place of death).

At an exact spirit-healer landing (no clear spot elsewhere in a reclaim disc) a fight predicted to take all of the health the
resurrection gives back is unsafe even if the bot could win it at full health. The 2-second scan counts a creature's pack as every
visible creature whose aggro reach overlaps its own (`PlayerbotThreat.Overlaps`), not only those standing inside its reach: a bot
walking between two creatures is attacked by both. The pack's melee exposure is computed once per creature per scan.

Playerbot population placement checks the same kind of danger before it creates or moves a bot: the zone, level, graveyard and race
faction are tested together (`PopulationZoneAtlas.PickSafe`), so an unsafe zone is skipped instead of monopolizing the pass, and a
refused plan creates nothing.

## The death loop breaker

`PlayerbotStallWatch.RecordDeath` (claude/bot-ghost-wait) stays as the last resort: three deaths within 60 yards in 10 minutes are
a death loop, reported like a stall; the bot takes the spirit healer and sets aside the errand it was on (every trainer for a
training trip), and a creature entry present at two deaths is set aside for target choice. The brain records both from one place
(`PlayerbotBrain.RecordDeath`): the attackers it captures at each living update are the loop breaker's entries and the risk's
danger memory alike, and the errand it keeps is the one both set aside, the risk at the first death (within 30 seconds of the
errand), the loop breaker at the third. Target choice skips both the set-aside entries and the remembered creatures. With the
estimate and the hazards, the live replay of the 2026-10-08 rehearsal no longer reaches the loop breaker (0 death loops in 5 runs, against 27 reports in 5 runs without them).

Each newly recorded loop also leaves a 60-yard zone centred on its deaths, with their creature entries, for
`World:Playerbots:Risk:DeathLoopMemorySeconds` (2700 seconds by default). Target choice skips creatures in the zone and the navigation guard
refuses routes into it; a bot revived inside may route out. Another three-death loop in the same zone refreshes the zone for
twice the configured duration (capped at one day) and suspends the current quest through the existing bounded quest suspension.
The 45-minute default is 4.5 times the 10-minute death window: the w29 G3 triage found repeat loops at one spot across a
4.75-hour run, while ordinary danger memory lasted only five minutes. This is an ArcaneCore bot policy, not a vmangos rule.

## Resurrection sickness and corpse recovery

A spirit healer is a fallback when a bot cannot safely reclaim its body. For a bot at or above `Death.SicknessLevel`, recovery keeps a reachable corpse run after a repeat death or an out-of-home death. It does not keep a repeated-death body when a recorded killer was at least three levels above the bot or the bot is already sick. It also keeps a body judged too strong when there is a clear revival spot within reclaim range; a chosen revive spot is at least the threat's own aggro radius plus 10 yards away. A death loop still requests the healer to escape the loop. This follows vmangos `WorldSession::SendSpiritResurrect` (`src/game/Handlers/NPCHandler.cpp`), `Player::ResurrectPlayer` (`src/game/Objects/Player.cpp`) and `Creature::GetAttackDistance` (`src/game/Objects/Creature.cpp`): the healer applies sickness and a 25% durability loss; corpse reclaim does not. The extra revive clearance is an ArcaneCore bot safety margin.

While spell 15007 is active, the autonomous brain drops a pending pull and starts no new fight, even when risk scoring is disabled. Quest exchanges, town errands, repair trips and home travel can continue. A creature that attacks the bot is still defended against. Body strength is judged using the bot's health without the temporary sickness; sick fight damage does not lower its learned healthy damage estimate. Broken worn gear or aggregate durability below 20% makes an affordable repair vendor elsewhere on the map a town destination. The vendor interaction still uses `CMSG_REPAIR_ITEM` and the normal server pricing.

`PlayerbotSicknessStandDownTests`, `PlayerbotUnsafeBodyRecoveryTests` and `PlayerbotTownGoalsTests` cover these decisions with a manual world clock. The live 2026-10-11 death-loop measurements motivated this policy; they are not a post-change live acceptance result.

## Party bots

A bot in a real player's group (`PlayerbotPartyAI`) follows its master's lead: it never weighs a pull (it takes its master's
targets) and never retreats on its own judgement. With `PartyRetreatOnWipe` (default on) it retreats when the group is wiping:
the master is dead, or half or more of the members on its map are. The report reads `decision=follow-master`.

## Where it shows

* `.playerbot status` / `.playerbot list`: the risk line at the end of each bot's line (after `error=`).
* `.playerbot inspect`: a `BOTINSPECT` line with the same text.
* The brain's goal is `Retreat` while it runs, `Rest` while it recovers.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Risk/`: the model on snapshots (`PlayerbotRiskModelTests`); on the real world with the
manual clock and flat ground (`RiskTestWorld`): a pack of three passed for a lone creature, an elite three levels up avoided
unless a soloable quest objective, a lost fight retreated past the leash with the creatures evading and the bot recovering and
pulling again once it forgets, a nearly won fight finished, the class escapes cast for a mage, rogue, hunter, priest and warrior,
a party bot staying until its master falls, an errand set aside after a death (`PlayerbotRiskWorldTests`,
`PlayerbotRetreatEscapeTests`, `PlayerbotRiskPartyTests`), and the report in status and inspect (`PlayerbotRiskReportTests`);
the detours (`PlayerbotDetourTests`: a hazard detour's stepped legs keep their checks, a pit opened on one stops the bot; a pack
detour keeps out of a hostile standing on its first waypoint).
`ConfigReloadTests` checks every risk key is live and range-checked. The live snapshot replay
(`PlayerbotLiveSnapshotReplayTests`) prints each bot's deaths.

## Water

The brain leaves water when its breath runs out (20 seconds left of the breath timer, `PlayerbotWater.BreathRunningOut`) whether it
fights or not: the breath timer runs whatever the fight (`LiquidEnvironment`), and a bot that a murloc or a crocolisk held below the
surface once stood its ground until it drowned. It swims for the shore (`PlayerbotWater.FindExit`) with its attackers behind it and
fights on once out; while no way out is known yet (the search waits 2 seconds between tries) it fights on where it is. With breath
to spare a fight below the surface goes on; out of combat a still bot under water is taken out at once (`PlayerbotWaterTests`).

## Limits

* Only EventAI casts are seen; spells cast by C# creature AIs or scripts are not in the estimate, and a creature's heals or
  crowd control are not counted.
* A hazard is known only once seen (visibility range) or after a death; the first walk into a creature out of sight is not avoided,
  and a remembered creature is placed at its home until a look sees it (a wandering one may be elsewhere).
* A walk refused for hazards leaves the goal to choose again; there is no search for a long way round beyond the two corner offsets.
* Linked aggro of creature groups (formations, `creature_linking`) is not predicted, only assistance and aggro radii.
* Enrage is only seen in the observed rates, not predicted.
