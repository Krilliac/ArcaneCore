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

| File (`src/ArcaneCore.Playerbots/Groups/`) | What it does |
|---|---|
| `PlayerbotGroupOptions.cs` | `World:Playerbots:Groups` (every key live through `.reload config`). |
| `PlayerbotGroupContent.cs` | The pure rules: which content needs a group and how big, who can tank or heal, the matching (`Match`). Unit tested. |
| `PlayerbotGroupCoordinator.cs` | The world feature: needs, matching, formation through real packets, the group's state machine, the GM report. |
| `PlayerbotGroupAI.cs` | One per member: what a bot does while it is in a bot-led group, instead of the brain. |
| `PlayerbotFightWatch.cs` | The no-progress watch of one bot's group fight (health drop or distance closed within 20 s). |
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

**Real players** (`InvitePlayers`, on by default): a group one member or role short may take a nearby real player (100 yards, same
team, ungrouped, of a fitting level) through the same ordinary invitation, but only one who is on the group's goal (holds its quest
unfinished, or is inside its dungeon, or stands at its entrance: on the meeting map within `SameGoalMeetingRadiusYards` (150) of the
entrance and in level range) and who either opted in to AI chat (`ai on`) or is liked: affinity of at least
`InvitePlayerAffinity` (20) toward one of the waiting bots, read through `EffectiveAffinity`. A rival of any of them, or a player a bot
flagged `NoWhispers`, is never asked, and a player is asked at most once per `InvitePlayerCooldownMinutes` (30). A player whose
remembered role (the P1 `PrefersTank` / `PrefersHealer` flags) fills the missing tank or healer comes first, then one whose class can.
A real player never leads a bot group and is never driven;
one who declines or does not answer within 30 seconds is not asked again for 10 minutes, and the group is given up. A real player's
own party with a bot (`.playerbot invite`, the party AI) is not a bot group and is left alone: that bot is not free.

## Doing it together

`PlayerbotGroupAI` drives each member while the group lives (the brain is not updated; its quests, suspensions and memories stay).
The group's state (`PlayerbotGroupState`, in `.playerbot groups`):

