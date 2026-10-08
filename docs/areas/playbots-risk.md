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

| File (`src/ArcaneCore.World/Playerbots/Risk/`) | What it does |
|---|---|
| `PlayerbotRiskOptions.cs` | `World:Playerbots:Risk`: `Enabled`, `Tolerance`, `RetreatHealthPct`, `NearlyWonHealthPct`, `RecoverHealthPct`, `DangerMemorySeconds`, `PartyRetreatOnWipe`. |
| `PlayerbotRiskModel.cs` | The pure arithmetic: the pre-engagement verdict (`Assess`) and the in-combat verdict (`Judge`). Unit tested on snapshots. |
| `PlayerbotRisk.cs` | The world side, one per bot: gathers the facts of each candidate, chooses the target, watches the fight, starts and runs the retreat, the recovery wait. |
| `PlayerbotFightTracker.cs` | The observed damage rates of the current fight over a rolling 8 seconds of world time, where each enemy joined, and the bot's damage per second across fights. |
| `PlayerbotRetreat.cs` | The retreat itself and the trail of places the bot walked through (`PlayerbotBreadcrumbs`). |
| `PlayerbotEscapes.cs` | The 1.12 class escapes, in order (pure). |
| `PlayerbotDangerMemory.cs` | The creatures and places of retreats and deaths, for `DangerMemorySeconds`. |

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
template melee damage per second, or level x 1, x 2.5 for an elite, when the template has none), as a share of the bot's current
health. The bot's damage per second is what it dealt in its recent fights (an average), or `2 + 1.8 x level` before its first. A
mage, priest or warlock deals less as its mana runs out (to 35% of its damage at none: the wand); a hybrid less so. Remembered
danger near the target or against its kind multiplies the risk by `1 + 0.5 x hits`; a ready class escape takes 10% off. A lone
same-level creature is about 0.4 to 0.5; three of them about 2.5.

**Reward** is the experience of the kills relative to a same-level kill (`XP::Gain`: gray gives nothing, an elite twice), plus 2
for a quest objective and the loot value (a humanoid 0.2, a beast 0.1, or 0.3 for a skinner).

The **decision**:

| Decision | When | What the bot does |
|---|---|---|
| `engage` | risk at most `Tolerance x min(0.9, 0.4 + 0.2 x reward)` | Pulls it. |
| `avoid` (`elite-above`) | an elite three or more levels above the bot that is not a quest objective | Next candidate. |
| `avoid` (`remembered`) | the bot fled from or died to this very creature within `DangerMemorySeconds` | Next candidate (it is not even listed). |
| `rest` (`low-health`, `low-mana`) | it would be an `engage` at full health and mana | When nothing else is worth it, the bot waits (eating or drinking when it carries food or water) until `RecoverHealthPct`, at most 90 s. |
| `detour` (`path-adds-N`) | the fight is acceptable without the creatures on the way | Walks via a waypoint 15, 25 or 35 yards to either side of the straight line, chosen so the route and the last leg stay 2 yards outside every path creature's reach; `avoid` when none does. |
| `avoid` (`pack-of-N`, `too-strong`, `elite`) | otherwise | Next candidate; with none, the brain's destinations or exploring as before. |

The last verdict is reported: `risk=2.47 reward=3.30 decision=avoid reason=pack-of-3 target=6`.

## In a fight

Each think in combat the brain asks `PlayerbotRisk.ObserveFight`. The tracker samples the bot's health and every enemy's
health (the target, its attackers and anything with the bot on its threat list, not evading). Over the window:
damage taken per second (net of heals) and damage dealt per second (an enemy counts only while seen in two samples, so an add
that just joined is not "damage"; one that died took its remaining health). Under 2 seconds of samples the estimate's priors
stand. Then `time to kill = enemies' health / damage dealt`, `time to die = bot health / damage taken`.

* A single enemy at or below `NearlyWonHealthPct` (20%) is finished unless the bot would die in less than half the time it needs.
* Winning (`time to die x Tolerance >= time to kill`): fight on.
* Losing: retreat at `RetreatHealthPct` (35%) or when the bot would die within 4 seconds; until then fight on (`behind`).

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
   yards further, up to 8 times.
3. Ends `safe` when nothing threatens the bot (the creatures evaded), `cornered` after 60 seconds or 8 extensions (it then fights
   that fight out), or `died`.

After a safe retreat the bot waits to `RecoverHealthPct` (eating or drinking when it can) before it pulls again; the creatures
it fled from and the place are remembered for `DangerMemorySeconds`. The report: `retreat reason=losing-to-3 escapes=Frost_Nova+Blink`.

A death also goes into the memory (the last enemies seen and the place), and a bot killed within 30 seconds of walking an errand
(a trainer, vendor or quest destination) sets that errand aside for 10 minutes (`PlayerbotSuspensions`) instead of walking the
same way into the same creatures after its revive.

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
`PlayerbotRetreatEscapeTests`, `PlayerbotRiskPartyTests`), and the report in status and inspect (`PlayerbotRiskReportTests`).
`ConfigReloadTests` checks every risk key is live and range-checked. The live snapshot replay
(`PlayerbotLiveSnapshotReplayTests`) prints each bot's deaths.

## Limits

* Creature spells are not in the estimate (only melee damage): a creature that kills outright (the Scourge invasion's Skeletal
  Soldier, Scourge Strike) or casts hard is under-rated until the bot has died to it once (then the memory and the errand rule
  keep it away).
* Travel that is not an approach to a target (to a trainer, vendor or quest giver) does not avoid creatures on the way.
* Linked aggro of creature groups (formations, `creature_linking`) is not predicted, only assistance and aggro radii.
* Enrage is only seen in the observed rates, not predicted.
