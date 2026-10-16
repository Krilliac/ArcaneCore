# Playerbots in groups: group content, formation, doing it together

How autonomous managed playerbots group up for content one bot cannot do: elite, dungeon and raid quests, objectives inside
instances, and quest targets the risk estimate rates too strong alone. The bot itself (goals, movement, the brain) is in
`docs/areas/playbots.md`; its risk model in `docs/areas/playbots-risk.md`; dungeon movement, area triggers and death recovery in
`docs/areas/playbots-dungeons.md`. A bot in a real player's group (the party AI, vmangos `PartyBotAI`) is unchanged and is not part
of this: the coordinator never takes a bot that has a real player for master.

References: vmangos `Group::ConvertToRaid`, `Group::AddLeaderInvite` / `AddInvite` and `GroupHandler.cpp` (invite, accept, loot
method, subgroups), `Player::KilledMonster` (the raid check of quest kill credit), `PartyBotAI` (follow, assist, `IsHealerClass`,
`ShouldAutoRevive`), `CombatBotBaseAI::AutoAssignRole`; the mangoszero playerbot module's actions for the client-side shape of the
answers. Neither reference groups bots by itself (vmangos party bots need a player; the mangoszero module's `lfg` strategy joins the
server's LFG queue): the matching is ArcaneCore's own.

## Pieces

| File (`src/ArcaneCore.World/Playerbots/Groups/`) | What it does |
|---|---|
| `PlayerbotGroupOptions.cs` | `World:Playerbots:Groups` (every key live through `.reload config`). |
| `PlayerbotGroupContent.cs` | The pure rules: which content needs a group and how big, who can tank or heal, the matching (`Match`). Unit tested. |
| `PlayerbotGroupCoordinator.cs` | The world feature: needs, matching, formation through real packets, the group's state machine, the GM report. |
| `PlayerbotGroupAI.cs` | One per member: what a bot does while it is in a bot-led group, instead of the brain. |
| `PlayerbotRun*.cs`, `PlayerbotGroupCoordinator.Runs.cs` | Dungeon and raid runs: catalogue, options, state machine, route, matching and the entry waiver (below, "Dungeon and raid runs"). |

Changes outside the folder, all additive: `PlayerbotRiskModel.NeededGroupSize` and `PlayerbotEngagementFacts.GroupSize`;
`PlayerbotRisk.GroupSignal`; `PlayerbotQuestGoals.HeldForGroup` (and a suspended quest or entry is no longer the creature the brain
hunts); `PlayerbotBrain.GroupHeld` / `ResumeAfterGroup`; `PlayerbotCombatSpells.GroupRole`; two hooks on the party AI
(`AcceptsBotGroup` for a bot-led group's invitation, `GroupLootVote` for its rolls); `ManagedPlayerbotFeature` calls the coordinator
every tick and lets the group AI drive a member; the login gate uses `PlayerbotMapPolicy.MayStayOnMap` (below);
`PlayerbotGoalKind.Group`.

## Recognising group content

Every 2 seconds each free bot (autonomous, not in a real player's group, in no group) is looked over, its quests first:

| What | Group goal |
|---|---|
| An unfinished creature objective of a quest with `quest_template.Type` 1 (elite: the client's "Group") | the quest's `SuggestedPlayers` (2 to 5), else 3; at the objective's nearest spawn on the bot's map |
| Type 81 (dungeon) | 5, or `SuggestedPlayers` when 2 to 5 |
| Type 62 (raid) | a raid of `SuggestedPlayers` when above 5, else 10 |
| Any other quest with `SuggestedPlayers` above 1 | that many (at most 5) |
| An objective that spawns only inside instances (`map_template` dungeon or raid) | that instance, reached through an entrance trigger on the bot's continent (`areatrigger_teleport` to it); the map's player limit (5 for a dungeon), or the quest's own size; the trigger's required level |
| A quest item dropped only by creatures that spawn inside instances (`creature_loot_template` through `creature_loot_info`, and the `reference_loot_template` tables its negative `mincountOrRef` rows name, nested; `PlayerbotGroupContent.DropIndex`) | as the instance row above, at the creature that drops it; done when each member has the quest's count of the item |
| A quest objective the risk estimate passed over alone (`PlayerbotRiskModel.NeededGroupSize`) | the smallest group (2 to 5) whose estimate would take it |

Type 41 is PvP, not elite (QuestInfo.dbc; in the live world database "Wanted: Hogger", 176, is Type 1 and the Alterac Valley quests
are 41). Only a raid quest is done by a raid: a raid group's kills credit raid quests alone (`QuestNpcServices.CreditCreature`,
vmangos `Player::KilledMonster`), so an elite quest suggesting more than five is done by a party of five.

**The risk estimate's group size.** `PlayerbotEngagementFacts.GroupSize` (1 alone): a group of N deals N times the bot's damage and
spreads what it takes over N players' health (the healer's heals stand in for its own damage), so the risk falls roughly with N
squared, and an elite may be up to three levels higher than alone (`GroupEliteLevelMargin`). `NeededGroupSize` judges the group at
full health and mana. A creature that kills outright, or one the bot remembers fleeing from, has no group size: it is avoided as
before. When a quest objective is avoided for strength and a group of 2 to 5 would take it, the verdict reads
`decision=avoid reason=group-of-N` and the risk keeps the signal for the coordinator.

**Held for the group.** Once a bot needs a group, the objective is held (`PlayerbotBrain.GroupHeld`): the quest goals do not choose
it as the creature to hunt, so the brain goes on with everything else it can do alone (other quests, grinding, trainers, vendors)
while it waits.

## Formation

Bots that want the same thing are matched: the same objective creature for open-world content (two quests that kill the same elite
overlap), the same instance for a dungeon or raid; the same team; on the meeting point's map within 1500 yards of it; at least the
entrance's level. `PlayerbotGroupContent.Match`:

* levels: every member within `LevelRange` of every other (each candidate's level is tried as the floor, lowest first);
* roles (only for three or more): `MinTank` tanks (warrior, paladin, druid: those whose talents make them tanks first, then any
  warrior, then the rest of the classes that can; highest level and gear first), then `MinHealer` healers (priest, paladin,
  shaman, druid), then the rest by who waited longest and level closeness; a raid needs one more tank per ten and a healer per five;
* the leader is the best tank (level, then the item levels it wears), else the bot that waited longest (the quest's owner);
* a raid is spread over subgroups of five, tanks and healers first, one of each per subgroup.

The leader invites through `CMSG_GROUP_INVITE`; each member's party intake answers `CMSG_GROUP_ACCEPT` (`AcceptsBotGroup`: the
inviter leads a forming group that has this bot), so observers see an ordinary group form. Until the first acceptance creates the group
the leader is an invitee itself and a second invitation would be dropped (vmangos `AddLeaderInvite`), so it invites one bot first and
the rest after. A goal for more than five: after the first acceptance the leader converts (`CMSG_GROUP_RAID_CONVERT`, vmangos needs
two members), invites the rest and moves members into their subgroups (`CMSG_GROUP_CHANGE_SUB_GROUP`). The loot is round robin
(`CMSG_LOOT_METHOD`, uncommon threshold). Invitations unanswered for 30 seconds give the group up (the bots may be matched again).

**Without partners.** A bot that found nobody within `FormationTimeoutSeconds` sets the goal aside for 10 minutes (it is not waited
for again meanwhile) and the hold ends: the brain may try the objective alone, where the risk estimate decides as before.

**Real players** (`InvitePlayers`, off by default): a group one member or role short may take a nearby real player (100 yards, same
team, ungrouped, of a fitting level) through the same ordinary invitation. A real player never leads a bot group and is never driven;
one who declines or does not answer within 30 seconds is not asked again for 10 minutes, and the group is given up. A real player's
own party with a bot (`.playerbot invite`, the party AI) is not a bot group and is left alone: that bot is not free.

## Doing it together

`PlayerbotGroupAI` drives each member while the group lives (the brain is not updated; its quests, suspensions and memories stay).
The group's state (`PlayerbotGroupState`, in `.playerbot groups`):

| State | Leader | Members |
|---|---|---|
| `forming` | holds | follow the leader; their intake accepts |
| `gathering` | holds until every living member is within 20 yards | walk to it |
| `travelling` | walks to the approach point (30 yards short of an open-world objective's spawn, 15 short of an entrance), keeping out of known hazards, waiting while a member lags more than 30 yards or rests | follow at 2-5 yards (vmangos `MoveFollow`) |
| `entering` | walks into the entrance trigger (`PlayerbotAreaTriggers` reports it like a client; the coordinator gave every member the trigger consent) | when the leader is inside, walk into the same trigger: the group's instance (the group is bound to it) |
| `engaging` | pulls the objective when it is in sight within 60 yards, else the nearest creature within 20 yards that would attack the group (cleared first), else walks towards the objective's spawn; waits for laggards, resting members and its own recovery | fight by role (below) |
| `wiped` | everyone alive in combat retreats (the solo retreat: back along the trail, past the creatures' leash) | same |
| `regrouping` | the dead are resurrected or run back; everyone rests | same |
| `leaving` | walks to the instance's exit trigger, then waits outside | follow; outside, wait |
| `disbanding` | each member sends `CMSG_GROUP_DISBAND` (the client's leave) and the brain takes over | |

**Fighting by role** (the group role overrides the rotation's talent role, `PlayerbotCombatSpells.GroupRole`, so a warrior without
Shield Slam still tanks a group without another tank): every member first answers a creature attacking a member within 50 yards; the
tank especially one attacking someone else, whose rotation then taunts it (`NeedsTaunt`: Taunt, Growl, Mocking Blow); the others then
assist the leader's victim. The healer's rotation heals the party (its group heal targets, vmangos `SelectHealTarget`), and fights when
nobody needs healing. Positioning is the brain's (`PlayerbotBrain.DecidePosition`: casters and healers at range, melee at melee, facing
the victim before a swing), and so is its fallback: a member holding at its range with nothing to cast while nobody fights the target
closes into melee after `PlayerbotBrain.RangedIdleLimitMs` (8 s). Without it a healer leading a pair (a paladin or priest in the group's
healer role keeps caster range) that has no attack spell it can cast never pulled, and the members, who assist the leader's victim, stood
with it (replays 2026-10-11 on the real terrain, quest 9 "The Killing Fields": 260-840 s in `engaging`).

**Between fights**: a member below 50% health or mana eats or drinks (the leader does not pull meanwhile); a healer resurrects a dead
member it can see (Resurrection, Redemption, Ancestral Spirit, and Rebirth when it carries the seed, through the ordinary cast; the dead
member's party intake accepts a group member's offer). The real rows (classic-db 2006, 7328, 2008, 20484) name no implicit target and the
corpse flag (`Targets` 0x8000) without `AllowDeadTarget`, so the healer casts like a 1.12 client: at the dead unit while its body is
unreleased, at the corpse (CMSG_CAST_SPELL with TARGET_FLAG_CORPSE and the corpse guid) once it released, walking to the body either way.
The server checks such a cast as vmangos does: a unit target is range and line-of-sight checked whatever the implicit target
(`Spell::CheckRange`), a corpse must exist and be in sight (`Spell::CheckCast` :5780), and a client's cast of a corpse-flag spell without a
unit or corpse is refused (`ValidateExplicitTargetMask`); a member loots the corpses whose round-robin turn is its own, and any corpse holding a quest item it needs (vmangos shows a
quest drop to every member who needs it, whoever holds the corpse). A dead member waits (unreleased) while a living
member could resurrect it, at most 60 seconds, then releases and runs back (`PlayerbotRecovery`; a ghost whose body lies inside walks
into the entrance and is revived there). A leader that died is waited for where the group stands.

**Sharing the goal's quests.** Once grouped (gathering, travelling or engaging, out of a fight) a bot member holding a sharable quest of
the goal (the goal's quest, or one asking for its creature or item) that another member lacks and could take pushes it to the party
(CMSG_PUSHQUESTTOPARTY, the client's "Share Quest") when every such member stands within the share distance (vmangos
`QUEST_SHARE_DISTANCE`, 14 yards); each quest once per group, noted in the events (`Tank shares quest 990701 with Mage`). The server
applies vmangos `HandlePushQuestToParty` (sharable flag, the sharer on the quest, the receiver able to take it, the log not full, one
offer at a time). A bot's party intake (bot groups and a real player's party alike) answers the server's pending offer as a client's quest
window does: accept (CMSG_QUESTGIVER_ACCEPT_QUEST with the sharer's guid) when the sharer is in its group and it can take a quest the
server settles, else decline (MSG_QUEST_PUSH_RESULT).

**Loot rolls** (items at or above the threshold): need when the item is an upgrade the bot can wear (`PlayerbotItemScore.UpgradeGain`
with its build's weights), greed otherwise (`GroupLootVote`).

**Done** when every member that has a quest asking for the objective has its count (all have the credit: the server shares kill
credit with members in reward range), or, without a quest, when the objective creature the group engaged died. Inside an instance the
group then walks out, then disbands. The brains turn the quests in.

**Failure**: two wipes, 30 minutes on one goal, a step (gather, travel, enter, leave, regroup) taking more than 5 minutes, two minutes at
the objective's spawn without it in sight, too few members left, or the leader gone. The goal is then set aside for every member for
10 minutes (its quest and objective in the brain's suspensions, so the brain does not walk into it alone either), and the group walks
out of an instance before it disbands. A group that cannot walk out in time is brought to the entrance by the teleport service.

**Stalls.** The brains' stall watch does not run while a group drives the bots, so a group has its own (`PlayerbotGroupStallWatch`,
checked by the coordinator every tick): when no bot member moved 10 yards or changed map, was in combat, ate, drank or looted, or took a
new target (dropping a target and taking the same one again is not progress) for `World:Playerbots:StallSeconds` (120), the leader gives
its target up (set aside for the group for 10 minutes; one that attacks a member is still fought) and chooses again, and one stall line is
logged on the leader in the brains' shape (`Playerbot <id> (<name>) stalled 120s: goal=Group target=<entry> quest=<id> at x,y,z map m;
it gives up that goal`), which bot QA parses as a stall with goal `Group`. A further bound without progress gives the goal up (reason
`stalled`, set aside for every member) and dissolves the group. The coordinator's warning and the events name the leader's last step
(`PlayerbotGroupAI.Step`: `lead:hold-not-ready`, `fight:114:hold:20yd`, ...). Never a group with a real player in it, a stranded bot
walking out, or the forming, wiped, leaving and disbanding states (leaving has its own bound: the step timeout, then the stuck members
are brought out), nor while the leader waits at the objective's spawn (that has its own two minutes). Outside engaging the stall line
names target 0. The leader's own new target right after a re-target (within 5 s) keeps the strike, so a group with several unreachable
targets still dissolves on the second bound.

## Few bots and stray cases

* With few bots most needs find no partners: the goal is set aside after the timeout and the bot keeps soloing what it can.
* `MaxGroups` caps the groups; `Enabled` off forms no new group (running ones finish).
  When `MaxGroups` is above 1, one slot is kept for dungeon runs (`MaxGroups - 1` for quest, instance-item and risk groups while runs can start). A failed goal is set aside 10 minutes, doubling per repeated failure for the same bot up to 2 h. Instance groups gather and walk on a distance budget (`distance / MoveSpeed * 3 + 120 s`, 300 s minimum, `TravelTimeoutMinutes` cap); a gather or travel timeout logs every bot member's (never a real player's) map, position and distances. An item objective is an instance goal only when no creature that drops it spawns outside an instance.
* A bot that logs in inside an instance (a world restart while it was in one) is let in: the login gate is now
  `PlayerbotMapPolicy.MayStayOnMap` (its map is allowed, or it is the dungeon it is in), as `playbots-dungeons.md` planned. A free bot
  inside an instance without a group walks out through the exit (a group of one in `leaving`): the brain has no way out.
* A free bot in a server group whose other members are all bots online, and that no bot group owns (the coordinator's groups are not
  restored after a restart, the server's are), leaves it. A group with an offline member is left alone: that may be a real player
  the party AI waits for.
* A member that is stopped, scripted, or leaves the server group leaves the bot group; the leader leaving ends the group.

## Dungeon and raid runs

Besides group content found through quests, free bots start dungeon and raid **runs** on their own: they group up for an instance
that fits their level, travel to its entrance, go in and kill the bosses of one wing in order, and walk out. A run is a bot group
(`BotGroup.Run` is a `PlayerbotRunMachine`; same formation, same members, same group AI) whose goal is `Dungeon` or `Raid`
(`PlayerbotGroupGoalKind`) with source `run`. Neither reference core runs dungeons with bots (vmangos party bots need a player; the
mangoszero module's `lfg` joins the server queue), so the rules below are ArcaneCore's own; where they read data they say which.
Files: `PlayerbotRunCatalogue.cs` (what can be run), `PlayerbotRunOptions.cs` (`World:Playerbots:Groups:DungeonRuns`),
`PlayerbotRunMachine.cs` (the state), `PlayerbotRunRoute.cs` (which trash to pull), `PlayerbotGroupCoordinator.Runs.cs` (matching,
the run's life, the waiver).

**Catalogue** (`PlayerbotRunCatalogue.Build`, rebuilt when the maps, area triggers, creatures or encounters change; what it leaves
out is logged once per build with its reason). Sources, all from the world database:

| Table | Gives |
|---|---|
| `map_template` | which maps are dungeons (`MapType` instance) or raids, their player limit, their name |
| `instance_encounter` | the bosses and their order (below) |
| `creature` / `creature_template` | where a boss stands (its first static spawn on the map) and its level |
| `areatrigger_teleport` + `areatrigger_template` | the entrance: the trigger on a continent whose teleport leads in (the one landing nearest the first boss), and its required level |

`instance_encounter` is the 1.12.1 client's `DungeonEncounter.dbc` joined to the server's `instance_encounters` (cmangos classic-db
z2815 `instance_dungeon_encounters`, `Difficulty` 0, joined on `entry = Id`): map, order index, encounter id, credit type (0: the death
of creature `CreditEntry`; 1: a spell cast on a player, e.g. the Four Horsemen), credit entry, `lastEncounterDungeon`, name. It is
imported by `ContentImporterCli` (`instance_encounter` in the refresh list, created by `InstanceEncounterDataModule`, schema step 52)
and read at startup (`IInstanceEncounterStore`) and by `.reload instance_encounter` (not part of `.reload all`; a run already under way
keeps the plan it was formed with). An empty or unreadable table is logged and means no runs, except that a reload honours
`HotReload:EmptyTables = KeepLoaded` and keeps the loaded encounters.

* **Bosses by encounter, not by rank.** A boss is an encounter whose credit is the death of a creature with a static spawn on the
  map. Creature `rank` is not used: in this world database no five-player map but Blackrock Spire (229) has a rank-3 (world boss)
  spawn, and Deadmines' bosses are rank 1 and 2. Encounters credited by a spell, or by a creature that is only summoned or event-spawned
  (Sneed, Nefarian, Ragnaros, Majordomo; spawns that only a spawn entry table resolves count too), are **scripted**: reported
  (`scripted=N` in `.playerbot groups`), never routed, never waited for.
* **Wings.** An encounter with `lastEncounterDungeon` ends a wing (Scarlet Monastery: four, Blackrock Spire: two); encounters after the
  last marker join the last wing; a map with no marker is one wing. A wing is the unit of a run (`map:segment`, `189:2` is the third
  Scarlet Monastery wing). Its marked boss is routed last, the others in order index.
* **Exclusions** (`Exclusions`, logged): `no-encounter-data`, `no-spawned-boss`, `map-not-instance`, and `no-entrance-on-continent`.
  Blackwing Lair is excluded by the last: its only area trigger (3726) lies inside Blackrock Spire (229), not on a continent, so there is
  no entrance a group can walk to; it is not a special case, any instance in that position is left out.
* **Group size.** A dungeon takes five (its player limit capped at 5: classic-db gives most five-player maps a limit of 10); a raid
  takes its player limit (10, 20 or 40; ten when none).
* **Level band.** The bot levels matched for a wing: from the higher of the entrance's required level and the weakest boss's minimum
  level minus `BandBelowBoss` (3), to the strongest boss's maximum level plus `BandAboveBoss` (3). `LevelBandOverrides` replace both ends
  (`"36": "15-22"` for a map, `"189:2": "30-38"` for a wing; the wing's entry wins). Members are also within `LevelRange` of each other.

**Two separate raid switches.** `World:Playerbots:Groups:RaidsEnabled` and `World:Playerbots:Groups:DungeonRuns:Raids:Enabled` are
independent: `StartRuns` checks only the second, while the first gates only quest and instance goals (raid quests and raid instances
found through quests). Turning one off does not turn the other off.

**Starting a run** (`StartRuns`, at the end of every 2-second scan, only when `World:Playerbots:Groups:Enabled`). When `World:Playerbots:Groups:DungeonRuns:Enabled` and a slot is
free (`MaxConcurrentRuns` dungeon runs, and for raids `World:Playerbots:Groups:DungeonRuns:Raids:Enabled` with `MaxConcurrentRaids`), and the start roll succeeds
(`StartChancePerMinute`, as the chance for one scan), the free bots (autonomous, alive, out of combat, on a continent, in no group, not
waiting, not in a real player's group) are filtered per wing: on the entrance's continent within `MaxTravelYards` of the entrance, in
the wing's band, the wing not set aside by the bot (a run given up for lack of a path or entrance: one hour; any other failure: 10
minutes), and passing the **prerequisites**. The wing with the most eligible bots that can fill the roles wins (then the lowest map and
wing); bots of one team only (the larger team first). `.playerbot groups` events say why a wing is short, once per change: `run
Onyxia's Lair: 10 bots in the level band lack the item requirement` (also `quest`, `condition`), `... 1 bots are locked to the raid`.

**Roles** (`PlayerbotGroupContent.RunRoleNeeds`, then `Match`): a dungeon party takes `MinTank` tanks and `MinHealer` healers (none for
fewer than three); a raid takes tanks `ceil(size * TanksPerTen / 10)` and healers `ceil(size * HealerPercent / 100)`, so 10, 20 and 40
players take 1, 2 and 4 tanks and 3, 5 and 10 healers at the defaults (1 and 25). This is vanilla's customary make-up, not a reference
core rule. A raid is spread over subgroups of five, tanks and healers first. A wing without enough tank-capable or healer-capable bots
is not formed.

**Prerequisites.** A bot is eligible only when it passes the very check a player walking into the entrance passes
(`AreaTriggerRequirements.Check`, the lock-status part of `HandleAreaTriggerOpcode`: the entrance level, the key item, the attunement
quest of `RequiredQuestDone`, the conditions-table reference; a game master passes). The whole raid must pass: one bot short removes
that bot, and the raid forms only if the rest still fill the size and roles. A raid never takes a bot with a **permanent bind** to the
raid map: it would enter its own save, not the group's (`InstanceManager.GetBoundSaveForSelfOrGroup`); a temporary bind does not exclude.
Dungeons have no bind exclusion.

**`World:Playerbots:Groups:DungeonRuns:Raids:SkipPrerequisites`** (off by default, live): with it on, the key item, the attunement quest and the conditions-table
reference are skipped, both when matching and at the door. At the door the server asks every `IAreaTriggerWaiver`
(`TeleportHandlers.HandleAreaTrigger`) and `PlayerbotGroupCoordinator.Waives` answers `Item | Quest | Condition` only when **all** of
these hold: `World:Playerbots:Groups:DungeonRuns:Enabled`, `World:Playerbots:Groups:DungeonRuns:Raids:Enabled` and `World:Playerbots:Groups:DungeonRuns:Raids:SkipPrerequisites` are on; the player's session is a managed bot's; the player
is a joined bot member of a group on a **raid** run; and the teleport leads into that run's raid map. A real player, a bot outside a
run, a dungeon run's bot, a member of a raid run at another teleport (another raid, a dungeon), and any call with the switch off get
`None` and are checked as always. The **level is never waived**, and a gate that is neither the quest nor the conditions table (an
instance script's block) is never waived. The waiver reads the options at every call, so switching it off takes effect at once even for
a raid already walking in.

**Travel policy.** `PlayerbotGroupCoordinator.TravelPolicyFor(botId)` is the seam for the follow-pathing lane's catch-up policy (`null`
outside a run): `FightThrough` (the default; path and engage what stands in the way, never teleport a follower) or `Teleport` when
`TeleportToEntrance` is on, which moves the living members of the group on the entrance's continent to the approach point once when
travelling begins (`TeleportService`; nothing else in a run teleports). A catch-up policy that offers a teleport must honour
`FightThrough` for a run. The group walks (`travelling`) fighting what blocks the leader (`FindObstacle`), 15 yards short of the entrance.

**The state machine** (`PlayerbotRunMachine`, pure: events in, step out; `.playerbot groups` shows `step=`):

| Step | Meaning | Next |
|---|---|---|
| `travel` | gather and walk to the entrance | `Arrived` -> `enter` |
| `enter` | through the entrance trigger | `AllInside` -> `clear` (or `leave` when every boss is dead) |
| `clear` | inside, pulling the trash on the way to the next boss | `BossEngaged` -> `boss`; a boss found dead is counted |
| `boss` | fighting the current boss | `BossDead` -> `clear`, or `leave` after the last |
| `wiped` | half the group dead or more while someone fights (or all dead): survivors retreat, the dead run back or are resurrected | `Regrouped` -> `retry` |
| `retry` | back at the entrance and in again; killed bosses stay killed, the run resumes at the first boss alive | `Arrived` -> `enter` |
| `leave` | every boss dead: the group waits up to 20 seconds for the loot, then walks out through the exit trigger | `Left` -> `complete` |
| `complete` / `giveup` | final; the machine ignores every later event | |

A **wipe** is counted once per wipe (`MaxWipes`: 3 for a dungeon, 5 for a raid; live, also for runs under way); at that many the run is
given up (`wiped`), so a run gets `MaxWipes` - 1 retries (`Wipes >= MaxWipes` gives up). A wipe while travelling counts too: the
`travel` step checks for one (`CheckWipe`, signal `PlayerbotRunSignal.Wipe`) and the machine counts a wipe in any step except `wiped`. The run is also given up (reason in the events and the machine's `Reason`) for: `travel-timeout` or
`enter-refused-or-timeout` (`TravelTimeoutMinutes`, 30; the enter step 5 minutes), `run-timeout` (`MaxRunMinutes`: 90 for a dungeon, 240
for a raid, from formation), `no-path-to-entrance` / `no-path-to-boss` (the leader could not plan a way for 30 seconds), `too-few` (fewer
than two bots left), `leader-gone`, or `leader-stopped` (the leader's bot stopped: `Forget`). A follower that stops leaves the group and
the run goes on. A given-up run walks out of the instance first, then disbands; every bot still in it sets the wing aside. Totals:
`completed` and `given-up` in the runs line. A run that ends for a content or code reason (`no-path-*`, a real step timeout, `objective-missing`, `stalled`, `wiped`) is also reported to bot QA as a `run failed` finding (docs/areas/playbots-qa.md; operator and lifecycle ends such as `leader-stopped`, `leader-left`, `leader-gone`, `too-few`, `invites-unanswered` and `disbanded` are not): its instance map, wing, the stage it failed at, the boss it was heading for and the reason, never the group or a bot, so repeated give-ups at one stage aggregate into one issue. Metric: `arcanecore_playerbot_runs_total{outcome}` with `completed`, `given_up` and `timeout` (a reason ending in `timeout`: `gather-timeout`, `travel-timeout`, `enter-refused-or-timeout`, `regroup-timeout`, `run-timeout`; every other end, operator stops included, is `given_up`) (docs/ops/metrics.md).

After a completed run has left the instance, its leader sends `CMSG_RESET_INSTANCES` while the group still exists, then every bot
disbands. This is observability: the leader asks the way a client would and the coordinator records each `SMSG_INSTANCE_RESET` or
`SMSG_INSTANCE_RESET_FAILED` reply, with the map and the failure reason (offline member or occupied instance), so a refusal is visible.
A refusal does not hold the group together, and the request is skipped when any member of the group is not a managed bot, because a
reset unbinds every player on the save. The fresh instance for the next run comes from the disband itself, which resets the group's
instances (`InstanceManager.OnGroupDisbanding`, vmangos `Group::Disband`); a refused request means that reset skips the same save.
The Deadmines scenario test runs the same bots twice and checks a new instance with living boss and trash; it uses synthetic content,
not an imported-world or client replay.

**Trash** (`PlayerbotRunRoute.NextPull`, pure): the leader plans a path to the current boss every 3 seconds; a hostile, living creature
not already in combat that stands within 15 yards of a point still ahead on that path is in the way, because walking
past it would pull it anyway; a pack is the creatures within 10 yards of its first member (how classic-db places its groups). The leader
pulls one pack at a time, the one nearest the start of the path (ties: nearest to the path, then lowest guid), the boss last once the
way is clear and it is within 60 yards.

Three rules about bosses (`PlayerbotRunFixesTests`):

* **No boss is ever pulled as trash.** `NextPull` leaves out every boss of the wing (`run.Plan.BossEntries`), not only the one being
  worked on; a later boss on the route is walked past, while the trash around it is still pulled.
* **An early kill is credited.** A later boss that dies early (fought on the way) is credited to the run: `PlayerbotGroupAI.NoteBossFight`
  notes the fight and `AdvanceRun` applies `BossDead`, whatever boss the route has reached.
* **A boss that patrols is not done.** For a run's boss, `ObjectiveMissing` counts it done only when no living creature of that entry is
  on the map (`FindLivingOnMap`, `GridLoadedAt`), so one that walked away from its spawn is not counted done; a dead or absent one is.
  A quest group's objective is still judged by sight (the rule under "Failure" above).

**`.playerbot groups` fields.** The summary adds `runs enabled|disabled: dungeon 1/2 raid 0/1 completed 3 given-up 1; 41 wings known, 9
left out.` A run's group line adds `run=The Deadmines(36):0 boss=2/6(Gilnid) scripted=1 wipes=1/3 step=clear pulls-left=2`: the map and
wing, the boss being worked on out of those routed (and its name), the scripted encounters, wipes against the limit, the step, and the
packs left in sight along the path. A bot's status line ends with `group=3:tank:engaging:leader:run`.

A live run needs the world database refreshed from a classic-db dump (the `instance_encounter` table) and terrain; the unit and world
tests below run on real classic-db rows copied into `RunFixtures`, not on a live replay.

## Leader handoff

A real player who brings managed bots into a dungeon or raid and passes the lead to one of them (the ordinary "Promote to leader",
CMSG_GROUP_SET_LEADER) hands the clear to the bots (`PlayerbotGroupCoordinator.Handoff.cs`, the pure rules in
`PlayerbotLeaderHandoff.cs`). The new leader says `I'll lead the clear of <instance>.` in party or raid chat.

* **Trigger.** `GroupManager.LeaderChanged`, read once per coordinator update: the new leader is a running managed bot, the old one a
  real player still online and still in the group (an offline leader's automatic pass or a player leaving the group never counts), and
  the new leader stands in an instance (`MapTemplate.IsDungeon`, a dungeon or a raid). Outside an instance nothing happens: the bots
  keep following the player through the party AI.
* **Adoption.** The player's own group is adopted as it is, a party or a raid: no re-forming, no travel, no teleport, nobody leaves,
  every member keeps its raid subgroup. The members are the bots on the same map instance as the new leader (others, and bots invited
  later, stay with the party AI); the real players are members too, marked real, and are never waited for (`MembersBehind`, the wipe
  check and the stall watch count bots only). Roles: the tanks and healers a run of that size needs (`RunRoleNeeds`), taken by talent
  preference then level; a group short of a tank or a healer still clears.
* **The clear.** An instance the catalogue routes (`Catalogue()`, the wing whose entrance landing or boss is nearest the leader) is run
  as a dungeon or raid run that has already arrived: its machine starts at `Clear`, bosses already dead (not alive on a loaded grid)
  are credited, and trash, bosses, roles, loot, rest, wipes (`MaxWipes`), the stall watch and the run's give-up rules all apply as for a
  run the bots formed. An instance without a route gets a clear list (`PlayerbotClearList`): the hostile creatures alive in the
  instance at the handoff (one pass over the map's creatures), visited nearest first; the leader walks to the next one and the group
  pulls it within 30 yards; one the leader cannot plan a way to for 30 seconds is given up; an empty list is rebuilt once (grids
  loaded meanwhile), then the clear is done. The list is cached: per tick only the current target is looked up.
* **The end.** When the last boss dies (or the clear list is empty and the loot settled) the leader says
  `<instance> is clear. The lead is yours again.`; a clear that stops early says `Stopping the clear of <instance>: <reason>.` (wiped too
  often, stuck, the group left the instance, nobody it follows is in the group any more, the server stopping, ...). Either way the bots
  stay in the player's group and where they stand: the lead goes back to the player who handed it over (CMSG_GROUP_SET_LEADER from the
  bot; the first real player online if that one left) and the party AI follows the player again. They never leave the group or walk out,
  because a bot-formed run's ending would take them out of the player's group and away from the player.
* **Taking the lead back.** A player who is no longer leader cannot take the lead in the client, so any order to the bots (`follow`,
  `stay`, `come`, `attack`, `stop`, `leave`, in party chat or a whisper) ends the clear (`called-back`), passes the lead back, and the
  party AI carries the order out. Other chat (a question, `help`) leaves the clear running. A lead passed back by the bot leader or to
  someone else also ends it, with no further pass.
* **Ownership.** One owner at a time: while the coordinator drives a bot (`Drives`) the party AI is disengaged; the party AI's chat
  hook (`PlayerbotPartyAI.GroupOwns`) asks the coordinator before it engages. Scripted bots (a controller holds them) are never adopted:
  the bot refuses with `I can't lead the clear of <instance>: <reason>.`, as it does when groups are off (`World:Playerbots:Groups:Enabled`, the kill
  switch), the map is not allowed (`PlayerbotMapPolicy`), no way out is known, or no bot is inside with it.
* **Capacity.** A handed-over group takes no slot of `MaxGroups` and does not count against `DungeonRuns` concurrency caps; it is no
  quest group either (no quest sharing).
* **Status.** `.playerbot groups` shows `handoff-from=<player>` (and `clear-list=<left>/<total>`) on the group's line, `:handoff` on a
  bot's group tag, and a summary line `leader handoffs: 1 leading, 3 adopted, 1 cleared, 1 ended early.`.

## Configuration (`World:Playerbots:Groups`, live)

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | true | | form groups for group content |
| `MaxGroups` | 4 | 0..64 | bot-led groups at once |
| `LevelRange` | 5 | 0..60 | most levels between members |
| `MinTank` | 1 | 0..5 | tanks a group of three or more needs |
| `MinHealer` | 1 | 0..5 | healers a group of three or more needs |
| `FormationTimeoutSeconds` | 300 | 30..86400 | how long a bot waits for partners before it sets the goal aside |
| `RaidsEnabled` | true | | raid quests and raid instances as quest and instance goals (convert to a raid); it does not gate raid runs, which only `World:Playerbots:Groups:DungeonRuns:Raids:Enabled` switches |
| `InvitePlayers` | false | | invite a nearby real player into a short group |
| `DungeonRuns:*` | see `docs/reference/configuration.md` | | dungeon and raid runs ("Dungeon and raid runs", above); `World:Playerbots:Groups:DungeonRuns:Raids:SkipPrerequisites` is the switch for the raids' key item, quest and condition |

## Where it shows

* `.playerbot groups` (GameMaster): a summary line (enabled, running, waiting, formed / completed / given up), one line per group
  (`group 1 quest:990720 quest=990701 size=3 source=quest state=engaging party failures=0 age=45s members=Tank(tank,leader),Heal(healer),Mage(damage) step=fight:990720:melee:2yd`:
  `step=` is the leader's last step, `stalled=1` follows once the stall watch re-targeted),
  one per waiting bot, and the last five events.
* `.playerbot status` / `list`: ` group=1:tank:engaging:leader` or ` group=waiting:quest:990720:size3` at the end of a bot's line; the
  risk part reads `decision=group` while the group AI drives it.
* `.playerbot inspect`: a `BOTINSPECT group=...` line.
* A member's goal is `Group`, `Follow`, `Assist`, `Loot`, `Rest`, `Recover` or `Retreat`.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Groups/`: the pure rules (`PlayerbotGroupContentTests`: quest flags and sizes, instance
sizes, role composition, level range, the leader, raid subgroups, the risk estimate's group size, the group's elite margin); end to
end on the scenario world with the manual clock (`GroupTestWorld`: a giver offering only the test's quests, an elite ogre, a brute, a
raid warlord, a Deadmines boss, flat ground, a test Taunt and Resurrection; `PlayerbotGroupResurrectionTests` adds the four real
resurrection rows through `SpellStoreFactory`: a priest and a paladin resurrect an unreleased member, a priest resurrects a released one
through its corpse): an elite quest done by a tank, a healer and a mage formed
through `CMSG_GROUP_INVITE` / `CMSG_GROUP_ACCEPT` / `CMSG_LOOT_METHOD` and disbanded with every member credited; an objective the risk
estimate passes over done by two; the healer healing the tank in a fight; the tank taunting the ogre off the healer; a dungeon quest:
through the entrance trigger into one instance bound to the group, the boss, out through the exit; a wipe retreated, the dead
resurrected, the group gathered again one failure down; a raid quest for six converted to a raid with the tank and the healer in
different subgroups; the formation timeout setting the goal aside; a real player's party bot left alone; `InvitePlayers` inviting a
nearby player who declines; the GM commands; a server group of bots nobody leads left; a bot stranded in an instance, stopped and
logged in again there (the login gate), walking out (`PlayerbotGroupWorldTests`, `PlayerbotGroupPlayerTests`). `ConfigReloadTests` checks every key is live and range-checked.
`PlayerbotGroupStallTests`: a pair led by a priest healer without an attack spell pulls and completes, with no stall line; a pair whose
objective no path reaches re-targets after one stall bound, dissolves after the next (`stalled`, the goal set aside for both) and logs a
stall line `PlayerbotQaSignalParser` reads as goal `Group`; the watch's bounds, a healthy group never stalling, and a target dropped and
taken again counting as no progress; a finished run leaving with a member stuck inside brought out, not dissolved; several unreachable
targets still dissolving on the second strike, while a new target long after the re-target or movement starts over; a group with a real
player never touched; and target 0 outside engaging, in the shape bot QA parses.

Runs: `PlayerbotRunFixesTests` (the stuck clock counting only continuous failure, bosses never pulled as trash, early-kill credit, the
patrolling boss, a quest group's objective still judged by sight, and `.reload instance_encounter` replacing the encounters and the
catalogue following); `PlayerbotRunCatalogueTests` (wings, boss order, scripted encounters, bands, sizes, exclusions, over real classic-db rows in
`RunFixtures`); `PlayerbotRunMachineTests` (travel, enter, clear, boss, wipe, retry, give up, complete, and the events each step
ignores); `PlayerbotRunRouteTests` (`NextPull`: ordering, packs, passed points); `PlayerbotRunMatchingTests` (`RunRoleNeeds`, the raid
quotas through `Match`); `PlayerbotRunWorldTests` on the scenario world (`GroupTestWorld` with the Deadmines, a boss and three raids
whose entrances copy the item, quest and condition requirements): a five-player run formed from bots in the band with a tank and a
healer, the start roll, no run without a healer, the travel policy, a stopped follower and a stopped leader (`Forget`), a raid refused
per requirement kind and formed with the quotas once every bot has the key item, the permanent-bind exclusion, `SkipPrerequisites`
matching and `Waives` (None for a real player, a bot outside the run, a dungeon run's bot, another teleport, the switch off). The
import is tested in `ArcaneCore.Data.Tests/InstanceEncounterImportTests`, the requirement check and waivers in
`ArcaneCore.Game.Tests/Teleport/TeleportRequirementsTests`.

`PlayerbotRunClearWorldTests` now drives five Horde bots through RFC trigger 2230 into one group-bound instance on the manual clock.
It checks the three-creature Ragefire Trogg/Shaman test pack dies before any boss engages, then forces three deaths during a pull and
checks Wiped, Retry, Enter and Clear with the same dead trash still dead. A second case has no resurrection spell: its ghosts walk back
to the RFC entrance and revive inside. The fixture uses classic-db z2815 entries, levels, bosses and teleports and build-5875 trigger
volumes, with compact test pack positions, scaled combat stats, an open corridor path and a test graveyard. Without terrain data, the
fixture links map 389 to its AreaTable.dbc area 2437 for graveyard lookup (the build-5875 Map.dbc linked zone, field 19, which production reads too).
`StartRun(plan, bots)` starts a selected catalogue
wing through the normal formation checks without the random roll. On a retry, a living member still inside walks through the exit to
join an outside leader before the group gathers. This test does not establish an imported-world or real-terrain RFC clear.

`PlayerbotLeaderHandoffTests`: the clear list (nearest first, the current target kept, the dead dropped, a shunned creature skipped
but kept, a given-up one never rebuilt), the role assignment (one tank and one healer for five, never a refusal), the wing choice and
the chat lines; on the scenario world, a real player with five bots inside RFC passing the lead (the group adopted as a run of map 389
at `Clear`, the take-over line in party chat, the first pack killed), the same outside an instance (nothing adopted), a party `follow`
taking the lead back (the coordinator lets go, the party AI drives, the bots stay in the group), the Deadmines without a route cleared
from its list (the boss dead, the clear line, the lead back with the player), and a raid of ten in two subgroups adopted whole with its
subgroups kept.

## Live replay (2026-10-08)

`PlayerbotLiveSnapshotReplayTests` on the wave-6 rehearsal snapshot (`D:/ArcaneCore-lanes/_logs/w6-integrate/rehearsal/orig-characters.db`
and `orig-world.db`, the live settings without their `Database`, `Auth` and `Realms` sections, terrain `D:/ArcaneCore-data/terrain-5875`;
the five desired bots: Dawnrover, paladin 6, Elwynn; Ironwander, warrior 5, and Mirthblade, rogue 8, Dun Morogh; Duststalker, hunter 8,
Durotar; Graveweaver, warlock 1, Tirisfal), six runs of 10 game minutes each with this branch and six with its base (`claude/bot-risk`
d3b26dcb):

* No group goal arose and no group formed (`formed 0`, nobody waiting): none of the five has an elite, dungeon or raid quest, and no
  quest objective was passed over as too strong alone for a small group. Ironwander and Mirthblade are the only two that could match
  (same team and zone, levels 5 and 8), and they never wanted the same thing.
* Deaths: 0 in all six runs (base: 0 in five runs, 4 in one). Every run passed (nobody still for 180 s while alive).
* Stalls reported by the stall watch: 2 in one run (Dawnrover at a quest giver, Ironwander at a grind target), 0 in the others (base: 0).
* Distance travelled, all five together: 10332 to 12558 yards (mean 11496); base 11577 to 13751 (mean 12673). The replay is not
  deterministic (positions differ between runs within the first 30 seconds); per bot the two sets overlap (Mirthblade 2914 to 4403 against
  3351 to 4709). The only brain-visible change for bots without group goals is that a quest or creature entry set aside is no longer
  chosen as the creature to hunt; no suspension showed in these runs, so the difference is not attributed to it.

## Limits

* An item objective is a goal only when every creature that drops it spawns inside instances; one dropped on the bot's own map stays the
  brain's solo work (as before). The group kills the dropper once; a drop chance below 100% that does not drop for everyone fails the
  goal after the usual two minutes without the objective in sight.
* Clearing outside a run is local: the leader pulls what stands within 20 yards of it on the way. A run plans a pull along the path to
  the boss (above) but has no crowd control, no marking and no boss mechanics: encounters are fought as tank-and-spank, and the bosses
  that need more end in wipes until `MaxWipes` gives the run up.
* A run never enters a wing whose entrance is not on a continent (Blackwing Lair), and scripted encounters (the Four Horsemen, summoned
  bosses) are never routed or waited for.
* The coordinator's groups live in memory: after a restart the bots leave the restored server groups and match again.
* A handed-over clear without a route visits creatures by straight-line distance, not along the instance's layout, and only the
  instance the leader stands in: it never travels to another wing. The adopted bots are those inside at the handoff; ones that zone in
  later follow the player.
* A bot without a quest for the goal is not matched for it (sharing happens inside a group formed for the goal, below).
