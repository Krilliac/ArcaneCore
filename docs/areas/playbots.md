# Playerbots and playtest clients

ArcaneCore has two kinds of bot. **Managed playerbots** are server-owned P0 players inside the
world daemon; they run autonomously or are driven by a
script, which is the **scenario harness** for automated testing of game systems. The
**MockClient playbot** (`arcane-mock playbot`, last section) is an external build-5875
protocol client.

## Managed playerbots

**Project layout.** The bot code is its own project, `src/ArcaneCore.Playerbots/` (assembly `ArcaneCore.Playerbots`, namespaces
`ArcaneCore.World.Playerbots.*`), which references the world core; the world never references it. The options classes
(`PlayerbotOptions` and every `World:Playerbots:*` section class except QA's) are in `src/ArcaneCore.World.Contracts/Playerbots/`,
and the core-owned bot state (`ManagedBotRegistry`, the explore atlas, item scores and talent builds) in
`src/ArcaneCore.World/Bots/`. The world daemon loads the pack from beside its executable as a built-in at startup
([code hot reload, tier 2b](code-hot-reload.md#the-playerbots-pack-as-its-own-assembly-tier-2b)); the bot tests are in
`tests/ArcaneCore.World.Tests/Playerbots/`.

`ManagedPlayerbotFeature` (`IPlayerbotService`) owns persistent bot characters on random,
password-less Player accounts and logs them in through an ordinary socketless `WorldSession`
(`Net/ManagedWorldSession.cs`): every bot action is a real CMSG run through the real world
handler (`TryManagedAction`), and server replies are captured into a bounded outbound queue.
Configuration is `World:Playerbots` (`Enabled`, `MaxBots` 8, `MaxRegisteredBots` 1000, `ThinkIntervalMs` 500,
`MaxActionsPerTick` 48, `AllowedMaps` [0, 1], `FaultBackoffSeconds` 30, `MaxFaults` 3,
`FaultWindowSeconds` 3600, `Policy`, ...; off by default).

**Think cadence.** Motion runs every tick for every bot; a bot *thinks* (targets, spells, retreat) when the rotation reaches it
(`PlayerbotThinkRotation`). The tick's think time is `PlayerbotThinkBudget`: `clamp(ThinkBudgetTickShare x (tick interval - the time the
world spent on everything but the bots) - what the bots already used this tick, ThinkBudgetMinMs, ThinkBudgetMaxMs)`, defaults
0.6 / 8 ms / 30 ms (min = max is a fixed budget; at least one bot always thinks). The floor rises with the bot count to `bots x tick interval x cost of a main-lane visit / (0.5 x ThinkStarvedGapMs) + the time the combat lane uses` (`PlayerbotThinkBudget.CoverageMs`; the visit cost and the combat lane's time are measured, sized to the configured tick interval so a slow tick does not raise the floor and feed back into slower ticks; 1000 bots at 1.3 ms a visit and 50 ms ticks: 13 ms plus the fights), so a full walk of the bots fits in half the starvation bound while the 0.5 headroom and `PriorityScale` absorb slow stretches. Two lanes: a bot in combat, dead or a ghost that has
not thought for `CombatThinkIntervalMs` (300) is thought for first, for at most `CombatLaneShare` (0.7) of the budget; then every bot from
the fair cursor (the rest of the budget, so a crowd of fights cannot starve the others). The actions split in two hard budgets as well (the combat lane has `floor(CombatLaneShare x MaxActionsPerTick)` of its own, the rest is the other bots'), and the main lane thinks for at least one bot every tick. With room for everyone every bot still thinks
every tick. `.playerbot cadence` prints the last minute's think gap p50/p95/p99/max (all bots and the combat/dead lane), thinks per tick,
the budget and the share of ticks that ended on the time or action budget (the same line is logged once a minute). The coverage floor is sized from the *configured* tick interval (`World:TickIntervalMs`), not the real one (`PlayerbotThinkBudget.RealTickMs` is not used by the budget): a tick that overruns does not raise the bots' share, so with 1000 bots (about 10 thinks a tick) a full pass of the main lane takes 1000 x real tick / 10, 8 to 30 s at live ticks of 80 to 260 ms.

**Pass-derived bounds.** The feature measures that pass (`PlayerbotMainPass`: a smoothed estimate, quick to rise and slow to fall and so biased above the mean, of the gaps between two main-lane thinks of one bot; combat-lane gaps are not counted; a gap over 120 s counts as 120 s, so the report bound tops out at 180 s, the intended absolute backstop) and two things follow it, neither feeding back into the budget:
- *Motion lease.* A route is walked for `max(4 s, 8 x ThinkIntervalMs, 3 x the bot's smoothed think gap)`, the gap term capped at `PlayerbotMotion.LeaseCapMs(pass) = clamp(1.5 x pass, 15 s, 120 s)`. A bot with no gap of its own yet (fresh login or re-add: its first think skips the gap measure) is taken to wait one full pass, so its gap term is `min(3 x pass, cap)`: the cap itself once the pass is over 5 s (a 15 s lease at an 8 s pass, 30 s at 20 s). Before this the cap was a fixed 15 s, under the real gap, and bots stopped mid-road (`bot-motion/lease-expired-midroute`).
- *think-starved.* A bot silent for longer than `max(ThinkStarvedGapMs, 1.5 x pass)` (`PlayerbotThinkStarvation.ReportBoundMs`; 3 x `ThinkStarvedGapMs` until a pass is measured; 10 s default, 0 = off) is reported once per episode as the `cadence/think-starved` anomaly, so it flags a bot that fell behind the rotation, not a rotation that is slow for every bot. The budget floor and the combat lane's yield (`PriorityScale`) keep the unraised bound.

A world stall longer than 120 s (a pause, a long GC, a debugger stop) is not one sample: every bot's first think after the stall measures the stall, so the whole first pass afterwards feeds capped 120 s samples (the cap above, one per bot) and the estimate rises quickly toward 120 s (`RiseWindow` 8). That is why it holds near 120 s for about one pass: leases run at the 120 s cap and the think-starved bound sits near 180 s. Once that pass is over, normal gaps pull it down at the slow rate (`Window` 64, `PlayerbotThinkBudget`), which halves the excess in about 44 samples. How fast it recovers afterwards depends on the population: well under one pass at 1000 bots, several passes below about 64 bots. It is the smoothing at work, not a pass that really took two minutes.

The rotation cursors follow bot ids, not list indexes: the list is sorted by id and rebuilt when a bot joins or leaves, and the cursor resumes at the first id after the last one visited (`PlayerbotThinkRotation.Track`), so a change before the cursor skips nobody.

**Caps.** `MaxBots` (0..5000, `PlayerbotOptions.MaxBotsCeiling`) bounds the bots **running** at once: a stopped, faulted or quarantined bot holds no slot, and a
start beyond it is refused with `playerbot-capacity` (a startup restore or quarantine retry waits for a slot). `MaxRegisteredBots`
(0..10000) bounds `.playerbot create` (`playerbot-registry-full`); every registered bot is a real account and character. Both
are live through `.reload config`: a raise admits more starts at once; lowering `MaxBots` below the running count stops nobody,
it refuses new starts until enough bots are stopped. Until 2026-10-08 `MaxBots` was capped at 64 and creation counted stopped
bots too; the ceiling of 1000 and its measurement are in `docs/integration/perf-limits-20261008.md` (a ceiling, not a
recommendation: watch `.server info` on a real world).

**Client fidelity (`World:Playerbots:ClientFidelity`, `Lean` by default, read at start).** A bot has no client, so by default the
server does no real-client packet work for it. *Lean* skips exactly this: (1) the map builds no `SMSG_UPDATE_OBJECT` blocks for a bot
viewer (create on entering range, values, out-of-range; `IPlayerSession.WantsUpdateObjects` false), so no per-viewer block, zlib output
or copy exists for it; the bot's visible set, `VisibilityVersion` and the map's observer index are maintained exactly as for a player,
and that set (`player.VisibleObjects` plus `map.FindObject`) is all bot code reads of the world; (2) the session's 128-packet receive
ring copies only the opcodes a bot reads back, the one declared set `PlayerbotPacketIntake.RingOpcodes` (mail and auction replies, loot
response, instance reset, spell cast and spellbook packets, quest dialogue, trainer list, and the party and social intake opcodes;
`PlayerbotPacketIntakeTests` scans the source and fails when a reader's opcode is missing). The bot's own login create block, its item
create blocks (they set `SentToClient`) and every non-update packet other than the ring copy are unchanged. A bot with an observer
attached (a scenario tap, an outbound packet observer, or `PacketCapture` on its session) receives everything, as in Full. *Full*
treats bots like real clients (every block built and compressed, every packet copied into the ring): **use it as the load-test switch**,
to measure what N real clients would cost the world thread. What Lean did not do is counted: metric `arcanecore.playerbots.packets_skipped`
(tag `kind` = `update_block`, `ring_packet`, `ring_byte`) and a `lean packets skipped` suffix on the `Managed bots` line of `.server info`.

GM commands: `.playerbot create|start|stop` (Administrator), `.playerbot status|list|inspect`
(GameMaster), `.playerbot scenario list|run` (Administrator, below). The autonomous brain's
behaviour is described in `docs/integration/playerbot-*.md`.

On world shutdown, managed bots stop in groups of at most 32 while the world thread is still running. Each bot's
logout queues its character state and waits for that character's save to flush; the next group starts only after
all stops in the current group settle. The world thread and final save queue stop afterward.

Before a pull the brain weighs each candidate's risk against its reward, and in a fight it retreats past the creatures' leash when
it is losing (`World:Playerbots:Risk`, live): `docs/areas/playbots-risk.md`. The risk decision ends each `.playerbot status`
line and has its own `BOTINSPECT` line. Chests, locked chests it can open, rares and the guards of gathering nodes are weighed the same
way (`World:Playerbots:Opportunities`, live): `docs/areas/playbots-opportunities.md`.

Bots also use the faction auction house and the mailbox: they list loot they have no use for, buy one gear upgrade within a budget
and collect what sold, expired or was won (`World:Playerbots:Economy`, live): `docs/areas/playbots-economy.md`.

In town and out in the world, bots continuously pursue better gear, bags, spells, mounts and trade-skill crafting (`PlayerbotEquipment`,
`PlayerbotTownGoals`, `PlayerbotCrafting` in `src/ArcaneCore.Playerbots/Progression/PlayerbotCrafting.cs`): they equip wearable gear
upgrades and larger bags from their pack, use learnable recipe, spell and mount items in their bags (`PlayerbotEquipment.TryLearnFromBagItem`),
buy affordable weapon/armor/bag/recipe upgrades and crafting tools/reagents from vendors (`PlayerbotTownGoals.TryBuyEquipmentUpgrade`,
`TryBuyCraftingSupplies`), train class and trade-skill spells (`QuestNpcServices.GetTradeSkillTrainerQuote`) and riding mounts whenever no
quest objective creature is in immediate sight, harvest nearby herb and mining nodes (`TryGatherNode`), skin looted beast and dragonkin corpses
(`TrySkinCorpse`; the loot window of a gather, skin or disenchant cast is taken on the brain update after it arrives, between thinks as a
corpse's is, not on the crafting rung's next think, which higher rungs, other loot readers and the bounded packet ring stood between:
`PlayerbotCrafting.UpdateGatherLoot`, GitHub #774), craft wearable upgrades, bags, ammo, bandages, potions, food and intermediate
reagents (moving to a nearby forge or anvil when `RequiresSpellFocus` demands one), and hunt creatures that drop missing crafting materials (`PreferredMaterialDropperEntry`).

Content one bot cannot do (elite, dungeon and raid quests, objectives inside instances, quest targets too strong alone) becomes a
"needs a group of N" goal: `PlayerbotGroupCoordinator` matches bots by goal, team, level and role, the leader invites the others
through the ordinary group packets, and `PlayerbotGroupAI` drives the members until the content is done (`World:Playerbots:Groups`,
live; `.playerbot groups`): `docs/areas/playbots-groups.md`.

A target number of bots kept online, spread over level bands and zones and held back by the world's tick time, is the population
manager (`World:Playerbots:Population`, off by default, live; `.playerbot population`): `docs/areas/playbots-population.md`.

Bots found guilds, fill and rank them, say a short line in guild chat and (off by default) wave at nearby real players
(`World:Playerbots:Social`, live): `docs/areas/playbots-social.md`.
Free bots near a battlemaster of their faction queue for Warsong Gulch and Arathi Basin (Arathi Basin where the server routes its neutral
banners, as it does the classic-db goobers; never Alterac Valley), fill the queues real players wait in, play the objective (the enemy flag, the node banners with spell 21651) and leave when the match ends:
`PlayerbotBattlegroundCoordinator` and `PlayerbotBattlegroundAI` (`World:Playerbots:Battlegrounds`, live; `.playerbot battlegrounds`):
`docs/areas/playbots-battlegrounds.md`.

The goal ladder can be re-ranked by a built-in CPU goal policy (`World:Playerbots:Policy`, **off by default**; with no `ModelPath` it scores with the ladder itself, so it always agrees and the bots choose the goals they would without it). It replaced the ONNX/TensorRT batch pass and the local Ollama planner; see Goal policy below.

### Autonomous and scripted mode

By default a running bot is **autonomous**: each world tick `PlayerbotBrain` chooses quest,
town, trainer, combat, loot and recovery goals within the shared per-tick action budget.

Movement is separate from both: `PlayerbotMotion` advances every bot's current route every
world tick (before any think, outside the action budget) and reports it the way a 1.12 client
does — START, heartbeats every 500 ms and at turns (`TurnRadians` = 0.10 rad), STOP — so observers see smooth running
whatever the think interval. Brain, goals and scripted controllers only choose routes
(`PlayerbotNavigation.TryPlan` / `TryAdvance`). On navigated mesh routes (`IMeshPointQuery` present),
`PlayerbotRouteSmoothing.VaryAndSmooth` applies a deterministic per-bot lateral lane offset (`±2.2` yards)
and gentle sinusoidal weave (`±0.85` yards) along outdoor corridors at least 18 yards long (tapered at start and
destination, skipped on combat chases) so multiple bots walking the same road spread naturally instead of marching single-file,
and replaces sharp interior polygon turns with 4-point quadratic Bezier arcs verified against floor height, doodad-aware
line of sight (`PlayerbotMoveCheck.LineClear`), and navmesh raycasts. `PlayerbotMotion` also gives up navigation loops. A mesh
route's proof covers its own legs only: a bot taken off its route (a chase, an escape, a fear, a same-map teleport) and handed it
again walks a straight line from where it stands to the next point, and when it starts more than `OffLegYards` (5) off the leg
that line is checked as a stepped one (floor and line of sight), so it no longer runs through hills and rocks at its own height
(bot-motion/under-map in the Valley of Trials). Details and
the 2026-10-07 root causes: `docs/integration/playerbot-movement-and-tick-health.md`; whether
real terrain/collision/navmesh data is needed: `docs/integration/maps-vmaps-mmaps.md`.

**Game events.** A bot respects the server's game-event state (`World:GameEvents`, `game_event_quest`, `game_event_creature`): a quest
of an event that is not running is never offered to it (the server's `CanTakeQuest`, vmangos `Quest::IsActive`) nor turned in
(`IsRewardable`, vmangos `PrepareQuestMenu`), and one already in its log is kept but its objectives are not hunted (alone or as a group
goal) until the event runs again; a quest giver or ender whose spawn is listed under an event is not a destination while the spawn gate
keeps it out of the world (`PlayerbotWorldDestinations.IsPresent`), so a complete quest whose only ender is a holiday NPC is not walked
to off season. This needs the world's game-event tables: a world migrated from the Codex-line schema has them empty (the live world of
the wave-8 rehearsal: bots took "Winter's Presents" 8827, "Dearest Colara," 8898 and the Lunar Festival and Midsummer quests in October)
until `arcane-content-importer refresh` fills them (docs/areas/content-import.md).

The live snapshot replay (`PlayerbotLiveSnapshotReplayTests`: `ARCANECORE_TEST_BOT_REPLAY_DB`, `ARCANECORE_TEST_WORLD_DB`,
`ARCANECORE_TEST_TERRAIN_DIR`, optional `ARCANECORE_TEST_BOT_REPLAY_SETTINGS` without a `Database` section, `..._NAMES`, `..._TRACE`,
`..._TIME`) runs the game-event clock from the snapshot's moment (`ARCANECORE_TEST_BOT_REPLAY_TIME`, ISO 8601, else the characters
file's last write time) with game time, as it already ran the death clock: before, events followed the real date while the replay ran
its game time about 30 times faster, so a replay in December would have turned Winter Veil on for an October snapshot. It waits for each
step's quest reward writes (on the fast clock a bot's 15-second exchange deadline ran out while the write was on its way, and the reward
counted as refused) and prints each bot's quests taken, rewarded and still open.

For an unhomed bot, `World:Playerbots:MaxDeliveryYards` (1200 by default, 0 disables the distance limit) compares
the quest giver with the nearest same-map ender. A proven longer trip is skipped at visible pickup and offscreen travel.
A completed quest follows exactly this rule (L is `MaxDeliveryYards` for an unhomed bot and 0 for a homed one; the bot's
continent C is map 0 or 1, and on any other map the quest is kept). 1. Reachable now: if any ender spawn of the quest
(present or not) is on C and either L is 0 or the bot is within L yards of one, the quest is kept. 2. Otherwise it is
statically infeasible only if it has a creature giver on map 0 or 1 and, for every giver map G, there is no ender spawn on
G, or L is above 0 and every giver-to-ender distance on G exceeds L; a quest with no creature giver on map 0 or 1 is never
infeasible. The quest is abandoned only when it is not reachable now and statically infeasible, so a bot on the ender's
continent, or one that merely wandered far from a local hand-in while finishing objectives, keeps it. Successful
abandonment uses `CMSG_QUESTLOG_REMOVE_QUEST` and frees the slot. An unhomed bot marks an abandoned quest *unreachable* for a while; a
homed bot and a server-refused packet record no mark. The mark is the bot's unreachable-quest deferral
(`DeferKind.UnreachableQuest`, below): it lasts 60 seconds, then 10 minutes, 1 hour, 6 hours and 24 hours with each repeat (the last holds, so a
quest the bot keeps abandoning is not taken again every few minutes for ever; a turn-in that can never be satisfied escalates the same way), and ends
sooner when the bot gains a level or the quest store, creature definitions or navigation dataset is replaced (the fleet-wide causes release
after a stable 0 to 60 s stagger per bot, not all at once). It is saved with a finite expiry and its strikes in the row's `RepeatCount`; a row
an older build wrote with no expiry (`long.MaxValue`) is restored with a staggered one to five minutes and two strikes. At most 64 are kept
(when full, a new mark replaces the ended one with the fewest strikes, else the one that ends soonest; see the defer ledger below). A refused abandon packet is retried only after the normal refusal interval. A quest whose enders are all on
the other continent from its giver is still abandoned when the distance limit is disabled; unknown or scripted enders
remain eligible. Homed bots keep their home-area and short-return rules. Giver-to-ender facts (per-map giver presence and
ender spawns with their nearest-giver distance) are shared and cached by quest store, creature content, definition
version and quest; pickup still honours ender spawn gates, while the completed-quest rule reads the bot's position and the
live option on each decision and ignores spawn gates.
In the read-only schema-53 world snapshot, 484 of 3,221 quests with creature givers and enders on the same map
(15.03%) have a nearest pair beyond 1200 yards. All six reported delivery stalls (120, 118, 1420, 6384, 164, 165)
fall beyond it; the shortest is quest 165 at 1312.7 yards. The default retains those six exclusions while allowing
local hand-ins after long objective travel. It can exclude other legitimate long deliveries, and 0 opts out of the
same-map distance limit. Terrain and hostile pockets have separate limits; long trips may be flown (see Flight paths).
At pickup and offscreen travel (`MayTakeQuest`, `RefreshEntries`), `PlayerbotQuestGoals.CanExecuteObjectives` also skips
non-auto-complete quests whose mechanics bots do not execute: exploration/escort/event-script quests (`QuestSpecialFlags.ExplorationOrEvent`),
GameObject objectives without an item-use source (`ReqCreatureOrGOId < 0`), item objectives (`ReqItemId`) not provided by `SrcItemId`, not already held in
inventory, not sold by any vendor (`NpcCatalog.SellsItem`), and having zero creature droppers in loaded `LootContent` (`PlayerbotDropIndex.DroppersOf`, such as
GameObject-harvested quest 365 "Fields of Grief" or 3902 "Scavenging Deathknell"), spell-cast or friendly creature objectives without a quest source item
(`ReqSpell != 0` or `!IsKillObjective` when `SrcItemId == 0`, such as quest 5382 "Doctor Theolen Krastinov, the Butcher"), and creature objectives whose
spawns exist only on another continent or an instance without an entrance trigger on the bot's map. When a creature objective (`ReqCreatureOrGOId > 0`)
is friendly or requires a spell cast and the quest provides a source item (`SrcItemId != 0`, such as quest 5441 "Lazy Peons" with Foreman's Blackjack 16114
on Lazy Peon 10556), `CanExecuteObjectives` and `Pick` accept it (`IsItemUseCreatureObjective`, requiring the item to remain in the bot's inventory once
`Incomplete` in the log); `PlayerbotQuestGoals.TryUseObjectiveItem` walks within interaction reach of visible targets (preferring sleeping targets,
`StandState.Sleep` or `LazyPeonAI.IsAsleep`), sends `CMSG_USE_ITEM` with `PlayerbotNavigation.UseItemOnUnitPayload`, and skips woken or recently used
targets (`IsUnavailableItemTarget`, `ItemUseTargetCooldownMs` = 60,000 ms) so `PlayerbotBrain.IsUntakeable` and `PlayerbotWorldDestinations` rotate to the
next spawn camp. For a completed quest already in the log,
`CompletedReturnQuest` (`CanReturnToEnder`) holds the quest in the log without selecting it as the active walking goal when its ender lies
beyond the walking return range (`MaxDeliveryYards` outside a home, `MaxUnhomedReturnWalkYards` = 3000 for an unhomed bot) and no known,
affordable taxi route reaches it (`PlayerbotTaxi.CanTravelByTaxi`); a leave-home quest (`PlayerbotHomeArea.QuestLeavesHome`) must also have
any spawned starter on the home map inside that home so a quest begun in another zone (such as Silverpine quest 428) is not treated as a
leave-home quest of a later home.

A far goal (a quest giver or ender, a corpse) is approached a leg at a time (`PlayerbotNavigation.TryPlanToward`). Beyond
`MaxPathPoints - 2` yards the map's region graph leads (`NavRegionGraph`, [collision-pathing](collision-pathing.md); built off the world
thread on the first far goal of a map, seconds for a continent): its search finds the way across the map coarsely, round mountains and out
of dead ends, and the leg is one path query to the farthest point of that way within `RegionLegYards` (1000) and half of `MaxRouteYards`
(the goal itself when that near). A goal the walkable mesh does not join to the bot's place gets the way to the portal nearest it, as a
partial path; when the region search reports `NavRegionRouteStatus.Unreachable` and either no portal lies closer (`portals.Count == 0`), the bot already stands within `NearestPortalRefusalYards` (4) of the final portal, or the Detour query to the chosen/first portal fails, `RegionLeg` refuses outright instead of falling through to straight-line Detour steps into dead-end edges. When a connected region route's query to the chosen portal is no route, the old steps below still get their turn. Before, a far goal was approached only by points on the straight line, and in the mountains the next one lay in rock: no
route at all from any bot-QA stall point of issue #191 (quest 5881, Stonetalon to Grish Longrunner at the Great Lift, 5,200 to 7,900
yards) or #194 (quest 1418, the Swamp of Sorrows to Kargath, 3,400 yards), and every bot set those quests aside. A partial path to the goal
itself was tried too: it reaches Grish, but from the swamp it ends at the point nearest Kargath (-9356.8, -3872.5) and goes no further,
while the way leads west and north by Deadwind Pass, Redridge, the Burning Steppes and Blackrock Mountain. Now every stall point's legs
reach the ender: 9 to 13 legs of 6,500 to 9,400 yards, each plan 5 to 80 ms (`FromAnIssueStallPoint_...`, real terrain).

Fragments of mesh and goals no walk reaches (bot QA cluster C3: the same stall point across bots and goals). Real-terrain probes
(`PlayerbotDeadEndPinProbeTests`) found two kinds behind some of the pins. A *fragment*: the ridge pocket above the Valley of Trials
(-137.6,-4265.7 map 1; issues #379, #402; 3 regions, a Detour search exhausts after 26 nodes) and the sliver inside Ironforge
(-4715.7,-871.2 map 0; #381, #537; 1 region, 2 nodes): nothing on the mesh is reachable from them, so a bot there was given the fragment's
own few yards or a refusal for every goal. A *goal no walk reaches*: Chief Hawkwind's platform in Mulgore from the Durotar ridge, 4,900
yards away. The platform is reachable in the game (a player gets there); the bots' *walking* mesh, which excludes steep slopes, does not
join it to Durotar, so it is a region of its own there. A bot was given an 847-yard leg and a 3,349-yard walk toward it that no walk on
that mesh ends. Both are read from
the region graph's connected components (`NavRegionGraph.ComponentOf`/`ComponentSize`, labelled once per graph, then an array read),
which is exact and needs no path search.

- **Unreachable goal skip** (`PlayerbotNavigation.IsUnreachableOnFoot`, used by `PlayerbotWorldDestinations` only, and diagnosed by `ConfirmUnclimbable` after three failed plans for a completed quest with a single ender so disconnected-component enders such as Chief Hawkwind 2980 on the Mulgore mesa are set aside and abandoned after three set-asides): after the tram, lift and taxi trips were tried, a spawn whose region is in another component than the bot's (walking
  graph, steep slopes excluded) and that stands outdoors (an indoor goal may be climbed to over steep stairs, `TryClimbIndoors`) is skipped
  without a path query, and the next spawn is judged in the same think. The answer is true only for a bot on a large part (its own
  component is not a fragment: more than `FragmentRegions` regions or more than `FragmentPolygons` polygons), and outdoors; a fragment bot
  or an indoor bot gets false, so `PlanToward`'s own escapes (`TryLeaveSteepGround`, `FromBeside`, the fragment hop, the stall path) run.
  A bot on steep ground is not told apart: the region lookup snaps it to a walking region within about 3 yards (horizontally, or just above
  or below) and it is judged from that region; with none in reach the answer is false. With no other spawn of the entry left, the entry
  is set aside. The quest follows only when none of its open objectives (creature, game object, item, as the bot's quest log has them)
  can still be served: for a hunted objective the quests of the log that the entry serves (as a creature objective or an item's dropper)
  are judged, for a giver or ender trip the quest of the trip. Neither counts towards abandoning the quest (`SuspendQuest(..., countsAsSetAside: false)`). Every other planner caller plans as before.
  The check also names the two components in the `bot-nav/goal-unreachable` anomaly (`RepeatedRefusals` plans in a row refused,
  `PlayerbotNavigationAnomalies.ObservePlan`): each place's own component whatever the answer, -1 only for the place that is on no walking
  region. Before, one place on none gave -1 for both, and a goal off the walking mesh (a retreat point in a hillside or in the air) read
  "bot -1, goal -1" although the bot stood on the continent (bot QA #807).
- **The skip is reported**: one warning per entry and bot per 600 seconds, `Playerbot <id> (<name>) goal unreachable on foot: goal=G
  target=T quest=Q at x,y,z map M components=a/b` (`ManagedPlayerbotFeature.LogUnreachable`); the goal is the one held when the skip was
  met, and every skip of one think is queued (at most 8 waiting) and logged. Bot QA parses it as its own kind,
  `UnreachableGoal` (label `unreachable goal`, cause `MissingData`), keyed by goal, quest and target, never by the bot's place; the stall
  detector and its bounds are untouched.
- **Fragment escape** (every `TryPlanToward` caller, asked only when a plan toward a goal farther than 60 yards is refused or only a route
  under 30 yards, never per tick): a bot whose component has at most `FragmentRegions` (3) regions takes one straight hop, a stepped
  terrain leg (`TryTerrainRoute`: floor within a yard per yard, in sight) to the nearest point within 20 yards on a larger component, tried
  at most once per 5 seconds of the world clock (a miss caused by a navigation tile still being read does not spend the window; callers
  that pass no clock never hop). A fragment is also small in area: a component of three regions with more than `FragmentPolygons` (500)
  polygons is a plaza, not a fragment. When the ground beside the fragment is a cliff or too far, as at both live pins, the stall path takes over:
  the brain's island escape (`EscapeIsland`) treats a fragment bot as an island bot and teleports it home or to its bind point.
- **Dead-end pin memory and Hearthstone escalation** (`PlayerbotStallWatch.RecordPinStall` / `IsDeadEndPin`, `PlayerbotSuspensions.SuspendEntryEscalating`):
  each stall give-up records the bot's 10-yard coordinate cell (a `DeferKind.PinCell` key packing map, floor(x/10), floor(y/10); bounded to 64 cells per bot). Once a cell
  accumulates `PinStallsToEscalate` (2) stalls, `PlayerbotNavigation` refuses destinations and partial-route endpoints inside that cell (`IsDeadEndPin`)
  for the cell's backoff since its last stall (10, 30, then 60 minutes from the second stall, read against the world clock at each ask; the first stall is recorded but is no pin yet; a cell whose backoff ended an hour ago starts again; from the third stall a replaced navigation dataset frees it),
  `GiveUpStalledGoal` escalates the stalled target entry's suspension (`SuspendEntryEscalating`, doubling up to `MaxSuspendMs` = 80 minutes) and marks
  repeated quest stalls unreachable (the escalating mark above), and `EscapeIsland` treats the pinned bot as an island escape, teleporting it home or to its bind point.
- **The defer ledger** (`PlayerbotDeferLedger`, one per bot, `PlayerbotSuspensions.Ledger`): the one place a bot records what it put off.
  A key is a `DeferKind` and an id; `Defer` gives it a deadline (the kind's step by strike: entry, quest and training keep their 10 minute steps, the entry doubling up to 80 minutes; an unreachable quest 60 s, 10 min, 1 h, 6 h, 24 h;
  a pin cell 10, 30, 60 min from its second stall) and optional wake causes
  (`DeferWake`: a level gained, a replaced navigation dataset, creature definitions or quest store). `PlayerbotBrain.ObserveDeferWakes` compares the bot
  with its last think once per alive think (a handful of field reads through feature references cached per brain, so a swap between a deferral beginning and the next think still wakes it), and wakes the waiters; a wake or a lapse keeps the strikes (so the next deferral is longer), `Succeeded` forgets them, an hour of quiet decays them (except an unreachable quest's, which only a turn-in clears: `PlayerbotQuestGoals` calls `QuestSucceeded` when the reward is confirmed, forgetting the quest's unreachable and quest strikes, so a quest that once succeeded starts clean).
  Caps: 64 per kind (1 for training; 257 in all, under the store's 512 rows per bot). Recording never refuses, so when a kind is full a new key always costs one existing entry its strikes: the ended entry with the fewest effective (decayed) strikes goes first, the oldest of those; a live deferral is cut short only when no entry has ended (then the one soonest to end). Time: the ledger runs on the world's 64-bit `Uptime` in milliseconds (the facades convert the 32-bit `NowMs` against it), so an ended deferral never reads live again at 2^31 or 2^32 ms of uptime, whatever the bot did or did not do in between.
  Entry, quest, training and unreachable-quest deferrals persist (`PlayerbotSetAside` kinds 0, 1, 2, 5; an entry's and an unreachable
  quest's strikes in `RepeatCount`); an unreachable quest whose set-aside ended is saved too (expiry = the save time) so a restart keeps its strikes until a turn-in; the rest live in memory. A restart restores an escalated entry for its full remaining time (up to the kind's longest) and continues its
  escalation. `PlayerbotSuspensions` is the facade the goals call.

Neeka Bloodscar, #194's ender, stands at the top of the Kargath tower, whose upper flight of stairs the mesh flags steep: the walkable mesh
ends 13 yards below her. A destination indoors (inside a model's group, `MapCollision.IsOutdoors`) is reached over steep polygons when the
walkable mesh ends within `ClimbReachYards` (20, flat) of it and the way over steep polygons is at most `ClimbExtraYards` (60) longer
(`PlayerbotNavigation.TryClimbIndoors`; `FromKargathsFloor_TheBotWalksUpTheTowerStairs_ToNeekaBloodscar`; each guard pinned by `PlayerbotClimbIndoorsTests`). Not in dungeons, raids or battlegrounds, where nearly every place is indoors. Steep terrain outdoors stays
refused.

Without the region graph (a map without one, or while it is built) the old steps plan: the point
`MaxPathPoints - 2` yards along the straight line (at most 90% of `MaxRouteYards`), or the walkable point the mesh reaches nearest to it.
Navigation-mesh tiles load with the map's grids round the players, so that point may lie on a tile not loaded yet, where the mesh
answers a straight line (vmangos `PathFinder`'s `HaveTiles` shortcut) that the terrain stepper refuses over any hill; the step is then
halved (down to 24 yards) until it stays on the loaded mesh, and walking it loads the next tile. Before, the bot stood at such a tile edge
for good: the wave-8 rehearsal's quest-35 stall (Dawnrover 107 yards north of the Elwynn tile boundary, 610 yards from Guard Thomas).

**Legs that do not come from a mesh corridor are checked for doodads and the mesh edge** (`PlayerbotMoveCheck`). The mesh proves its own
corridors walkable; nothing else did, and the line of sight they were checked with ignores M2 doodads (the vmangos default for spells), so
bots walked through fences, trees and rocks: the server does not collide a managed bot's movement packets. The legs concerned are the
stepped terrain route (`TryTerrainRoute`: no mesh, or an endpoint tile not loaded), its per-step check (`PlayerbotMotion.Validate`,
`TryStepOnMap`), the walk to a follow-drop edge and the retreat trail
(`PlayerbotRetreat.TrailRoute`). Each is checked with (a) a line of sight at chest height (1.2 yards) **including doodads**
(`ignoreM2: false`; spell line of sight is unchanged, the shared default stays) and (b) where a mesh is loaded under the leg, the mesh
raycast (`IMeshPointQuery.Raycast`): a leg that leaves the mesh (a hole, a building's footprint, a wall edge) stops there. A leg whose
start is off the mesh, or whose ray ends where no mesh tile is loaded beyond (tested with a second, zero-length query one yard past the
edge), is not judged by the mesh and keeps (a) alone. The per-step check of a stepped leg (`PlayerbotMoveCheck.TryValidateStep`) also
needs a walkable floor under the step: at most 2 yards above the step's height, and at most `DirectStepMaxDrapeYards` (11) below it, the
step then draped onto that floor (a route whose waypoint heights drifted above the ground; the bot itself is draped first, before it steps),
but never more than `DirectStepMaxDropYards` (2.5, equal to `PlayerbotMotion.DirectStepDrapeMinYards`: a standing bot lower than that is not snapped first, so its first step is not refused) below the lower of the step's two ends: a bot standing on the floor does not step off an edge into a pit or down a cliff (`AHazardDetour_KeepsEachLegsProof_TheMotionChecksTheSteppedLegs`, `PlayerbotDirectStepDropTests`, `DirectStepRouteWithElevatedWaypoints_DrapesToWalkableFloor_WithoutAirwalkAnomaly`). A step down a slope that falls more than that over the step is refused too; descending walkable slopes is a separate design task. A blocked leg of the retreat trail (`TryClip`) is cut half a yard before the obstacle (bisection of the segment, 6
steps) when at least 1.5 yards of progress remain, else refused; a stepped terrain route is refused whole instead of
cut, and `TryPlanToward` then plans a shorter chunk (down to 24 yards), so the mesh's own shorter step stays preferred. Cost: one raycast and one vmap query per leg when the route is planned, a few
more only when blocked; the per-tick step check uses the line of sight alone; no allocation beyond the route. Tests:
`PlayerbotMoveCheckTests` (stand-in line of sight with an M2-only wall, a mesh hole, a border with no tile) and
`ADoodadFenceNearKharanos_BlocksAStraightLeg_TheDefaultSightLetsThrough` (real Dun Morogh fence, `ARCANECORE_TEST_TERRAIN_DIR`).

Every bot route excludes the mesh's steep polygons (`NavTerrain.SteepSlopes`). A bot standing on steep ground, or on a patch of walkable
polygons among them, got no route at all (no path, or a partial path ending at the patch's edge) and stood there for good: Ironwander on
the mountainside north of Coldridge Valley (-5605.6, 155.8, 453.3), live 2026-10-09, `stalled 7960s: goal=Grind target=1196` (the goal
and target were the last ones it had chosen; every plan since had failed). When the ordinary query fails and the bot is not on walkable
ground with an off-mesh destination, `PlayerbotNavigation` asks the mesh again with steep polygons allowed and takes that path up to its
first corner on walkable ground from which the ordinary query is a route, refusing any way that leads more than
`SteepEscapeClimbYards` (1.5) above the bot (`PlayerbotSteepGroundTests`; on the real terrain
`FromIronwandersSlope_TheBotWalksDownToTheIceClawBear`). The stall watch also gives the goal up again after every further stall bound
without progress (`PlayerbotStallWatch.GiveUps`, `StallGiveUps` on the brain), not only when the stall begins; the stall is still counted
and logged once.

**Ashenvale quest 6562 (bot QA #199).** Je'neu Sancrea (entry 12736) is an overworld ender on map 1 at
(3355.07, 1033.14, 6.03), not a creature inside Blackfathom Deeps (map 48). From the recorded stall at
(2770.4, 1275.2, 319.1), the real mesh with steep polygons excluded ends at the bot's feet; with them allowed, the first
16 corners of its route gain 97.5 yards of height along legs steeper than 45 degrees. The ridge is not a mesh island
(`IsOnMeshIsland` is false: the probes to the south-west lead 60 yards and more) and the ordinary mesh plans routes to
goals down the slope, so the bot is free to leave once this quest stops being its goal. One failed plan proves nothing (a
bot momentarily on a steep patch gets the same refusal), so `PlayerbotWorldDestinations` confirms before it acts: the
third failed plan (`ConfirmFailures`) of a completed quest with a single indexed ender, from the same spot (within 3 yards),
runs the diagnosis `PlayerbotNavigation.RequiresSteepAscent` once for that quest and spot (two queries capped at
`DiagnosisSearchNodes`, 8192, not the 32,768 of a full route). It sets the quest and entry aside for this bot for ten
minutes only if the steep-allowed path gains at least `SteepAscentYards` (15) on legs steeper than `SteepLegSlope` (0.9),
and logs the bot's name and the measured climb. A gentle uphill to a turn-in, a transient failure, and a quest with a second
ender are never set aside. The third set-aside of the same quest abandons it with the client's abandon packet instead of
cycling every ten minutes. The brain's `AbandonDistantCompletedQuest` (codex/w6-setaside-persist; it runs before
`CompletedReturnQuest` and, for a homed bot, drops a completed quest with no ender in the home or within 600 yards)
acts first on far enders;
this set-aside covers the enders that are near on the map but behind steep ground. Real-terrain tests:
`Quest6562_FromAshenvaleRidge_TheClimbToTheEnderIsMeasuredOnSteepLegs` and
`AtTheAshenvaleStall_TheRidgeIsNoMeshIsland_AndTheBrainWalksTheBotOff`; the decision itself is tested through the brain in
`PlayerbotQuestSteepReturnTests`. None of this establishes a walkable approach from the ridge or completes the quest.

**Enders among hostile creatures (bot QA #251/#261, #242, #243).** On a completed quest's return trip, a strong hazard
caused only by overlapping mobs may be crossed when each mob is manageable alone at full health and is no more than three
levels above the bot (`PlayerbotRiskModel.BeyondStrengthAlone`, `PlayerbotRisk.MaxLevelsAbove`). A mob individually beyond the
bot's strength, an over-level mob, a lethal spell, a remembered flee, and a death or retreat place still block the route;
`TryAdvance` applies the same rule if hazards change while walking. A connected route blocked by one of these hazards is
diagnosed after the same three failures from one spot as a steep ascent, then the quest is set aside for ten minutes. The
third set-aside no longer abandons a completed quest (bot QA #283: 47 finished quests were abandoned in one 4-hour window): if the
bot's bind point is on the same map within `BindNearEnderYards` (250) of the ender, the bot teleports there (non-urgent, the
brain's five-minute bind-teleport cooldown, and not when the bot already stands within `BindArrivedYards` (50) of it) and keeps the quest; either way
the quest is set aside for ten minutes again. A hazard that never goes away cannot hold it forever: after `MaxHeldSetAsides` (12, about two hours) set-asides
with no level gained since the first one the quest is abandoned after all (a level gained restarts the count; in memory only).
A steep-ground refusal still abandons on the third set-aside, and its ID is retained in a bounded, per-bot persistent refusal so
its giver cannot offer it again to that bot. `PlayerbotQuestEnderPocketTests` covers the hand-in and over-level refusal with synthetic creatures;
`FromStarbreezeStall_TheMeshRoutesToGaerolas` probes the extracted terrain without simulating live combat.

Bot QA #218 exposed a second stop near Je'neu: from three Ashenvale approach points the region route reached
the last walkable point about 5.6 yards from his centre and found no further mesh route. The bot had required
5 yards between centres, while the quest server accepts strict 3D distance below 5 yards plus both bounding
radii (vmangos `src/game/Objects/Object.cpp`, `WorldObject::IsWithinDist`; Je'neu model 12953 radius 0.306 and
player radius 0.389). `PlayerbotQuestGoals` now uses that same reach before planning another step; the
packet-level `QuestGiverWithinServerRadiusAdjustedReach_IsQueriedWithoutAnotherPath` confirms the exchange.

Some walkable ground is a navigation-mesh **island**: polygons the mesh joins to nothing round them, so every route from there ends at the
island's edge nearest the goal and the next plan is refused, whatever the goal. Inside the round wall of the Molsen Farm in Westfall
(x -10305..-10274, y 1355..1385) the collision data closes it too: the wall stands 1.7 to 3 yards above the ground inside, and no floor
within a step and in sight leads over it. Live 2026-10-11 (bot QA #180, live-w23-r1, main 85023136) Wfbotac and Wfbotaj stalled there with
every goal they took (quest 65 to Wiley the Black, quest 2039 to Bingles Blastenheimer, the Harvest Watchers beside the farm), for hours and
across restarts (Wfbotac was saved there). When a stall gives a goal up (`GiveUpStalledGoal`, including a failed tram trip's bot-local
set-aside), the brain asks
`PlayerbotNavigation.IsOnMeshIsland` (12 partial queries 120 yards out; an island ends every one within 55 yards; the queries share
`IslandTotalSearchNodes` (16384 nodes, at most `IslandSearchNodes` (2048) each) and one that spends the budget or is cut short proves no island,
bot QA #1483: it had run 12 synchronous searches of 8192 nodes, a 504 ms think) and, out of combat in the
open world, moves the bot off as from an unsafe landing: a homed bot teleports home (`PlayerbotHomeTeleportReason.Island`, logged `stalled on
ground no route leaves`) when that teleport would be made, else (an unhomed bot, or the home teleport's 5-minute cooldown) to its bind point.
The bind-point teleport, here and after an unsafe landing, is refused as the teleport home is: in a group (a grouped bot can still run the
brain), a duel, on a battleground, in the air, during another teleport (an unsafe landing replaces the resurrection's near one), to a map
not allowed; not urgent, at most one every 5 minutes, so a bind point that is itself an island cannot loop (`PlayerbotMeshIslandTests`,
`AGroupedUnhomedBot_RevivedAmongStrongHostiles_IsNotTeleportedToItsBindPoint`). An exploring bot that stays within 55 yards of its
first position for 15 seconds asks the same island question once before the full stall bound. This handles the isolated upper Stormwind
ridge near (-8490, 489, 150), where short routes repeatedly ended within ten yards, and the lower ledge near (-8486, 510, 121), where
the bot could move twelve yards without leaving the island and the mesh ends within 50 yards in every direction. Both previously held the
bot for two minutes
(bot QA #207, `StormwindRidge_ExploringBotEscapesTheIslandBeforeTheStallBound`). Connected ground around the sampled #221, #180,
#130 and #131 points remains on ordinary exploration (`ConnectedExploreGround_DoesNotTriggerAnIslandTeleport`).
An island beside water or a void, where a probe point
has no polygon near it (a NoPath answer), is never detected: its bots still stall and give their goals up one by one.

**Intercontinent quest handoffs.** The bot's tram and elevators stay on one continent, and its ordinary
quest travel has no ship or zeppelin route between maps 0 and 1. Before acceptance, visible and offscreen
starters are skipped when all known static creature enders stand on the other continent; a local alternative,
unknown scripted ender or instance ender is left eligible. This excludes the Winterspring to Eastern
Plaguelands deliveries 6030 (#236) and 5601 (#235) from autonomous pickup. Previously completed copies
held by unhomed bots are abandoned through the normal quest-log packet. The existing home-area rule still
limits long local turn-ins. `DukeAndPamelaStyleCrossContinentEnder_IsIneligibleForOffscreenTravel`,
`CrossContinentEnder_DoesNotBecomeAVisibleStarterGoal` and
`PreviouslyCompletedCrossContinentDelivery_IsAbandonedByAnUnhomedBot` cover these decisions.

#### Travel by the Deeprun Tram

Stormwind and Ironforge are 3,960 yards apart in a straight line with no road a bot can walk between them (the Elwynn and Dun Morogh
regions are walled apart by mountains). Before, a bot with a destination on the other side walked the straight line until it stuck:
live 2026-10-11, 117 dwarf and gnome bots with Dun Morogh, Ironforge and Loch Modan turn-ins (400, 2160, 353, 1679, 1879, 2997, 2039,
2218; reverse 1338) stood at the Stormwind tram entrance (-8285, 554) and on the Elwynn ridge. Now they ride the tram as a player does
(`PlayerbotTram`):

* **When.** `PlayerbotWorldDestinations` asks `TramNetwork.Choose` before it plans a walk. The bot and goal must be in known zones on
  opposite sides of the tram, and the bot must be Alliance. The Stormwind side includes Stormwind, Elwynn, Westfall, Redridge, Duskwood,
  Deadwind Pass, Burning Steppes, Swamp of Sorrows, Blasted Lands, Stranglethorn Vale and the Temple of Atal'Hakkar exterior (1477);
  the Ironforge side includes Dun Morogh, Ironforge, Loch Modan, Wetlands, Badlands, Searing Gorge, Thoradin's Wall (293) and the
  northern Eastern Kingdoms. Blackrock Mountain (25) remains unclassified. These are build-5875 AreaTable zone IDs, not proximity to an
  entrance. A Coldridge bot walks to Talin or Sten in its own zone and takes Ironforge's entrance (ClassicDB 2175) for Stormwind; a
  Goldshire bot uses Stormwind's entrance (2173) for Coldridge. Duskwood to Thorium Point (Searing Gorge) uses the tram via Ironforge.
  Unknown zones do not
  start a trip. A failed trip backs off only the entrance it began at (`PlayerbotTransitBackoff`,
  `World:Playerbots:Travel:TransitBackoffSeconds` doubling per failure); the other entrance is taken at once, and meanwhile the destination
  falls back to the walk and the ordinary stall watch. A lift that failed is backed off the same way, alone.
* **The tram, from the data.** `TramNetwork.Build` reads the area triggers: the two entrances on the continent whose
  `areatrigger_teleport` leads to map 369 (classic-db 2173 Stormwind, 2175 Ironforge), each paired with the exit on map 369 nearest its
  landing whose teleport lands back beside it (2171, 2166). Anything else (one entrance, an exit missing or landing elsewhere) is no
  tram, and no bot rides. The cars are the map-wide elevators of map 369 (the six "Subway" objects 176080-176085 and their
  TransportAnimation.dbc paths; docs/areas/transports.md).
* **The trip** (`TramStage`): walk to a point on the continent floor inside the entrance trigger, on the side of the exit's landing
  (`TramPlatform.Approach`; the bot may take that one teleport only while its trip heads there,
  `PlayerbotAreaTriggers.AllowTeleportsTo(369)`). The trigger centre is not that point: it is the middle of the instance volume, and
  Ironforge's floats 6.3 yards above the floor. On the tram map, pick the car that stops at this platform soonest at a time the bot can
  be beside it (`PlayerbotTram.NextDock` samples the car's animation every 250 ms over two cycles), walk to the platform spot beside
  where it will stand (8.6 yards off the track's centre line, the car's half width from its model bounds plus two, where the floor is
  level with the landing), wait, and step aboard when it stands there. Aboard, the bot reports what a client standing on a transport
  reports: MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and its offset on the car, at the world position the car's
  animation and the offset give, every 500 ms (`PlayerbotMotion.SendAboard`); observers' clients place it on their own car by the offset.
  When the car stands at the other platform the bot steps off on the side nearest the exit (a heartbeat without the transport block,
  `SendStepOff`) and walks into the exit trigger, which puts it on the continent beside the other city's entrance. The destination then
  plans again from there.
* **Map 369 stays off `AllowedMaps`.** The brain drives a trip before anything else (`PlayerbotTram.Drive`), so nothing else moves the bot
  meanwhile and the stall watch does not judge a bot waiting for its car; a fight on the way to the entrance ends the trip. The bot may
  plan and walk on the tram map only while its trip is under way (`PlayerbotMapPolicy.MayMoveOn`); a bot there without one (logged in
  after a save on the way: the login gate admits the tram map when the tram joins a continent of AllowedMaps,
  `PlayerbotMapPolicy.MayLogInOnTram`) walks nowhere but into the nearest platform's exit.
* **Fails closed** with a stall reason (logged as `Playerbot <id> (<name>) tram: ...; it gives up that goal`, the goal set aside for the
  bot itself as for any stall, but never for every bot, see below): no car runs on the tram map, no car stops at the platform within two cycles (about five minutes), no way to the side of a car,
  no way to the entrance (no progress for 60 s), or a car that did not stop at the other platform within a cycle. On the tram map the bot
  then leaves by the exit of the platform it is at; when it cannot walk into that exit within 60 s without progress, the server sends it
  where the exit would (that trigger's own teleport).
* **Beside the home, the shared set-asides and death.**
  * *Home return.* A homed bot on a trip is on "another map" (369) or far from its home, which an Auto return teleports from at once. The trip
    runs before the home return (`PlayerbotBrain.Update`), so no home teleport cuts a ride short; once the bot is out on the continent with no
    errand left, the home return applies as usual. A completed turn-in that began the trip runs before the home return too.
  * *Stalls.* The stall watch never judges a bot on a trip. A failed trip is the bot's own set-aside only (`GiveUpStalledGoal(shareable:
    false)`): it says the tram did not carry the bot, not that the goal beyond it is unreachable, and it is reported at the exit the bot left
    by. Shared, three bots behind one stopped car would set every bot's quest on the other side aside, and bot QA would read it as that
    quest's fault. Its line is never marked `outside its home` (only `stalled` lines are).
  * *Death.* A death ends the trip and its entrance consent (`PlayerbotTram.Cancel` when the brain records the death). The ghost's corpse
    run may walk the tram map while its body lies there (`PlayerbotTram.AllowsWalkingOn`), reclaims it as anywhere else (no spirit healer
    stands on map 369, so the body is the way back), and the revived bot, on the tram map without a trip, leaves by the nearest platform's exit.

**Live regression w22 (2026-10-11 08:05, `live-w22-r1`).** 67 dwarf and gnome bots homed in Westfall (1,879 to 2,913 yards from the
Stormwind entrance; `before-start-characters.db`) began their trip at their first think and gave it up about 60 s later, every one
still where it started, logged as `tram: the trip failed` without a reason. Two causes:
* **The way was refused whole.** The trip walks to the entrance trigger's centre, which floats 4.8 yards above its floor. From Sentinel
  Hill the real mesh finds the whole 2,314-yard way to the floor under it, but answers it as incomplete (its end is the centre projected
  onto the mesh), and a route longer than `MaxRouteYards` (2000) was cut at the bound only when it was complete: an incomplete one was
  refused, and `TryPlanToward` tries no shorter step after a refusal by the mesh. Now a partial answer whose end lies within a yard (flat)
  and within `ProjectedEndYards` (6) vertically of the destination counts as reaching it (`PlayerbotNavigation.EndsAt`) and is cut at the
  bound like a complete one; the bot walks the first 2,000 yards and plans the rest from there. This holds for every far bot walk, not
  only the tram's, and for the routes planned from a step beside the bot (`FromBeside`). The height bound matters: an incomplete answer
  is also a search that stopped short on the polygon nearest the goal, which can lie straight under a destination on a level the bot
  cannot reach (a tower or cliff top, a bridge, an upper floor); such an end is no arrival (a complete path's end is found within 5 yards
  vertically, the tram entrance's 4.75). Storeys of a building closer than 6 yards can still pass; the stall watch gives those goals up.
* **The reason was lost.** A trip that failed on the continent noted its reason on the trip only; `PlayerbotTram.Drive` now always returns
  it, and the log reads e.g. `tram: no way to the Deeprun Tram - Stormwind Instance entrance from -10193.8,1089.2,36.8; it gives up that
  goal`.

`PlayerbotTramLiveRegressionTests` (real terrain and DBCs) replays Wfbotak from its live start: through Elwynn to the Stormwind gate,
the entrance at 328 s, aboard at 346 s, out at Ironforge at 427 s (before: still at Sentinel Hill, failed at 60.7 s with "tram: the trip
failed"). `PlayerbotTramFarStartTests` is the same in the fixture world (a 2,330-yard incomplete way under the trigger; a trip with no way
fails with its reason; the height bound for the partial and the exact planner: 4.75 yards under the destination accepted, 10 refused).

**Live 2026-10-12.** 406 refusals of `tram: no way to the Deeprun Tram - Ironforge Instance entrance` (Dun Morogh -6598, 314 and
-6494, 324; Loch Modan -5568, -453; Wetlands -4716, -888) and some of the Stormwind entrance (-9067, 8). The walk aimed at the trigger
centre. Ironforge's centre is 6.3 yards above the continent floor the paired exit stands a player on, outside the pathfinder's near box
(5 yards) and `ProjectedEndYards` (6): on a mesh of that floor the way to the centre is incomplete and is not an arrival, so a Loch Modan
start (inside one route) is refused whole. The approach is that floor, inside the volume, on the landing's side
(`PlayerbotTramEntranceApproachTests`).

Tests: `PlayerbotTramRouteTests` (the brain drives a bot from the Stormwind tram exit to Ironforge with the classic-db trigger and car
rows, a synthetic animation and flat floors, with the anticheat judging the bot as a client: no fake transport, no speed; a destination of
`PlayerbotWorldDestinations` on the Ironforge side begins the trip; a bot on the tram map without a trip plans nothing there and leaves by
the nearest exit; no car fails the trip closed and the bot goes back out; the entrance is not taken without a trip),
`PlayerbotTramNetworkTests` (the network from the rows, `Choose`, `NextDock`), and `PlayerbotTramRealTerrainTests` (needs
`ARCANECORE_TEST_TERRAIN_DIR` and `ARCANECORE_TEST_DBC_DIR`: the same ride on the real map 369 mesh and models and the client's
TransportAnimation.dbc; the bot boards an Ironforge-bound car 49 s after arriving and is out at Ironforge 140 s after it started), and
`PlayerbotTramInteractionTests` (a bot homed in Westfall rides to Ironforge without a home teleport and is taken home only after the trip; a
failed trip sets the quest aside for the bot but not for every bot; a bot killed on the platform drops its trip, walks back to its body on the
tram map as a ghost, is revived there and leaves by the Stormwind exit).

Not done: only quest destinations (`PlayerbotWorldDestinations`) begin a trip; trainer, vendor and home-area walks
(`PlayerbotTrainerDestinations`, `PlayerbotTownGoals`, `PlayerbotHome`) do not use the tram yet, and party followers follow their leader
only through triggers the leader took (`PlayerbotFollower`). Zones outside the classified road networks do not begin tram trips.
Only a death on the platform is covered by a test; a death aboard a car (its body in the tunnel, or a revival
from the transport when the release finds a graveyard, `GraveyardRepopService.RepopAtGraveyard`) is not.

#### Travel by elevator

The Barrens and Thousand Needles are joined on foot only by the Great Lift, Thunder Bluff's mesas only by their two lifts, Freewind Post's
mesa only by its lift. Before, a bot with a destination on the other level walked the straight line and stalled against the cliff: live
2026-10-11 (issue #186), every bot with quest 1111 complete set it aside after three bots stalled in Thousand Needles on their way to
Wharfmaster Dizzywig in Ratchet (stalls at -5450,-3750 on the Shimmering Flats edge and -4817,-2079 under the cliff). Now they ride the lift
as a player does (`PlayerbotElevator`), following the tram rider's design without its map change:

* **The lifts, from the data.** `ElevatorNetwork.Build` takes every GAMEOBJECT_TYPE_TRANSPORT spawn whose TransportAnimation.dbc path
  rises at least 20 yards and stays within a yard of its shaft (not a tram car, a door or a plunger), its levels the spawn position plus
  the path's lowest and highest node offset, and groups the cars within 40 yards between the same levels into one lift. classic-db 5875
  with the 5875 client gives, on Kalimdor, the Great Lift (spawns 16874 and 16876, -44 to 85), the Freewind Post lift (16875, 16877), and
  the Thunder Bluff lifts (18298 and 18435, 69 to 130; 20639 and 20640, 69 to 140), two counterweighted cars each on a 30 s cycle.
* **Which regions a lift joins: the one hand table.** `ElevatorRegions.Known` names, per lift, the AreaTable region of its lower and its
  upper level: the Great Lift Thousand Needles (400) below and The Barrens (17) above; Freewind Post Thousand Needles (400) below and
  Freewind Post (area 484) above; both Thunder Bluff lifts Mulgore (215) below and Thunder Bluff (1638) above. The data does not give this:
  the terrain's area map is flat (one area per cell, the same above and below a cliff) and does not follow the cliffs (the Great Lift's top
  landing lies in Thousand Needles' "The Great Lift" area, 485; Thunder Bluff's zone covers the Mulgore ground round the mesa's foot; the
  Thunder Bluff lifts' lower stop stands on a platform 9 yards above that ground). Beside a lift (150 yards) the bot's height decides its
  level instead; a mesa's region counts as the top only from 30 yards below it up. A lift without a row joins nothing.
* **When.** `PlayerbotWorldDestinations` asks `ElevatorNetwork.Choose` before it plans a walk (after the tram): a bot on top of a mesa
  whose destination is not up there rides the mesa's lift down, wherever the destination lies (quest 5361 is taken on Freewind Post and
  ends at Ghost Walker Post in Desolace); otherwise a lift whose regions put the bot on one level and the destination on the other is
  taken, the one the shorter straight-line way passes. Not when the navigation mesh joins the bot to a destination within one route's
  reach, however far the lift is (a route that stops short, at the foot of the cliff, is no way), and not while the bot stands farther from
  the lift than `MaxRouteYards` (2000), nor farther than `ElevatorNetwork.NearLiftYards` (150): a trip is the local ride, and begun from
  far out its approach plans each leg with `PlayerbotNavigation.PlanToward`, which that far out often gets no route to the lift, so the
  walk ended after `MaxRefusedPlans` refusals in a row or the 60 s no-progress bound (live 2026-10-25, issue #1482). Of the five
  evidence lines in the issue, measured from the lift's coordinates it names: three began 2057 to 5453 yards out (past `MaxRouteYards`),
  one 1181 yards out (the Great Lift, -3557.6,-2240.7) and one 450 yards out (the Undercity lift, 1877.4,-161.6). Between 150 and 2000
  yards, when no direct walk reaches the goal and the lift is not backed off, `TryBegin` hands the caller a route towards the landing beside
  the nearest car that `PlanToward` finds a route to (`PlayerbotRoute.ToLiftLanding`; the landing of each car is cached per map, a refused
  approach is not planned again for 5 s) and the trip begins on a later think, once the bot is near (all lifts share the rule). The route
  must end within 150 yards of the lift or at least 30 yards nearer it than the bot, so a leg that gains nothing is not handed back for
  ever. Callers walk it as they walk a direct route, skip the flight-path and held-off checks as they do for one, and the explore cell is
  not blocked because it ends away from the goal. When the approach is refused or its tiles are still being read the caller plans the goal
  itself as before. This moves the approach out of the trip; it does not make the canyon floor of Thousand Needles any easier to cross, so
  a bot there whose approach is refused is reported by its caller as an unreachable goal instead of as `bot-elevator/trip-failed`.
  Undercity starts more than 150 yards from the lift (the one in the evidence is about 450) now take this approach route instead of a
  trip; that change is not verified against the live data. The "in the shaft pit" failures at the lift itself and the Thunder Bluff
  lifts (not analysed) are not addressed. A failed ride is not tried again for 10 minutes.
* **The ride** (`ElevatorStage`): walk towards the lift; within 60 yards, pick the car that stands at the bot's level soonest after the bot
  can be beside it, walk to the landing beside it (the floor within 2.5 yards of the level, 6 to 9 yards from the car's centre: the car's
  half width from Elevatorcar.m2's bounds plus a step, nearest the bot), wait, step aboard when the car stands at that level for at least a
  second more, and report what a client standing on a transport reports (MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and
  the offset, every 500 ms, at the world position the car's animation gives). When the car stands at the other level the bot steps off
  onto the landing there nearest its destination, and the destination plans again from there. Nothing teleports the bot.
* **Fails closed** with a stall reason (`elevator: ...`, the bot's own set-aside as for the tram, never every bot's): no way to the lift or
  its landing (no progress for 60 s), no car in the world, no floor beside a car, no car standing at the landing within two cycles and 30 s
  (90 s). Aboard: a car that does not stand at the other level within a cycle and 30 s is left at the next level it stands at, and a car that never stands anywhere drops the bot after two cycles and 30 s.
* **Never off a car where it stands.** Under a car the static collision data has no floor but the bottom of the shaft (a car is a dynamic
  object, in no vmap; measured on the real Great Lift: 5.3 to 5.5 yards down into the pit at the foot, 134.8 yards at the top), so the bot
  is never stepped off or dropped where it stands. A car that leaves the world under the bot (or a bot knocked off it in the shaft) lets
  it fall only when the fall costs less than half its health (vmangos fall damage at the world's rate: MOVEFLAG_FALLINGFAR, then
  MSG_MOVE_FALL_LAND with the fall's time); otherwise, or over no known floor, the bot is placed on the nearest landing (a same-map
  teleport, as vmangos' undermap recall does; logged), and the ride fails. A party taking the bot over, or anything else that cancels a
  ride aboard, never moves it off a car mid-shaft: the ride alights at the next stop and the party AI takes the bot on the landing
  (`PlayerbotElevator.HoldsForLanding`). A bot found on a car at login or revival boards it and rides to the next stop; one found hanging
  in a shaft (the car elsewhere, or not in the world after 10 s) is placed on a landing. A corpse killed aboard loses the transport and
  its observers are told (the death root's acknowledgement carries the cleared block; a bot already rooted relays a heartbeat).

Tests: `PlayerbotElevatorNetworkTests` (the lifts from the classic-db and DBC rows; `Choose` for issue #186, quest 5361 and Thunder Bluff),
`PlayerbotElevatorRideTests` (a fixture cliff and the Great Lift's real paths: a destination in Ratchet begins the ride, the brain walks,
waits, rides up and steps off on the plateau with the anticheat judging it, then walks on; the ride down; the 90 s wait for cars that never
stop; the 60 s bound on a landing the bot cannot reach), `PlayerbotElevatorSafetyTests` (the same fixture with no floor under the cars:
a cancel mid-shaft or at a stop, a party invite through ManagedPlayerbotFeature, a car lost low over a floor or high over none, a login
hanging in a shaft, on a parked car and on a moving car, a death aboard seen by an observer), `PlayerbotElevatorRealTerrainTests`
(needs `ARCANECORE_TEST_TERRAIN_DIR` and `ARCANECORE_TEST_DBC_DIR`: the four lifts and the region table against the real terrain and
AreaTable.dbc, and the choices of issue #186 from the live stall points) and `PlayerbotElevatorRealRideTests` (the Great Lift ridden on
Kalimdor's real mesh and models: aboard 26 s after reaching the foot, off at the top 15 s later, the mesh leading on towards Ratchet).

Not done: as with the tram, only quest destinations begin a ride. The Undercity lifts and the other lifts on map 0 have no region row, so
no bot rides them. A bot far from a mesa whose way there is not a lift's
regions (Desolace to Thunder Bluff) walks until it is in Mulgore.

**Death recovery bounds are judged on attempts, not on world time alone.** `PlayerbotRecovery` gives a corpse run up for the spirit
healer when the walk stopped closing for `StuckMs` (10 s), faults a fallback that stalls the same way
(`playerbot-recovery-spirit-healer-failed`), and faults a body never released for `NoProgressMs` (60 s)
(`playerbot-recovery-stalled`). A bound now trips only once the recovery has also acted in more than `MinBoundThinks` (4) thinks
since the progress it measures from; a think refused for lack of action budget is no attempt. A bot is thought for only when the
shared budget and the 8 ms per-tick cap reach it (`PlayerbotThinkRotation`), while `PlayerbotMotion` carries the ghost on every tick,
so a think gap longer than a bound used to be judged a stall at the first think after it. Live 2026-10-10 (200+ bots, a boost storm
with world updates of up to 8.7 s): Graveweaver's ghost faulted with `playerbot-recovery-spirit-healer-failed` three times in 3.5
minutes, each time at its first think after a gap, and was disabled. At the ordinary 500 ms think interval a bound spans 20 or more
thinks, so nothing changes there (`PlayerbotRecoveryThinkGapTests`).

**An unsafe body is given up for the spirit healer at once.** A bot playing alone (the brain sets `PlayerbotRecovery.UnsafeBody`; party,
group and battleground bots keep their body run) asks at every think of a ghost whose body lies on its map whether the body is worth
reclaiming (`PlayerbotRecovery.JudgeBody`, in this order): `DiedHere`, the bot already died within `DeathLoopYards` (60) of the body
inside the death watch's window (600 s; `PlayerbotStallWatch.DeathsNear`, this death and an earlier one; the third is a death loop,
which takes the healer already); `TooStrong`, with the risk estimate on, a creature that would attack the revived bot and whose aggro
reach covers part of the reclaim disc is one the bot could not take even at full health (`PlayerbotRisk.TooStrongNear`: the creature
and its assistance weighed by `PlayerbotRiskModel.Assess` at the half health and mana a reclaim gives, strength only: no quest credit,
loot or danger memory; `Avoid`, not `Rest`); `OutsideHome`, the body lies outside the bot's home (`PlayerbotHomeArea.Contains`, the
zone clip included) and the spirit healer leaves the bot inside the home or nearer its centre than the body
(`PlayerbotRecovery.SpiritHealerLanding`: the graveyard of the body's position, where vmangos `SendSpiritResurrect` teleports the
revived bot, else the healer the ghost sees; a prediction through `GraveyardSelector.FindClosest`, which a battleground's own
graveyards, a run-time outdoor PvP link or `World:Death:GraveyardFallbackToDefaults` can only move), by at least
`OutsideHomeGainYards` (50) when it does not leave it inside the home. The body is given up only when a spirit healer can be had at
all, one in sight or a graveyard for the ghost's position (looked up once per body and 50-yard cell): with neither, the ghost would wait
for a healer until the fallback faulted (`playerbot-recovery-spirit-healer-failed`), and vmangos leaves a spirit with no linked
graveyard where it died, so it reclaims its body as before. The same gate holds for a death loop's request
(`PlayerbotRecovery.TakeSpiritHealer`, `SpiritHealerRequested`): with no healer to be had the looping bot keeps its corpse run instead
of faulting, and the loop's errand set-aside and the home return take it from there. A repeat death also sets aside the errand that led there, as a death loop
does. Any of them takes the spirit healer for this death (resurrection sickness and 25%
durability, vmangos NPCHandler.cpp:430-471) and logs `Playerbot X takes the spirit healer instead of its body at (...): reason (detail)`
at Information; the home return then takes the revived bot home as after any resurrection (Auto: a teleport when the way is far or
crosses a higher-level area). A first death in a safe spot inside the home is reclaimed as before, and so is a body round which only
creatures the bot can take stand. Live 2026-10-11 05:45-06:00 UTC: the Westfall-homed level-14 Wfbotaz died among Raven Hill's level 20+
skeletons and spiders, reclaimed its body there and died again, two death loops in fifteen minutes (`attackers=202/531/569`, then
`202/217/569`); the healer came only with the third death of a loop (`PlayerbotUnsafeBodyRecoveryTests`).

**Tightened (live 2026-10-11 08:04-08:10 UTC: 80 bodies given up in six minutes, 31 too strong, 10 died there, 39 outside the home).**
`DiedHere` counts only while a creature that would attack the revived bot (`PlayerbotRecovery.Threats`, or a hazard) still stands in
reach of the body; a cleared spot is reclaimed (the errand that led there is still set aside). `TooStrong` is strength, not worth: a
creature counts only when `Assess` avoids it and `PlayerbotRiskModel.BeyondStrength` holds (one that kills outright, an elite 3+ levels
above, or a risk at full health and mana above `Tolerance x 0.9`, the highest bar any pull is taken at). Before, the pull bar of a fight
that gives no quest credit or loot refused level 9-13 Defias and gnolls to level-14 bots (entries 95, 474, 478, 504, 1118 in the log).
`OutsideHome` counts only when the home return cannot teleport the revived bot home (`ReturnMode` `Walk`, or a return teleport less
than `TeleportCooldownMs` ago): otherwise the body is reclaimed and the Auto return walks or teleports the bot home for nothing. A
remaining source of over-firing is outside this change: the level-14 Westfall bots' `TooStrong` details imply an observed damage per
second (`PlayerbotFightTracker.ObservedDps`) of about 5-6 against the level estimate of 27.

**The healer's landing is judged too.** The spirit healer revives the bot at the graveyard of its body (vmangos `SendSpiritResurrect`),
which can be as unsafe as the body: Raven Hill's graveyard stands among its level-24 Skeletal Horrors. Before the body is given up (an
unsafe body or a death loop's request), the landing (`SpiritHealerLanding`) is asked of `PlayerbotRecovery.LandingUnsafe`
(`PlayerbotRisk.TooStrongAt`: a creature beyond the bot's strength whose aggro reach covers the landing); when it is unsafe and a spot
within the reclaim radius of the body is clear of every threat, the body is kept (`BodyKeptOverLanding`, logged once: `keeps its body
... the spirit healer would revive it among creatures too strong for it`) and the corpse run revives the bot on that spot. When the
healer is taken and revives the bot on an unsafe landing, the bot leaves in the same think, still out of combat (the activation is
handled synchronously; the creatures aggro only in their own update): a homed bot teleports home (`PlayerbotHomeTeleportReason.UnsafeLanding`,
past the 5-minute cooldown), an unhomed one (or one whose home teleport is refused) to its bind point (`TeleportToHomebind`, as the
tram's way out). Live: Wfbotfe (homed, 08:07-08:09) and Dawnrover (unhomed, death loops at 08:23:32 and 08:27:47) were revived at
that graveyard, attacked before their next think, and died there again and again; the home teleport was refused in combat.
The landing is judged where the resurrection sends the bot: when the body's graveyard differs from the ghost's, `SendSpiritResurrect`
teleports the revived bot there (`GraveyardRepopService.TeleportToCorpseGraveyard`), a near teleport still pending in that think, so
the escape judges `TeleportService.DestinationOf` and replaces that teleport (an urgent `TryTeleport` never counts the server's own
pending teleport as its escape). The creatures are those round the landing itself, seen by the bot or not
(`PlayerbotRecovery.ThreatsAround`). A death loop's request for the healer is never overridden by a kept body: the healer is the way out
of a body death loop and the escape covers its landing, so at most two bodies are kept per death-loop window. The keep-body answer is
kept per body for 2 s. The unhomed bind-point escape is not rate-limited: it happens at most once per resurrection, and a bot that
goes back to the same place has its errand set aside by the death watch.

**Why the spirit-healer step faulted.** Not a server refusal: in the world test that replays Wfbotfe's sequence (revived at the healer
among the skeletons, retreat, death, three times) the motion's loop watch (`PlayerbotMotion`, A, B, A within 30 s) counted the ghost's
walks to the same healer and the living bot's retreats between them as one errand going back and forth, refused the healer's place for
60 s, the ghost could plan no route to it, and the stuck bound (`StuckMs`, 10 s) faulted the recovery; live, the fault came 21 s after
the death loop. The first think of every death now forgets the loop watch (`PlayerbotMotion.ForgetLoops`). A refused
CMSG_SPIRIT_HEALER_ACTIVATE (the bot still dead after the synchronous handler) is logged at Information with the handler's rule
(`QuestNpcServices.SpiritHealerRefusal`: alive, NPC state not ready, not interactable with the reason from `InteractableNpc`, not a
ghost, a settlement pending), once per reason and death, and the fault itself is preceded by an Information line with the goal, its
distance, whether a route existed and whether the goal is refused after a movement loop.

**Waiting to recover without food.** After a retreat, or before a pull it is too hurt for, the bot waits for
`World:Playerbots:Risk:RecoverHealthPct` (goal `Rest`, `PlayerbotRisk.KeepWaiting`, at most `MaxWaitMs` 90 s). With food or drink it
eats or drinks; without, it now sits (`CMSG_STANDSTATECHANGE`; vmangos `Player::RegenerateHealth` gives half as much again per tick
when not standing) and stands up when the wait ends. Health or mana rising during a rest is progress for the stall watch
(`Fingerprint.RestMark`). A wait in which neither rose for `FruitlessWaitMs` (20 s, ten regeneration ticks) ends, and no new wait
begins for `MaxWaitMs` unless health or mana rises meanwhile: under resurrection sickness (spell 15007, every stat -75%) a warrior,
rogue, hunter or shaman regenerates no health out of combat at all, and the wait could only run out and be begun again by the next
pull verdict. Live 2026-10-10: Wfbotcx (level 14), revived by the Darkshire spirit healer after a death loop, stood there with
`stalled 120s: goal=Rest` (`PlayerbotRestStallTests`).

`World:Playerbots:MovementPackets` (default `true`, live through `.reload config`) chooses how
those moves reach the world. `true`: each one is a client MSG_MOVE_* packet, encoded and dispatched
through the opcode table to `MovementHandlers` like any client's. `false`: the server applies the
same move itself (`WorldSession.TryManagedMovement` -> `MovementHandlers.MoveManagedBot`): the
same admission (not while teleported or on a taxi, a valid block, the bot moves itself), the same
relocation with the locomotion observers and the same MSG_MOVE_* relay to observers, without the
dispatch or the packet encode/decode. Observers receive byte-identical streams in both modes
(`PlayerbotMovementPacketsTests`); `PlayerbotMotion` reads the option at every move, so a reload
switches running bots at their next move. Acknowledgements (speed, root, teleport, far transfer)
and `CMSG_AREATRIGGER` stay client packets in both modes.

In **scripted mode** an `IPlayerbotController` replaces the brain.
`ManagedPlayerbotFeature.StartScriptedAsync(idOrName, controller)` starts a bot that way;
`SetControllerAsync(botId, controller)` switches a running bot (null returns it to autonomous
mode). While scripted, the brain is never updated (no goals, no packets drained, no actions)
and the shared action budget does not apply; the controller's `Tick` runs on the world thread
every tick with a `PlayerbotControllerContext` (`TryAction`, `AcknowledgeServerOrders`).
Stopping a bot detaches its controller; `IsScripted(botId)` reports the mode. Switching back
to autonomous mode resumes the brain with whatever state it had before.

### Goal policy

`World:Playerbots:Policy` (`src/ArcaneCore.Playerbots/Policy/`, off by default) lets a scored policy re-rank the out-of-combat goal
ladder in `PlayerbotBrain.Update`. It is **advisory**: the rule ladder stays the default and the fallback, and a promoted goal is tried by its
own controller under its own rung's guards, which recheck the live rules, and under the guards of the rungs it jumps: resurrection
sickness stands every promotion but `Town` down (as the ladder stands every rung below town down), and a quest creature in sight keeps
a bot off a `Train` or `Mount` trip. A promotion its controller refuses leaves the ladder in charge for the rest of that snapshot
(counted `refused`), so it is not retried every think. Chat (`World:Playerbots:Chat`) is separate and unaffected.

**Goals.** `PlayerbotPolicyGoal`, in ladder order: `Quest` (a giver or ender in sight), `Town` (vendor, trainer list, repair),
`Opportunity` (a chest, locked chest or rare in reach, [playbots-opportunities.md](playbots-opportunities.md)), `Train`,
`Mount`, `QuestTravel` (a giver or ender out of sight), `Grind` (a creature in sight), `Explore` (always open). The first open goal is the
ladder's choice. `Opportunity` is open when the opportunity controller has a find in reach that is not set aside (`PlayerbotOpportunities.HasCandidate`:
one scan of the visible objects into a reused list, no weighing); a promotion goes through the controller's own think, which weighs the find and refuses it as
the ladder's rung would (a refusal is counted `refused`). A promotion that chooses a guard to pull hands the think to the combat path, as the rung does. The goal record's `goal`
label is `Opportunity` only when the opportunity controller drove the think at which the decision was taken (`PlayerbotBrain.OpportunityDriven`:
it walked, opened, waited or chose a guard to pull). The brain's `Loot` goal covers a chest and a corpse alike, so it is not mapped by `kind`
alone: looting a corpse after a fight has no policy goal (`goal` null), as before. Crafting and auction errands keep their rungs but have no cheap probe, so no policy moves them; `Quest` is the first rung (never
a promotion) and `Grind` is the floor (a policy that picks it leaves the ladder in charge, counted `floor`).

**Hook.** One call at the top of the ladder (`PlayerbotBrain.Policy.cs`, `TryPolicyGoal`, after rest, equipment, class upkeep and the walk
home). Each think it first **decides** from the bot's current snapshot: the best open goal by the snapshot's scores, tried first when it is not
the ladder's own choice; then, every `IntervalMs` (3000), it takes a **new snapshot** (features plus the open goals, from the same probes the
rungs check) for the next batch. With no engine (`Enabled` false) the hook returns at once and the ladder runs exactly as it always has.
With the engine on and the rule ladder scoring, the policy always agrees, but its snapshots still run the rungs' probes (which refresh
the same candidate caches the rungs use) every `IntervalMs`: the goals chosen are the ladder's, the think's work is not quite the same.

**Features** (`PlayerbotPolicySchema.FeatureNames`, each about 0..1): level, money, free bag slots, quests in the log, quests ready to hand in,
goals the stall watch set aside (deferred or suspended), stalls, zone, map, attackable creatures in sight, health, inside the home area.

**Scoring, off the world thread.** At the end of each world tick `PlayerbotPolicyEngine.Dispatch` hands each map's snapshots to a worker
task as one batch (one batch in flight per map; at most 1024 rows, past that a snapshot is dropped and counted). The worker scores them in
chunks within `TickBudgetMs` (2 ms wall time per map batch; the rows past it are left unscored and the overrun counted) and publishes each
bot's scores. The brain uses them on a later think, while the snapshot is no older than `StaleAfterMs` (4500 world ms); on the think
right after the one that took it (no other think of the bot between, of any kind) the bound is 30 s instead, because a snapshot is first
usable there however far apart the bot's thinks are (8-17 s at p95 for a 1000-bot Debug server, where the 4.5 s bound alone made every
verdict stale). Late, missing,
dropped or non-finite (NaN, infinite) scores make that think fall back to the ladder. Every think on a snapshot decides again and is
counted (`applied`, `agreed`, `floor`, `explored`, `missing`, `stale`, `non-finite`, `refused`; a refused promotion also counts once as
`refused` on top of its think's own source), and the feature logs one Info line of the counters every five minutes while the policy is on.

**Policies.** `IPlayerbotPolicy.Score(features, count, scores)`: a batch of rows in, one score per goal per row out, higher is better.
`RuleBasedPolicy` scores each goal by its ladder rank, so its best open goal is always the ladder's own choice (the default, and the fallback).
`PlayerbotModelPolicy` is the built-in model: optional per-feature standardisation (`mean`, `scale`), then 1..3 dense layers (ReLU between,
linear last; one layer is a linear model), evaluated with `TensorPrimitives` SIMD dot products on the CPU. A bot costs at most a few thousand
multiply-adds (12 features x 64 hidden + 64 x 8 goals is about 1.3k), so there is no GPU path: moving a batch to a GPU would cost more than
scoring it, and deployed servers have none. Feature 5 (`set_asides`) counts the bot's set-asides that still have time left; since the defer ledger it no longer counts an unreachable quest whose set-aside ended but whose strikes are kept, so a model trained earlier sees that input shifted (lower) for such bots.

**Weights file** (`ModelPath`, JSON, at most 4 MiB, relative to the profile folder): `{"format":"arcanecore.playerbot-policy","version":1,
"features":[...],"goals":[...],"mean":[...],"scale":[...],"layers":[{"inputs":I,"outputs":O,"activation":"relu"|"linear","weights":[O*I],
"bias":[O]}]}`. The feature and goal names must equal the server's, in order (a model trained on another layout is refused: adding
`Opportunity` changed the goal list, so a model trained before it, with seven goals, no longer loads and the ladder scores until one is retrained; telemetry written before the change has `v` 1 with the old `open` bit order, so a reader reindexes it: every bit from `Train` (old bit 2) up moves one place). A file that is
missing, unreadable, malformed, of the wrong layout or with a non-finite number **never stops the server**: it is reported once
(anomaly `bot-policy/model-load-failed`, with the reason), logged as a warning, and the ladder scores instead.
Training is described below.

**Exploration.** `ExplorationRate` (0..0.2, default 0): the chance that a snapshot, instead of being scored, names one of the ladder's open
runner-up goals at random (never the ladder's own choice, never `Grind`), which its thinks then try first. It exists to gather telemetry the
ladder alone never produces; it needs `Enabled`.

**A/B arms.** `AbPolicyShare` (0..1, default 1): the share of bots in the policy arm. The rest are in the ladder arm, fixed per bot by a
hash of its guid: they take snapshots and score them like the others, but never promote or explore, so they behave as the ladder alone
and their telemetry (`arm` `ladder`, with the `pick` and `src` the policy would have had) is the comparison. The per-think counters count
the ladder arm's would-be choices too.

**Telemetry** (`World:Playerbots:Policy:Telemetry`, off by default; needs `Enabled`). One record per decision: the first think that had a
snapshot's scores or exploration goal (a think on a stale snapshot, or before its scores came back, is not one, so a snapshot whose scores
never came is not written), written after `OutcomeMinutes` (5) of world time with its outcome. JSON lines, one object per line:

| Key | Meaning |
|---|---|
| `v` | Record version (3). Version 3 added the `losing-burst-N` retreat `reason` and the `round_damage` retreat feature to fight records (every other record kind keeps its layout). Servers built between PR 1427 and the bump wrote that change under version 2, so a version 2 fight record may already carry `round_damage` and `losing-burst-N`: a fight-record reader should go by the length of `retreat.f` (7 or 8), not by `v`. Version 2 added the `Opportunity` goal, which moved every goal after `Town` up one bit in `open` (`Explore` went from bit 6 to bit 7); a version 1 file has the old seven-goal bits. The trainer (`tools/playerbot-policy/train.py`) reads all three (1, 2, 3), remapping version 1 bits and labelling every record with the current version. An older `train.py` rejects version 3 goal records even though their layout is unchanged. |
| `k` | `goal` (this table); `fight`, `skip` and `opportunity` are the combat records below; `health` is the tick-health record below. |
| `t` | Unix ms of the decision. |
| `bot` | The bot's player guid (hex). |
| `map`, `zone` | Where it decided. |
| `f` | The feature vector (`FeatureNames` order). |
| `open` | The open goals (bit `1 << goal`). |
| `ladder`, `pick` | The ladder's choice; the policy's (or exploration's) choice, null when there were no scores. |
| `src` | `agreed`, `applied`, `explored`, `nonfinite`, `floor` or `refused` (the promoted goal's controller did not act). |
| `x` | 1 when the choice was exploration. |
| `arm` | `policy` or `ladder`: the bot's A/B arm (`AbPolicyShare`). |
| `goal`, `kind` | The goal the think ended on, as a policy goal (null for one no policy chooses) and as the brain's goal. `Opportunity` is the label only when the opportunity controller drove that think (the brain's `Loot` goal alone is also a corpse, so `kind` `Loot` with `goal` null is a corpse). `QuestTravel` is the label when the quest destinations planned that think's trip (its `kind` is `Quest`); records before this label have such thinks as `Quest`. |
| `w` | The outcome window in seconds. |
| `o` | The outcome over the window, in this order: `lvl`, `lp` (levels gained with their fractions: experience over the next level's, so it compares across levels and level-ups), `xp`, `money`, `items`, `quests` (with `lvl`, the stall-watch fingerprint delta: levels, experience, copper, item count, quests handed in), `stalls`, `deaths`, `anomalies` (the bot's anomaly-ledger reports). |

The world thread only queues finished records (8192 at most; past that a record is dropped and counted); a writer task formats and appends
them to `Directory` (`telemetry/playerbot-policy`, relative to the profile folder) as `policy-NNNNNNNN.jsonl`, each at most `MaxFileBytes`
(16 MiB), at most `MaxFiles` (16) of them, the oldest deleted before the next is made (when a reader holds the oldest open, no new file is
made and lines are refused and counted until it can be deleted; one warning when refusing starts, one line when it ends, and the counts in the five-minute line). A decision whose bot leaves, or gets a new brain (noticed at that brain's first policy think), before its window ends
is dropped, never written with a partial outcome; deaths are seen only on thinks the brain drives (not while a group or battleground
driver has the bot). A full disk or missing folder stops the telemetry (fault site `playerbot.policy-telemetry-io`), never the
server.

**Tick health** (`k` `health`, written with the goal records whenever telemetry is on and the tick monitor runs). Every 10 s of world time the
world thread reads the watchdog tick monitor's cumulative count of frames over its budget (`Ops:Watchdog:TickMonitor`, the same count as the
overrun warning) and queues one record; the writer task formats it like the others. The first interval after start only takes the baseline.
Keys: `v`, `k`, `t` (unix ms at the interval's end, the clock `t` of the goal records uses), `ms` (the interval's length, so it began at
`t - ms`), `over` (frames over the budget within it), `budget` (the budget in ms). It exists so a trainer can drop decisions made while the
world lagged (a concurrent build pushes bot think gaps to seconds): a decision whose `[t, t + w]` overlaps a health interval with
`over > 0` describes a lagging world. No percentile is written: the tick monitor keeps only its count cheaply. A file with no `health`
record comes from a build without it, or from a host with the tick monitor disabled.

**Combat decisions** (telemetry only, written with the goal records when telemetry is on; a later phase may score them, gated in
[docs/design/playerbot-policy-phases.md](../design/playerbot-policy-phases.md)). Nothing here changes a decision: the risk model tells
`PlayerbotCombatTelemetry` what it assessed and whether the choice took it (`PlayerbotRisk.CombatSink`, after `Assess` and after
`Judge`; a sink fault is counted and never reaches the decision), and the rest is observed after each think. Two kinds of record:

- `fight`, one per fight (from the first think in combat to the first out of it, or the bot's death), written when the bot's next fight
  starts (so its downtime is known) or after ten minutes without one (`downtime` null). Its keys:
  - `engage`: the pull the risk model took (an engage, or a detour with a route), when the fight's first target is that creature; null
    with risk scoring off or when something else started the fight (a wanderer that aggroed first). It holds `f` (`level`, `health`, `mana` (1 without mana),
    `level_diff`, `elite`, `health_ratio`, `distance`, `risk`, `reward`, `adds`), `decision` (`engage` or `detour`), `reason` and
    `entry`.
  - `targets`: each target pick (the first and each switch, at most four): `c`, the live enemies it chose among (at most four, the
    chosen one always included), each `target_health`, `level_diff`, `distance`, `attacking_bot`, `elite`; `pick`, the index of the
    chosen one; and `ttk`, seconds from the pick to that target's death (null if it lived). The target is the bot's melee victim, else
    its own target when that is one of the enemies.
  - `retreat`: the retreat decision, null with risk scoring off. It is the verdict that ran (`retreat` 1) or the stay closest to
    running (`retreat` 0, the least time to die per time to kill). It holds `f` (`health`, `dps_in`, `dps_out`, `enemies`,
    `time_to_kill`, `time_to_die`, `target_health`, `round_damage`: the non-crit max hit of each melee attacker added up, as a
    share of the bot's maximum health) and `reason` (`losing-burst-N` is a retreat only the round-damage trigger caused).
  - `rest`: `f` (health, mana and resurrection sickness at the fight's end) and `rested` (1 when the bot's goal became `Rest` within
    5 s of the looting that followed the fight, or of the fight's end; `Rest` also covers waiting with no food and waiting sickness out,
    which `sick` helps tell apart).
  - `o`: `died`, `killed` (the first target died), `ttk` (seconds to that, or null), `fight` (seconds), `damage` (health lost,
    summed over thinks from the think before the fight; a heal within one think hides that much damage), `damage_pct` (of maximum
    health) and `downtime` (seconds to the next fight; null past ten minutes, or when the bot left first).
- `skip`, a pull candidate the choice did not take (`decision` `avoid`, `rest`, or a `detour` with no route; once a minute per
  creature, at most eight waiting per bot), with the same `f`, `reason` and `entry`, and `o.wait` (seconds until the bot's next fight, null past ten minutes).
- `opportunity`, one per opportunity decision the controller took (`PlayerbotOpportunities.Sink`, the same pattern as `CombatSink`; a sink
  fault is counted and never reaches the decision). A verdict of engage or refuse is a decision; a wait (hurt, recovering) is not. Keys, in order:
  `v`, `k`, `t`, `bot`, `map`, `zone`, `f` (`level`, `health`, `mana` (1 without mana), `distance` (yards, capped at 100, over 50), `kind`
  (the find's kind, 0 chest, 1 locked chest, 2 node, 3 rare, over 3)), `kind` (`chest`, `lockedchest`, `node`, `rare`), `entry`, `reward` (the
  net reward in same-level kills, `PlayerbotOpportunityModel`), `risk` (the predicted damage as a share of the bot's health; a hard refusal's
  infinity is written as 10), `guards` (the guard count; a rare is not one), `reason`, `engaged` (1 when the bot went for it) and `o`.
  A find the bot went for is written when its episode ends, with `o`: `loot` (copper: the coins plus the vendor price of the items the bot
  stored from the chest's loot window, counting at most one item per free bag slot (a stack that joins another is missed); 0 for a find given up;
  null when it succeeded but the loot was not seen, as for a rare, whose corpse the brain loots, or a chest taken before its window was read),
  `died` (0 or 1), `seconds` (from the decision to the loot, the abandonment, the kill or the death) and `lost` (1 when a rare was tapped and
  killed by another player, by the server's own tap rule (`PlayerbotBrain.HasLootRights`: groups and pets count as the bot's); a kill that earned nobody the corpse (creatures did most of the damage) is neither a success nor `lost`: `loot` 0, `lost` 0). A find refused or passed over has `engaged` 0 and nulls in `o` (there is no outcome to learn from), once a minute per
  find per bot; its `reason` is the model's, or `untakeable` (the first guard is one the brain would not take as a target) or `passed-over`
  (another find was better). An episode whose bot leaves before it ends is not written.

**Training.** Offline, from the server's own telemetry only (`tools/playerbot-policy/`, README there):
`train.py TELEMETRY_DIR -o model.json` fits progress per minute for each (state, goal) (levels with fractions, plus weighted quests,
less weighted stalls, deaths and anomalies) as a ridge model per goal or a small ReLU network, holds out a fifth of the rows, refuses to
write a model from fewer than `--min-rows` (1000), and writes the weights file above. `ab_report.py TELEMETRY_DIR` compares the two arms
of a run with `AbPolicyShare` 0.5, per bot-hour, with bootstrap intervals over bots. No label may come from Claude or any other LLM:
training data is what the server's bots did and what followed. Whether a model ships is decided on that report.

### Contention

Hundreds of bots with the same quests used to hunt the same mob: the target sort was "quest creature, then nearest" with no tie-break, so
every bot picked one creature. `World:Playerbots:Contention` (all keys live, `.reload config`) spreads them out and lets a bot decide
whether to help someone who tagged a mob first. Code: `PlayerbotContention.cs`, `PlayerbotBrain.DropIfSomeoneElses`,
`PlayerbotBrain.CollectTargets`, `PlayerbotQuestGoals.RefreshObjectiveEntry`.

* **Soft claims on untagged mobs.** A bot claims the mob it walks to in one world-wide table (`PlayerbotClaims`: mob guid to first claimant,
  claimant count and an 8-second expiry, refreshed at each think, ended when the bot tags the mob, drops it or dies; one dictionary sized
  once, expired claims dropped lazily). Choosing a target is one pass keeping the lowest weight: distance, plus `ClaimPenaltyYards` (25) per
  other bot claiming the mob, plus a stable per-(bot, mob) hash offset of up to 10 yards. The bot's own claim costs nothing and a tagged mob
  carries no claim penalty. No LINQ and no allocation on this path.
* **Someone else's mob.** The held target is re-checked every think: when another player tagged it (or fights it) and the bot is not on its
  threat list, the bot drops it, leaves it alone for `SomeoneElsesMemorySeconds` (15) and chooses again in the same think. It never waits on it.
* **Helping instead.** At most once per mob per memory window the bot may help, when the tagger is within 30 yards and losing (under 40% health
  or two enemies on them), the bot is above 60% health with no turn-in walk or retreat pending, and a roll passes: `HelpChance` (0.35) times the
  bot's sociability (a stable 0..1 from its guid), times 2 for a real player, times 1.5 when both are on the same kill quest. A helper attacks the
  mob (a priest, paladin, druid or shaman with a direct heal tops the tagger up first), skips the loot, and stops when the mob dies or the tagger
  leaves combat.
* **Group request.** When both are ungrouped and share the kill quest, the bot raises a group signal with source `contention`
  (`PlayerbotRisk.GroupSignal`); the coordinator matches by objective and, with `World:Playerbots:Groups:InvitePlayers`, invites a nearby real player.
* **Stolen kills.** A corpse that is not the bot's to loot (another player's tap, or no credit) gets no loot request and no 10-second wait; the
  spawn is left alone for the memory window.
* **Invincible targets.** Some creatures cannot be killed by a plain attack. Their EventAI holds them at 1 health from spawn (event 11,
  action 42 `SET_DEATH_PREVENTION` with a nonzero first parameter, `Unit.InvincibilityHpThreshold` 1) until a particular spell hits them: the
  Blasted Lands servants 7668-7671 of quest 2681 "The Stones That Bind Us" die only to Shackle Shatter (10805, 10834, 10835, 10836; rows
  766801 and 766803 and the like). Two layers keep bots off them. (1) Quest side: `CreatureAiContent.IsInvincibleUntilSpellHit(entry)`, built
  once at load from the entry's rows, is true when a spawn row sets the prevention and no row of the entry lifts it (action 42, parameter 0)
  on an event other than a spell hit (a health or timer lift a plain attack can reach keeps the creature killable). `IsKillObjective` is then
  false, so `CanExecuteObjectives` refuses the quest at acceptance and `Huntable` never picks those entries for a quest already in the log.
  (2) Combat safety net: `PlayerbotInvincibleWatch` (one per brain, called from the fight step beside `FightGoesNowhere`) counts how long the
  held target has stayed alive with `InvincibilityHpThreshold != 0` and health at or below it; after
  `World:Playerbots:Contention:InvincibleTargetAbandonSeconds` (8, 1..600, live) the bot sets that creature aside exactly like a fight that
  goes nowhere (`MarkUnreachable` for `StalledTargetMs`, `MarkFruitless`, then explores) and reports the anomaly `bot-combat` /
  `invincible-target-abandoned` once per creature entry per bot, again only after 10 minutes, doubling up to 6 hours. A creature with a
  threshold but health above it, or at low health with no threshold, is never abandoned, nor is any target on a dungeon or raid map (scripted
  boss phases hold at 1 health) or a `NotSelectable` one. The brain resets the watch whenever it chooses a target afresh. Also: `IsSkipped` drops
  these entries from solo grind and hunt target choice; a spell-hit lift with spell id 0 (any spell of its school) counts as an ordinary lift;
  rows keyed by spawn guid are ignored (fail open, the watch catches them); and a bot that already holds such a quest in its log abandons
  it through the client packet (`PlayerbotQuestGoals.UnkillableQuestToShed`, checked each quest update outside combat, set aside afterwards).
  The anomaly key is `bot-combat/invincible-target-abandoned`. Follow-up, not done: using the quest's stone
  and casting Shackle Shatter (quest 2681's real mechanic) so a bot could complete these objectives.
* **No credit.** A quest creature that gained no credit (kill or quest item) in three minutes of *contested hunting* is set aside for the bot for
  `ContentionSuspendMinutes` (5). Only time at the spawns with every creature in sight taken by others counts (a lost or stolen mob adds 20 seconds); resting, dying and town trips add nothing, a gap of over five seconds pauses the clock, and walking around the spawns while every creature of the entry is taken does count. The bot then moves on: the bot hunts its next unfinished objective, or raises a `contention` group signal when none is left. Among
  its objectives a bot takes the one a hash of its guid ranks first, so bots with the same quests do not all take the first. A quest item
  objective is hunted at the creatures that drop the item (read once from the loot tables; items with more than 24 droppers, elites and
  creatures that do not spawn on the bot's map are left out). A trace dropper is left out too: one whose chance for the item, times 20,
  is below the best dropper's (`PlayerbotDropIndex.HuntedDroppersOf`; a row's chance is its absolute ChanceOrQuestChance, an
  equal-chanced group row an equal share of what the group's explicit chances leave, a reference row's chance scales the table it
  names). Small Crag Boar drops Crag Boar Rib at 0.02% and Chunk of Boar Meat at 0.0094% where Crag Boar drops the rib at 52%: a bot
  too low for the other droppers hunted it with no credit until the contention clock set it aside (GitHub #809); now it has no item
  objective there until it can hunt a real dropper.
* **Empty camps.** With nothing of the quest's entry in sight the bot walks to its spawn points (`PlayerbotWorldDestinations`). A spawn
  point it reaches (within 8 yards) with no live creature of the entry in sight is marked visited, with the spawns of the entry within 10
  yards of it (one camp), for the spawn's respawn delay (the mean of its min and max, clamped to 30 s to 10 min; at most 128 visits,
  only expired ones are dropped to make room, and none is marked while 128 are live). "Alive in sight" counts only a creature the bot would
  take (the grind filter: not unreachable, entry not set aside, inside the home, not tapped by or fighting someone else, not left alone as
  someone else's, attackable), so a tapped kobold does not keep the camp from being left; the walk then goes to the nearest spawn not visited. When every known spawn of the entry is visited (or
  unreachable) the bot has no destination for it until the earliest visit ends and explores meanwhile. Before, the nearest spawn was
  picked again on arrival: live 2026-10-12 (bot QA #368, "stall: goal Grind target 80") 54 bots stalled 177 times at the empty kobold camp of
  quest 21, some nine times each. A stall of a Grind or Combat goal that has no target creature also sets the entry aside for that bot for 10 minutes
  (`PlayerbotSuspensions`, as for a quest goal; defensive target selection also falls back from `Combat.Attackers` to `Combat.ThreatenedBy`,
  `docs/areas/playbots-combat.md`), so the quest's objective is passed over instead of planned again; it is the bot's own
  set-aside, not shared with other bots (an entry set aside for everyone would also stop bots killing it where it stands in sight).

### Flight paths

Bots discover flight masters and fly between known taxi nodes on long trips (`PlayerbotTaxi`; `World:Playerbots:Taxi`, every key live).
Every request goes through the real handlers (`WorldSession.TryManagedAction`); a bot never writes its taxi mask or starts a flight.

- **Discovery.** At most every 2 seconds a bot looks at the objects it sees. A living flight master it may talk to (the server's own
  `InteractableNpc` check: range, faction reaction) whose node (the nearest network node with a mount for the bot's team, as the server maps
  it) the bot does not know gets CMSG_TAXIQUERYAVAILABLENODES. The server learns the node (SMSG_NEW_TAXI_PATH). A master that did not teach its
  node is asked again after a minute. Known nodes are never asked for.
- **Discovery detour.** A bot that is idle for taxi (no trip, no tram or lift ride) and sees an unknown flight master spawn within about 80
  yards of itself (`DetourYards`) walks to it, learns the node (query and mask check only, never a flight), and the goal plans again. A learn
  trip that fails ("node-not-learned", no way, refused plans) backs that node off like any other failure. A node that is backed off, taxi
  off, or a master further away is not walked to.
- **Planning.** `TryTravelByTaxi(session, player, mapId, destination, options)` is called by `PlayerbotWorldDestinations` for a quest
  destination after the tram and lift checks (a lift's own walk is kept). The estimate of the walk is the straight line (no path query). Taxi
  is used when it is at least `MinRouteYards` (default 600), the bot knows a node within `MaxWalkToMasterYards` (default 300) of itself (or, when none is that close, a farther known node whose walk plus the walk from the landing is at most half the straight line), a
  Dijkstra over known nodes of the map (fare = path price) reaches a known landing node within `MaxWalkToMasterYards` of the destination, the
  two walks together are at most half the straight line, a flight master of the departure node that the bot may approach exists (found in the
  creature content, so one out of sight counts), and the bot can pay the whole fare now. The fare used is the undiscounted path price, an
  upper bound of what the server charges after reputation and honor discounts. Otherwise the goal walks as before.
- **The trip.** The brain drives it before anything else (`PlayerbotTaxi.Drive`): walk to the master, stop, send CMSG_ACTIVATETAXI (one hop) or
  CMSG_ACTIVATETAXIEXPRESS (several hops), read SMSG_ACTIVATETAXIREPLY, then wait. In the air (UNIT_FLAG_TAXI_FLIGHT) the bot neither thinks nor
  moves and the stall watch does not judge it. After the landing the goal that asked for the trip plans again from there (usually a short walk).
- **Failure.** A refusing reply (for example combat or money; a bot already flying gets no reply at all; a mounted bot is dismounted by the managed action before either request), no reply, no way to the master, no master where the node is, a
  walk whose plans were refused 3 times in a row (`MaxRefusedPlans`; a pending plan does not count, a route resets the count), a stop that
  never succeeded in 20 attempts (`MaxStopAttempts`, "stop-refused"), or a budget that never accepts the request ends the trip and backs off
  the departure node (not taxi as a whole): the wait follows `World:Playerbots:Travel` `BackoffMs` (60 s, doubling per failure of that node,
  to 300 s), capped by `BackoffMinutes`. A landing clears the node's failures. Other known nodes still fly; the goal walks meanwhile. The
  same refused-plan bound and per-point backoff apply to the tram (per entrance, which is also failed at once when its teleport trigger
  refuses the bot, `PlayerbotAreaTriggers.TryConsumeTeleportRefusal`) and to lifts. A failed tram or lift leg gives up only that leg: the
  quest and its giver are not set aside and no dead-end pin is counted; while the point is backed off the destination waits without a
  set-aside. A completed quest across the tram for a bot below `TramMinLevel` is held (never abandoned or marked unreachable) and taken up
  again at the level. A fight on the way to the master cancels the trip without a backoff. Death cancels it.
- **A killed flight master is not a failure.** A bot that stands at its master's spawn (within 6 yards, `AtSpawnYards`) and finds nobody to
  talk to looks at the spawn's creature in the map's creature system. If it is dead or a corpse, or is gone from the map with its respawn
  timer still running, the trip ends as `TaxiStep.MasterDead`: nothing is reported (no `bot-taxi/master-missing`, so bot QA files no issue),
  no failure is counted (no `repeated-failure`, the node's failure count and its doubling wait stay as they were), and the departure node is
  held off until about the respawn (at least 5 s, at most `BackoffMinutes`; a master that never respawns holds for the cap). The goal
  walks meanwhile, and the node flies again once the hold ends. A master that is alive but not where the node says, has no creature
  and no respawn pending at the spawn, or whose respawn is already due yet has not happened (a link or pool holds it, or the timer is
  stuck), still ends the trip as a failure and reports `bot-taxi/master-missing` (Warning).
- **Config** (`World:Playerbots:Taxi`): `Enabled` (true), `MinRouteYards` (600, 100..20000), `MaxWalkToMasterYards` (300, 10..2000),
  `BackoffMinutes` (10, 1..1440; the per-node cap of the Travel schedule).

Deviations from retail 1.12 and vmangos: the client picks its own route and fare; the bot computes both from the same node and path data. It
sends CMSG_ACTIVATETAXI for a single hop and the express opcode for several (which opcode a 1.12.1 client sends for a single hop is
UNVERIFIED). Intermediate legs are charged by the server when each starts, as for a player. Cross-map paths and scripted (SEND_TAXI) paths are
not used.

### Travel

A bot crosses to a far place the way a 1.12 player does, by its own means (`World:Playerbots:Travel`; every key live; not vmangos keys).
A caller begins a journey through the `IPlayerbotTravel` service (`CanReach`, `TryBegin`, `IsTraveling`); `.playerbot travel $name #map #x
#y #z` begins one for live validation and prints the planned legs. The brain drives it. Nothing writes the bot's position: every leg goes
through the real opcodes (movement heartbeats, CMSG_USE_ITEM, CMSG_CAST_SPELL, CMSG_GAMEOBJ_USE, group, trade and taxi requests).

- **Means.** Walking (with the Deeprun Tram, lifts and known flight paths taking over long stretches of a walk), flight paths
  (`PlayerbotTaxi`; its switch is `World:Playerbots:Taxi:Enabled`), boats and zeppelins (`PlayerbotShip` on the routes of
  `PlayerbotShipNetwork`: the classic `gameobject_template` entries 20808, 176231, 176310, 176244, 177233 are boats, 164871, 175080, 176495
  Horde zeppelins; Booty Bay - Ratchet takes both factions, the other boats the Alliance; each route's two ports are its TaxiPathNode stop
  frames), the hearthstone (`PlayerbotHearthstone`: item 6948, its on-use spell, to the bind point), a mage bot's own Teleport and a portal
  a mage bot opens on request (`PlayerbotMageService`, `PlayerbotPortalBoard`).
- **The planner** (`PlayerbotTravelPlanner`, pure) searches a small graph (start, goal, every port, the bind point, each own-teleport
  destination, each portal's mage and destination) for the fewest seconds whose copper fits the spend cap: the lesser of the bot's money,
  `MaxTravelSpendCopper` and what the journey has not spent yet (a replan plans with what is left). The cap holds for the whole journey: the
  fares of the flights its walks take (offered only up to what is left) and the portal fees it paid are charged once the server took them
  (`TravelJourney.SpentCopper`), and a portal leg begun again after a fight asks the mage it already paid, without a second trade. Costs: a walk is the straight line times 1.3 at 7 yards a second; a flight the walks to the departure node and from
  the landing node plus the hops at 32 yards a second, its fare the undiscounted path price; a boat or zeppelin its round trip (`period`), free;
  the hearthstone and a Teleport 15 s; a portal 25 s plus the fee. Two walks never follow each other; the hearthstone and each Teleport are
  used at most once. Ties: fewer legs, then less copper.
- **Facts** (`PlayerbotTravel.BuildFacts`, from the bot's real state): faction, level, money; the hearthstone when it is ready (switch on,
  carried, off cooldown); the bot's own Teleports (spell known, rune carried, off cooldown); the portals of listed mages of its faction within
  `MageServiceRadiusYards` on its map; the ship routes the bot may ride (faction, the Boats and Zeppelins switches, a route with a backed-off
  dock left out). Two places are walkable when they are on one map and, where the map's walking region graph answers, in one walkable
  component (the check `IsUnreachableOnFoot` makes); a place the graph cannot place (no navmesh, the graph or a tile not read yet, a point
  at sea such as a port, a small fragment) counts as joined, and the walk's own failure then replans. A flight needs a known departure node
  within `MaxWalkToMasterYards` of the stretch's start, a stretch of at least `MinRouteYards`, and a known landing node the fare to which fits
  the cap (the search `TryTravelByTaxi` makes).
- **The journey** (`PlayerbotTravel.Drive`). The brain drives, every think and before the taxi, tram and lift drivers: `PlayerbotShip.Drive`,
  `PlayerbotHearthstone.Drive`, `PlayerbotMageService.Drive` (the bot's own mage leg), `PlayerbotMageService.DriveMage` (a mage serving the
  board), then the journey. A leg driver that answers Traveling owns the bot that think (no stall judged, no target held). The journey walks
  its walk legs itself and only begins the others on their drivers. A walk right before a boat, zeppelin or portal leg ends within 100 yards
  of that leg's start (the next driver walks the last yards to its dock or mage). Within 10 yards of the goal the journey has arrived. A fight
  makes the journey yield (the brain fights) until it is over, except aboard a ship; a fight before the bot is aboard (on the way to the
  dock, waiting for the ship, on the gangway) drops that ship leg, a fight during a hearthstone or Teleport cast cancels the cast (not a
  teleport already begun), and the journey begins the leg again afterwards. Between two legs (never in the middle of one) a bot below 45%
  health or 35% mana rests first: the brain eats, drinks or sits until `World:Playerbots:Risk:RecoverHealthPct` (at most the risk wait),
  then the bot stands and the next leg begins (a stand the session refused, its action budget spent, is sent again at the next think; no leg
  begins seated). A switch turned off (Travel `Enabled`, or the means of the next leg) ends the journey at its
  next safe point without a report; each driver has its own safe point (aboard a ship, the bot rides to the next port and steps off there).
- **Failure.** A failed leg (its driver reported it once) defers only that leg (`GiveUpTransitLeg`: no quest set aside, no dead-end pin);
  the journey replans once from where the bot stands, leaving out the failed means or point on top of the drivers' backoffs; a second
  failure, or a replan with no route, fails the journey (`travel/leg-failed`, once); a journey is never replanned from aboard a transport. A
  journey is bounded by twice its planned seconds plus ten minutes (`travel/overdue`), checked before the leg drivers: the leg under way is
  dropped by its driver's rule, except aboard a ship, where the journey ends once the bot stepped off at the next port; a walk leg's own
  transit goes too while it has not left (a taxi trip still walking to its master, a tram trip still walking to its entrance, a lift trip not
  aboard), so no fare is paid for a journey that is over. A cast leg is bounded
  before the server took its cast too (30 s, or 20 thinks put off: a bot that would not stand still, an action budget spent). Nothing is reported on arrival. Death, a controller, a battleground or a bot-led group taking the bot
  over cancels the journey and its legs (`PlayerbotTravel.CancelLegs`); a bot aboard a ship is never put off at sea (its leg rides to the
  next port, driven by the feature until it stepped off, then the new driver takes it). Chaos probes keep off a bot mid-leg.
- **Ships.** Walk to the walkable point nearest the departure port; wait for the route's ship to wait there with at least 2 s of its stop
  left (bounded by 1.5 round trips); step onto the deck through movement heartbeats and the heartbeat with MOVEFLAG_ONTRANSPORT (the server
  boards it); ride (the server carries passengers through the map change); step off at the arrival port and walk off the pier. Sampled
  every world tick (`PlayerbotShip.Pump` from `PlayerbotMotion.Pump`) so a short stop is not missed. A failure at the departure dock backs
  that dock off. A failure while the server still carries the bot (`ride-overdue`, `disembark-refused`, `wrong-map`, `left-ship` with the
  passenger link still held) is reported at once, but the leg settles: it stays under way, rides to the next port the ship waits at and
  steps off there, and only then ends; so no bot is left aboard without a driver (bounded by two more round trips).
- **Hearthstone.** Stop, CMSG_USE_ITEM for the stone, wait out the cast standing still, confirm the bot stands at its bind point. An
  urgent escape always teleports directly (a ten second cast a fight interrupts is no escape). The brain's other bind-point trips use the
  hearthstone when it is ready; when it is not, only one made for the bot's safety (a bot stranded on ground no route leaves, or exploring
  among creatures too strong for it) falls back to the direct teleport, and the walk to a completed quest's unreachable ender then does
  not go to the bind point.
- **Mage service.** An idle mage bot (alive, out of combat, ungrouped, on no trip of its own) lists itself on the in-world portal board
  with the portals it knows and can cast; a requester's journey asks for a portal of a listed mage near it; the matched mage stops, buys a
  Rune of Portals from a vendor stocking it within 300 yards when it has none, waits for the requester, takes the fee
  (`MageServiceFeeCopper`) by a real trade, casts the Portal, invites the requester into its party when the portal is party-only, and leaves
  that party when the requester went through. The bot-group coordinator leaves a serving mage and a requester alone. A mage's own Teleport
  is a CMSG_CAST_SPELL at itself.
- **Transit backoff.** A failed transit point (a flight path node, a tram entrance, a lift, a ship's departure dock) waits
  `TransitBackoffSeconds` (60), doubling per failure of that point up to `TransitBackoffMaxSeconds` (300); a success clears it.
- **Config** (`World:Playerbots:Travel`): `Enabled` (true), `Boats` (true), `Zeppelins` (true), `Hearthstone` (true), `MageService`
  (true), `MageServiceRadiusYards` (150, 10..1000), `MageServiceFeeCopper` (0, 0..100000), `MaxTravelSpendCopper` (10000, 0..10000000),
  `TransitBackoffSeconds` (60, 10..3600), `TransitBackoffMaxSeconds` (300, 10..86400, not below `TransitBackoffSeconds`). Flight paths:
  `World:Playerbots:Taxi:Enabled` (true).
- **Anomaly codes.** `travel`: `leg-failed`, `overdue`. `ship`: `ship-missing`, `never-docked`, `board-refused`, `left-ship`, `wrong-map`,
  `ride-overdue`, `disembark-refused`, `dock-unreachable`, `route-bad-data`. `hearth`: `no-hearthstone`, `on-cooldown`, `use-refused`,
  `cast-interrupted`, `not-arrived`, `services-unavailable`. `mage`: `teleport-cast-refused`, `cast-interrupted`, `not-arrived`,
  `match-expired`, `mage-gone`, `mage-unreachable`, `fee-trade-failed`, `party-invite-refused`, `portal-cast-refused`, `portal-not-spawned`,
  `portal-not-opened`, `portal-not-used`, `portal-vanished`, `portal-unreachable`, `portal-use-refused`, `rune-purchase-failed`,
  `request-lost`, `services-unavailable`.
- **Off-thread reachability** (`PlayerbotTravel.CanReachStatic`, for the home progression's sweep): the same map, or maps the faction's
  ship routes join, from the routes the world thread published at the first journey; the Travel, Boats and Zeppelins switches count, no
  walk is judged there.
- **Home returns.** The home progression's travel seam (`IPlayerbotHomeTravel`) is registered as `PlayerbotTravelHomeTravel`: a town home
  on another map counts as reachable without a teleport when published boat or zeppelin routes a bot of either faction may ride join the
  two maps, and a home across Teldrassil's water on map 1 when a rideable route has a port on each side; its return rules (`Decide`) stay
  the Home options'. A homed bot whose home lies across the sea (another map, or Teldrassil's water) begins a journey there
  (`IPlayerbotTravel.TryBegin`) instead of the home teleport, except in `ReturnMode` Walk; when none can begin, the return rules decide as
  before (the config-gated teleport) and the next journey home waits the transit backoff, doubling while it keeps failing.
- **Quest goals.** `PlayerbotWorldDestinations` begins a journey (`IPlayerbotTravel.TryBegin`) for a quest goal across the sea: a hunted
  creature with no spawn on the bot's map (its first spawn, by map then spawn, on a map bots may enter that is no dungeon or battleground;
  a homed bot's hunt stays inside its home), or a destination across Teldrassil's water on map 1 (a giver, a completed quest's ender, a
  hunted creature). A spawn across the water is passed over without a path query, and the nearest one gets the journey only when no entry
  on the bot's side was walked to; while its goal waits after a failed journey it is judged as before (a known flight path, or the free
  unreachable skip that sets it aside); the tram's gate holds (`TransitAllowed`: no boat for an unhomed bot below `TramMinLevel`). The brain
  drives the journey from the next think, and the destination plans again where it ends. Each goal (its creature entry and quest) waits as
  the home return does: a journey for it that failed or could not begin waits the transit backoff before the next, doubling while it keeps
  failing, and one that arrived clears the wait; other goals do not wait for it (64 goals held at most). A completed quest's ender on
  another map begins none: the quest goals return only to an ender on the bot's map and abandon a cross-continent delivery ("Intercontinent
  quest handoffs"). Tests: `QuestGoalJourneyTests`.

Known limits. The deck point (6 yards above the ship's origin, inside an 8 by 3 yard rectangle) and the zeppelin tower climb are
UNVERIFIED against the hull models and real terrain (the server has no transport collision); the ship tests run on a flat synthetic world.
The region-graph walk check only separates places the graph can place (ports at sea are not), so a walk across water can be planned and
then fail and replan. Quest pickup still skips a quest whose enders all stand on another continent ("Intercontinent quest
handoffs") and a completed quest's ender on another map is no journey (see Quest goals above). Portal fees need trading on
(`EconomyFeature`); party-only portals need the requester ungrouped.

### Home area

A managed bot can be anchored to an area. Without one it follows its own goals anywhere on the map (its starting-zone quest log, trainers,
corpse runs, risk-weighted pulls); 200 bots teleported into Westfall spread over five zones within ten minutes. This is an ArcaneCore feature:
no reference server anchors a bot, so every number here is an ArcaneCore choice. Code: `Playerbots/PlayerbotHome.cs`, the brain's
`TryReturnHome`, `PlayerbotQuestGoals` and `PlayerbotWorldDestinations`; the return mode in `Playerbots/PlayerbotHomeOptions.cs`. Tests:
`PlayerbotHomeTests`, `PlayerbotHomeZoneTests`, `PlayerbotHomeTeleportTests`,
`ManagedPlayerbotHomeTests`, `ManagedPlayerbotHomeStoreTests`.

* **Setting it.** `.playerbot home $name|$prefix* <$tele|here> [#radius] [zone|nozone]` and `.playerbot home $name|$prefix* clear` (Administrator). A selector is one
  bot by name or id, or every bot (running or not) whose name starts with a prefix of 2 to 11 letters and one trailing `*` (`Wfbot*`), so a stray `a*`
  cannot anchor the fleet. `$tele` is a `game_tele` name (its map and position are the centre); `here` is each running bot's own position. The map must be
  one bots may enter (`World:Playerbots:AllowedMaps`). The reply is `accepted` at once and the result line (`N set ... M skipped ... K failed`) follows.
* **Radius.** In yards, two-dimensional distance, **50 to 5000, default 600** (`PlayerbotHomeBounds`). A value outside is refused before anything changes.
* **Zone clip (default on).** A circle cannot follow a zone border: homed at Sentinel Hill with radius 750 (or Westfall with 1200) it reached Raven Hill
  in Duskwood (creatures 202/213/539/565/569, level 20 and more), and level-14 bots died there again and again (QA death-loop findings, map 0 near
  -10650,450). So a home is also clipped to the **zone of its centre**: a point is inside only when it is inside the circle *and* in that zone. The zone
  is the AreaTable zone (the top-level parent area) at the position, from the terrain's area flag: `PlayerbotZoneLookup` answers exactly what
  `Map.GetZoneAndAreaId` answers (vmangos `TerrainInfo::GetZoneAndAreaId` through `AreaTable.GetZoneAndAreaId`, i.e.
  `TerrainManager::GetZoneAndAreaIdByAreaFlag`), with the flag-to-zone answer cached. The home's zone is taken from its centre when the brain first uses
  it (world thread), so it follows the terrain data, not a stored number. **Fail closed:** a position whose zone the terrain does not know is outside; a
  clipped home whose centre has no known zone contains nothing (every creature, quest and destination is rejected; logged once, `zone=unknown`), and
  `.playerbot home` refuses to set one (`... refused (no zone known ...; add nozone)`, or for a tele place a one-line refusal). `nozone` (before or after
  the radius) turns the clip off and gives the plain circle; `zone` states the default.
* **Storage.** `managed_playerbot_home` (characters schema 56), one row per bot, separate from the bot revision; the zone clip is the column `IgnoreZone`
  (characters schema 58, `ManagedPlayerbotHomeZoneDataModule`), stored inverted so the rows that existed before it read as **clipped**. A home that cannot
  be stored is not applied (fail closed) and is counted as failed. It is re-applied when the bot starts.
* **Staying inside.** "Inside" means inside the circle and, for a clipped home, in its zone. Goal selection ignores creatures outside the area (`IsSkipped`; a
  bot still fights back against whatever attacks it), new quests are taken only from a giver inside the area and only if every creature that ends the quest
  and every creature it asks to kill has a spawn inside (`QuestInside`), the destinations of the world walks (quest givers, enders, kill spawns) are the
  ones inside, and an exploring bot inside `ExploreRadius` whose next leg would cross a clipped home's zone border deflects along the boundary (`ZoneBorderDeflectDegrees`, `DeflectZoneBorderLeg`: ±35°, ±65°, ±95°, ±125° from its heading) before falling back to the centre.
* **Walking home.** A bot on the home map outside the radius, or inside it but out of a clipped home's zone (a bot that wandered over to Raven Hill), with
  no errand, drops its intent and walks to the centre through the ordinary route planner
  (`PlayerbotNavigation`), in route-sized legs with the same hazard guard and risk model as every other walk. **Hysteresis:** it starts at 1.0 of the
  radius and stops at **0.75** (`ReturnFraction`) and back in the zone, so it does not oscillate at the edge; the way back itself may cross other zones (it
  is never restricted). A clipped home whose zone is not known is walked by the circle alone. An exploring bot beyond **0.8** of the radius (`ExploreFraction`)
  aims its next leg at the centre instead of further out (while inside `ExploreFraction`, a leg that leaves a clipped home's zone deflects along the zone boundary first so a graduated start-zone home with `FreshZoneHomeRadius` 4500 follows the zone's interior instead of snapping 180° back to the level-1 start spawn); when that inward leg is refused (on the motion's loop blacklist, through a hazard, no path) the
  next ones turn 25, 50 and 75 degrees either side of it (`PlayerbotBrain.InwardFanDegrees`, `InwardLeg`), instead of asking for the refused one again on
  every think. A turned leg is at most the bot's distance × cos of the turn long, so it always ends closer to the centre than the bot (inside the home,
  also in a small one); a turn whose leg would end out of a clipped home's zone is skipped for the next one (live 2026-10-11, bot QA #154: 74 of 204 stalls 460-480 yards from the Sentinel Hill centre). A stall reported by `PlayerbotStallWatch` sets the walk aside for 30 s at once (state
  `blocked`); the brain's own no-movement stalls (the bot stands still for the stuck time on a route) and failed route plans count instead, and the
  third in a row sets it aside. The goals carry on while it is set aside.
  **Stranded unhomed bots:** the explore index lists only creatures within a few levels of the bot, so an unhomed bot among much stronger creatures
  has none nearby. When a stalled Explore goal is given up and the atlas holds no creature of the bot's level within `StrandedExploreRings` (3 rings, about 480 yards)
  on its map and creatures more than `StrandedLevelMargin` (2) levels above it stand there, the bot teleports to its bind point (non-urgent, the five-minute bind-teleport
  cooldown; live 2026-10-12, bot QA #286 and others), unless it is already within `BindArrivedYards` (100) of the bind point or the bind point is stranded for its level too.
  An over-levelled bot in a low zone or a city, and a homed bot (which keeps `InwardLeg`), are not teleported.
* **Errands override the walk home.** A vendor or trainer visit that has something useful to do (`PlayerbotTownGoals.HasCandidate`,
  `PlayerbotTrainerDestinations.HasCandidate`), a quest exchange already under way, and the turn-in of a completed quest (the way to its ender is never
  restricted) may take the bot anywhere, in any zone; the bot walks home afterwards. The zone clip does not change this: a capital trip for a trainer or a
  vendor is allowed as before (decided: these NPCs stand in towns, and starving a bot of training and repairs is worse than the walk). What the clip does
  keep closed is combat by choice out of the zone: while the bot is on the home map the grind filter rejects every creature outside the area wherever
  the bot is, so a bot on its way to Stormwind picks no fight on the road (it still defends itself). Combat, looting, rest, retreat and the ghost
  recovery run before the home check and are not touched; a dead bot recovers its corpse and walks home after the resurrection.
* **Return mode (`World:Playerbots:Home:ReturnMode`, live).** `Walk`, `Teleport` or `Auto` (default). Westfall-homed level-14 bots that had drifted
  to the Darkshire road in Duskwood (about -10703,-918) and to Loch Modan (about -5886,-2951) walked home across higher-level zones and died in death
  loops on the way (world.log 2026-10-11: `Wfbotfk ... death loop ... attackers=202/531/569`), until the operator sent them home by hand with
  `.playerbot send Wfbot* SentinelHill force`. **Walk** is the walk above, always (and a bot on another map is left `off-map`). **Teleport** makes every
  return a teleport to the centre. **Auto** teleports when the bot is on another map, farther than `TeleportDistanceYards` (default **1500**, 100..20000;
  the Darkshire road stray stood about 2000 yards from Sentinel Hill), when the straight line home crosses an area whose AreaTable level (`AreaLevel`, the 5875
  AreaTable.dbc exploration level of the area the terrain's area flag names, sampled every 20 yards, `PlayerbotAreaLevelLookup`) is more than
  `DangerLevelMargin` (default **5**, 0..60) above the bot's level (Raven Hill is a level-24 area 630 yards from Sentinel Hill: a level-14 bot
  teleports, a level-20 one walks; an area with no known level is never dangerous), or when its walk was set aside as blocked (failed plans, no
  progress, a stall); otherwise it walks. The rules are `PlayerbotHomeRules.Decide`; the level check is made once per return. The points of the line
  inside the home area itself are left out, and a level no higher than the area at the home's centre never counts (`PlayerbotHomeReturn.DangerLevelOnLine`):
  the operator homed the bot there, so a bot homed above its level does not teleport at every return. Inside a WMO group that has an area in
  `wmo_area_template` the level is that area (`TerrainInfo.GetAreaFlag`, vmangos `TerrainInfo::GetAreaFlag`); a cave that is not a WMO, a
  world without the table, or a place with no loaded model tile reads the ADT cell area, so a sub-area with a higher level of its own
  reports the outdoor area level and its danger can be under-reported. The teleport is the one
  the follower's catch-up (`PlayerbotFollower`, `World:Playerbots:Follow:CatchUpMode`) and `.playerbot send` use: the ordinary
  `TeleportService.TeleportTo` (vmangos `Player::TeleportTo`), its near or far acknowledgement answered by `PlayerbotMovementControl` as a client's,
  to the home's centre. **Never** in combat, in a duel, dead or as a ghost, in a group, on a battleground, to a home map `PlayerbotMapPolicy.Allows`
  refuses (`AllowedMaps`), or into a dungeon or raid the bot is not already inside: `.playerbot send` summons nobody into an instance without its
  group (`PlayerbotFleetCommands.SkipReason`, vmangos `HandleGroupgoCommand`, `LANG_CANNOT_SUMMON_TO_INST`) and a lone bot has none, so a home on an
  instance map is reached only by a near teleport inside it. At most **one return teleport per 5 minutes** per bot (`PlayerbotHomeReturn.TeleportCooldownMs`):
  a bot that is out again sooner has its return set aside until then, so a home it cannot stay in costs one teleport per 5 minutes, not one per
  think. A party AI, a bot-led group, the battlegrounds coordinator or a scripted controller drives
  the bot instead of the brain, so its home and the teleport are suspended then. A refused teleport is not retried for 30 s; on the home map the return
  is set aside (`blocked`) rather than walked, off the map the bot stays `off-map`. Each return teleport is logged at Information
  (`Playerbot <Name> teleports home to <place> (<reason>; <distance> yd) from (...) map M to (...) map H`).
* **Off-map policy.** In `Walk` mode a bot on another map than its home does not move itself: it is reported `off-map` and left alone, and an
  operator brings it back (`.playerbot send <Name> <place>`). In `Auto` and `Teleport` it is teleported home when the home map is allowed.
  While off-map the in-area filters are **off** (`PlayerbotBrain.SyncHomeFilters`): the circle has no meaning on another map, and applying it would skip
  every creature and take no quest, starving the bot. It behaves as a bot with no home (normal autonomous grinding, questing and exploring on the map
  it is on) and the filters apply again the moment it is on the home map.
* **Scripted and party bots.** A scripted controller, a party AI or a bot-led group drives the bot and ignores its home (shown as `suspended`).
* **Seeing it.** `.playerbot status` appends `home=inside:120`, `returning:810`, `off-map`, `blocked:810` (distance rounded to 10 yards) or
  `zone-unknown` (a zone-clipped home whose centre has no known zone, so it contains nothing; re-home the bot or set it with `nozone`), followed by
  `,teleports=N` once the bot has teleported home (`home=inside:0,teleports=1`); `.playerbot inspect` prints the place, map, centre, radius, zone
  (`zone=40`, `zone=off`, `zone=unknown`), exact distance, state, the return mode, the return teleports so far and why the last one was made
  (`... state=inside mode=auto teleports=1 last-teleport=distance`). The set reply names the zone of a tele place (`... radius 750, zone 40`) or
  `no zone clip`.
* **Provisioning.** `tools/ops/provision-bots.ps1 -Home` sets each bot's home to the place it is teleported to ([bot provisioning](../ops/bot-provisioning.md)).
* **Stalls outside the home.** A goal on which `PlayerbotSharedSetAsides.BotsToShare` (3) different bots stalled within 30 minutes is set aside for
  every bot (30 min, doubling to at most 2 h). After a full day without a stall on that goal, its next
  shared set-aside starts at 30 minutes again (`QuietGoal_LosesItsRepeatPenaltyBeforeTheNextSharedStall`).
  Only the stalls of bots **inside their home area** (or with none) count
  (`PlayerbotBrain.GiveUpStalledGoal`, `StallOutsideHome`): a homed bot outside it (on another map, on its way back, on a turn-in or capital
  errand) stalls where its home does not keep it, which says nothing about the goal for the bots whose home it is. It still sets the goal aside
  for itself (10 min), and its stall line ends `it gives up that goal outside its home` (bot QA: cause `OutsideHome`, never published). The
  shared line is logged as soon as the set-aside is made (`ManagedPlayerbotFeature.LogStall`; before, only beside the bot's next new stall line,
  so one completed by a further give-up of an old stall waited, and was logged beside a stall on another goal) and names where the bots behind it
  stalled: `Playerbots: 3 bots stalled on quest 109 for 30 min; every bot sets it aside (stalls at x,y,z map M; ...)`. Live 2026-10-11 (build
  f5bc89a5, 200 bots homed at Sentinel Hill with the 550-yard circle, no zone clip, no return teleport): 46 of the 49 stalls behind the 50 shared
  set-aside lines were made outside the home, and quest 109, ended by Gryan Stoutmantle at Sentinel Hill, was set aside for every bot for up to two
  hours by bots stalled in Redridge and at Raven Hill (`PlayerbotSharedSetAsideTests`).
* **Known travel gaps (backlog).** Measured on the same run (`character_queststatus` of the 200 `Wfbot` bots, world.log stall lines):
  * *Turn-ins across the Deeprun Tram.* Dwarf and gnome bots homed in Westfall hold completed Dun Morogh, Ironforge and Loch Modan quests
    (65 bots complete-unrewarded on 400 "Tools for Steelgrill", 79 on 2160, 28 on 353, 19 on 2218, 16 on 1679, 9 on 1879 and 2997, 7 on 2039;
    the other way, quest 1338 "Stormpike's Order" ends in Stormwind for bots in Loch Modan). A completed quest's turn-in is never restricted by
    the home, so these bots walk from Westfall towards Ironforge-side enders: on foot that side is out of reach, they stop at the Westfall/Elwynn
    ridge (-9540,1015; -9488,820) or at the Stormwind tram entrance (-8285,554), stall, set the quest aside for 10 min, and try again. The tram
    is map 369, which an autonomous bot's area triggers walk through (`PlayerbotAreaTriggers`, not in `AllowedMaps`), and the brain has no
    transport step (riding the tram's GAMEOBJECT_TYPE_TRANSPORT, `GameObjectMapSystem.Elevators`). Two ways out, both open: (1) teach bots
    the tram (allow map 369 for that trigger pair, ride the car, leave by the far trigger), or (2) decide that a homed bot does not walk out of
    its home for a turn-in whose ender is not reachable (needs a reachability rule: the navigation mesh alone answers per tile; a zone
    connectivity graph would have to be sourced). Way (1) is done ([Travel by the Deeprun Tram](#travel-by-the-deeprun-tram)); bots still on
    foot there are logged `outside its home` and set nothing aside for other bots.
  * *Terrain stuck points on the way home.* Several bots stalled at the very same coordinates: Raven Hill in Duskwood (-10607.0,294.0,32;
    quests 783, 2158, 6181), the hill west of Lakeshire in Redridge (-9471.9,-1792.0,132; quests 106, 109), north Westfall (-9540.1,1015.0,52;
    quests 400, 2160, 2997). These are navigation obstacles, not quest faults; the zone clip and the return teleport bring homed bots back from
    the first two, the third is on the Westfall-to-Ironforge line above.

### Think time and the destination step budget

A think is measured per phase (`PlayerbotThinkProbe`); `World:Playerbots:SlowThinkLogMs` logs the slow ones with their phases, and the
destination planner's work is split into "destinations entries", "destinations transit", "destinations journey" and "destinations advance"
(the plan queries stay in "grind destinations"). A think over the threshold only because the garbage collector paused the process (think
time minus the GC pause under the threshold) is `bot-brain/think-gc-stall` at Info, not `think-slow`, and never reaches bot QA. The
`bot-brain/think-overran` check (a whole `Update` over 500 ms) subtracts the collector's pause during that `Update` the same way (bot QA #1483).

`PlayerbotWorldDestinations.Update` checks a step budget before each costly step (the tram, lift and taxi block, the unreachable skip, the
path plan, a water journey): the think is past 25 ms, the destination loop past 20 ms (once a candidate was judged), or the think has made
too many path queries. A spent budget **defers** the step: nothing is stored against the spawn (no back-off, no blocked spawn, no refused
plan), and the next think resumes at the same candidate while its list is unchanged. A deferral is never "planning" to the stall watch, so a
bot that is over budget on every think is still seen as stalled. After three deferrals in a row the next think runs its step whatever the
budget, so the destination loop itself always progresses; the brain's own 15 ms optional-work gate can still skip a destination update.
The cached nearest-spawn lists are kept per identity (quest givers, one hunted entry, one quest return; three at most), and blocked spawns
survive switches between them.

### Faults and quarantine

An exception out of a bot's update (brain, scripted controller or `PlayerbotMotion`) is an
**action fault**. It is logged with the whole exception (type, message and stack), the bot's
session closes and the next checkpoint (every 5 s) quarantines it: the character is saved and
the `managed_playerbot` row turns `Faulted` with the fault as `ErrorCode`
(`quarantined (fault 1/3): action: <message>`), but **keeps `DesiredEnabled`**. After
`FaultBackoffSeconds` (30; doubled for every further fault, at most an hour) the bot logs in
again, autonomous (a scenario controller that faulted is not reattached). The `MaxFaults`-th
fault (3) within `FaultWindowSeconds` (3600 s of world time) disables it for good:
`DesiredEnabled` off, `ErrorCode` `disabled after 3 faults: ...`, no retry. A failed retry
login counts as a fault, and so does a failed restore at startup (for example a bot saved on a
map outside `AllowedMaps`): before, that cleared `DesiredEnabled` too. A failed operator
`.playerbot start` is still reported and not retried. `.playerbot start` or `.playerbot stop` clears the quarantine and the
fault history, and wins over a retry already scheduled: the retry re-checks under the operation lock that the bot is still
desired and still in that quarantine, and otherwise ends (`quarantine-cleared`) without logging the bot in. The stored
`ErrorCode` (echoed by `.playerbot` status as `error=`) has control characters of the exception message replaced by spaces
and is cut to 128 UTF-16 units without splitting a surrogate pair. A world restart restores every desired bot, quarantined ones included (the
fault history is per process). Before 2026-10-07 one fault set `DesiredEnabled` off, so a
single transient bug removed a bot until an operator noticed:
`docs/integration/playerbot-faults-20261007.md`. Ordinary session closes that are not faults
(a GM kick) still stop the bot and clear `DesiredEnabled`, as before.

### Another world on the same database

Every `managed_playerbot` write is checked against the row's `Revision`. A refused write (`managed-revision-conflict`) means **another writer owns the row**, usually a second world
process on the same characters database; it is not a bot fault, so a checkpoint, a start or a stop never counts it toward `MaxFaults`:

- A bot whose checkpoint is refused leaves this process's active set. This process writes no `managed_playerbot` row, no set-aside row and no home for it, so the row keeps
  everything the owner wrote (`DesiredEnabled` included); one Warning is logged per bot. It is published as stopped here whatever the row says.
- A restore or `.playerbot start` that is refused answers `managed-revision-conflict` and leaves the row alone.
- A stop writes the `Stopping` row first and saves the bot's set-aside rows only after that write succeeded. If the write is refused it adopts the stored row, drops the bot and writes
  nothing: the other writer's state, goal, error code and set-aside rows survive.
- A row that differs from ours **only in `DesiredEnabled`** is an operator edit (a SQL `UPDATE`): the bot is dropped as above but does not count toward a split brain.
- More than 3 other refusals in one checkpoint are a **split brain**. The process that owned the rows (usually the older, legitimate world, whose checkpoints are now stale) logs `Split brain` at Critical
  with the last conflicting row's revision, state and update time, **stops every active bot** without a managed-row or set-aside write and stops driving their brains, and stops issuing playerbot
  writes: checkpoints, creates, starts, stops, deletes, home changes, the fleet reset and the world's shutdown write nothing (each operator command refuses with `playerbot-split-brain`, including a start, create, stop, delete
  or fleet reset that was already queued behind the checkpoint that latched it; the world's shutdown writes nothing either; entering split brain already stopped every bot, so it has no session left to close). Home changes (`.playerbot home`) do not take the
  operation lock, so a bulk home change cannot block starts: they are checked at entry and before each bot, and a single store write already in flight when the latch is set can still land (the command reports how many it
  stored before the latch). The population manager stands still while it is set: no pass starts, a rush is stopped, and a pass that was already running ends quietly at its next member-row write or bot operation
  (one store call already in flight can still land; a bot the pass had created but not yet started or recorded when the latch was set stays a registered bot without a population row, which the population treats as an operator bot). Quarantine is cleared, so no retry timer fires; a quarantined bot's row stays Faulted, which the population does not restart, until an operator starts it. It shows as a `WARNING` line in `.playerbot status`, in `.playerbot splitbrain` and in the gauge `arcanecore.playerbots.split_brain` (1 while set).
- **Character rows are not covered.** The kicked sessions still save their characters when they close, and the ordinary character autosave continues: there is no clean way to discard a
  session's save, and this change does not add a bypass of the save pipeline. On a file-backed SQLite database the writer lock keeps a second world out in the first place; on MariaDB,
  MySQL and PostgreSQL only `Cluster:Fencing` (`CharacterWriteFenceInterceptor`) bounds the second writer's character writes.

The older world stands down and the intruder keeps running on purpose: neither world can stop the other, and standing down is the only move that guarantees one writer. On MariaDB, MySQL and
PostgreSQL there is no file lock to keep the intruder out in the first place; `Cluster:Fencing` is what bounds it there.

Recovery: stop the other world first. `.playerbot splitbrain clear` (Administrator) re-reads the stored registry rows, reports how many there are, how many are wanted running and how many are
faulted, and lifts the latch so playerbot writes resume. No bot is active at that point, so nothing in memory needs reconciling, and each start reads its stored row afresh. After a clear the population
manager (if enabled) restarts its members over its next passes, as in normal operation; every other bot stays stopped until you start it. Clearing while the other world still runs reopens the writes and the conflicts will latch it again. A restart also clears it.

The incident behind this: a deploy started a second world on the live `characters.db`; it restored 863 bots, failed to bind the port and exited, while the refused checkpoints
counted as faults and disabled 512 bots. A world on a file-backed SQLite characters database now refuses to start while another holds the database
(`<characters database>.world.lock`, taken before the schema initializers) and checks that its port is free before it restores any bot ([operating a realm](../guide/operations.md)).

### Party bots (a real player's group)

`Playerbots/Party/` (vmangos `src/game/PlayerBots/PartyBotAI.cpp`; the player-facing actions of mangoszero's
`modules/Bots/playerbot/strategy/actions`). A player can invite a bot; it then follows, assists, obeys its master and goes
where the master goes.

- **Intake.** Every world tick for a grouped bot, and once per think interval for an autonomous bot in no group (a scripted
  controller drains its own queue), `PlayerbotPartyAI.Intake` drains only SMSG_GROUP_INVITE, SMSG_MESSAGECHAT,
  SMSG_LOOT_START_ROLL and SMSG_RESURRECT_REQUEST from the bot's capture queue (a filtered drain; an unfiltered one would steal
  other components' packets). The answers are a client's own replies and do not wait for the shared action budget. A pending
  invitation is also read from the group state, so one whose packet the 128-entry drop-oldest capture queue evicted is still
  answered; an evicted SMSG_LOOT_START_ROLL is not (that roll waits out its timer).
- **Invitations** are accepted (CMSG_GROUP_ACCEPT) or declined (CMSG_GROUP_DECLINE, which tells the inviter) by
  `World:Playerbots:Party:InvitePolicy`: `None` (only the `Allowlist`), `GuildOrFriends` (the allowlist, the bot's guild
  mates and players on the bot's own friend list), `Anyone` or `Affinity` (default: the allowlist, guild mates and friends, a player the
  bot likes (affinity at least `AcceptInviteAffinity`, 10) or one on the bot's own goal: the same unfinished quest, or for a dungeon goal a player inside that dungeon or at its entrance (the meeting map, within `World:Playerbots:Groups:Cooperation:SameGoalMeetingRadiusYards`, 150, of it, and in level range); a rival is always declined). A player's own friend list does not count: anyone can put any bot
  on it with one CMSG_ADD_FRIEND, so it shows no consent. The same-goal ground is for grouping only (a group invitation, the help-quest
  invite): a trade a bot opens on a player's chat request ("trade me") needs the allowlist, a guild mate, a friend on the bot's list or
  affinity of at least `AcceptInviteAffinity`, and is never opened for a rival; being on the bot's quest is not enough. `.playerbot invite <bot>` (GameMaster; the
  vmangos `.partybot add` analogue) invites a running autonomous bot into the GM's group through the ordinary invite and makes it
  accept whatever its policy says; the invite keeps every ordinary rule (faction, full group, leader or assistant).
- **Filling a party or raid.** `.bot party fill` (GameMaster) invites online autonomous bots of the caller's team that are alive,
  in the world, in no group and not scripted, within 5 levels of the caller while there are enough of them (otherwise the nearest
  levels), and summons them (a bot already being teleported is left to arrive): a party (or no group) is filled to five; once the
  group is a raid each use adds up to five more (one subgroup's worth), up to 40. Before 2026-10-16 it answered "Your party is
  already full." in a raid of five. `.bot raid #count` (`.playerbot raid`, GameMaster) gathers `#count` such bots into the caller's
  raid at once: with no group it forms one and makes it a raid; a party is converted (CMSG_GROUP_RAID_CONVERT rules: only its
  leader, otherwise the reply names the leader); in a raid the caller must be the leader or an assistant. The count is clamped to
  the free raid slots (40 less the members) and the reply says so. Both commands pick the same way
  (`Playerbots/Commands/PlayerbotGroupFill.cs`): tanks first (a warrior before a paladin or druid), then healers (a priest or
  shaman before a paladin or druid), then the nearest levels; a fill of up to five takes one of each, a larger one the raid run
  make-up (`World:Playerbots:Groups:DungeonRuns:Raids` `TanksPerTen` and `HealerPercent`: 10, 20 and 39 bots take 1, 2 and 4
  tanks and 3, 5 and 10 healers at the defaults). The reply gives the counts, the raid size and up to 10 names (`and N more`).
- **Raids.** Nothing in the party AI depends on the subgroup: the master, following, the assist targets (any member's attacker
  within 50 yards), the wipe check, resurrection, auto-revive and loot rolls (SMSG_LOOT_START_ROLL goes to every raid member at
  the group reward distance) work the same in subgroups 2-8 as in subgroup 1, and a bot invited into a raid by hand accepts by
  its invite policy as into a party. A bot's chat reply to a group line goes out on raid chat in a raid, since party chat reaches
  only the speaker's own subgroup.
- **Master.** The group's leader when it is a real player online (a socket client, never a managed bot), otherwise the first
  real player online in member order (vmangos `GetPartyLeader`). While the bot has one, `ManagedPlayerbotFeature` ticks its
  party AI instead of `PlayerbotBrain` (a scripted controller still wins). Out of the group the brain drives it again, a fresh
  one (the old one's routes and targets belong to another place). When every real player of the group is offline or gone, the
  bot waits `MasterTimeoutSeconds` (60, 1..3600), then leaves the group (CMSG_GROUP_DISBAND, vmangos `requestRemoval`).
- **Leader handoff.** Inside a dungeon or raid, pass the lead to one of your bots (Promote to leader) and the bots clear the
  instance on their own; the new leader says `I'll lead the clear of <instance>.`. When it is done it says so and passes the lead
  back; say `follow` (or any order) to take it back sooner. Outside an instance passing the lead changes nothing. Details:
  [Leader handoff](playbots-groups.md#leader-handoff).
- **Out of combat** the bot follows at 2-5 yards at a random angle (`PlayerbotNavigation.TryPlan`/`TryAdvance`), eats and drinks
  through `PlayerbotConsumables` while its master is within 30 yards, and catches up with a master it cannot simply walk to as
  [Following and catch-up](#following-and-catch-up) describes: by default it teleports (vmangos `.goname`) to a master more than 100
  yards away or on another map, and to a distant one it has no reasonable way to (short-distance detours up to 45 yards straight / 160 yards
  along the mesh are walked rather than teleported, and short-distance blocked paths wait a stall window before teleporting), through the teleport service the GM `.goname`/`.namego`
  commands use, so the map resolver's instance rules and the group's instance bind decide where it lands (into the master's
  instance). It lands on the master's spot, not 5 yards above as `.goname` puts a GM: it has no client to fall. A master on a map
  the bot may not be on (outside `AllowedMaps` and no dungeon or raid, `PlayerbotMapPolicy`), on a taxi flight (vmangos idles
  then), or in another instance of the bot's own map id (the service teleports within one map id by a near teleport, which keeps
  the bot's instance) is waited for, not followed. A refused teleport is retried after 10 s.
- **Combat** (vmangos `SelectAttackTarget`/`SelectPartyAttackTarget`): the target the master ordered, else the master's
  victim, else whoever attacks the bot, else whoever attacks another member within 50 yards; `PlayerbotCombatSpells`, then a
  chase to 4 yards and CMSG_ATTACKSWING.
- **Players as targets** (`PlayerbotHostility`, one rule for the party AI, the brain and the battleground AI). A player is a target only
  where the combat hooks allow the attack: never a teammate or a Game Master in GM mode, and an enemy player only while PvP-flagged
  (or in a started duel with the bot). On top of that a bot starts on a player unprovoked only when ordered: the master's `attack` on a
  selected player is obeyed (reply `Attacking <name>.`) when that player is a valid target, else the bot answers `I have no target
  to attack.`. Without an order a bot fights back a player who attacks it, assists the master against a player the master attacks
  and defends a member a player attacks, the same rule real players are held to. A managed bot outside a group does the same for itself:
  the brain (`PlayerbotBrain.Pvp.cs`) fights back a player attacker through `PlayerbotPvpFight`, before the quest return, the creature
  threat choice and the combat linger, and lets go past 45 yards or when the player is no longer hostile. Just after it, a bot with an
  event-scenario stay (`PlayerbotBrain.Stay.cs`, think phase `stay`) owns the think: it defends itself, walks back to the stay or stands, and
  chooses no destination, until the owner's lease runs out (the stay is not a defend post: no PvP flag, no post scan, retreat runs first).
- **Raid marks** (vmangos `PartyBotAI::SelectAttackTarget`, PartyBotAI.cpp:366-377). While the bot or its master is in combat, the
  skull of the group (`MSG_RAID_TARGET_UPDATE`, set by the leader or an assistant) is attacked before the master's victim; an explicit
  `attack` order still comes first. A mark on a unit that is gone or not attackable is skipped.
- **Round-robin loot.** vmangos PartyBotAI.cpp:565-585 unassigns the bot's round-robin loot on every party kill
  (SMSG_PARTYKILLLOG, broadcast to the group there). ArcaneCore sends that packet to the killer alone, so at every think out of
  combat the bot looks over the corpses it can see (within 74 yards, the group reward distance) for loot it holds
  (`LootBag.Owner`), whoever made the kill and whatever the bot was doing (fighting another mob of the pack, passive, staying).
  It walks up, opens each and releases it untouched (CMSG_LOOT, CMSG_LOOT_RELEASE), and the loot service opens the leftovers to
  the group. A staying bot walks back to its place afterwards. A release the shared action budget held back is tried on the
  next think; a corpse it could not reach or open three times is left.
- **Loot rolls** are answered at once with `LootRoll` (`Need` by default, or `Greed` or `Pass`): in `Need` mode (`PlayerbotLootRolls.VoteFor`),
  the bot rolls `RollVote.Need` on wearable gear upgrades (`PlayerbotEquipment.TryPickSlot`), container bag upgrades (`IsBagUpgrade`), and
  unlearned spell, recipe or mount items (`CanLearnFromItem`), and `RollVote.Greed` on all other items, so a roll never waits out its
  timer for a bot (mangoszero `LootRollAction`).
- **Dead:** a group member's resurrection is accepted (CMSG_RESURRECT_RESPONSE; a stranger's declined). With `AutoRevive`
  (default on) the bot revives in place at half health when vmangos `ShouldAutoRevive` allows it (a released ghost; or nobody
  fighting, no living healer class to resurrect it, and a living member within 15 yards), and otherwise waits up to 2 minutes
  (vmangos would wait for ever and never releases a party bot outside battlegrounds); then, or without `AutoRevive`, it
  releases and runs back to its body like the brain (`PlayerbotRecovery`), to the end: the released ghost is not revived at the
  graveyard.
- **Commands**, by whisper or party chat and only from the master, one word, any case: `follow`, `stay` (hold this place: no
  following, no teleport, fight back only what attacks the bot), `attack` (the master's current target), `stop`/`passive`
  (stop fighting, follow without attacking), `come` (walk to the master, then hold there), `status` (a whisper back: level,
  health %, mana % and the current activity) and `leave`. A command sent right after the bot joined counts even before the
  bot's first turn in the action budget. Each is acknowledged by whisper (CMSG_MESSAGECHAT in the bot's own
  language). With [bot chat](#bot-chat) on (the default) any other line is the chat's; with it off, a whispered word that is
  no command gets the command list and a whisper from anyone else gets one polite answer (once per sender a minute, at most 8
  senders a minute). Other bots' lines are always ignored.

`.playerbot inspect` prints `BOTINSPECT party=master:<name> mode:<follow|stay|passive>` (or `party=none`); while the party AI
drives a bot its goal is `Follow` or `Assist` (appended to the persisted `PlayerbotGoalKind`). A controller that takes over a
grouped bot (scripted mode) makes the party AI let go: no party goal, master or mode is reported while it drives, and when it
detaches a bot still grouped is engaged afresh. Brain bots acknowledge a
far teleport through `PlayerbotMovementControl.AcknowledgeTransfer`, pumped every tick from `PlayerbotMotion.Pump` (commit `c5c66cd9`),
so a cross-map summon or an instance entry no longer strands them; the party AI does the same when it follows its master into an instance.
The fleet commands are in [Bot commands](#bot-commands).

### Bot commands

`Playerbots/PlayerbotFleetCommands.cs` (selection and ring placement in `PlayerbotFleetSelection.cs` and
`PlayerbotFleetPlacement.cs`). Fleet control of the registered managed bots, under `.playerbot`. Every command works on the
bots in the service snapshot only, never on a real player.

| Command | Level | Effect |
|---|---|---|
| `.playerbot summon <sel> [#count] [force] [to $player]` | Administrator | Teleport running bots to you (or the named online player), in rings around the spot. With a count, the nearest bots on that player's map go first. |
| `.playerbot send <sel> <$tele\|here> [force] [to $player]`, `.playerbot send <sel> at $map $x $y $z [force]` | Administrator | Teleport running bots to a `game_tele` place, to your position (`here`, or the named player's), or to the given map and coordinates (`at`: no player needed, so RA and the console can use it; the map must exist, the numbers must be finite and inside the map, each bot stands at the collision ground (terrain and models) found from the given z on the loaded continent map, as with `here`, so a spot inside a building or a city keeps its floor, and at the given z where that map is not loaded or knows no ground; no `to`, not a battleground map; a `game_tele` that the place lookup matches for `at` and the four words (an exact name, else a name containing them) still wins over the position form), in rings. |
| `.playerbot follow <sel> [to $player]` | Administrator | The bots follow you (or the named player) without a party and fight back. |
| `.playerbot stay <sel> [to $player]` | Administrator | The bots hold position (they fight only what attacks them) and obey the player's whispered orders. |
| `.playerbot dismiss <sel>` | Administrator | End a follow or stay; the bots go back to autonomous play. |
| `.playerbot near [#radius]` | GameMaster | List the running bots within the radius (default 50, at most 500 yards) of you: level, class, health, state, goal, distance (at most 20 lines). |
| `.playerbot revive <sel>` | Administrator | Revive the dead bots among the selection, as `.revive` does. |
| `.playerbot defend <sel> here #radius [force]`, `.playerbot defend <sel> at $map $x $y $z #radius [force]`, `.playerbot defend <sel> clear` | Administrator | The running bots hold a defend post at your position (`here`) or at the given map and coordinates (`at`: no player needed, same map, number and battleground checks as `send ... at`; the post keeps the height you give) ([Defend posts](#defend-posts)); radius 10 to 200 yards (clamped). More bots than the bulk cap need `force`. `clear` ends it. |
| `.playerbot siege waypoint add [at $map $x $y $z]\|list\|clear`, `siege start <sel> #entry [raids 1\|2] [force]`, `siege stop`, `siege status [$leader]`, `siege watch [on\|off]` | Administrator | Bot-led raids march the stored waypoints in step and kill the creature with the entry ([Siege](playbots-groups.md#siege-playerbot-siege)). Memory only. |
| `.playerbot eventscenario list\|status [#event]` | Administrator | Bot scenarios that start and stop with a game event, from the `game_event_playerbot_*` tables ([scenarios on events](playbots-scenarios-events.md)). Read only. |
| `.playerbot travel $id\|$name #map #x #y #z` | Administrator | One running bot begins a journey to the place by its own means ([Travel](#travel)); the reply lists the planned legs (means, end, seconds, copper), or the refusal (a controller, the party AI, a bot-led group or a battleground holds the bot; or the planner's: travel off, no route, a leg of another journey under way). |
| `.playerbot start <sel> [force]`, `.playerbot stop <sel> [force]` | Administrator | Start or stop every registered bot the selector names; one summary comes back when the worker finishes. |

**Selector `<sel>`.** `all`, a name **prefix** with one trailing star (`Wfbot*`; letters only, a bare star or a star inside a
word is refused), a bot id or one character name. Matching is case-insensitive and by registered name; a selector that
matches nothing answers `No playerbot matches '<sel>'`. Any other argument shape shows the command's help.

**Bulk cap.** `World:Playerbots:MaxBulkCommand` (default 50, live; see [configuration](../reference/configuration.md)) is the
most bots one command moves or starts. A selection larger than the cap, with no smaller `#count`, is refused with
`give a count <= N or add 'force'`; `force` lifts the cap. `all` plus `force` is the explicit "everyone" form. `defend ... here` has no count: it answers `N bots match; add 'force' above CAP`.

**Console.** The interactive server console ([server-console](server-console.md)) has no invoking player. `summon`, `send ...
here`, `follow`, `stay` and `near` mean "to me", so from the console they refuse unless `to $player` names an online player
(`near` takes no target and always refuses). `defend ... here` also refuses from the console (it has no `to $player`); `defend ... clear`, `send $tele`, `dismiss`, `revive`, `start` and `stop` need no player.

**Instances.** A bot is moved into an instanceable map only when it is in the target player's group or already in that very
instance (vmangos `HandleGroupgoCommand`, `TeleportCommands.cpp:181-266`); otherwise it is skipped as `instance`. A bot
already on the same map id but in a different instance than the target is skipped as `other-instance` (a near teleport would
keep its own instance). Into a non-instance map the destination must pass `World:Playerbots:AllowedMaps`, else
`map-not-allowed`. Battlegrounds are refused for `send`.

**Far teleports.** A summon or send may cross maps (map 1 to map 0). An autonomous bot answers it through
`PlayerbotMovementControl.AcknowledgeTransfer`, pumped every tick from `PlayerbotMotion.Pump` (commit `c5c66cd9`): it sends the
MSG_MOVE_WORLDPORT_ACK a client would after its loading screen, so the bot resumes on the new map.

**Reply and skip reasons.** `Playerbot <verb>: matched=N <done>=M skipped=K (reason=count ...)`, then up to 15 lines
`<name>: <reason>` and `... and N more.`. Reasons:

| Reason | Meaning |
|---|---|
| `is-target` | The bot is the player the command is relative to. |
| `dead` | The bot is dead or a ghost (`revive` it first). |
| `scripted` | A scenario or controller drives the bot. |
| `teleporting` | The bot is already mid-teleport. |
| `instance` | Instance target, bot neither grouped with the target nor inside it. |
| `other-instance` | Same map id, a different instance than the target's. |
| `map-not-allowed` | The destination map is not in `AllowedMaps`. |
| `security` | The bot's account outranks the invoker (never from the true console; a remote-administration invoker is checked like any other). |
| `not-running` | The registered bot has no live in-world session. |
| `not-managed` | The session is not a managed bot. |
| `no-free-spot` | No ring slot with ground and line of sight was left for it. |
| `teleport-refused` | The teleport service refused the move. |
| `in-player-group` | `follow` or `stay`: a real player's group already leads the bot. |
| `alive` | `revive`: nothing to do. |
| `not-leashed` | `dismiss`: the bot was not following or staying. |

`start` and `stop` list the operation's own failure code instead (for example `playerbot-capacity`) under `failed=`.

#### Defend posts

`.playerbot defend <sel> here #radius [force]` gives the running autonomous bots a post: the GM's map and position and a radius (10 to 200
yards, clamped). `.playerbot defend <sel> at $map $x $y $z #radius [force]` gives the same post at an explicit map and position and needs no
player (RA, the console); the map must exist and the numbers be finite and inside the map, and the height is kept as given. Bots in a real player's group, scripted, or driven by the party AI, a bot-led group or a battleground are skipped
(`in-player-group`, `bot-busy`, `bot-scripted`). Every bot of one command shares a post id. A post is **memory only** (`PlayerbotDefendPost`
on the brain): it is not saved: a restart, a bot stop, a playerbots hot reload or `.playerbot defend <sel> clear` ends it. A brain replacement (a bot leaving a
real player's party, a controller detaching) keeps it. The PvP flag the post turned on is tracked per player (`PlayerbotDefend.OwnFlag`), so
whichever of those ends the post also turns `PvpDesired` off again, before the character is saved; a flag the post did not turn on is left alone.

A bot with a post owns its think right after death and recovery and the transit legs (a lift ride), before any quest, grind or explore step
(`PlayerbotBrain.Defend.cs`). In order:

1. **PvP flag.** It sends CMSG_TOGGLE_PVP on when the server does not show `PvpDesired`, again at every think until it does; `clear` sends it
   off (the server's five-minute linger then runs out, vmangos `Player::UpdatePvPFlagTimer`). Undercity's guards are flagged, so Alliance bots
   flag on their first swing; Horde defenders are not, so they flag themselves.
2. **Fight.** A player attacking the bot first, then what it fights already while inside the post (10 yards of chase margin), a creature
   attacking it, then the scan of the post: the nearest PvP-flagged hostile player inside the radius (`PlayerbotHostility.MayEngage` with the
   `Defend` grant), else what a friendly creature (`Hooks.IsFriendly`, so a guard) that is in combat inside the radius fights: its victim and
   its attackers. The scan runs at most once per second per defender (as does the heal's look at the post mates); the fight is `PlayerbotPvpFight`, for players and creatures alike.
3. **Heal.** A priest, paladin, druid or shaman heals the most wounded defender of the same post (below 85% health) within 40 yards when
   the heal is safe (`IsSafeHealTarget`, as for a tagger).
4. **Return.** Outside the full radius, the bot walks back with the home walk (`TryReturnHome`: lift-aware, the Auto return mode's teleport
   rules apply) and stops once it is within 0.75 times the radius of the centre (`PlayerbotHome`); no errand holds a defender off its post.
   **Not while it carries resurrection sickness** (spell 15007, `SpiritHealerResurrection.ResurrectionSicknessSpell`, read through
   `PlayerbotSickness.Holder`; a permanent aura does not count): a defender the spirit healer revived stays at the graveyard where it stands,
   rests, sits and buffs (steps 1 to 3 still apply, so it fights back what attacks it) and walks back the think after the aura ends. Without this the
   walk-back took it under the raid that had killed it, where it died again (live 2026-10-20: 3 deaths in 253 s near the Undercity lift top). The
   risk line of `.playerbot status` and `inspect` reads `decision=defend-wait-sickness; <the real risk line>` meanwhile (both are shown); a dead or ghost defender shows the real line only. A defender without the aura is unchanged.
5. Otherwise it holds, rests and buffs. The goal shows as `Home`; `.playerbot inspect` prints `defend=r=40 post=3 map=0 centre=x,y,z`.

An unflagged enemy player is never attacked, and a hostile player outside the radius only if it attacks the bot.

#### Following and catch-up

`Playerbots/Party/PlayerbotFollower.cs` (rules in `PlayerbotFollowRules.cs`, the drop in `PlayerbotFollowDrop.cs`), configured under
`World:Playerbots:Follow` (every key live through `.reload config`). The party AI drives it; it takes a session and a leader, so any
other follow of a managed bot can drive it the same way.

The live report behind it (2026-10-10, build a88f6135): the master dropped into the Moonbrook mine just short of the Deadmines portal
(landing at -11138.0, 1506.9, 24.4) and his bots, following, ran round the mine through its Defias and died or looped. On the real
mmaps the follow planner's way from the mine ledge 11.9 yards above him is 89.5 yards for 16.4 yards of straight distance, past an
elite Defias Conjurer; from the Moonbrook surface above it is 320.6 yards for 22.6; no off-mesh (drop) connection exists in the nine
navigation tiles round the mine. The 100-yard teleport never fired, so the bots walked; and a party bot whose master went into the
Deadmines waited outside, because the master's map had to be in `AllowedMaps` ([0, 1] on the live server).
`PlayerbotFollowDeadminesTests` replays both places.

Each think the follower first takes the reference decision (`PlayerbotParty.DecideFollow`, vmangos `PartyBotAI::UpdateAI`
:795-817 and :884-891): hold within 5 yards; wait for a master between maps, on a taxi flight, in another instance of the bot's map
id or on a map it may not be on; teleport to a master on another map or, in `Teleport` mode, more than `TeleportDistanceYards` (100,
vmangos `IsWithinDistInMap(pLeader, 100.0f)`) away; otherwise walk. A walking follower plans to the point beside its master and judges
every fresh plan (`PlayerbotFollowRules.Judge`). The way is **no way** when:

- the navigation mesh has no route (`NoRoute`);
- the master is within 50 yards across and 30 yards or more above or below (`Vertical`: mangoszero's playerbot
  `MovementAction::Follow`, `AiPlayerbot.SightDistance` 50 and `SpellDistance` 30, teleports then);
- the route passes inside the aggro radius of a hostile `DangerLevelMargin` (3) levels or more above the bot, or of an elite (rank 1
  to 3) at or above its level (`Danger`; the radius is the server's own, `PlayerbotRecovery.Threats`);
- the route is longer than `MaxPathRatio` (3) times the straight distance and more than `MinDetourYards` (40) longer than it
  (`Detour`);
- the follower made no progress towards a master standing still for `NoProgressSeconds` (8) twice in a row (`Stalled`). The first
  such window re-plans from where the bot stands (and forgives the motion's loop blacklist round the master); progress is the way
  left shrinking by 2 yards, and a master who moves more than 5 yards starts a new window, so trailing a running master never
  stalls.

A good way is walked. Otherwise a **drop** is taken in every mode when the master is at least 3 yards below and within 40 yards
across (`PlayerbotFollowDrop`): the floor is followed towards him (and in a fan up to 90 degrees either side) to its edge; past it the
floor beneath must lie at least 3 yards lower, the way over the edge and the column down must be clear, the vmangos fall damage
(`Player::HandleFall`: nothing under 14.57 yards; gravity `Movement::gravity`, vmangos `spline/util.cpp:24`) must cost less than `MaxFallHealthPercent` (50) of the bot's current health, and
the floor it lands on must lead to the master by the mesh within twice the straight distance plus 10 yards. The bot walks to the edge,
reports the step off it as a client does (MSG_MOVE_HEARTBEAT with MOVEFLAG_FALLINGFAR one step out) and, the free-fall time later
(vmangos gravity 19.29 yd/s²), MSG_MOVE_FALL_LAND with that fall time: the server applies the fall damage. In the Deadmines it drops
12.4 yards from the ledge (no damage) and stands beside its master 2.2 s after he landed. A fall always ends on the ground or is
forgotten: an order that stops the follow mid-air (stay, a fight, leaving the group) lands it first; a bot that dies or is
teleported away mid-air is not landed afterwards. A drop searched for in vain is searched for again only after 2 s. Then by
`CatchUpMode`:

- `Teleport` (default, the reference behaviour: vmangos PartyBotAI and mangoszero's playerbot both teleport a follower): teleport for
  any of the reasons above. From the Moonbrook surface the follower teleports at once instead of running the 320-yard mine.
- `FightThrough`: never teleport, on the bot's map or to another: walk the way it has (a partial way towards the master when there is
  no route), drop where a drop leads down, engage the hostiles whose aggro reaches the next 40 yards of the way (after the ordinary
  targets: the ordered one, the master's, the bot's and the group's attackers; never when passive), and, with the master on another
  map, walk into the area trigger on the bot's map that leads there (a dungeon entrance or exit; the bot may take the triggers that
  lead to its master's map, and only those, whatever `AllowedMaps` says); with no such trigger it waits. A blocker the bot failed
  to reach three times is left alone. Stalls re-plan for ever. A follower that keeps dying stays a corpse-run
  follower.
- `Auto`: teleport only for `NoRoute` or `Stalled` (and to another map); otherwise as `FightThrough`.

A catch-up teleport needs `World:Playerbots:Party:TeleportToLeader` (off: never), a server-owned session with no teleport pending (the client half
acknowledges near and far teleports every tick: `PlayerbotMovementControl.Update`, `PlayerbotMotion.Pump`), a master on the ground
(not in the air), the bot not duelling (vmangos `PartyBotAI.cpp:808`, `!IsInDuel()`), and the bot out of combat (vmangos teleports only inside its `!IsInCombat()` block) unless its master left the
fight: out of combat himself and farther than `LeaderCombatRangeYards` (50, vmangos' party assist range) or on another map. The
master's map counts as one the bot may be on when `AllowedMaps` lists it (an empty list allows every map) or when it is a dungeon or
raid (`PlayerbotMapPolicy`; the login gate lets a bot stay in the instance it is in). Each catch-up teleport and drop is logged at
Information by `PlayerbotFollower` with its reason and the measured way.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `CatchUpMode` | `Teleport` | `Teleport`, `FightThrough`, `Auto` | How a follower catches up (above). |
| `TeleportDistanceYards` | 100 | 10..1000 | `Teleport` mode: a master farther away on the bot's map is teleported to (vmangos). |
| `MaxPathRatio` | 3 | 1.5..20 | A way longer than this times the straight distance (and `MinDetourYards` longer) is a detour. |
| `MinDetourYards` | 40 | 0..500 | A way at most this much longer than the straight distance is always walked. |
| `DangerLevelMargin` | 3 | 0..60 | A hostile this many levels above the bot (or an elite at its level) on the way makes it no way. |
| `LeaderCombatRangeYards` | 50 | 0..1000 | A bot in combat teleports only to a master out of combat and farther than this. |
| `NoProgressSeconds` | 8 | 2..120 | The no-progress window of the loop detection. |
| `MaxFallHealthPercent` | 50 | 0..99 | A drop is taken when the fall costs less than this % of the bot's health (0: only harmless falls). |

### Bot chat

`Playerbots/Chat/`, configured under `World:Playerbots:Chat` (every key live through `.reload config`). Bots answer players who
talk to them, in character, from their real state. It is on by default with only the **built-in** provider: no network, no key,
no cost. Language-model providers are opt-in additions in front of it.

- **Triggers.** A whisper to the bot; a party (or raid) line that names the bot as a whole word ("Bob, where are you?"); a
  `/say` line within hearing that names it, from a player of the bot's own faction. `Channels` (`Whisper, Party, Say` by default)
  narrows this. Lines from other bots and from the bot itself are never answered. The one-word party commands (`follow`,
  `stay`, ...) keep their meaning and are never sent to a provider.
- **Order.** `PlayerbotPartyAI` builds the bot's facts on the world thread (name, race, class, level, zone and subzone from the
  area table, the brain's or party AI's goal and the current quest's title, the party master, and recent events seen between
  looks: a level gained, a death, a return to life, money picked up) and hands the line to `PlayerbotChat.TryAsk`: cheap checks
  and a bounded queue (`MaxQueuedRequests`, 16), never I/O. Two background workers try the providers in order and put the reply
  on a queue that `ManagedPlayerbotFeature` drains on the world thread; the bot says it through its own CMSG_MESSAGECHAT (a
  whisper back, the party line, or `/say`), so observers get the ordinary SMSG_MESSAGECHAT. A line taken by the chat but not
  answered (the cooldown, every cap spent, a full queue, every provider failing) gets no reply: the fixed replies above apply
  only with chat off or on a channel it does not answer.
- **Failover.** A provider is skipped while its key variable is unset, while it cools down, past its `MaxRepliesPerHour`, or,
  if priced, once the day's spend estimate reaches `MaxDailySpendUsd`. A failure moves on to the next provider: a 429 waits out
  its `retry-after` (at most 10 minutes), a 5xx/529, timeout (`TimeoutSeconds`, 10) or connection failure backs off 15 s
  doubling to 5 minutes, a 401/403 waits 10 minutes, another 4xx (a bad model id) 5 minutes; a refusal or an unreadable answer
  just moves on. The built-in provider is always last, so a bot whose model providers all fail still answers. No failure reaches
  the bot's behaviour: a reply that cannot be said is dropped and logged, never a fault.
- **Limits.** `PerPlayerCooldownSeconds` (8): one reply per player across all bots in that time. `MaxRepliesPerHour` per provider
  (120). `MemoryExchanges` (4): the earlier exchanges with the same player sent with a model request (at most 512 conversations
  are kept). Replies are one line of at most 255 characters with `|` (the client's link and colour escape) replaced.
- **Orders in plain language** (`NaturalLanguageCommands`, on): from the master only, "follow me", "wait here", "attack my
  target", "stop attacking", "come here" run the same party command as the one-word form (acknowledged the same way). A model
  provider returns `{"reply", "intent"}` JSON whose intent is one of `follow`, `stay`, `attack`, `stop`, `come` or `none`;
  anyone else's order gets a polite no.
- **GM.** `.playerbot chat status` (GameMaster): on/off, channels, queued, answered, unanswered, the spend estimate and cap, and
  per provider its kind, model, key variable and whether it is set (`key=ANTHROPIC_API_KEY:present`), replies this hour against
  the cap, errors, the last error class (`rate-limited`, `unavailable`, `unauthorized`, `rejected`, `refused`, `timeout`,
  `network`, `bad-response`, `reasoning-only`, `empty-reply`, `moderation-failed`) and the remaining cooldown, then a `Bot chat safety:` line (below).
  `.playerbot chat flags [player]` and `.playerbot chat pardon <player>` (GameMaster) are described under Safety and provider
  policies.

**The built-in provider** (`PlayerbotBuiltinChat`, templates in `PlayerbotChatTemplates`) reads the line's intent with a few
patterns, in this order: severe abuse (ignored), an insult (a short brush-off), "are you a bot?" (the honest answer: a bot run
by this server), an order, help, grouping ("want to group?": a yes when the bot's `InvitePolicy` would accept the player's
invitation, otherwise a polite no; already grouped says so), what it is doing or where it is going (its goal, quest title and
zone), where it is (zone and subzone, or that it does not know), its level, race and class, thanks, goodbye, a greeting, and
otherwise a short in-character line. Each answer has several phrasings plus race, class and level variants (`greeting.race.Orc`,
`who.class.Mage`, `who.level.novice` below 10, `who.level.veteran` at 60); a phrasing whose placeholder has no value is
skipped, and the same line is never said twice in a row to the same player. It follows the same triggers, cooldown and hourly
cap as the models. To add a line, add a string to the table; to add a variant, add a key. Lines say only what the bot knows of
itself and plain game mechanics, never invented lore.

**Model providers** (`Providers`, at most 8, tried in order; a listed `Builtin` entry must be last and sets its own cap):

| Key | Meaning |
|---|---|
| `Kind` | `Anthropic` (Messages API: `POST {BaseUrl}/v1/messages`, headers `x-api-key` and `anthropic-version: 2023-06-01`), `OpenAICompatible` (`POST {BaseUrl}/chat/completions`, `Authorization: Bearer` when a key is set: OpenAI, OpenRouter, Ollama, LM Studio), `InProcess` (a GGUF model run inside the world process, below), or `Builtin`. |
| `BaseUrl` | Anthropic: the origin (empty = `https://api.anthropic.com`). OpenAICompatible: the API base with its version (`https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `http://localhost:11434/v1`). A key is never sent over plain `http` to a host other than this machine (the configuration is refused). |
| `Model` | Empty = `claude-haiku-4-5` for Anthropic; required for OpenAICompatible. |
| `ApiKeyEnvironmentVariable` | The variable that holds the key. Unset: `ANTHROPIC_API_KEY` for Anthropic, none otherwise. `""`: a keyless local server. Keys are read only from the environment, at each request: never from configuration, never logged or shown. |
| `Headers` | Extra headers, e.g. OpenRouter's `HTTP-Referer` and `X-Title`; they cannot replace the key or version headers. |
| `MaxRepliesPerHour` | 1..100000 (120). |
| `MaxTokens` | The reply limit, 16..1024 (150). |
| `TokenLimitParameter` | OpenAICompatible: `max_tokens` (default) or `max_completion_tokens` (newer OpenAI models). |
| `ReasoningEffort` | OpenAICompatible: sent as `reasoning_effort` when set: `none`, `minimal`, `low`, `medium`, `high` or `xhigh` (any case); empty (default) sends nothing. Set `none` for a thinking model (below). The `Classify` moderation call inherits it, and defaults to `none` for a server on this machine when it is empty. `omit` never sends the field, to the reply or to the classifier call (for a server that rejects it). |
| `JsonResponseFormat` | OpenAICompatible: send `response_format: {"type":"json_object"}` with the reply prompts, so the server constrains the model to a JSON object (`true` by default; Ollama and OpenAI support it). Set `false` for a server that rejects the field: LM Studio answers 400 to `json_object` (it only takes `json_schema`), which shows as `failed: rejected` and a cooldown; the warning then names this option. A reply that is still not readable JSON (fenced, escaped, trailing text are tolerated) is reported as the `bot-chat/prompt-json-invalid` anomaly and the next provider answers; it is not a server fault. |
| `InputUsdPerMillionTokens`, `OutputUsdPerMillionTokens` | Prices for the spend estimate. Unset: the built-in Anthropic price of a known model (Claude Haiku 4.5 $1/$5), otherwise unpriced. Set 0 for a local model. |
| `Moderation`, `ModerationBaseUrl`, `ModerationModel`, `ModerationApiKeyEnvironmentVariable` | The optional moderation step before a line is sent to this provider: `None` (default), `Endpoint` or `Classify` (see Safety and provider policies). |

A thinking model (qwen3.5 on Ollama, for one) puts every generated token in `message.reasoning` and answers with
`content: ""` and `finish_reason: "length"`, even at `MaxTokens` 1024. That is not a reply: it is reported as `reasoning-only` (not
`bad-response`), the log says "the model spent its tokens thinking; set ReasoningEffort=none for this provider", and a second one in
a row backs the provider off (15 s, doubling to 5 min) instead of asking the model on every line; the same holds when the `Classify`
moderation call is the one that only thinks. Only an HTTP provider (`OpenAICompatible`, `Anthropic`) is backed off for unreadable
answers: an `InProcess` model's bad answer is about the content, never the provider. Fix it by turning reasoning off for
that provider:

```json
{ "Kind": "OpenAICompatible", "BaseUrl": "http://localhost:11434/v1", "Model": "qwen3.5:4b",
  "ApiKeyEnvironmentVariable": "", "ReasoningEffort": "none" }
```

The prompt's system part comes in two pieces: the rules (1.12 setting, short in-game chat style, nothing outside the game, no
claim of being an AI unless asked directly, a brush-off for abuse, the JSON answer format), identical for every bot and request,
then the bot's own facts. Anthropic gets the rules as a separate system block with `cache_control`; OpenAI-compatible endpoints
get one system message that starts with them, so providers that cache prompt prefixes can. Claude Haiku 4.5 caches only a
prefix of 4096 tokens or more, and the rules are far shorter, so in practice nothing is cached there today; the breakpoint costs
nothing.

A local model first, the cloud as a fallback, the templates last (a local model costs nothing; `OPENROUTER_API_KEY` set in the
server's environment; the OpenRouter prices are an example, check the provider's current price):

```json
"Chat": {
  "Providers": [
    { "Kind": "OpenAICompatible", "BaseUrl": "http://localhost:11434/v1", "Model": "qwen3:4b",
      "ApiKeyEnvironmentVariable": "", "InputUsdPerMillionTokens": 0, "OutputUsdPerMillionTokens": 0, "MaxRepliesPerHour": 1000 },
    { "Kind": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1", "Model": "openai/gpt-4o-mini",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY", "InputUsdPerMillionTokens": 0.15, "OutputUsdPerMillionTokens": 0.6,
      "Headers": { "HTTP-Referer": "https://github.com/Krilliac/ArcaneCore", "X-Title": "ArcaneCore" } },
    { "Kind": "Anthropic", "MaxRepliesPerHour": 60 }
  ]
}
```

**Cost control.** Every model reply is one request of roughly 400-800 input tokens (the rules, the facts, up to four remembered
exchanges) and at most `MaxTokens` (150) output tokens. On Claude Haiku 4.5 ($1 input, $5 output per million tokens) that is
about $0.0015 a reply at most, so the default cap of 120 replies an hour costs at most about $0.18 an hour per provider, and the
default `MaxDailySpendUsd` of $1 stops the priced providers after roughly 650 replies in a UTC day. The estimate uses the token
counts each response reports (cache writes at 1.25x and reads at 0.1x the input price) and is an estimate, not a bill; a provider
without a known price is held back by its `MaxRepliesPerHour` alone, so give OpenRouter and OpenAI entries their prices. The
per-player cooldown keeps one player from spending the budget, the bounded queue drops rather than piles up, and the built-in
templates answer once the money or the providers run out. Tests never touch the network: `IBotChatClient` is faked, and the four
live checks in `PlayerbotChatLiveTests` (one tiny request each to Anthropic, OpenAI, OpenRouter and a local server) run only
with `ARCANECORE_TEST_LIVE_LLM=1` and that provider's key or `ARCANECORE_TEST_LOCAL_LLM_URL` set.

#### In-process model

`Playerbots/Chat/Inference/`. A provider entry `{ "Kind": "InProcess" }` answers with a small chat model that the world process
itself runs on the CPU, in managed C# only: no network, no key, no other process or service, no native library. It costs nothing
(its price is zero, so it never touches `MaxDailySpendUsd`) and it is still a model provider, so the safety screening, disclosure,
opt-out and the per-player cooldown apply exactly as for the remote ones. **No model ships in the repository and the built-in
templates stay the default**: without an `InProcess` entry nothing here is loaded, and with one the templates still answer every
line the model cannot.

- **Engine.** `GgufFile` maps the GGUF file read-only (v2/v3: header, metadata, tensor table, alignment). The weights are never
  copied: each matrix row is dequantized on the fly into a per-thread buffer (`GgmlQuant`: F32, F16, Q4_0, Q5_0, Q8_0, Q4_K, Q6_K)
  and dotted with `TensorPrimitives` (SIMD). `BpeTokenizer` reads the gpt2 byte-level BPE vocabulary, merges and pre-tokenizer
  (`qwen2`, `llama-bpe`, `smollm`, `gpt2`) from the metadata. `ChatModel`/`ChatInferenceContext` run the `llama` and `qwen2`
  decoders (RMSNorm, RoPE, grouped-query attention with a key/value cache, SwiGLU, tied or separate output, qwen2's q/k/v biases).
  A file of another architecture, tokenizer or quantization, or with RoPE scaling, is refused at load with the reason.
- **Threads and budget.** Replies run on the engine's own BelowNormal-priority threads (the scheduler thread, plus one per slot
  when several slots run in a step), each slot with `Threads` (2) BelowNormal helpers for the matrix products: never the thread
  pool, never the world thread. `MaxConcurrent` (1) replies run at once and
  `MaxQueued` (2) may wait; past that the request is "busy" and the templates answer. A reply that is not done within `BudgetMs`
  (6000, checked between passes, prompt included) is dropped; `CircuitBreakerOverruns` (3) overruns in a row rest the model for
  `CircuitBreakerSeconds` (300; anomaly `bot-chat/inprocess-circuit-open`). `BudgetMs` must be below `TimeoutSeconds` x 1000.
- **Prompt.** ChatML (`<|im_start|>role ... <|im_end|>`, special tokens inserted by id, never parsed from a player's text): a short
  fixed rule block with the grounding rules (one short 1.12-era chat line; only the facts given; no invented quests, bosses, items,
  places, players or events; no emojis, quotes, lists or real-world talk; the honest bot answer when asked), then the bot's facts
  (the same `DynamicSystem` the remote providers get) and a one-line reminder, then the remembered exchanges and the line. The
  rule block is identical for every bot, so the key/value cache keeps it between replies and only the bot's facts and the
  conversation are computed per reply. The model writes the line itself (no JSON), so plain-language orders are not read from an
  in-process reply; the templates' command patterns still work. Sampling is the sampler chain ported from Sonder-Inference
  (`Inference/Sampling/`, Apache-2.0, THIRD_PARTY_NOTICES.md): penalties over the last `RepetitionWindow` (64) tokens with
  `RepetitionPenalty` (1.15), then `TopK` (40), `TopP` (0.9), `MinP` (0.05), `Temperature` (0.7) and a seeded draw (greedy at
  temperature 0); `Seed` set makes it deterministic. Before every token the one-chat-line constraint masks anything that would
  start markup or a role token (any `|`, `<think`), or a second line once the line has text; leading blank lines are trimmed. The
  reply stops at end-of-turn, at `MaxNewTokens` (48) or at any of `StopSequences` (none by default; the match is not said).
- **Scheduler.** One BelowNormal engine thread runs the iteration-level scheduler ported from Sonder-Inference
  (`Inference/Scheduling/`) over the `MaxConcurrent` slots: each step every resident reply gets either a prompt chunk of
  `PrefillChunkTokens` (64) or one decode token (decode first), and the slots run in parallel. Requests are classed: a player who
  named or whispered the bot (`interactive_player`) before another bot (`bot_to_bot`) before guild chatter, announcements and
  lines put to a whole channel (`ambient`). When no slot is free, a waiting request of a higher class preempts a running one of a
  lower class (that reply is recomputed later from its prompt and what it wrote); an ambient request still waiting after
  `AmbientQueueMs` (4000) is dropped. A request is cancelled when the chat's deadline passes, when its bot stops (logout,
  removal: `PlayerbotChat.Abandon`) or, for an ambient line, when a player starts talking to that bot; an abandoned reply is not
  said and counts against no provider. With an in-process provider the chat runs `2 + MaxConcurrent + MaxQueued` workers (at most
  16) so the scheduler sees the competing requests.
- **Prefix cache.** Each slot keeps the tokens it last held; across slots, whole 16-token blocks of prompt keys and values are kept
  in a pool of `PrefixCacheTokens` (1024; the ported KV block manager: chained block hashes, LRU eviction of unreferenced blocks)
  and copied into whichever slot a reply lands on. The system rules every prompt starts with are computed once at load.
- **Screen.** A reply is dropped (and the templates answer) when it is empty, longer than `MaxReplyCharacters` (160), holds markup,
  quotes, a link, an emoji or a non-Latin character, a leaked role token (`im_start`, `assistant:`) or a model name; a reply cut by
  the token limit keeps its last whole sentence. The ordinary output screening then applies as for any model.
- **Failure.** A missing or unreadable file is reported once (anomaly `bot-chat/model-load-failed`) and logged; every request then
  fails as `rejected` and the templates answer. A request is never an exception to the bot.
- **Status.** At start: `Bot chat provider #N: InProcess <model> <arch> <params> <quant>, <threads> threads, budget <ms> ms` and a
  line with the layers, vocabulary, context, mapped megabytes and cache size. `.playerbot chat status` adds `Bot chat in-process:`
  with the load state, replies, tokens, tokens per second (generation and prompt), reused prompt tokens, fallbacks, overruns, the
  circuit, busy, filtered, unloaded, cancelled, errors and dropped, then the scheduler (`sched active= queued= preempt=` priority/KV
  `dropped=`, per class `ttft=` / `itl=` / `queue=` p50/p95 in ms over the last 256 replies) and the prefix cache (`warm=` tokens
  prefilled at load, `restored=` tokens copied in, `stored=`, `blocks=` used/total, `evicted=`). At start a further line names the
  classes, slots, prefill chunk, prefix-cache size, the shared prefix's tokens and prefill time, and the sampling. Meters
  `arcanecore.playerbot.chat.inprocess.replies`, `.tokens`, `.overruns` and `.fallbacks` (by `reason`, now also `dropped`), and by
  `workload`: histograms `.ttft`, `.itl` (a reply's mean), `.queue` (ms), counters `.preemptions`, `.dropped`, and `.prefix_tokens`.

`World:Playerbots:Chat:InProcess` (read when the model loads, at start: changing it takes a restart):

| Key | Meaning |
|---|---|
| `ModelPath` | The GGUF file, relative to the content root or absolute (required with an `InProcess` provider). |
| `Threads` | 1..16 (2): threads one reply computes on. |
| `MaxConcurrent` | 1..4 (1): replies computed at once; each has its own threads and cache. |
| `MaxQueued` | 0..64 (2): replies waiting for a slot. |
| `BudgetMs` | 250..60000 (6000): the hard wall-clock budget of one reply. |
| `MaxNewTokens` | 8..256 (48). |
| `ContextTokens` | 256..32768 (1024, capped at the model's): prompt plus reply. |
| `Temperature`, `TopK`, `TopP`, `MinP`, `RepetitionPenalty`, `RepetitionWindow`, `Seed` | Sampling (0.7, 40, 0.9, 0.05, 1.15, 64, unset); `MinP` 0..1, `RepetitionWindow` 0..1024. |
| `StopSequences` | At most 8 strings of 1..32 characters that end a reply (none). |
| `AmbientQueueMs` | 250..60000 (4000): how long ambient chatter may wait for a slot before it is dropped. |
| `PrefixCacheTokens` | 0..16384 (1024; 0 = off): prompt tokens kept in the shared prefix cache. |
| `PrefillChunkTokens` | 32..512 (64): prompt tokens one reply computes per scheduler step. |
| `MaxReplyCharacters` | 40..255 (160). |
| `CircuitBreakerOverruns`, `CircuitBreakerSeconds` | 1..100 (3), 10..3600 (300). |

```json
"Chat": {
  "Providers": [
    { "Kind": "InProcess", "Model": "smollm2-360m", "MaxRepliesPerHour": 600 }
  ],
  "InProcess": { "ModelPath": "models/chat/SmolLM2-360M-Instruct-Q8_0.gguf", "Threads": 2, "BudgetMs": 6000 }
}
```

**Which model.** The owner provides the file; nothing is downloaded by the server. Small instruct models that fit the engine:

| Model | File | License | Memory | Notes |
|---|---|---|---|---|
| SmolLM2-360M-Instruct | Q8_0 (~0.39 GB) | Apache-2.0 | ~0.4-0.45 GB | llama, `smollm` pre-tokenizer, tied output; 960 wide, so its K-quant files fall back to other types: use Q8_0. |
| Qwen2.5-0.5B-Instruct | Q8_0 (~0.53 GB) or Q4_K_M (~0.40 GB) | Apache-2.0 | ~0.45-0.6 GB | qwen2, biases, tied output; its Q4_K_M mixes Q4_K, Q5_0, Q6_K and Q8_0 tensors, all read. |

Memory is the mapped file (paged in as it is read) plus, per slot, the key/value cache (SmolLM2-360M: about 80 MB at 1024 tokens;
Qwen2.5-0.5B: about 25 MB) and a few megabytes of buffers; the tokenizer tables add 10-30 MB. CPU: a reply costs roughly 2 x the
parameter count in floating-point operations per token, so a 360M model is about 0.7 GFLOP per prompt token and per reply token.
The cached rule block is computed once; each reply computes its facts and conversation (typically 80-150 tokens) and up to 48
reply tokens, on `Threads` BelowNormal threads. Expect a few seconds per reply on a desktop core pair; measure on the live
machine before enabling it:

`tools/ArcaneCore.ChatModelProbe` (in the solution) loads a file through the same engine and prints the model's shape and tensor
types, the tokenizer round trip of a sample line, the prompt and generation tokens per second (cold, then with the rule block
cached) and the greedy reply to a sample bot prompt with what the screen keeps of it. Run its built executable with the model path
(and optionally the thread count and context size). Pick `BudgetMs` from what it reports, under the live load.

#### Safety and provider policies

`Playerbots/Chat/PlayerbotChatSafety*.cs`, configured under `World:Playerbots:Chat:Safety` (every key live through `.reload config`,
range-checked; see the configuration reference). A public server that sends players' lines to an external model provider is
responsible for what it sends: Anthropic, OpenAI and OpenRouter each have usage policies that apply to the requests made with the
server's key, whoever typed the words. These safeguards keep the obvious violations away from the provider and limit players who
keep trying; they reduce the risk, they do not remove it, and they are no substitute for reading the policies of the providers you
configure. Nothing here applies to the built-in provider, which sends nothing anywhere.

- **Screening before a provider sees a line** (`Enabled`, on). Every line is screened on the chat worker before the first model
  provider is tried:
  1. the **local filter** (`PlayerbotChatSafetyFilter`), by category (`Categories`, all by default): `SexualMinors`, `SelfHarm`,
     `Threats` (real-world: "I know where you live", doxxing, swatting), `Hate` (slurs, calls for violence against protected
     groups), `Harassment` ("kys"), `IllegalGoods` (making explosives, buying hard drugs) and `PersonalData` (email addresses,
     phone numbers, street addresses). The term and pattern lists ship in `PlayerbotChatSafetyTerms.json` (an embedded resource);
     `TermsFile` names an operator file of the same shape that is added to them (or replaces them, `ReplaceDefaultTerms`) and is
     read again within 10 seconds of a change. The shipped list is deliberately conservative: it aims at what provider policies
     forbid, not at rudeness ("noob" and "idiot" stay the built-in brush-off's business), and stays clear of words common in WoW
     chat ("kill", "die", "bomb", "gun", "naked", a "suicide pull"). Matching folds case, accents, leetspeak (`k1ll y0urs3lf`),
     spaced-out letters (`k y s`) and repeated letters (`kiiill`); terms match whole words, and a trailing `*` matches longer words.
     Patterns are compiled with `RegexOptions.NonBacktracking` (matching time linear in the line, whatever the pattern: an
     operator's pattern with a back-reference or lookaround is skipped with a warning) and a 100 ms timeout; a line the filter
     cannot judge in time goes to no model (the built-in provider answers) but costs no strike. The timeout is wall-clock time, so a
     process pause during a match (a garbage collection, a starved CPU) can trip it on a harmless line. A pattern's first match is
     the slow one (its matcher is built then), so every pattern is run once on the thread pool when the filter is loaded, at start
     and after a reload, and a line waits for that warm-up rather than paying it. A timed-out screening is counted
     (`timed-out=` in the status, metric `arcanecore_playerbot_chat_screening_timeouts_total{kind}`) and logged as a warning at
     most once a minute, naming the pattern (`Harassment pattern #2`, `personal data (phone)`), never the line.
  2. each provider's optional **moderation step** (`Moderation` on the provider entry, `None` by default): `Endpoint` posts the
     line to an OpenAI-compatible `/moderations` endpoint (`ModerationBaseUrl`, default the provider's own `BaseUrl`;
     `ModerationModel`, default `omni-moderation-latest`; `ModerationApiKeyEnvironmentVariable`, default the provider's key), so an
     Anthropic entry can use OpenAI's moderation endpoint; `Classify` sends the provider a one-word SAFE/UNSAFE classification
     request (at most 16 output tokens, counted in the spend estimate) before the reply request. A flag keeps the line from every
     provider; a failed step skips that provider (`moderation-failed`) and the next one is tried, so a line is never sent
     unchecked.

  A flagged line reaches no model provider. `OnFlagged` (`BuiltinReply`) answers it with the built-in brush-off (for `SelfHarm`, a
  short line suggesting a trusted person or a local crisis line; for `PersonalData`, a caution), or nothing (`Ignore`). It is not
  remembered either, so it is never sent later as conversation memory.
- **Screening what a model says** (`ScreenOutput`, on). A model's reply is run through the same local filter before the bot says
  it; a hit is never said: the built-in provider answers the player's line instead (`OnOutputFlagged`: `BuiltinReply`) or nothing
  (`Drop`). The flag is recorded with the reply's excerpt and is no strike against the player. A reply the filter cannot judge in
  time fails closed: it is never said either and takes the same path (`OnOutputFlagged`), with no flag record (nothing was found,
  even when a personal-data pattern ran out of time, which on a player's line counts as personal data) and no strike; it is
  counted and warned as an `output` timeout. If the filter's warm-up meets an unexpected error, one warning names the error's
  type and lines are screened without it.
- **Flags, strikes and cut-off.** Every flag is kept in an in-memory ring of `FlagLogSize` (200) records and logged as a warning:
  the character's name and GUID, the account id, the bot, the category, the source (local filter, moderation or output
  screening), the time, and, while `StoreExcerpt` is on, at most 60 characters of the line with emails, phone numbers and addresses
  replaced. There is no persistent flag table (that would need a schema change): the log warnings are the durable record, and the
  ring, strikes, cut-offs and players' choices are lost at a restart. A flag of `SexualMinors`, `Threats`, `Hate`, `Harassment`,
  `IllegalGoods` or the moderation step is a strike (`SelfHarm` and `PersonalData` are not); strikes older than
  `StrikeWindowMinutes` (60) fall away. `StrikesBeforeCutoff` (3) strikes within the window cut the player off from the model
  providers for `CutoffMinutes` (60): built-in replies only. With `AutoMute` (off) the cut-off also mutes the player's chat for
  `AutoMuteMinutes` (30) through the account mute of `.mute` (persisted, set by "Bot chat safety" at GameMaster level, lifted by
  `.unmute`; a longer mute already in force is kept).
- **GM.** `.playerbot chat flags [player]` lists the newest 15 flag records (one player's when named, after a line with their
  strikes, remaining cut-off and AI choice); `.playerbot chat pardon <player>` clears a player's strikes and cut-off (not a mute).
  `.playerbot chat status` ends with `Bot chat safety: screening=on screened=.. flagged=.. moderation-flagged=..
  output-flagged=.. builtin-only=.. timed-out=.. cut-offs=.. cut-off-now=.. auto-mutes=.. opted-out=.. opted-in=.. disclosed=..` and the
  filter's size.
- **Disclosure and choice.** While a model provider is configured, a player who talks to a bot gets, once per login and before
  the first reply, a system message: `DisclosureText` ("Bot replies on this server may be written by an AI service, which
  receives what you say to bots.") plus how to opt out (`Disclosure`, on). With `AllowOptOut` (on), whispering any bot `ai off`
  limits that player to built-in replies, `ai on` undoes it and `ai` tells them where they stand; these whispers are answered by
  the safety itself and never sent to a provider. `RequireOptIn` (off) turns it around: the model providers answer only players
  who whispered `ai on`, and the disclosure says so. A player's choice lasts until the server restarts.
- **What is sent.** A model request carries the bot's facts, the bounded memory (`MemoryExchanges`) and the current line, with
  players named only by character name: never an account name, an address or a GUID. With `StripPersonalData` (on), email
  addresses, phone numbers and street addresses in the line and the memory are replaced by `[email]`, `[phone]` and `[address]`
  (this matters when `PersonalData` is left out of `Categories`; otherwise such a line is flagged before it gets that far). If
  one of those patterns runs out of time, the whole text is sent as `[removed]` (fails closed), counted and warned as a `strip`
  timeout. The excerpt of a flag record is stripped the same way, and its timeouts count as `strip` too.

**Operator responsibilities.** Read and follow the usage policies of every provider you configure, and keep the server's own
rules in line with them. Keep `Enabled` on whenever a model provider is configured (the start log warns when it is off). Review
`.playerbot chat flags` and the log warnings, extend the list through `TermsFile` for what your players actually say, and consider
a provider's moderation step on a busy public server. Tell your players, in your server's own terms, that bot chat may be
processed by a third-party AI service. The screening is a set of word lists and patterns: it will miss some lines and flag a few
harmless ones, and it says nothing about what a provider will accept.

### Bot speech

Every line a bot says goes through one path (`World:Playerbots:Chat:Speech`, all on by default): `PlayerbotChat.TrySpeak` takes a
`BotSpeech` (Say, Yell, Party, Raid, RaidWarning for a raid leader, Battleground, Guild, Whisper, a world channel by name, or a text
emote), screens it with the output filter, and the world thread says it through the bot's own session as a client packet
(`CMSG_MESSAGECHAT`, `CMSG_TEXT_EMOTE`); the server's own chat handlers deliver it. Nothing is broadcast by the server.

- **Limits** (`PlayerbotSpeechLimiter`): per bot a gap between lines it starts (`BotGapSeconds`) and a per-minute cap
  (`BotLinesPerMinute`); per target `TargetGapSeconds`; shared per place: a world channel per zone (`ChannelLinesPerMinute`, the
  minute stretched by the number of bots in it over `ChannelCrowdBots`), the say/yell area (`LocalLinesPerMinute`), a group
  (`GroupLinesPerMinute`), a guild (`GuildLinesPerMinute`); the same text in the same place once per `DedupeSeconds`.
- **Listeners**: a line a bot starts on its own is said only when a real player can hear it (`RequireRealListener`); a reply's
  audience is its addressee.
- **World channels**: bots join General and LocalDefense of their zone, Trade in capitals and LookingForGroup, and leave the old
  zone's on a zone change, with real join/leave packets (`WorldChannels`, `ChannelChangesPerPass`, on the social pass).
- **Answers**: a bot answers whispers, and lines naming it in say, yell, party, raid (in raid chat), battleground, guild and world
  channels, on the channel the line came on (`World:Playerbots:Chat:Channels`, all by default). A real player's open question in guild chat ("anyone
  up for SM?") may be answered without the bot's name, by the built-in only with a group offer, a greeting or thanks.
- **Bots hearing bots** (`BotToBot`): another bot's line naming the bot is answered with small talk only, at most
  `BotToBotMaxTurns` turns per exchange (a new exchange after `BotToBotQuietSeconds`), `BotToBotPairLinesPerHour` per pair and
  `BotToBotLinesPerMinute` in all. Never an order, never the AI disclosure.
- **Emotes** (`EmoteReplies`): a text emote aimed at a bot is answered in kind (a wave with a wave, a bow with a bow, a hug with a
  blush), once per emoter per `EmoteReplyGapSeconds`.
- **Observability**: each said line logs at Debug (`Bot {Bot} says {Channel} ({Topic}, {Length} chars)`) and counts in
  `arcanecore.playerbot.chat.lines_spoken`; refused ones in `arcanecore.playerbot.chat.lines_dropped` by reason
  (docs/ops/metrics.md). Lines heard are raised as `PlayerbotChat.LineHeard` for other features to react to.

#### Conversations

Bots talk with each other and with the world channels (`World:Playerbots:Chat:Conversation`, all on by default, live key by key).
Every line still goes through `TrySpeak`, the limiter and the real-listener check above, and is filled from the bot's real state
(`PlayerbotPersonaFacts`: health and mana, gear, bags, money, needs, spare goods, quest progress, role and spec, guild, group,
nearby players and creatures, the dungeon that suits its level); a line with a slot that has no real value is never said. The
built-in lines read like 2005 chat (LFG/LFM, WTB/WTS, inc, oom, ty, gz) and need no model; a configured model may enrich replies.

- **Conversations** (`PlayerbotConversations`, on the social pass): about `ConversationsPerHour` times an hour two bots talk for
  2..`MaxLines` lines (never past `1 + BotToBotMaxTurns`): a ding, a boss the group killed, loot, a death, food or water one lacks
  and the other carries, a quest both are on, a dungeon, what each is doing, a guild hello. Groupmates use party chat, bots within
  `NearYards` say, guildmates guild chat; only where a real player hears it. Lines are `LineGapSeconds` + up to
  `LineJitterSeconds` apart; a line refused for a moment is retried until `LineDeadlineSeconds`. At most `MaxActive` at once,
  `PairCooldownMinutes` per pair, `BotCooldownMinutes` per bot, `CandidatesPerPass` bots looked at per start. A real player
  speaking to either bot ends the conversation at once, so the player is answered first.
- **World channel reactions** (`WorldReactions`): a real player's LFM line for a dungeon that fits a free bot (and the role it asks
  for) gets a whisper ("inv", "lvl 24 priest here, inv?"); a WTB line naming goods a bot carries spare gets an offer priced at twice
  the vendor value. At most `ReactionsPerLine` bots per line, `WorldReactionsPerMinute` lines a minute, one player per
  `ReactionPlayerCooldownMinutes`; heard lines wait in a queue of `MaxHeardQueue`. The bots' own LFG and WTS posts are the bot voice's (below).
- **Social acts from chat**: asked to invite, trade, share a quest (the master only) or look for a group, a bot does it with its own
  client packets (`CMSG_GROUP_INVITE`, `CMSG_INITIATE_TRADE`, quest sharing, an LFG post), checked again on the world thread.

### Bot voice

Bots say what they want and need (`World:Playerbots:Chat:Voice`, all on by default; `PlayerbotNeedsVoice`, run on the social pass).
Every line is read from the bot's real state, written by the built-in lines (`PlayerbotVoiceTemplates`, 1.12 chat slang, many
variants each) or the configured model, screened by the chat safety filter and said through the bot's own session within the speech
limits above. A phrasing is used only when every value in it is real; nothing is invented. Work per pass is bounded: the pass-wide topics
read the group coordinator and the running battlegrounds, then at most `BotsPerPass` bots are looked at (those grouped with a real
player first, the rest in turn) and at most `LinesPerPass` lines are handed on.

- **LFG / LFM** (`LookingForGroup`): for each group goal bots wait on (by goal and team), the one waiting longest posts in
  LookingForGroup (outside a capital `GeneralSharePercent` of the time in General) "LFG SFK, 24 mage" alone or "LF2M SFK, need tank and
  heals" with the roles still missing, once per `LookingForGroupRepeatSeconds`. A real player's whisper to the poster asking for an
  invite ("inv", "can i join", "i can heal") within `PostLifeSeconds` puts the player into that group's matching
  (`PlayerbotGroupCoordinator.Volunteer`): the group forms with the player when enough bots wait, and the leader invites through the
  normal group path. Too low, already grouped or no longer looking is answered so.
- **Quest help** (`QuestHelp`): a bot waiting for partners on an elite quest asks General "anyone for <quest>?" once per
  `QuestHelpRepeatSeconds`.
- **Trade** (`Trade`): once per `TradeRepeatSeconds`, in Trade in a capital (General `GeneralSharePercent` of the time, and only then
  outside one): WTS for the best real surplus in the bags (a green or better weapon, armour or recipe that is not bound, bind on
  pickup, a quest item or an upgrade; a stack of five or more trade goods the bot's recipes do not need; worth `MinSellCopper` or more)
  at `PriceMultiplier` times the vendor price; else WTB for a reagent its wanted recipes lack; else "WTB water" for a mana user with
  nothing to drink. A whisper asking about the item gets the price and where the bot is, or "sold it". The sale itself is not made:
  a bot only trades with its group or master.
- **Party needs** (`GroupCalls`, a bot in a group with a real player): "oom" (under 10% mana), "need heals" (under 30% health with a
  healer there), a tank's "inc 3" on a pull, "<mob> on me" to the tank, "rez pls" once per death with a resurrecting class alive,
  "ty <name>" after a resurrection or a long buff, "brb drinking/vendor/training" when its brain stops it, "ding" after a level up,
  "share <quest> pls" in a bot group on a quest it lacks, "first time in <zone>" for a zone none of whose areas it had explored. A real
  player's "ding" in party or guild chat gets one bot's "gz".
- **Run plan** (`RunCalls`): the bot leading a dungeon or raid run says "everyone in?", "pulling <boss>", "<boss> down", "wait for
  mana", "wipe, run back" and "gg" as the run moves on.
- **Battleground callouts** (`BattlegroundCalls`): from the match's real state, one bot of the team says each event in battleground
  chat (the same callout not again within `BattlegroundCallSeconds`): Warsong Gulch "flag taken", "EFC mid", "flag returned"; Arathi
  Basin "inc BS", "lost farm", "LM capped", "capping ST"; Alterac Valley "IB tower under attack", "TP burned", "SH GY assaulted".

## Scenario harness

Namespace `ArcaneCore.World.Playerbots.Scenarios`. A scenario is an `IPlayerbotScenario`
(`Name`, `Description`, `RunAsync(ScenarioContext)`) that logs managed bots in scripted mode
and drives them step by step against the real world handlers, asserting server state.
`ScenarioRunner.RunAsync(scenario, bots, world, services, clock, options)` runs it and returns
a `ScenarioReport`; afterwards it stops (logs out and saves) the bots, or returns them to
autonomous mode (`ScenarioRunOptions.StopBotsAfterRun`).

**Bots.** `ScenarioContext.LoginAsync(name, race, class)` reuses the bot of that name or
creates one, and starts it scripted. `ScenarioBot` is the controller: it records every packet
it sends and every packet its session captures (`ScenarioPacketLog`, run-wide sequence
numbers; the tap sits before the session's bounded drain queue, so bursts cannot evict a
reply) and, by default, acknowledges server teleports and movement orders like a client.

**Typed client actions** (`ScenarioBot`; each runs one CMSG through its handler and the bool
is transport admission only, outcomes come from replies or state): `TargetAsync`,
`AttackAsync` (selection + swing), `StopAttackAsync`, `CastAsync`; `LootAsync`,
`LootMoneyAsync`, `LootItemAsync`, `ReleaseLootAsync`, `UseGameObjectAsync`; `InviteAsync`,
`AcceptInviteAsync`, `DeclineInviteAsync`, `LeaveGroupAsync`, `SetLootMethodAsync`;
`InitiateTradeAsync`, `BeginTradeAsync`, `SetTradeItemAsync` (item GUID; bag and slot are
resolved), `SetTradeGoldAsync`, `AcceptTradeAsync`, `CancelTradeAsync`; `SendMailAsync`,
`GetMailListAsync`, `TakeMailItemAsync`, `TakeMailMoneyAsync`; `RequestDuelAsync` (spell 7266),
`AcceptDuelAsync`, `CancelDuelAsync`; `SayAsync`, `PartyAsync`, `WhisperAsync`, `ChatAsync`
(in the bot's team language: the server refuses Universal outside AFK/DND); `QuestHelloAsync`,
`AcceptQuestAsync`, `CompleteQuestAsync`, `RequestQuestRewardAsync`, `ChooseQuestRewardAsync`;
`AreaTriggerAsync` (instance entry: the far teleport is then acknowledged automatically);
`SendAsync(opcode, payload)` for anything else. Payload layouts are in `ScenarioPackets` and
follow the server's own handler parsing. The MockClient keeps its own independent encodings
on purpose (it is a second oracle), so the builders are not shared with it.
Ships (`ScenarioTransports`, docs/areas/transports.md): `context.ShipAsync(entry)` finds a route's ship,
`BoardAsync(ship, x, y, z)` sends a heartbeat standing on it at that offset, `LeaveShipAsync()` one without it,
`TimeSkippedAsync(ms)` a CMSG_MOVE_TIME_SKIPPED; `ScenarioTransports.TransferPending` and `NewWorld` decode the
map-change packets.

**Typed decoders** (`ScenarioDecoders`, mirroring the server writers): group list, party
command result, group invite, trade status, mail result and mail-list count, duel requested,
duel complete, duel winner, loot response, loot-money share, attacker state update, spell go,
cast result, spell failure, chat message, XP gain, quest kill update and quest complete. The proc engine's packets have their own
decoders (`ScenarioProcDecoders`: SMSG_SPELLDAMAGESHIELD, SMSG_PROCRESIST), and so have the class scripts' (`ScenarioClassDecoders`:
SMSG_SPELLNONMELEEDAMAGELOG, SMSG_PERIODICAURALOG).
`ScenarioBot.WaitForPacketAsync(opcode, decoder, match, since)` waits for a decoded reply
received after a `Mark()`.

**Setup helpers** (`ScenarioContext`, world thread, harness only): `PlaceAsync` (an ordinary
teleport, waited until acknowledged and arrived), `PlaceFacingAsync` (two bots face to face,
two yards apart), `GiveItemAsync`, `GiveMoneyAsync`, `LearnSpellAsync`, `SetHealthAsync`. They
exist only on the harness object, which only tests and the Administrator-only, config-gated
scenario command construct; no opcode or player command reaches them.

**Steps, waits, assertions, report.** `StepAsync(name, body)` records each step's wall and
game time; the first failing step ends the run. `WaitUntilAsync(what, condition, timeout)`
evaluates the condition on the world thread and is always bounded. `Expect`, `ExpectEqual`,
`ExpectAsync(bot, fact)`, `ExpectMoneyAsync`, `ExpectItemCountAsync`, `ExpectGroupAsync` (server
roster) and `QuestStateAsync` assert server state. `ScenarioReport` lists the steps (ok/FAIL,
wall ms, game ms) and, on failure, the failing step, the message and each bot's last relevant
packets (movement and object-update noise filtered).

**Deterministic clock.** `WorldRuntime.UseManualClock(stepMs = 50)` is a code-only test seam
(chosen before `Start`, never configuration). Game time (`NowMs`, `Uptime`, tick diffs) then
advances only through `AdvanceClockAsync(ms)` or `AdvanceClockUntilAsync(max, condition)`, which
run ticks of at most the step back to back and check the condition after every tick. Queued work
(`Post`, `InvokeAsync`, `AdvanceClock*`, a session's packet queue) wakes the world thread at once;
otherwise it ticks every `TickIntervalMs` of wall time with a zero diff, so sessions and
`InvokeAsync` work while the simulation stands still. The real clock never wakes early. Because a
step now costs well under a millisecond of wall time, anything that completes in real time (an
async tile read, a database reply) gets proportionally fewer game ms: the tile wait
(`TileWait`) is spent only when 15 s have passed on the game clock and, for a read really in
flight, on the wall clock too. The death clock (`WorldDeathClock`) follows world time on the manual
clock, so corpse-reclaim delay and resurrection sickness run in virtual time. `ScenarioClock.Manual(world, time)` drives it; a
`ScenarioTimeProvider` registered as the host's `TimeProvider` follows game time tick by tick
(mail delay, trade anti-scam window, duel countdown) and can jump ahead (`Advance`). On the
manual clock a wait's timeout is game time, after which it keeps polling without advancing until
the same timeout has also passed in wall time (and at least `ManualWallGrace`, 3 s, after the
game budget ran out): the game budget burns in well under a second, while asynchronous I/O such
as a database commit runs on real time and can take seconds on a loaded machine. Code that reads
`DateTime` or `Stopwatch` directly, rather than the world clock or `TimeProvider`, does not
follow the manual clock. `ScenarioClock.Real` (live server) polls in wall time.
A production wall-clock budget shorter than that wait defeats it: the quest reward, economy and
dungeon chest loot settlements give up after 5 s and answer with their failure reply (a loaded
SQLite file made the AV scraps turn-in and an auction cancel miss it), so `ScenarioTestWorld` raises
all three settlement budgets to its 30 s step timeout.

**Built-in scenarios** (`PlayerbotScenarioCatalog`; bots `Scnalpha` and `Scnbeta`, created on
first use): `smoke` (login, hear own /say), `group-chat` (invite, accept, both group lists and
the server roster, party chat, leave), `trade` (Linen Cloth 2589 for 75 copper through the
trade window; both inventories and purses), `duel` (spell 7266, accept, countdown, melee to the
1-health finish; SMSG_DUEL_WINNER checked against server state). After them come the other public
scenarios this assembly ships (`PlayerbotScenarioCatalog.Shipped`, discovered: `dungeon`, `ship`, `wsg`), then
`IPlayerbotScenario` services registered in DI; a name belongs to its first entry. Shipped content
scenarios need content a live world may not have and then fail at a named step. `ScenarioSteps` holds reusable blocks (form a group, open and
accept a trade, leave earlier groups).

**Battlegrounds** (`ScenarioBattlegrounds`, kept out of the shared harness files): bot actions `BattlemasterHelloAsync`,
`JoinBattlegroundAsync` (CMSG_BATTLEMASTER_JOIN), `PortBattlegroundAsync`, `LeaveBattlefieldAsync`, `BattlefieldStatusAsync`, `PvpLogDataAsync`,
`PlayerPositionsAsync`, `CancelAuraAsync`; decoders for SMSG_BATTLEFIELD_STATUS, MSG_PVP_LOG_DATA, SMSG_UPDATE_WORLD_STATE and
MSG_BATTLEGROUND_PLAYER_POSITIONS; lookups of a battlemaster spawn of a type (`battlemaster_entry`), a game object spawn and an area trigger. The
scenario `wsg` (`WarsongGulchScenario`, listed by `.playerbot scenario list` through the shipped-scenario discovery) logs in a human and an
orc warrior, queues each at a battlemaster of its continent, ports both into one match, waits out the two-minute start, captures the Horde flag,
drops the Alliance flag by cancelling the flag aura, returns it, captures twice more (SMSG_BATTLEFIELD_WIN / _LOSE, the final scoreboard) and
waits until both bots are back at their entry points. `WarsongGulchScenario.EnterMatchAsync` is the reusable opening. On a live
world it needs the Warsong Gulch content (map 489, its triggers, safe locations, flag objects and battlemasters), a battleground
template of one player per team (the retail minimum is five, so two bots never start a match otherwise) and
`World:Playerbots:Scenarios:MaxDurationSeconds` of about 600 (two 2-minute waits plus the captures).

**Dungeons** (`ScenarioDungeons`): `RaiseLevelAsync` (ordinary level-up to an entrance's level) and `TakeAreaTriggerAsync` (stand
in a trigger, send CMSG_AREATRIGGER, wait for the far teleport). The scenario `dungeon` (`DungeonEntryScenario`) groups `Scnalpha`
and `Scnbeta`, raises them to level 10, sends the leader through The Deadmines entrance (trigger 78, map 36) into a new instance
that the group is bound to (non-permanent), sends the member through the same trigger into the same instance, checks party chat
inside, and, when `AllowedMaps` lists map 36, logs the member out inside and back in into the same instance (a managed bot may
only log in on an allowed map). Both leave through the exit trigger 119 and the group is disbanded. It needs map 36 and triggers
78 and 119 with their teleports (`map_template`, `areatrigger_template`, `areatrigger_teleport`) and fails at its first step
naming what is missing. On the live content it passes once the world database has been refreshed with Map.dbc and AreaTable.dbc
(`DungeonImportedMapsTests` runs it on the importer's own output; docs/integration/instance-maps-20261008.md). If a step fails after the entrance (the member refused, party chat, the exit), the scenario still
brings every bot that is inside back to where it stood before the entrance and disbands the group (`cleanup: ...` steps, run
under their own bound even after the run's deadline): otherwise the shared bots would stay saved on map 36, the default
`AllowedMaps` [0, 1] would refuse their next login (`login-refused`), and every pair scenario would stop working. A bot that is
offline at that point (a failed relog inside, only tried when `AllowedMaps` lists 36) cannot be moved, and its login there is
allowed. It leaves both scenario bots at level 10 or more. Tests: `DungeonScenarioTests` (including a dungeon that admits one
player, so the member is refused inside the run).

**Ships** (`ScenarioTransports`): `ShipAsync`, `BoardAsync` (a heartbeat carrying ONTRANSPORT, the ship and an offset),
`LeaveShipAsync`, `TimeSkippedAsync`, and the SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD decoders. The scenario `ship`
(`ShipCrossingScenario`) waits for the Ratchet - Booty Bay boat (`gameobject_template` 20808) at one of its ports, places
`Scnalpha` on the dock, boards it at a fixed deck offset, waits for the map change (SMSG_TRANSFER_PENDING naming the ship and the
old map, SMSG_NEW_WORLD carrying the offset, the bot aboard on the other continent), waits for the other port (told apart by its
TaxiPathNode row), checks the bot is at its deck offset, and steps it off within 30 yards of the port. A cleanup step takes the
bot off the ship and back to where it stood, also after a failure. It needs `World:Transports:Enabled` and the ship content
([transports](transports.md)); on the real clock it takes up to one round trip (about six minutes), so
`World:Playerbots:Scenarios:MaxDurationSeconds` must be 600 for `.playerbot scenario run ship`. Tests: `TransportScenarioTests`
(synthetic boat), `RealTransportContentTests.ABot_RidesTheRealBootyBayBoat_...` (real content and terrain, env-gated).

### Running scenarios on a live server

`.playerbot scenario list` and `.playerbot scenario run <name>` (Administrator) run a registered
scenario against the running world on the real clock and print the report lines to the GM.
Both are refused unless `World:Playerbots:Enabled` and `World:Playerbots:Scenarios:Enabled` are
true (default false). `World:Playerbots:Scenarios:MaxDurationSeconds` (120, 5..600) and
`StepTimeoutSeconds` (20, 1..300) bound a run; one run at a time. Scenario bots are real,
persistent characters (running, they count against `MaxBots`; registered, against `MaxRegisteredBots`); they are placed where `Scnalpha`
stands, and the setup helpers change their money, items and health. Enable it only on test
realms.

### Scenario tests

`tests/ArcaneCore.World.Tests/Playerbots/Scenarios/`. `ScenarioTestWorld` is a `WorldTestHost`
with the manual clock, a `ScenarioTimeProvider`, a SQLite character database (bots, items,
mail, quests and spells persist through the real EF stores) and small synthetic content
(`ScenarioTestContent`: a mailbox and a quest giver at the human start, a hostile wolf with
loot and a kobold quest target 60+ yards away, the Duel spell and flag, faction templates).
The tests run the built-ins plus `proc-damage-shield` and `proc-reflect-duel` (`ProcScenarioTests`, docs/areas/procs.md), `class-seal-judgement` and
`class-consecration` (`ClassScriptScenarioTests`, docs/areas/class-scripts.md), `control-possess`, `control-charm` and `spirit-of-redemption`
(`UnitControlScenarioTests`, docs/areas/unit-control.md; decoders and client packets in `ScenarioControlPackets`), `group-loot` (group, free-for-all loot, kill, money split,
item), `mail-item` (persisted letter with item, delivery delay, take), `melee-kill` (swing,
kill, XP credit) and `kill-quest` (accept, kill credit, turn in, settled reward row), and
check database rows after the run. Setting `ARCANE_SCENARIO_REPORT_DIR` collects every report.
`CombatStatScenarioTests` runs `combat-stat-auras` (duel, a damage taken curse and an attacker hit buff cast through CMSG_CAST_SPELL, white
swings that must all land for tenfold damage); its two spells are installed by swapping the spell store on the world thread.
`ScenarioTestWorld.StartAsync(configure)` registers extra services after the synthetic content (a later store registration
replaces it). `GameObjectScenarioTests` uses it for `meeting-stone` (a party queued at a meeting stone takes in a solo bot,
which later leaves) and `ritual-of-summoning` (a warlock's ritual, two helpers, a far bot that accepts the summon); the
meeting stone actions and decoders are in `ScenarioMeetingStones` (`JoinMeetingStoneAsync`, `LeaveMeetingStoneAsync`,
`MeetingStoneInfoAsync`, `SetQueue`, `MemberAdded`, `JoinFailed`). The scenario content only supports human warriors:
creating a human priest (5) or warlock (9) bot there fails with `create-failed`.
`ScenarioTestWorld.StartAsync(configure)` also lets a test register its own content and seams after
`ScenarioTestContent` (a later registration wins). `CreatureAiScenarioTests` uses it for creature AI across sessions: an
orc bot walks up to a CALLS_GUARDS townsman, a human bot hears the shout and the guard post's guard runs to the orc and
swings (`SMSG_ATTACKERSTATEUPDATE`); the human waves (`ScenarioCreatureActions.TextEmoteAsync`, CMSG_TEXT_EMOTE) at a herald
whose EventAI RECEIVE_EMOTE row greets it by name (`ScenarioCreatureDecoders.MonsterChat`).

Teleport and death lane (wave 2): `pet-teleport` (`PetTeleportScenarioTests`: a hunter bot's pet comes back at its side after a far
teleport to Kalimdor, the hunter gets its pet bar again and a watcher bot's client is sent the pet; the content creates warriors only,
so the bot's class byte is set to hunter on the world thread) and `raid-lock` (`RaidLockScenarioTests`: two bots form a raid group with
CMSG_GROUP_RAID_CONVERT, Molten Core is added to the map registry on the world thread, the leader is locked inside, the stored
`group_instance` row is read back from SQLite, and the member who was outside enters the same instance and is locked too).
`BattlegroundScenarioTests` and `BattlegroundWorldScenarioTests` start the scenario world with `WarsongGulchTestContent` (map 489, its safe
locations, flag stands with their event rows, flag room triggers, flag auras, a battlemaster per side) through the `StartAsync(configure)` hook.
`TransportScenarioTests` use the same hook for synthetic ship routes (`TransportWorldContent`): `ship-duel` (two
bots board a ferry, duel aboard while it sails away from the flag, and the duel ends fled when one steps off) and
`ship-crossing` (a bot rides a ship through its map change and arrives aboard on map 1).
`PartyScenarioTests` runs `party-master` (`Scenarios/ScenarioParty.cs`, `PartyScenario`): the master is a real socket client
(`IPartyScenarioMaster`, a `WorldTestClient` whose reader records every packet and acknowledges teleports like a game client) and
the bot `Scnfollower` runs autonomously. The master, on the test's `World:Playerbots:Party:Allowlist`, invites the bot and it
accepts; the bot follows a 40-yard walk;
the master targets the wolf and whispers `attack`, and the bot kills it; the group roll on the wolf's uncommon item gets the
bot's vote within 2 s of game time and resolves on the master's vote; after `stay` the bot holds while the master walks off;
`status` is answered; the master takes the Deadmines entrance (trigger 78) and the bot lands in the same instance, and comes
out with it through the exit (119); the master leaves the group and the brain drives the bot again. The test adds the
Deadmines content, an uncommon item to the wolf's loot, AllowedMaps [0, 1, 36] and flat ground at the start's height (the bot
plans its walks there). Without a master (the catalog's instance, `.playerbot scenario run party-master`) it fails at its first
step: a live run has no socket master to give it.

### Chaos bots

`World:Playerbots:Chaos` (every key live through `.reload config` and `.playerbot chaos`) designates a bounded slice of running
autonomous playerbots (`ActiveChaosBots`, default 4, `0..5000`, plus any bot explicitly assigned with `.playerbot chaos assign`) as
adversarial server-breakers (`PlayerbotChaosEngine`, `PlayerbotChaosOptions`, `PlayerbotChaosCommands`).

* **Execution.** Each active chaos bot continues its ordinary autonomous gameplay so it visits real world states (combat, quests,
  looting, death and resurrection), and at the end of its think (`ManagedPlayerbotFeature.ThinkCore`, phase `chaos`) dispatches
  `ProbesPerThink` (default 2, `1..16`) adversarial probes through `WorldSession.TryManagedAction` without consuming the shared
  `MaxActionsPerTick` budget.
* **Probe families (`ChaosProbeCategory`).** Controlled by `MalformedPayloads` (default true), `StateRaces` (default true) and
  `BoundaryMovement` (default true):
  - `TruncatedWire` and `MutatedWire`: 0-byte, 1-byte, partial-GUID, all-`0xFF`, all-`0x00`, boundary-integer and random-byte frames
    across every registered `OpcodeHandler.World` handler.
  - `DeathAndResurrection`: out-of-order `CMSG_REPOP_REQUEST`, `CMSG_RECLAIM_CORPSE`, `CMSG_RESURRECT_RESPONSE`,
    `CMSG_SPIRIT_HEALER_ACTIVATE` and `CMSG_SELF_RES` while alive, while already a ghost, or with bogus/self GUIDs.
  - `CombatAndSpell`: self-attack, zero/max-GUID attack, rapid swing/stop, `CMSG_CAST_SPELL` with spell `0` / `uint.MaxValue` /
    self-target / unknown target masks, and bogus `CMSG_CANCEL_CAST` / `CMSG_CANCEL_AURA` / `CMSG_CANCEL_CHANNELLING`.
  - `InventoryAndBag`: self-slot swaps, out-of-range bag/slot indices (`0, 19, 23, 38, 118, 254, 255`), boundary split/destroy counts
    and `CMSG_USE_ITEM` with mismatched item GUIDs.
  - `QuestAndNpc`, `LootVendorEconomy` and `SocialTradeMailBg`: gossip/questgiver/trainer/binder, loot/release/vendor/repair/mail,
    trade/duel/group/battleground packets with self, nearby or bogus GUIDs and boundary counts.
  - `BoundaryMovement`: conflicting `MovementFlags`, extreme orientations, `NaN`/`Infinity` coordinate guard checks, and
    `CMSG_MOVE_TIME_SKIPPED` / `CMSG_MOVE_FALL_RESET` boundary values.
* **Containment and QA reporting.** Expected `MalformedPacket` exceptions (`ArgumentOutOfRangeException` with
  `ParamName == MalformedPacket.ParamName`) increment `MalformedRejected` and do not fault the bot. Any other unhandled exception
  is caught per probe, recorded in `FaultLedger.Shared` (`playerbot-chaos`), stored in `RecentFaults` (up to 32 with opcode,
  category, probe name and hex payload), and logged as `Playerbot {BotId} chaos failed ({Type}: {Message})` so Bot QA
  (`PlayerbotQaSignalParser`) aggregates and ranks it under `what="chaos"` without kicking or quarantining the chaos bot.
* **Post-probe invariant checks.** After every probe, `PlayerbotChaosEngine` verifies finite player coordinates (rolling back and
  recording a fault if any handler ever left `NaN`/`Infinity`), `MapCombat.VerifyPlayerLifeInvariants`,
  `MapCombat.VerifyUnitCombatInvariants`, `Map.VerifyVisibleSetInvariants` and `SpellSystem.VerifySpellStateInvariants`.
* **GM commands.** `.playerbot chaos [status|on [all|#activeBots] [#probesPerThink]|off|burst [#probes] [$bot]|assign $bot|unassign $bot|faults|clear [all]]`.

## MockClient playbot (external protocol client)

`arcane-mock playbot` runs one external build-5875 client against an owned numeric
loopback realm. It uses the real SRP, world authentication, character creation,
login, gameplay and logout handlers. Use a disposable Player account and an
explicit character name; the first repertoire creates/selects a Human Warrior.

```powershell
# Set ARCANE_BOT_PASSWORD privately before running; no password argument is accepted.
dotnet <verified-artifact-path>/arcane-mock.dll playbot --account BOTONE --character Botone --password-env ARCANE_BOT_PASSWORD --duration-s 120 --steps 100 --attack-entry 6 --report <new-report-path>.json
```

The deterministic selector queries nearby observed creatures, explores using
three-yard movement steps and approaches an explicitly allowed creature entry.
Combat is disabled when `--attack-entry` is omitted or zero. It waits when player
health/combat facts are unknown, stops its attack
when injured or its target disappears/dies, and releases spirit once after an
observed player death. A nearby configured NPC that is observed targeting this
character with its full GUID and combat flags can be answered defensively.
Below 60 percent health outside combat, an observed owned starter-food item117
with positive stack can be used through the normal sit/item-use flow. The client
waits for own spell433, food aura, one consumed stack and healing, then stands.
That action shares the same reader and the overall session traffic budget.
Heroic Strike requires spell 78 in the observed initial
spellbook and at least 150 raw rage. Loot comes from an observed nearby lootable
corpse and a decoded server loot window. Reports distinguish actions sent from
packet families received; movement sends alone do not establish terrain acceptance.

The action loop adapts to received state and chooses again after each observation
window. It does not run the fixed `starting-zone` quest script. Current coverage
does not include quest chains, terrain navigation, general food selection, corpse recovery,
general spell planning or arbitrary classes. Local exploration stays within 30
yards of the login origin and 20 exploration/approach movement sends. Approach
targets must be within 25 yards and two yards vertically. The runtime bounds
duration to 1..600 seconds, actions to 1..500, observed objects to 4096, and
post-authentication traffic to 10000 frames/eight MiB. Logout has a separate
30-second cleanup bound. Each process owns one connection and one frame reader.
Run a second client with a separate disposable account/character rather than
sharing a character across processes.

## Optional installed local model

Add `--llm true --model qwen3.5:4b`. The model provider defaults to
`http://127.0.0.1:11435/`; `--model-endpoint` accepts another literal loopback HTTP
origin. It uses the installed Ollama model and never calls a model download API.
Warm and decision requests use a 30-second idle lease. This reduces idle
retention; it does not lower peak loading memory. The observed qwen3.5:4b runner
committed about11.8GiB on this Windows host despite a2048-token context, so model
loading and builds need separate resource admission. The default selector works
without loading it.
Explicit loading has a 30-second bound; ordinary decisions have five seconds.
Generation disables thinking/streaming, uses a 2048-token context, limits output
to 96 tokens, and requests a JSON schema containing only supplied action IDs.
Responses are bounded to 16 KiB and validated locally. Credentials, character
names, chat, coordinates, proprietary DBC data and raw logs are excluded from
the model request. Proxying and redirects are disabled.

The model selects from deterministic candidates; it cannot introduce commands,
packets, targets or coordinates. The client drains incoming traffic and validates
the selected candidate again before sending. Provider failures, malformed or
stale selections use the deterministic fallback. Model loading failure also
uses the baseline. LLM operation is disabled by default. The existing eight-case
local selector benchmark establishes a useful candidate, not general gameplay
accuracy; actual client qualification and live reports are separate evidence.

Reports contain action kinds/providers/fallback codes, numeric target/health
facts, traffic counts, received packet families and logout status. They exclude
account/password values and server chat. `--report` creates a new file and refuses
an existing path, preserving earlier evidence. Standard output also receives the
JSON report; a run/traffic/protocol or logout failure returns a nonzero exit code.

Protocol layouts reuse `ScenarioConnection`, `ScenarioWire`, `StartingZoneProbe`,
`StartingZoneLoot` and the normal client protocol classes. Their pinned
vmangos/wow_messages citations remain authoritative for build 5875. WoWWiki
1.12.1 and Wowhead Classic are supplemental version-checked information sources;
later Classic mechanics are not automatically treated as 1.12.1 behavior.