| State | Leader | Members |
|---|---|---|
| `forming` | holds | follow the leader; their intake accepts |
| `gathering` | holds until every living member is within 20 yards (on a run's retry step a leader revived inside the instance instead walks out to the exit, and the group is not gathered while it is inside; if it finds no path it holds, and only the 60 s catch-up below moves the group on) (after `GatherCatchUpMs`, 60 s, missing members are brought to the leader by the follow catch-up teleport, or the gathering moves on without those that cannot be) | walk to it |
| `travelling` | walks to the approach point (30 yards short of an open-world objective's spawn, 15 short of an entrance, snapped to the navigation mesh by `SnapToMesh` if a mesh ray hits a boundary; counts as reached after `ApproachNoProgressMs`, 45 s, without closing by more than 2 yards), keeping out of known hazards, waiting while a member lags more than 30 yards (after `LagCatchUpMs`, 30 s, laggards are teleported to the leader or the wait is waived) or rests | follow at 2-5 yards (vmangos `MoveFollow`) |
| `entering` | walks into the entrance trigger (`PlayerbotAreaTriggers` reports it like a client; the coordinator gave every member the trigger consent) | when the leader is inside, walk into the same trigger: the group's instance (the group is bound to it) |
| `engaging` | pulls the objective when it is in sight within 60 yards (preferring one on the leader's own level before a nearer one on another level, `Better`), else the nearest creature within 20 yards that would attack the group (not a civilian, on the leader's level, and its aggro reach touches the leader's route segment to the goal: `CountsAsObstacle`), else walks towards the objective's spawn; waits for laggards, resting members and its own recovery | fight by role (below) |
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

**Raid marks and players.** In a fight a member first sticks to the group's raid marks: its own focus icon (`Member.FocusIcon`, the
skull by default), then the skull (`PlayerbotParty.MarkedTarget`, the one the party AI uses, within `PlayerbotParty.MarkRangeYards`,
100 yards), before it assists the leader's victim. A target may be a player: a player who attacked a member (or the bot) and a leader's
victim pass the one hostility rule (`PlayerbotHostility.MayEngage`, flagged or dueling enemies only), so the group fights back and
assists against players the way a party bot does; `Target` stays the creature view, `TargetUnit` is the unit either way. A non-siege leader that can plan no way to its target shuns it for the group for `NoPathShunMs` (60 s; a run's boss counts toward `RunStuckMs` instead), and a member remembers a target it could not reach for `UnreachableMemoryMs` (30 s, `PlayerbotUnreachableTargets`) and does not assist or follow a mark onto a shunned or unreachable unit. A non-siege fight against a creature that neither drops its health nor closes in by more than `ProgressYards` (1 yard) for `PlayerbotFightWatch.StallMs` (20 s) is given up and the target shunned for `FightStallShunMs` (300 s). When a fight is dropped for no path, `LastTargetGuid` and `LastTargetEntry` are kept until another target or non-fight think so the group stall watch still names and shuns that target.

**Lifts.** Every think first lets `PlayerbotElevator.Drive` run: a member whose way leads over a lift (`WalkTo` asks
`PlayerbotElevator.TryBegin` before it plans a far walk, the mesh route it found is used when the lift is not the way) rides it to the
other level, as a brain does (the Great Lift, the three Undercity lifts); a fight on the way to the landing ends the ride, a failed ride
plans the walk again after the lift's backoff.

**A dead leader.** `PlayerbotGroupCoordinator.PromoteLeader` passes the lead to the best living bot member with the leader's own
`CMSG_GROUP_SET_LEADER` (`PlayerbotGroupLeadership.BestSuccessor`: alive, tank before healer before damage, the higher level first) and
follows the server group's answer. It is called by the siege machinery only (design commit 6); other groups keep waiting for their
leader as before.

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
the objective's spawn without it in sight, 30 seconds failing to plan a way to the approach point or objective (`no-path-to-approach`, `no-path-to-objective`, like a run's `no-path-to-entrance` / `no-path-to-boss`), too few members left (a real player counts toward the two only while online: when the player of a one-bot-and-one-player pair disconnects or logs out the group ends at once with `player-gone`, with no grace period; a group that has already succeeded, or is walking out, is not failed by it), or the leader gone. The goal is then set aside for every member for
10 minutes (its quest and objective in the brain's suspensions, so the brain does not walk into it alone either), and the group walks
out of an instance before it disbands. A group that cannot walk out in time is brought to the entrance by the teleport service (at most `MaxBringOuts`, 2 times, before it disbands).

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

## Cooperation (`World:Playerbots:Groups:Cooperation`, live)

Bots help each other fill groups. Everything here reads the social memory through `EffectiveAffinity` and `IsRival`, is decided by a
deterministic hash (the same state gives the same answer; no `Random`), and is on by default.

* **Recruiting** (`Recruit`). A waiting group short of members, or of the tank or healer it needs, waits `RecruitWaitSeconds` (20) and
  then asks the free bots of its team within `RecruitRadiusYards` (300) on the group's map and in its level range. A bot qualifies when
  it is idle or on a lower-priority goal (Explore, Grind, Town, Train; never Combat, Recover, a dungeon run, or a quest within two
  objectives of done), is not grouped and has not set the goal aside. A rival, or a bot whose affinity toward any waiting member is at or
  below `RefuseAffinity` (-40) (either direction), never joins. It accepts with chance
  `clamp(RecruitBaseChance (0.35) + 0.4 x Sociability + 0.003 x affinity toward the leader + 0.2 if it fills a missing role, 0, 0.95)`
  (+`GuildBonus` 0.2 for a guildmate of the waiting group, +`LfgAnswerBonus` 0.15 when it answered a call), rolled by a hash of the bot and
  the group's stable id and the pair's current refusal window: a candidate that refuses is remembered with the time of the refusal and is not asked
  again for `RefusalMemoryMinutes` (10) from then, and gets a fresh roll afterwards while the group still waits. The roll's window is the clock
  divided by that period, shifted by a phase hashed from the (candidate, group) pair, so different pairs do not all turn over at the same instant. Missing roles are filled first, then the higher affinity and trust toward the members, then the nearer. The recruit gets the
  quest through the usual quest share; one that cannot take the quest still joins and helps. The group cap is `MaxGroups` (16); when it
  holds waiting bots back that is logged and noted once a minute.
* **Answering calls** (`AnswerBotCalls`). A bot that hears another bot's looking-for-group line in a channel offers itself to the
  coordinator; if the coordinator can build the group with it (the caller is waiting, the hearer is eligible and the roll, with the
  `LfgAnswerBonus`, passes) it is pledged and joins at the next scan without the wait, and only then replies in the channel ("inv me,
  I'll tank"). With no real match it says nothing. A hearer that is busy, of the other team, on another map or out of the level range
  is turned away at once, the post's candidates are worked out once for all its hearers however many ticks they hear it over, and at most `MaxLfgAnswersPerLine` (4) hearers
  get a place from one post (the count lives as long as the post, `World:Playerbots:Chat:Voice:PostLifeSeconds`, 600 by default, up to 64 posts at a time).
* **Guild calls** (`GuildCalls`). When the coordinator really has a dungeon or raid group forming, its longest-waiting bot says
  "anyone for <dungeon>?" in guild chat (once per ten minutes per guild and goal, dropped if the group is gone by then). Guildmates that
  heard it are weighed at the next scan without the wait.
* **Mentoring** (`Mentoring`). A bot at least `MentorLevelGap` (10) levels above a friend or guildmate (affinity at least
  `MentorAffinity` 30, no rival) whose quest was set aside, failed twice, or who called for help in a fight, may team up with it as a group
  of two with chance `MentorChance` (0.3) x its Helpfulness. The goal is the friend's quest objective (`source=mentor`); it ends with the
  objective like any quest group, or after `MentorMaxMinutes` (20). Strangers and bots within ten levels never mentor. The scan runs every
  30 seconds, inside a guard that records a fault under `playerbot.groups`. A bot that soloes a quest and is neither waiting nor set aside
  but calls for help is mentored on the quest it was on when it called (its brain's current quest, else the first unfinished objective with
  a spawn on its map); a bot on no quest is not mentored. The mentor group lifts the brain's set-aside of that quest and creature.
* **Raid escalation** (`RaidEscalation`, needs `RaidsEnabled`). A risk signal ("too strong alone, feasible for N") may now ask for up to
  `MaxRaidSize` (40) instead of five; a group of more than five converts to a raid (`CMSG_GROUP_RAID_CONVERT`) and recruits as above.
  With too few bots around it times out like any group goal (`FormationTimeoutSeconds`) and is set aside.
  Only a rare elite or a boss (rank 2 or 3) may ask for more than five; a plain elite or any other creature needs five or fewer or is passed over. The
  size is found by bisection (about six estimates for a raid of 40).

| Key | Default | Meaning |
|---|---|---|
| `Recruit` | true | recruiting |
| `RecruitWaitSeconds` | 20 | wait before a silent recruit round (1..3600) |
| `RecruitRadiusYards` | 300 | how far a recruit may be (10..1500) |
| `RecruitBaseChance` | 0.35 | base accept chance (0..1) |
| `GuildBonus` | 0.2 | chance and preference bonus for a guildmate (0..1) |
| `LfgAnswerBonus` | 0.15 | chance bonus for a bot that answered the call (0..1) |
| `RefuseAffinity` | -40 | affinity at or below which a bot never joins (-100..0) |
| `AnswerBotCalls` | true | bots answer other bots' looking-for-group lines |
| `RefusalMemoryMinutes` | 10 | how long a refusal of a waiting group is remembered; the candidate rolls afresh after it (1..1440) |
| `MaxLfgAnswersPerLine` | 4 | the most bots that may take a place from one looking-for-group line (1..64) |
| `GuildCalls` | true | the guild call for a forming group |
| `Mentoring` | true | mentoring |
| `MentorChance` | 0.3 | mentoring chance before Helpfulness (0..1) |
| `MentorMaxMinutes` | 20 | longest mentor group (1..240) |
| `MentorLevelGap` | 10 | levels the mentor must be above (1..60) |
| `MentorAffinity` | 30 | affinity a mentor needs toward the friend (-100..100) |
| `RaidEscalation` | true | risk signals above five become raid goals |
| `MaxRaidSize` | 40 | largest raid goal (6..40) |
| `SameGoalMeetingRadiusYards` | 150 | how near a dungeon's entrance a player on the continent still counts as on its goal (10..1500) |

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
than two bots left), `leader-gone`, `player-gone` (the group's real player is no longer online), or `leader-stopped` (the leader's bot stopped: `Forget`). A follower that stops leaves the group and
the run goes on. A given-up run walks out of the instance first, then disbands; every bot still in it sets the wing aside. Totals:
`completed` and `given-up` in the runs line. A run that ends for a content or code reason (`no-path-*`, a real step timeout, `objective-missing`, `stalled`, `wiped`) is also reported to bot QA as a `run failed` finding (docs/areas/playbots-qa.md; operator and lifecycle ends such as `leader-stopped`, `leader-left`, `leader-gone`, `player-gone`, `too-few`, `invites-unanswered` and `disbanded` are not): its instance map, wing, the stage it failed at, the boss it was heading for and the reason, never the group or a bot, so repeated give-ups at one stage aggregate into one issue. Metric: `arcanecore_playerbot_runs_total{outcome}` with `completed`, `given_up` and `timeout` (a reason ending in `timeout`: `gather-timeout`, `travel-timeout`, `enter-refused-or-timeout`, `regroup-timeout`, `run-timeout`; every other end, operator stops included, is `given_up`) (docs/ops/metrics.md).

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

## World-boss raids (`World:Playerbots:Groups:WorldBosses`, live)

Bots raid the world bosses of the continents: a muster of up to 40 free bots walks to the boss and kills it, while real players keep the
first claim. Files: `PlayerbotWorldBoss.cs` (pure: catalogue, status, campaign, muster, engage decision, outcomes, warning texts),
`PlayerbotWorldBossMeters.cs`, `PlayerbotGroupCoordinator.WorldBoss.cs` (watch, muster, the raid's life) and
`World.Contracts/Playerbots/Groups/PlayerbotWorldBossOptions.cs`. A raid is an ordinary bot-led group of the `WorldBoss` goal: the generic
machine forms, gathers and walks it; only the Engaging step is its own (`AdvanceWorldBoss`). It needs `World:Playerbots:Groups:Enabled` and
`World:Playerbots:Groups:RaidsEnabled`; raids count in `MaxGroups` and in the quest-group limit.

**Catalogue.** Azuregos, Kazzak, the four Emerald Dragons (a dragon is looked for at its portal while the Nightmare Dragons event runs and its
portal's dragon is not killed), Teremus, and with `DeriveFromContent` every rank-3 creature with a spawn on map 0 or 1 that is no faction
leader, not friendly and not an event-gated spawn; `IncludedEntries` adds (`entry`, or `entry@map` for a boss placed by something other
than a database spawn), `ExcludedEntries` removes and wins. An `entry@map` with no creature template is ignored with a warning. The entry
set is frozen as a snapshot (`IsWorldBossEntry`) that the kill talk reads from the map threads.

**Watch.** Every `WatchSeconds` each boss is resolved: a living creature is Up (with its position, whether it is in combat, whether another
player holds its tap or fights it, how many real players stand within `PlayersNearYards`); the respawn ledger gives a dead one's time; a
boss whose grid is not loaded is Unknown, never Up on no evidence. A spawn is found by its spawn guid; a dragon or placed boss by entry, once,
then by its cached guid (the cache is dropped when the lookup finds nothing or another entry).

**Campaign** (per boss, in memory; a restart starts in Watch). A boss that has been Up for `PlayerGraceMinutes` opens a muster when
`MaxConcurrentRaids`, `MaxAttemptsPerDay` (per UTC day; only a raid that reached the walk to the boss counts), `RetrySeconds` (also after a
failed muster or a forming failure), `MaxGroups` and the quest-group slot allow it. The muster ends after `MusterWindowSeconds` without a
raid.

**Muster.** Free bots on the boss's map within `MusterRadiusYards`, level at least the lower of the boss's level and the player level cap
minus `LevelGap`, one team (the team with more candidates first). The nearest to the boss calls the raid; each other candidate agrees by
the cooperation roll (a hash of boss, UTC day and team: a refusal holds for the day). Size is `SizeOverrides` (`entry:size` pairs, 5..40) or
`DefaultRaidSize`, capped by the cooperation raid cap; fewer than `MinRaidSize` agreeing, or roles that do not fit, keep the muster open.
A bot that is silent or refuses an invitation is dropped and forming goes on while `MinRaidSize` remain (other groups still end on one
silent bot). The raid walks by default; `CatchUpTeleport` turns the leader's teleport of stragglers on.

**Engage.** The raid gathers `RallyYards` short of the boss and the leader warns the raid ("Raid forming ...", "Pulling ...", "... is dead!").
The pull is released when `PresentFraction` of the members stand at the rally and none rests, or after `PullWaitSeconds` with at least
`MinRaidSize` there (fewer: `not-enough`). It is held, and the stall watch leaves the raid alone, while another player's tap or fight is on
the boss (a claim that blinks for a few seconds counts as one claim) or `PlayersNearCount` real players stand within `PlayersNearYards`;
after `HoldSeconds` of that the attempt ends as `boss-taken`. The leader's pull also refuses a boss that turns "someone else's" between two
scans. A member carrying Mark of Frost (23182) does not fight again and holds at the rally. A pull with some real players near (fewer than `PlayersNearCount`) is
recorded as the `worldboss-pull-near-players` anomaly. The kill counts only with the raid's own tap (a raid member or a member's pet holds
`LootTapper`): a corpse tapped by others is `boss-taken`. After `MaxWipes` wipes the raid ends as `wiped` (an anomaly at full size); a raid
lasts at most `MaxRaidMinutes`; its step limits reuse `World:Playerbots:Groups:DungeonRuns:TravelTimeoutMinutes`.

**Keys** (all under `World:Playerbots:Groups:WorldBosses`; defaults and ranges in `docs/reference/configuration.md`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | raids form; off: no muster opens and raids under way finish |
| `WatchSeconds` | 30 | seconds between looks at the bosses |
| `PlayerGraceMinutes` | 15 | minutes a boss must have been Up before bots go for it |
| `PlayersNearYards` | 100 | radius in which real players hold the pull (0 turns it off) |
| `PlayersNearCount` | 3 | real players within that radius that hold the pull |
| `MusterRadiusYards` | 3000 | farthest a bot may stand and be asked to muster |
| `LevelGap` | 2 | levels below the boss's (or the cap) a bot may be |
| `MinRaidSize` | 30 | fewest members a raid starts with |
| `DefaultRaidSize` | 40 | size asked for when `SizeOverrides` has no entry |
| `SizeOverrides` | empty | `entry:size` pairs |
| `MusterWindowSeconds` | 180 | how long an open muster waits for enough bots |
| `MaxConcurrentRaids` | 1 | raids at once, server wide |
| `MaxAttemptsPerDay` | 3 | attempts per boss and UTC day |
| `RetrySeconds` | 1800 | wait before the same boss is mustered for again |
| `RallyYards` | 50 | distance from the boss at which the raid gathers |
| `PresentFraction` | 0.8 | share of the raid at the rally that releases the pull |
| `PullWaitSeconds` | 120 | longest wait at the rally for that share |
| `HoldSeconds` | 180 | longest hold on another's claim before `boss-taken` |
| `MaxWipes` | 2 | wipes recovered from before giving up |
| `MaxRaidMinutes` | 90 | longest a raid may last |
| `CatchUpTeleport` | false | teleport stragglers to the raid |
| `DeriveFromContent` | true | add the rank-3 creatures of the continents to the catalogue |
| `IncludedEntries` | empty | further bosses (`entry` or `entry@map`) |
| `ExcludedEntries` | empty | bosses removed from the catalogue |

**Observability.** Counter `arcanecore.playerbot.worldboss_raids{outcome}` (`formed`, `killed`, `wiped`, `boss-taken`, `boss-gone`,
`not-enough`, `forming-failed`, `abandoned`, `timeout`, `stalled`, `no-path`, `muster-failed`, `other`) and gauge
`arcanecore.playerbot.worldboss_active`. Anomalies (area `group`): `worldboss-pull-near-players`, `worldboss-wiped-full-size`,
`worldboss-corpse-tap-ledger-dead`. A raid that ends in a timeout, a wipe-out, a stall or without a path goes to bot QA as a `RunFailed`
signal. Fault sites: `playerbot.groups.worldboss.watch`, `.muster`, `.advance`; `playerbot.social.bosskill` for the kill talk's zone name.
The group report prints one `worldboss:` line per catalogued boss (state, phase, attempts, cooldown, last opening verdict).

**Not in this phase.** Boss tactics beyond sitting out the Mark of Frost (Mark of Nature on the dragons is not handled: they may wipe the
raid), players joining or leading bot raids, a faction contest, scouting Unknown bosses, spawning bosses.

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

## Siege (`.playerbot siege`)

A siege is a GM-ordered march of one or two **bot-led raids** along stored waypoints to a creature entry, and its death. A bot leads each
raid and commands it alone; nobody has to be in a group (the GM may watch invisibly, `.gm on` and `.gm visible off`), and a real player can
still be fought, or lead a group of his own. All state is in memory (`PlayerbotGroupCoordinator.Siege.cs`); there is no configuration key.

* **Commands (Administrator).** `.playerbot siege waypoint add` stores the invoker's position as the next waypoint (all on one map, at most 64);
  `waypoint add at $map $x $y $z` stores that map and position instead and needs no player, so RA and the console can use it (the map must
  exist, the numbers must be finite and inside the map, the height is kept as given; the one-map rule is the same, refused as `waypoint-other-map`);
  `waypoint list` prints them, `waypoint clear` forgets them (both refused as `siege-running` while a siege runs); `siege start $name|$prefix*|all
  #entry [raids 1|2] [force]` enlists the running bots; `siege stop` ends it (outcome `stopped: gm`).
* **Forming.** Free, living, ungrouped bots of one team on the waypoints' map are split over the raids: tanks, then healers, then damage dealers,
  dealt round the raids (the highest level first); the best tank leads a raid. Each is an ordinary bot-led group (same invitations, raid conversion,
  subgroups of five) with goal kind `Siege`, always a raid, and goes straight to `Engaging`. A siege raid takes no slot of `MaxGroups`, has no goal
  timeout and no wipe rule of the quest groups, and works with the groups kill switch off: the GM stops it. Refusals: `siege-running`,
  `no-waypoints`, `bad-raids`, `bad-objective`, `groups-unavailable`, `too-few-bots` (two for every raid); skipped bots carry `not-running`,
  `busy`, `grouped`, `dead`, `other-map`, `in-group`, `other-team`, `raid-full`.
* **Lockstep.** The raids share one `Step`, the index of the waypoint they head for. It moves on only when every raid's leader stands within
  6 yards of that waypoint and none fights; a raid that is there first holds for the others, and a raid whose leader is dead holds them too until it
  has a leader again or ends. A siege whose leaders get no nearer to the step goal for 120 s (see **Stall watch** below) is reported stalled: one event note, a
  log warning, a raid warning from each led raid and `, stalled: ...` in the status line; a step or progress clears it. Past the last waypoint the leaders walk to the
  living objective. Lifts are ridden by the ordinary walk (`PlayerbotElevator`, the three Undercity lifts).
* **Furthest-waypoint credit (`CreditedStep`).** The credit is the lockstep without the need for both leaders to stand at the waypoint at the same moment. A defender post at a waypoint (the
  Undercity lift bottom has one 7.6 yards from it) turns every approach into a fight that drags the leader off the 6-yard disk, so the two leaders are never in it together. Each raid records the highest
  waypoint index its leader has stood at (`IsAtWaypoint`, level-aware, including the 10-yard height check; `PlayerbotSiegeRaid.FurthestWaypoint`, checked from the step on, never lowered; it belongs to the raid,
  so it survives `PromoteLeader`). When every led raid's record is at least the step, at least one leader is not at the step's waypoint now, and no raid blocks it (`BlocksCredit`), the step moves on by **one**, never more
  (an event note and a log line say so). Where all leaders are at the waypoint now, the lockstep above decides, with its combat and push rules. The credit keeps the lockstep's lag rule: none while a living member of any raid is more
  than `LagYards` behind its leader, unless the lag is waived (`LagWaived`; a step change, by the credit or the lockstep alike, restarts every raid's lag clock, so each waived step costs a fresh `LagHoldMs` even where the next waypoint lies in the same disk; `NoteLagWaived` says so as for the lockstep. A waiver and the credit can meet only for a single raid: a waived lag needs a pushing leader at its waypoint, and with another raid away from it the siege blocks that push), and none while a leader answers an
  attacker (a chase within the leash). A leader that passed a later waypoint therefore buys one step per bump, as a visit to each would. Not with an unled raid.
* **Route tether (`RouteYards`, `PlayerbotSiegeCommander.RouteTetherYards`, 40 yards).** The route of a step is the segment from the waypoint behind it (where the siege began, for the
  first) to its goal (the waypoint, or the living objective past the last). A hostile counts for the siege (the pull candidates, the skull and cross, the `Hostiles` of the facts) only within 40
  yards of that segment in the plane, and on its levels (no more than `WaypointLevelYards` above or below the segment's heights, so a hostile on another Undercity floor does not count),
  besides the objective entry, which always counts, and any hostile that fights the raid (its melee victim is, or its threat list holds, a member): the raid does not ignore what hits it.
  A leader more than 40 yards off the route (`OffRoute`) that is out of combat advances back to it before it pulls or marks a tank (after Regroup and Hold); in combat nothing changes.
* **Per-step deadline (`StepDeadlineMs`, 600 s; `PlayerbotSiege.StepSinceMs`).** No fight, lag or loop at a waypoint holds the siege for good: when the step has lasted `StepDeadlineMs` (counted from `StartSiege`
  or the last `SetStep`), a waypoint is still ahead, every raid is led and none is in a retreat latch, the step moves on by **one** (an event note, a log warning "waypoint N not taken in 600s, moving on to waypoint N+1"
  and a raid warning), and the clock starts afresh, so it is never more than one step per deadline. An unled raid or a retreat latch holds it, and the clock restarts where progress was impossible: on the siege's first tick past forming and when a retreat latch ends (a latch is bounded by the retreat limit). Time a raid spends without a leader pauses the clock (`PlayerbotSiege.TrackLeaders`: when every raid is led again the start moves forward by the unled time, never past now), so a leader that dies again and again cannot hold the step for good.
  The deadline raid warning is sent whatever the announcer's gap rule says.
* **The lag is bounded everywhere.** The lag clock (`LagSinceMs`) runs whenever a living member is more than `LagYards` behind its leader, pushing or not and at the waypoint or on the way, and resets when the lag
  clears or the step changes. After `LagHoldMs` (90 s) `LagWaived` holds: the regroup ends (`Decide` regroups for lag only while `MaxLagYards > LagYards && !LagWaived`; resting and hurt members still hold it), and the
  lockstep and the credit go on without the straggler. So the waiver and the credit can meet: a credit after a waived lag is allowed and says so in its event.
* **What the scan counts, and the marching leader.** Besides the tether, the scan skips civilians (`Civilian` template or behaviour) and units more than `WaypointLevelYards` (10) above or below the leader, unless they are the
  objective or fight the raid (`FightsRaid`). A siege-raid leader whose `Fight` finds no path to a Creature or Player target shuns it for 60 s (`BotGroup.Shun`); the scan and the pull target honour the shun for players too,
  so the skull moves to the next hostile. The siege's objective is never shunned and the scan keeps it whatever the shun table says. A leader under a non-push **Advance** fights only what attacks it itself (it does not turn to peel for a member or follow a mark; the members answer their attackers as ever), and
  `raid.MoveTo` is the step's goal for every order but Retreat and Rally.
* **Status detail.** `raid.Detail` ends with `furthest=<waypoint index>`, ` offroute` when the leader is more than 40 yards off the route, and `deadline=<seconds left>` for the step.
* **The commander** (`PlayerbotSiegeCommander.Decide`, a pure function; the coordinator re-reads the raids every 400 ms) picks an order for each
  raid in this order: **Retreat** (in combat: the tank dead, the average health under 35 % or a third of the raid dead *in this engagement*, i.e.
  since the raid last entered combat after at least 5 s out of it; one latch in two phases, see below; a raid retreats at most **twice** and at most
  **once per engagement**, then it commits, says `No more retreat - press on!` once and fights on, and it never begins a retreat while a member fights
  the objective or the skull is on it); out of combat **Regroup** (a member more
  than 30 yards behind, resting, or hurt below 60 % for at most 90 s) or **Hold** (at the waypoint while the other raid is not); **Pull** (a
  hostile within 40 yards, kept until it is farther than 55: hysteresis); **AssignTank** (two or more hostiles and no cross); **Advance**; **Assault**
  (in combat). Between the retreat rules and the rest, in combat, sit the two **push-through** rules (below). The scan for hostiles takes any creature the raid may attack and that is hostile to it, whatever its NPC flags (every Undercity
  target carries gossip or quest-giver flags); friendly and neutral NPCs are out, critters too (the objective entry excepted).
* **Push through (`PlayerbotSiegeCommander.PushOf`).** The commander advances only out of combat, but a trickle of returning defenders and
  respawning guards can keep a raid in combat at one waypoint for good. So a raid in combat gets the order **Advance** (no new order: the raid's
  `Push` says why, `quiet` or `stalemate`; the status detail carries `push=quiet`, the raid warning is `Pushing through to waypoint N`, the leader's AI
  step is `lead:siege-advance:push`) in either case: (a) **quiet**, no hostile counted near the leader (`Hostiles == 0`, the 55-yard scan) for
  `PushThroughQuietMs` (10 s) running; or (b) **stalemate**, the raid has been in combat at the same step for `StalemateMs` (180 s), its average health is
  at least `StalemateHealthPct` (80 %), no member died in the last `StalemateLossWindowMs` (60 s) and the hostile count is not lower than it was when the
  step's clock started (a slow win is not a stalemate; a trickle that refills the count is; a push in force (`PushInForce`) is not cancelled by a falling count, which is what walking away from the hostiles or killing them does). Both are off while the raid fights the objective
  (a member's victim is the objective, or the skull is on it), on the **final leg** (the step heads for the last waypoint or beyond: `Step >= waypoints - 1`),
  while a retreat or rally latch runs, and in a fight the retreat rules call lost (tank dead, average health under 35 %, a third dead: those still retreat,
  once per engagement, at most twice, with the same counting as before). A push never begins a retreat and is never counted as one.
  **A push is the siege's decision, not one raid's** (`GrantPushes`, every 400 ms): a raid that qualifies is held back while the other raid is in combat and
  does not qualify (the lockstep would wait for that fight and the pushing leader would idle among its hostiles), and once its leader stands at the waypoint
  while a peer is not there (or a raid is unled, or the waypoint is the last) the order is Assault and the push is held (`Push` is none). Only a push that was
  **granted** is held: it stays in force (`PushInForce`) and goes on when the hold ends. A qualification that is refused starts nothing and is judged again at every read
  (see "A push in force carries" below). A push held back only by a member's lag stays granted (see the lockstep below).
  **Clocks.** The *in-combat clock of a step* starts at the first coordinator read in combat at that step, takes that read's hostile count as the baseline of
  a stalemate, and resets when the step changes (starting again at once if the raid is still in combat) and after 5 s out of combat (`StepClockQuietMs`, the
  length of the retreat's quiet); a shorter gap does not break it. It does **not** reset when a retreat begins: a retreat keeps the step (its point is the
  waypoint behind), so a fight chased out of its rally after `RallyHoldMs` carries the time it has already run; the stalemate then still needs 80 % health and
  no loss in 60 s, which a raid that has just retreated rarely has. The *quiet clock* runs only while the raid is in combat with `Hostiles == 0`, starts over at
  the first read that is not, and also starts over at a step change (each waypoint needs its own 10 s). The loss clock is the last read in which a member was
  dead that was not at the read before (the first read counts nobody); **a member that goes offline is not found alive and counts as a loss**, as does one
  that logs back in dead.
  **A push in force carries.** A push **begins only from a granted one** (`GrantPushes` said yes, the order is Advance): a qualification the siege refused starts nothing, so it is judged again at every read and cannot be carried later past the 80 %, loss-window and hostile-count checks it passed only at the moment of the refusal (`NextPushInForce`). A granted push the coordinator keeps in `PushInForce` and feeds back (`PlayerbotSiegeFacts.Pushing`); `PushOf` returns it
  unchanged, through the step bump (which zeroes both clocks), a hold at the waypoint for the peer, a falling hostile count and a dip under 80 % health. It ends on:
  out of combat, nobody alive, a member fighting the objective, the final leg, a retreat latch, the denial below (`PushDeniedStep`) and a fight the
  retreat rules call lost (those still retreat). A block of the siege's grant (the peer is in a fight that does not qualify, or the leader waits at the waypoint for a peer that
  is not there) only **holds** the push: the order is Assault meanwhile (`Push` is none, `PushInForce` stays), and it resumes when the block ends.
  **While a raid pushes through** the skull and cross are taken off their units when the push begins (and `PullTarget` is empty). The **leader walks**: away from its
  waypoint it fights nothing (its target is none, so `PlayerbotGroupAI.Update` reaches the walk; its attackers follow it), and at its waypoint, waiting, it fights back.
  **Every member answers any attacker of any raid member** within `AssistYards` (50 yards), its own attacker first, then the nearest, and with none follows the leader;
  there are no marks and no new targets (`SelectTarget`). A rally is different: there a member fights back only its own attackers (a healer also a raid member's).
  **At the waypoint the pushing leader picks no fights, but answers what attacks it**: it takes what is at the waypoint (within `WaypointYards`, 6 yards, of it, in 3D so that a
  unit on the other level of a lift top is not) and an attacker that is on it, wherever it stands within `SiegeAnswerLeashYards` (30 yards, 3D) of the waypoint (a caster, a
  flagged player, a long-reach melee unit). A leader that cannot hit that attacker from where it stands closes on it (a melee tank under fire from a ranged attacker; a ranged
  leader casts from where it stands) but only while the attacker is inside the leash: an attacker that kites beyond it is not followed, the leader walks back to the waypoint
  (and stands there taking what it takes until the lag hold ends, `LagHoldMs`), and so it does when the attacker dies or stops attacking. Units outside the disk that do not
  attack the leader are ignored. While the leader fights from the waypoint its own blacklist entry is lifted on every think (only that place, `PlayerbotMotion.Unblacklist`), so the chase out and the walk back do not read as a loop. While the leader answers, the raid reads it as at the waypoint, so the lag hold and the no-progress clock do not flip because it left the disk
  (`PlayerbotGroupCoordinator.UpdateAnswer`); the **lockstep step advance** needs the leader really in the disk (`AtWaypointReal`, level-aware), so the step never moves while the leader is out answering. An answer **begins** only from a real, level-aware reading (`IsAtWaypoint`) at the current step, for the current leader, while the raid
  pushes, when the leader's AI has asked for it (`AnswerRequestedMs`, fresh for `AnswerFreshMs`, 3 s). It **lasts** until the leader is back in the disk and no longer fighting (a
  walk back longer than 3 s keeps it), and is **bounded**: it ends when the leader is more than `SiegeAnswerLeashYards` plus `WaypointYards` (36 yards) from the waypoint in the
  plane or more than `WaypointLevelYards` (10 yards) off its level, and once the leader has spent `LagHoldMs` out of the disk answering on the step (summed over the step's excursions, not per excursion: a re-request does not restart it; fighting inside the disk costs none of it; once spent the step has no further answer and the leader fights only what is in the disk; the next step has its own). The request must come from the current leader (`AnswerRequestedLeader`). It belongs to its step and its leader: a step change, a promoted leader or the end of
  the push ends it, so it neither carries over to the next waypoint, nor is inherited, nor sustains itself (a leader fighting at waypoint N is not read at N+1). Chasing without that leash drew the leader out of the 6-yard
  radius, so that its target was none and it walked back, then chased again; the loop watch read that as an A, B, A pattern and refused the waypoint, the walk reported no path
  and the push was denied after 30 s. **A leader death is not a retreat**: a retreat needs every tank dead (`WouldRetreat`); the death of the leader promotes the next tank
  (`PromoteLeader`) and the push goes on. Members are unchanged (any attacker of a raid member within 50 yards). Since the push has its own no-progress bound
  (`PushStallMs`, 30 s, which ends a push whose leader cannot get on whatever it does), `LeadSiege` lifts the waypoint's own loop-watch refusal on every think while the waypoint is
  refused (`PlayerbotMotion.Unblacklist`: only the entries at that place; the other refusals, the loop pattern and the rejection count stay, so the guard against a walk refused
  again and again still trips). `Unblacklist` is 2D like the refusal itself (`IsBlacklisted`), so it also lifts a refused spot with the same XY on another level: the walk would
  refuse that spot as well. **A pushing leader away from its waypoint does not rest, eat, loot or buff** (queued loot packets wait, no `FinishLoot`, `Resurrect`, `LootHeld`,
  buffs or `Rest`, and a rest already running is ended): a sit at 30 % health with weak food stood the leader still until the push was withdrawn. **"At the waypoint" is
  level-aware**: within `WaypointYards` in the plane and no more than `WaypointLevelYards` (10 yards) in height, because a lift-top waypoint shares its XY with the floor
  up to about 100 yards below it and the Undercity levels are about 20 yards apart (lift bottom -40.8, floors -59 to -62; within 6 yards in the plane walkable ground does not climb more than about 7).
  **The no-progress bound** (`NotePushProgress`): while the raid pushes and its leader is not at its waypoint, the leader's 2D distance to the step goal must set a
  new least value, by more than `PushProgressYards` (2 yards), within `PushStallMs` (30 s). A push that does not is withdrawn and denied for the rest of the step
  (`PushDeniedStep`): the raid is back at Assault, which is the baseline, a note and a log warning say so (`push withdrawn, the leader got no nearer to waypoint N`), and
  the status detail ends in `push=denied`. Standing at the waypoint counts as progress (the lockstep and its own bound govern there), and so does an elevator trip of the leader at any stage (`PlayerbotElevator.IsTraveling`): both levels of an Undercity lift share their XY, so a wait and a ride gain no 2D yards; the elevator has its own bounds (60 s of no progress on the walk to the stop, two cycles and 30 s waiting for the car, a cycle and 30 s aboard, and aboard is also dropped at two cycles and 30 s if the car never stands anywhere).
  **Lockstep:** the step moves on once every leader stands at its waypoint, no living member of any raid is more than `LagYards` (30 yards) behind its leader, and each
  raid is out of combat or pushing. A pushing leader that reaches the next waypoint waits there, fighting back, until the members that disengaged (on the other lift
  level, say) have caught up. That wait is bound too: after `LagHoldMs` (90 s) of a member more than 30 yards behind, the lag is waived for that step change (`LagWaived`):
  an event note, a log warning and the raid warning `Pushing on without the stragglers` say so, and the status detail ends in `lag=waived` while the step cannot move
  yet because the peer, standing at its waypoint too, has a straggler of its own that is not waived yet (a peer that is not at its waypoint blocks the push instead, which resets the lag clock). Assault between waypoints ignores the lag because its leader does not walk. **Stall watch:** progress is the leader's 2D distance to the step goal setting a new least value by more than `PushProgressYards` (2 yards; `NoteGoalProgress`, a new step or a lift ride
  starts afresh), fighting or not, or a member fighting the objective; a fight in place that gets no nearer is not progress however long it runs (it used to count until `StalemateMs`), nor is a retreat
  latch, a hold of a push for the peer (`PushInForce` set, no progress) or a push whose leader gets no nearer (a pushing leader that is getting nearer is progress). A siege with no progress
  for `StallMs` (120 s) is reported stalled: `siege N: no progress towards waypoint K`.
  **Lifts:** a ride to the stop (`ElevatorStage.ToStop`) is ended by combat
  (`PlayerbotGroupAI.Update`), so a raid in combat at the lift top would begin the ride and cancel it at every think for as long as it stayed in combat
  (the cancel keeps no backoff). A raid that pushes through is exempt (`PlayerbotGroupAI.CombatEndsRide`): its leader and members ride on; when the push
  ends (the raid is back to Assault, or retreats), the cancel applies again. A member aboard is never cancelled (as before).
* **The retreat, in two phases.** The retreat point is the waypoint behind the step (where the siege began when the step is the first).
  **Retreat** (disengage) lasts while the leader is more than `RallyYards` (8 yards) from it: nobody fights or casts (a cast-time spell stops the
  mover, so healers do not heal either), the leader walks to the point, the members follow it, and no mark is set. **Rally** (the order name once
  the leader is within 8 yards) stands there: the leader holds, no mark is set and nothing new is pulled, and each member fights back only a hostile
  that attacks it (a healer also one that attacks a raid member). The latch ends when the raid has been out of combat for 5 s, or has stood at the
  point for `RallyHoldMs` (30 s) and still fights, or has been retreating for `RetreatMaxMs` (60 s) in combat before reaching the point (the bounds: a pursuit or pin that does not end is fought, not run from again), or the objective is engaged.
  A leader that can find no path to the retreat point (`lead:siege-retreat:no-path`) rallies where it stands, so the raid fights back and the 30 s bound applies; while it rallies only for that reason (and is more than 8 yards from the real point) it tries the walk again every 3 s, and when a plan succeeds the raid retreats again. The time at the point restarts at most once per latch, on the first retry that plans or the first promotion under a no-path rally, so a path that flaps cannot keep the 30 s bound from being reached (the latch then ends about `2 x RallyHoldMs` plus one retry period after the first rally, plus the time the raid spends walking: the bound counts only while it rallies, so a walk that keeps going ends on 5 s of quiet or the objective instead). A new leader (a promotion) does not inherit the no-path rally; a rally at a real retreat point keeps its time on promotion.
  A retreat is counted (`retreats=` in the status) where its latch begins, not on an order change: a raid that loses its leader holds meanwhile
  and resumes the same retreat after the promotion, uncounted. A flicker of the combat flag is not a new engagement. The leader's loop watch starts
  afresh at the start of a retreat (the walk back to the waypoint it came from reads as an A, B, A loop). Creatures leash after about 50 yards, and
  a defender drops a target beyond its post radius plus 10 yards: space the waypoints at least 60 yards apart so that the retreat point is out of
  the pursuit.
* **Marks and warnings.** The leader sets the skull (the unit to kill) and, for AssignTank, the cross (the second hostile, followed first by
  the tanks) through its own client packet MSG_RAID_TARGET_UPDATE, and says one **raid warning** (CMSG_MESSAGECHAT, type raid warning; Orcish for
  the Horde, else Common) whenever the order changes, never twice the same line in a row and at most one per 3 s (`PlayerbotSiegeAnnouncer`).
* **Leader.** When the leader dies the raid's best living member (a tank first) takes over through the old leader's own CMSG_GROUP_SET_LEADER
  (`PromoteLeader`); a leader that leaves is replaced by the member the server group now names. A raid with nobody alive ends.
* **Winning.** The objective entry is seen alive and later none is alive on the map *while the grid at its last known position is loaded*: each
  leader says `Siege complete`, the raids disband and the outcome is `won`. With that grid unloaded the objective is only unseen (the status line
  says so) and the siege goes on. A hot reload ends a siege as `stopped: reload` and tells the watchers.
  Bots with a defend post are not free for a siege (nor for a quest group). A bot with an event-scenario stay (`PlayerbotStay`) is still
  free for a siege, which enlists it and clears the stay, but the quest-group, run and battleground pick sites leave it out. A siege whose raids have all ended is `stopped: raids-ended`.
* **Status and watch.** `.playerbot siege status` prints the siege (step, running time) and a line per raid (leader, who lives, the last order and
  its numbers, retreats; the order is one of `hold`, `regroup`, `advance`, `pull`, `assigntank`, `assault`, `retreat`, `rally`; the detail adds `push=stalemate|quiet` while the raid pushes, `push=denied` once the no-progress bound has withdrawn the push for the step, and `lag=waived` while the lag hold is waived); `status $leader` only that raid
  and its last three raid warnings. `.playerbot siege watch on|off` makes the coordinator
  send the GM one system line per raid whenever its order, step or strength changes (at most one batch every 2 s), and one when the siege ends;
  without an argument it says whether you watch. Both are in memory with the siege.
* **Status.** `.playerbot groups` adds ` siege=1:raid1:advance:step0/2` to a raid's line.

## Configuration (`World:Playerbots:Groups`, live)

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | true | | form groups for group content |
| `MaxGroups` | 16 | 0..64 | bot-led groups at once |
| `LevelRange` | 5 | 0..60 | most levels between members |
| `MinTank` | 1 | 0..5 | tanks a group of three or more needs |
| `MinHealer` | 1 | 0..5 | healers a group of three or more needs |
| `FormationTimeoutSeconds` | 300 | 30..86400 | how long a bot waits for partners before it sets the goal aside |
| `RaidsEnabled` | true | | raid quests and raid instances as quest and instance goals (convert to a raid), and world-boss raids; it does not gate raid runs, which only `World:Playerbots:Groups:DungeonRuns:Raids:Enabled` switches |
| `InvitePlayers` | true | | invite a nearby real player on the group's goal into a short group (opted in to AI chat, or liked) |
| `InvitePlayerAffinity` | 20 | -100..100 | affinity that lets the bots invite a player without an AI chat opt-in |
| `InvitePlayerCooldownMinutes` | 30 | 0..1440 | minutes before the same player is invited again |
| `WorldBosses:*` | see "World-boss raids" | | raids on the world bosses of the continents ("World-boss raids", above; 24 keys) |
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
player never touched; and target 0 outside engaging, in the shape bot QA parses. `PlayerbotGroupTargetingTests` checks `CountsAsObstacle`, `Better`, `PlayerbotFightWatch`, the leader's 60 s no-path shun, `LastTargetGuid`/`LastTargetEntry` retention, and the member's 30 s unreachable memory. `PlayerbotGroupBoundedWaitTests` checks the 30 s lag catch-up and waiver, the 60 s gathering catch-up and drop, the 45 s approach no-progress bound, `SnapToMesh`, the 30 s quest group `no-path-to-approach` / `no-path-to-objective` faults, and the two bring-out cap in `Leaving`.

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
join an outside leader before the group gathers, and a leader revived inside walks out to the exit itself while the group stays in
`gathering` (a regression test pins that the group does not leave `gathering` while the leader is inside, with trash hitting a revived
member meanwhile). Without a path out the leader holds and the 60 s `GatherCatchUpMs` fallback is what moves the group on. This test does not establish an imported-world or real-terrain RFC clear.

`PlayerbotGroupAiPvpTests` (a member assists the leader against a flagged player and leaves an unflagged one alone, defends the leader
against a flagged attacker, follows the skull over the leader's victim and its own focus icon over the skull), `PlayerbotGroupLeadershipTests`
(the successor choice, and a dead leader passing the lead to the living tank with the client packet) and `PlayerbotGroupLiftTests` (a
group leader whose way leads up the Great Lift rides it).

`PlayerbotLeaderHandoffTests`: the clear list (nearest first, the current target kept, the dead dropped, a shunned creature skipped
but kept, a given-up one never rebuilt), the role assignment (one tank and one healer for five, never a refusal), the wing choice and
the chat lines; on the scenario world, a real player with five bots inside RFC passing the lead (the group adopted as a run of map 389
at `Clear`, the take-over line in party chat, the first pack killed), the same outside an instance (nothing adopted), a party `follow`
taking the lead back (the coordinator lets go, the party AI drives, the bots stay in the group), the Deadmines without a route cleared
from its list (the boss dead, the clear line, the lead back with the player), and a raid of ten in two subgroups adopted whole with its
subgroups kept.

World-boss raids (`PlayerbotWorldBoss*Tests`, `WorldBossTestKit`, `WorldBossRealRows`): the options and their ranges; the catalogue on the real world.db rows (the knownbosses, the derived ones, the faction leaders left out, operator entries); the status and campaign rules; the muster (level floor, caller, size,roles); the engage decision and the outcome and meter names; and on the group test world with the manual clock a full kill, a level-63 bossagainst level-60 bots, the holds that put players first (another's tap, real players near, a player's kill, the stall watch), the wipe counter,the catch-up guard, the caps, a battleground never taking a raid member, and a forming raid that drops silent invitees (`PlayerbotWorldBossFormingWorldTests`).

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

Cooperation tests: `PlayerbotRecruitmentTests` (the pure chance, refusal, ordering and guild-bonus rules), `PlayerbotRecruitWorldTests`
(recruiting, bot LFG answers, guild calls), `PlayerbotMentorWorldTests`, `PlayerbotRaidEscalationWorldTests`, and the player invites in
`PlayerbotGroupPlayerTests`.
