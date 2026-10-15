# Playerbot opportunities: chests, nodes and rares

How an autonomous managed playerbot weighs the risk of a find (a chest, a locked chest it can open, a rare creature, the guards of a
gathering node) against what it is worth, and goes for it when the trade is good. Goals and movement are in `docs/areas/playbots.md`;
the risk arithmetic it reuses is in `docs/areas/playbots-risk.md`. Configuration: `World:Playerbots:Opportunities` (every key live
through `.reload config`). Chest seeking is ArcaneCore's own: vmangos bots do not look for chests.

## Pieces

| File (`src/ArcaneCore.World/Playerbots/Opportunities/`) | What it does |
|---|---|
| `PlayerbotOpportunityOptions.cs` | `World:Playerbots:Opportunities` and its ranges (`Validate`). |
| `PlayerbotOpportunityModel.cs` | The pure arithmetic: the loot estimate, the verdict (`Decide`), the death probability, the bot's temperament, and the ledger of finds set aside (`PlayerbotOpportunityLedger`). Unit tested on snapshots. |
| `PlayerbotOpportunities.cs` | The world side, one per brain: the scan, the facts of each find, the episode (clear the guards, walk, open, loot), the anomalies. World thread only. |

The brain offers the goal in one place, `PlayerbotBrain.TakeOpportunity`, just after the economy goals and before the optional
travel and grinding (`PlayerbotThinkProbe` phase `opportunities`). Every urgent goal comes first and is never preempted: combat,
loot, death recovery, the retreat and the health wait (`Risk.KeepWaiting`), town errands, crafting and the economy. A quest or grind
goal is interrupted only for a find that clears a higher bar (below).

## What counts as a find

A scan every 2 seconds (the first at once) over the objects the bot can see within `ScanRadiusYards`, nearest first, at most four
candidates weighed per scan. Outside the bot's home area (`PlayerbotHomeArea`) nothing is a find.

| Kind | Rule | Option |
|---|---|---|
| Chest | A `GAMEOBJECT_TYPE_CHEST` nobody has opened (ready, no loot window, no user, not in use), with a loot table, no quest or event gate, no linked trap, not a multi-open vein, within the level gate, with no lock or a lock that opens with a click. | `Chests` |
| Locked chest | The same, behind a key lock the bot holds the key to (`GameObjectLocks.CheckDirectUse`), or a Pick Lock lock where the bot knows Pick Lock and its Lockpicking skill reaches the lock (`GatheringRules.CanOpenLock`: a rogue). | `LockedChests` |
| Node | A herb or mining node. The gathering itself is `PlayerbotCrafting`'s; only the risk side is here (`NodeVeto`, wired to `PlayerbotCrafting.GatherVeto`): a guarded node is left alone only when the guards are a hard refusal (an elite far above, a guard five levels above, a pack larger than `MaxGuards`, a creature that kills outright, remembered danger), too dangerous for the bot as it is now, or the bot too hurt to face them. The node's worth never vetoes it (the bot gathers it anyway), so one ordinary same-level guard does not. An unguarded node is never vetoed. | `Nodes` |
| Rare | A living rare or rare-elite creature the bot can attack and nobody else has tagged. | `Rares` |

## The decision

All amounts are in **same-level kills** (one kill with its coin and trash: `KillCopper(level) = 20 + 0.55 x level^2` copper).

**Reward** = loot + the guards' experience - time cost.

