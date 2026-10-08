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
the victim before a swing).

**Between fights**: a member below 50% health or mana eats or drinks (the leader does not pull meanwhile); a healer resurrects a dead
member it can see (Resurrection, Redemption, Ancestral Spirit through the ordinary cast; the dead member's party intake accepts a
group member's offer); a member loots the corpses whose round-robin turn is its own. A dead member waits (unreleased) while a living
member could resurrect it, at most 60 seconds, then releases and runs back (`PlayerbotRecovery`; a ghost whose body lies inside walks
into the entrance and is revived there). A leader that died is waited for where the group stands.

**Loot rolls** (items at or above the threshold): need when the item is an upgrade the bot can wear (`PlayerbotItemScore.UpgradeGain`
with its build's weights), greed otherwise (`GroupLootVote`).

**Done** when every member that has a quest asking for the objective has its count (all have the credit: the server shares kill
credit with members in reward range), or, without a quest, when the objective creature the group engaged died. Inside an instance the
group then walks out, then disbands. The brains turn the quests in.

**Failure**: two wipes, 30 minutes on one goal, a step (gather, travel, enter, leave, regroup) taking more than 5 minutes, two minutes at
the objective's spawn without it in sight, too few members left, or the leader gone. The goal is then set aside for every member for
10 minutes (its quest and objective in the brain's suspensions, so the brain does not walk into it alone either), and the group walks
out of an instance before it disbands. A group that cannot walk out in time is brought to the entrance by the teleport service.

## Few bots and stray cases

* With few bots most needs find no partners: the goal is set aside after the timeout and the bot keeps soloing what it can.
* `MaxGroups` caps the groups; `Enabled` off forms no new group (running ones finish).
* A bot that logs in inside an instance (a world restart while it was in one) is let in: the login gate is now
  `PlayerbotMapPolicy.MayStayOnMap` (its map is allowed, or it is the dungeon it is in), as `playbots-dungeons.md` planned. A free bot
  inside an instance without a group walks out through the exit (a group of one in `leaving`): the brain has no way out.
* A free bot in a server group of bots nobody leads (the coordinator's groups are not restored after a restart, the server's are)
  leaves it.
* A member that is stopped, scripted, or leaves the server group leaves the bot group; the leader leaving ends the group.

## Configuration (`World:Playerbots:Groups`, live)

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | true | | form groups for group content |
| `MaxGroups` | 4 | 0..64 | bot-led groups at once |
| `LevelRange` | 5 | 0..60 | most levels between members |
| `MinTank` | 1 | 0..5 | tanks a group of three or more needs |
| `MinHealer` | 1 | 0..5 | healers a group of three or more needs |
| `FormationTimeoutSeconds` | 300 | 30..86400 | how long a bot waits for partners before it sets the goal aside |
| `RaidsEnabled` | true | | raid quests and raid instances (convert to a raid) |
| `InvitePlayers` | false | | invite a nearby real player into a short group |

## Where it shows

* `.playerbot groups` (GameMaster): a summary line (enabled, running, waiting, formed / completed / given up), one line per group
  (`group 1 quest:990720 quest=990701 size=3 source=quest state=engaging party failures=0 age=45s members=Tank(tank,leader),Heal(healer),Mage(damage)`),
  one per waiting bot, and the last five events.
* `.playerbot status` / `list`: ` group=1:tank:engaging:leader` or ` group=waiting:quest:990720:size3` at the end of a bot's line; the
  risk part reads `decision=group` while the group AI drives it.
* `.playerbot inspect`: a `BOTINSPECT group=...` line.
* A member's goal is `Group`, `Follow`, `Assist`, `Loot`, `Rest`, `Recover` or `Retreat`.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Groups/`: the pure rules (`PlayerbotGroupContentTests`: quest flags and sizes, instance
sizes, role composition, level range, the leader, raid subgroups, the risk estimate's group size, the group's elite margin); end to
end on the scenario world with the manual clock (`GroupTestWorld`: a giver offering only the test's quests, an elite ogre, a brute, a
raid warlord, a Deadmines boss, flat ground, a test Taunt and Resurrection): an elite quest done by a tank, a healer and a mage formed
through `CMSG_GROUP_INVITE` / `CMSG_GROUP_ACCEPT` / `CMSG_LOOT_METHOD` and disbanded with every member credited; an objective the risk
estimate passes over done by two; the healer healing the tank in a fight; the tank taunting the ogre off the healer; a dungeon quest:
through the entrance trigger into one instance bound to the group, the boss, out through the exit; a wipe retreated, the dead
resurrected, the group gathered again one failure down; a raid quest for six converted to a raid with the tank and the healer in
different subgroups; the formation timeout setting the goal aside; a real player's party bot left alone; `InvitePlayers` inviting a
nearby player who declines; the GM commands; a bot stranded in an instance walking out (`PlayerbotGroupWorldTests`,
`PlayerbotGroupPlayerTests`). `ConfigReloadTests` checks every key is live and range-checked.

## Limits

* Dungeon goals come from creature objectives; a dungeon quest that asks only for items is not recognised (its drop source would need
  the loot tables).
* Clearing is local: the leader pulls what stands within 20 yards of it on the way; there is no dungeon route or pull planning, no
  crowd control, no marking.
* Resurrection targets a dead member's body; a member that already released (a ghost) runs back instead (no corpse-target cast).
* The coordinator's groups live in memory: after a restart the bots leave the restored server groups and match again.
* Bots do not hand quests to each other (a bot without the quest is not matched for a quest goal).
