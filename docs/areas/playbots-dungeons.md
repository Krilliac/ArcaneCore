# Playerbots in dungeons: movement, area triggers, death recovery

The dungeon half of the managed playerbots (`src/ArcaneCore.World/Playerbots/`) that does not depend on the party AI: a bot can move
inside the dungeon it is in, crosses area triggers the way a 1.12 client does, and recovers from a death inside or outside an instance
without being quarantined. General bot behaviour: `docs/areas/playbots.md`. Faults and quarantine: `docs/integration/playerbot-faults-20261007.md`.

## Map policy (`PlayerbotMapPolicy`)

`World:Playerbots:AllowedMaps` (default `[0, 1]`) limits **open-world travel**. In addition a bot may always plan, walk and stay on
the map it is on now when the map registry (`map_template`) says that map is a dungeon or raid (vmangos `MapEntry::IsDungeon`):

| Bot's map | In AllowedMaps | Registry says | Allowed |
|---|---|---|---|
| continent | yes | common | yes |
| continent | no | common | no |
| the dungeon/raid it is in | no | instance / raid | **yes** |
| a battleground | no | battleground | no (its own systems decide) |
| unknown to the registry | no | — | no (as before) |
| anything | — (empty list) | — | yes |

`PlayerbotNavigation.TryPlan` and `TryAdvance` use `MayMoveOn`. `MayStayOnMap(Player, PlayerbotOptions)` is the same rule for the
login gate in `ManagedPlayerbotFeature.StartCoreAsync` (today `_options.AllowedMaps.Contains(player.Map.MapId)`); that file belongs to
the party-AI lane and is wired after merge. Until then a bot saved inside a dungeon under the default AllowedMaps is refused at its next
login (`login-refused`, quarantine) — the scenarios below always bring their bot back out.

## Area triggers like a client (`PlayerbotAreaTriggers`)

While a bot follows a route, `PlayerbotMotion.Pump` checks every world tick whether the bot's route position has entered an
`areatrigger_template` volume on its map — a sphere, or a box turned by its orientation (`AreaTriggerZone.Contains`, vmangos
`IsPointInAreaTriggerZone`, the exact volume without the server's 5-yard tolerance). On entry it reports where the bot is (a heartbeat
at the current route position, so observers see no jump) and sends **CMSG_AREATRIGGER once**; the volume fires again only after the
bot has left it. The server half is the ordinary handler (`TeleportHandlers.HandleAreaTrigger`, vmangos MiscHandler.cpp:653-800, with
the ghost rules of `GhostEntryRules`, MiscHandler.cpp:712-756): zone check with tolerance, listeners (quests, inns), requirements,
teleport.

* A bot that **arrives** inside a volume without walking into it (teleport, login, scenario placement) does not fire it: the first
  position on a map, and the first after `PlayerbotMotion.Reset` (death, teleport), only records what it stands in. This keeps a bot
  landing at a dungeon exit, beside the entrance box, from bouncing straight back in.
* Triggers are enumerated through `WorldMaps.AreaTriggers` and cached per map; the cache is rebuilt when the trigger table is reloaded.
* CMSG_AREATRIGGER is never refused for lack of the shared action budget (a client always reports its own triggers).
* **Live consequence.** Every trigger fires, as for a player: an autonomous bot whose route crosses a dungeon entrance (and meets its
  level requirement) is teleported in, and may then move there (map policy). Until `MayStayOnMap` is wired into the login gate, such a
  bot is refused at its next login if it is still inside then.

### Far transfers

The brain returns early for a player that is not in the world, so an autonomous bot never acknowledged a **far** teleport (it stayed
between maps for good). `PlayerbotMovementControl.AcknowledgeTransfer`, called first in every `PlayerbotMotion.Pump`, sends
MSG_MOVE_WORLDPORT_ACK for a bot in the far stage whatever drives it, like a client ending its loading screen. Before this lane no
autonomous bot took a far teleport (graveyards on its own continent are near teleports); a release from a dungeon and an entrance
walked into both are far.