* **Loot** is the loot template's expected value (`PlayerbotOpportunityModel.ExpectedItems`, the loot generator's own rules: groups,
  references, chance 0 ungrouped never drops, quest rows left out) plus the template's gold. Per item the larger of its vendor price
  over a kill's copper and its quality floor (grey 0, white 0.05, green 0.25, blue 0.8, epic 2, legendary 4 kills); plus, for an
  upgrade (`PlayerbotItemScore.UpgradeGain` over the bot's stat weights), `1.5 + 2.5 x` the share of the item's score it gains.
* **Guards' experience** is `PlayerbotRiskModel.Reward` of the pack, loot excluded.
* **Time cost** = (the walk to the find at `MoveSpeed` + the seconds to kill every guard one at a time at the bot's damage per
  second) x 0.04. A grinding bot earns about one kill every 25 seconds, so a second spent here is 0.04 kills not earned there.

**Risk** is the guard pack scored as one pull by `PlayerbotRiskModel.Assess`: the guards are the creatures whose aggro reach covers
the find (`PlayerbotRecovery.Threats` and `PlayerbotThreat.Reaches`) plus those their assistance call brings
(`CreatureMapSystem.AssistanceRadiusOf` / `CanAssist`), nearest first. The facts are the bot's, as in a pull: health, mana, ready
escapes, group, remembered danger. The loot value handed to `Assess` is the find's loot less the time cost.

**Engage** when all of these hold, otherwise refuse:

1. Net reward `>= MinValue` (x 1.5 when the find would interrupt a quest or grind goal; waived once the bot has begun the find, so it
   does not walk away from a half-cleared pack).
2. The risk is at most `Risk:Tolerance x RiskAppetite x personality x min(0.9, 0.4 + 0.2 x net)` (x 0.8 when interrupting): the same
   acceptable-risk curve as a pull, with the opportunity's own appetite on top.
3. The chance of dying `1 / (1 + e^(-7 (risk - 0.95)))` is at most `MaxDeathProbability` (4% at risk 0.5, 15% at 0.7, 50% at 0.95).

Hard refusals, whatever the reward and the appetite:

* more guards than `MaxGuards` (`pack-of-N`);
* a guard with a spell that kills outright (`lethal`), or one the bot remembers fleeing from or dying to (`remembered`);
* an elite three levels above the bot, six in a group (`elite-above`);
* any guard five or more levels above the bot, elite or not (`over-level`);
* a death probability above the cap (`death-risk`).

A bot that is recovering (a retreat, the health wait, hurt or drained so that a full-health bot would take the pull) gets the verdict
**wait**: it rests and looks again, with no backoff. A bot's **personality** is a stable factor from its guid, between
`1 - PersonalityVariance` and `1 + PersonalityVariance`, applied to the appetite, so a crowd of bots does not take the same finds.

### Defaults and why

| Key | Default | Range | Reasoning |
|---|---|---|---|
| `Enabled` | `true` | | Chests are free value an idle bot walks past; off restores the old behaviour exactly. |
| `ScanRadiusYards` | `45` | 10..150 | Near enough that the walk is a small share of a kill's time, far enough to see a chest across a camp. |
| `RiskAppetite` | `1` | 0.25..4 | Opportunities are judged exactly as a pull is at the default; 2 takes finds twice as dangerous, 0.5 half. |
| `MinValue` | `0.75` | 0..20 | A find must be worth most of a kill: below that the trip loses to grinding. |
| `MaxGuards` | `4` | 0..12 | Beyond four, assistance chains and add counts make the estimate unreliable. 0 takes unguarded finds only. |
| `MaxDeathProbability` | `0.15` | 0.01..0.5 | A chest is never worth a coin-flip with death; 15% is the risk model's own "not comfortable" line. Holds at any appetite. |
| `PersonalityVariance` | `0.15` | 0..0.5 | Enough to spread a crowd over different finds, too little to change what a bot is willing to die for. |
| `Chests`, `LockedChests`, `Nodes`, `Rares` | `true` | | Each kind on; turn one off to leave it alone. |

## The episode

Once a find is chosen the bot claims it (`PlayerbotClaims`, the table bots share to keep off one mob) and runs it as one episode of
at most three minutes:

1. **Clear.** The guards are pulled **one at a time**, nearest first: the controller hands the brain a creature to fight, the brain
   fights it, and when it dies the next is weighed afresh against the bot as it is now. The risk is asked again before each pull.
2. **Open.** With no guard left it walks to within 4 yards (`PlayerbotNavigation.PlanExact`), stops, and sends `CMSG_GAMEOBJ_USE`, or
   `CMSG_CAST_SPELL` for Pick Lock. A Pick Lock that fails with "try again" is cast again, up to 8 times.
3. **Loot.** The loot window (`SMSG_LOOT_RESPONSE`) is answered with the brain's loot queue: every item that fits, the coin, then
   release. A rare is fought like any pull; the brain loots the corpse.

## Giving up, and not retrying

| Event | What the bot does |
|---|---|
| Someone else opened it, or it is gone | Dropped at once, nothing recorded (if it respawns the scan finds it again). |
| Another bot holds the claim, or a real player stands within 12 yards of the chest | Left alone for 30 seconds, no strike. |
| A retreat or death during the episode (danger memory grew; a death also at once, `BotDied`), the verdict turns to refuse, no route to the chest, no loot window, the walk stops, timeout, or the next guard to pull is one the brain would not take as a target (unreachable, set aside as someone else's, outside the home area: the brain's `IsUntakeable`, anomaly `unreachable`) | Set aside, keyed by guid, for 60 s, doubling to 120 s, 240 s and at most 300 s (`PlayerbotOpportunityLedger.Defer`). |
| The find was taken | Not looked at again until it respawns, and never sooner than 60 s. |
| The bot is recovering | The goal becomes `Rest` (the health wait), no strike. |

A find set aside is not even weighed until its time is up, so a stuck chest never costs a scan. A find that is weighed and refused is set aside for 20 seconds (no strike), so the scan does not weigh it again every 2 seconds; a wait is looked at again at once.

A guard the controller handed to the brain as its target is taken back when the episode ends without a fight (refused, a player at the chest, no way to the guard): the brain drops it unless the bot is already in combat with it. While the bot fights the guards the brain keeps the claim on the find alive (`KeepClaim`).

## Anomalies

Area `opportunity` (`PlayerbotAnomalies.Report`), once per episode, nothing on success:

| Code | Meaning |
|---|---|
| `chest-no-loot` | The use was sent and no loot window came within 8 seconds. |
| `chest-empty` | The chest opened and held nothing. |
| `chest-pick-failed` | Pick Lock failed for a reason a second cast does not mend, or on every one of 8 casts. |
| `unreachable` | No route to the find, or the walk stopped. |

`PlayerbotBrain.Opportunities.Report` gives the find in progress, or the last verdict (`opportunity=refuse reason=over-level risk=... net=... death=...`), for tests and debugging.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Opportunities/`: the arithmetic on snapshots (`PlayerbotOpportunityModelTests`) and the
controller over a real world host with real chests, loot tables, locks, guards and bots (`PlayerbotOpportunityTests`).