## Death recovery (`PlayerbotRecovery`)

`Decide` (pure) picks the step:

| Ghost | Body | Situation | Step |
|---|---|---|---|
| no | — | — | release (CMSG_REPOP_REQUEST); after 60 s without a release: fault `playerbot-recovery-stalled` |
| yes | this map | away from it | walk to it |
| yes | this map | at it, reclaim delay running | wait (progress) |
| yes | this map | at it, hostile within 25 yd of it | wait (not progress) |
| yes | this map | at it | CMSG_RECLAIM_CORPSE |
| yes | another map | entrance trigger here leads there | walk into the entrance; the server revives the ghost inside |
| yes | another map, none, or stalled/stuck | — | spirit healer |

* **Death in a dungeon.** The release sends the ghost to the graveyard of the dungeon's zone, outside (vmangos `ghost_entrance`).
  `FindEntrance` picks the nearest trigger on the ghost's map whose `areatrigger_teleport` targets the body's map, else a dungeon the
  body's map is nested in (the server forwards the ghost, `GhostEntryRules`). The ghost walks there; the area-trigger layer reports
  the trigger; `TeleportService.TeleportTo` revives a ghost entering its body's map (`MapCombat.ReviveForDungeonEntry`, vmangos
  Player.cpp:1953-1966) at the entrance.
* **Progress bound.** No progress for 60 s (`NoProgressMs`), or a walk that stopped closing on its goal — or could not be planned at
  all — for 10 s (`StuckMs`), gives up the body for the **spirit healer**: the nearest healer the ghost sees (ghosts see spirit
  services; `GhostVisibilityRule`), else the graveyard of its position (`GraveyardSelector.FindClosest`). Within 3 yards it sends
  CMSG_SPIRIT_HEALER_ACTIVATE (vmangos NPCHandler.cpp:416) through the ordinary handler (`QuestNpcServices.SpiritHealerActivate`,
  50 % health and resurrection sickness). The fallback has its own fresh bound; only when it stalls too, or there is no healer and no
  graveyard, does the recovery fault with `playerbot-recovery-spirit-healer-failed`.
* The former throws `playerbot-recovery-stalled` (ghost) and `playerbot-recovery-stuck` are gone; every fault still goes through
  `ManagedPlayerbotFeature`'s quarantine.

## Scenarios (`dungeon-bot-*`, `Scenarios/ScenarioDungeonBots.cs`)

All use their own bot (`Scndelver`), need The Deadmines (map 36) and its entrance trigger 78 with its teleport, and bring the bot back
alive to where it logged in, whatever happened.

| Name | What it shows |
|---|---|
| `dungeon-bot-walk-inside` | a bot in The Deadmines plans and walks 15 yards with its own navigation, the dungeon outside AllowedMaps |
| `dungeon-bot-walk-into-entrance` | a bot walking across trigger 78 (no scripted CMSG_AREATRIGGER) is teleported in |
| `dungeon-bot-ghost-entrance` | killed inside and switched to autonomous: releases outside, walks in as a ghost, is resurrected at the entrance, no fault |
| `dungeon-bot-spirit-healer` | its body sinks 500 yards below the ground: the corpse run stalls, the bot takes the spirit healer, no fault |

The walking scenarios drive the bot with `ScenarioRouteWalker`, a controller that only calls `PlayerbotNavigation.TryPlan`/`TryAdvance`
(the brain's own navigation) and acknowledges server orders. On a live server the spirit-healer scenario needs a spirit healer at the
graveyard of the Deadmines entrance area (classic content has one).

Tests: `tests/ArcaneCore.World.Tests/Playerbots/Dungeon/` (map-policy matrix, trigger volume maths, the decision tree, world-level
recovery) and `Playerbots/Scenarios/DungeonBotScenarioTests.cs` on `DeadminesTestContent.RegisterForBots` (adds the dungeon's zone
row, a graveyard for it outside and a spirit healer) with the default AllowedMaps.
