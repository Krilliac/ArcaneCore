# Playerbot population: keep a number of bots online, as long as the world's tick time allows

The population manager keeps `TargetOnline` managed playerbots running, spread over level bands and over the zones that fit
their levels, and backs off when the world's own tick time says it cannot afford them. The bot itself (goals, movement, the
brain) is in `docs/areas/playbots.md`; the level and kit a created bot is given is `.character boost`
(`docs/areas/character-boost.md`). It is **off by default** (`World:Playerbots:Population:Enabled`), and an operator who never
turns it on sees no difference.

This is an ArcaneCore operator feature, not a game rule. The idea of a bounded population that adds and removes bots at an
interval is vmangos `RandomBot.MinBots/MaxBots/Refresh` (`PlayerBotMgr.cpp:54-56`, `368-391`); the tick-time budget, the bands and
the zone placement are ArcaneCore's own. Nothing here changes what a bot does once it runs.

## Pieces

| File (`src/ArcaneCore.Playerbots/Population/`) | What it does |
|---|---|
| `PlayerbotPopulationOptions.cs` | `World:Playerbots:Population` (every key live through `.reload config`), the validation and the list bindings. |
| `PopulationBudgetController.cs` | The control loop as a pure function: samples, p95, counts, confirmed windows -> hold, add or park, with a stable state code; `PopulationBudgetWindows` counts the windows. |
| `PopulationTickWindow.cs` | `IPopulationTickSource` and `WorldTickWindow`, the ring of the last 4096 tick body durations the p95 is read from. |
| `PopulationBandPlanner.cs` | Pure planning: bots per band (largest remainder), the next band, faction, race and class, level; the (race, class) cache. |
| `PopulationZoneAtlas.cs` | The zones a bot may live in, built from the area table and the graveyard catalog; `Fits`, `Pick`. |
| `PopulationProvisioner.cs` | Gives a running bot its level and kit (`CharacterBoost.Run`) and its place (the teleport service). |
| `PopulationNames.cs` | A letters-only syllable generator for the names of new characters. |
| `IPopulationPlacementVeto.cs` | The seam the home-area lane implements (below). |
| `PopulationAccounting.cs` | `PopulationCounts` (the population's own share of the target and its bots per band), the per-pass actions and the rate-limited action log. |
| `PopulationParking.cs` | `PopulationBotFacts`; `PopulationParking.Order` (which running bot to park first and why) and `RestartOrder` (the rotation). |
| `PlayerbotPopulationFeature.cs` | The world feature: capture, one pass at a time, add, park, relocate, the published status. |
| `PlayerbotPopulationCommands.cs` | `.playerbot population [status\|enable\|disable\|target N]`. |

Outside the folder, all additive: `PlayerbotOptions.Population` (and its binding and validation), the live keys in
`WorldConfigKeys`, `IPlayerbotPopulationStore` and `PlayerbotPopulationMember` in the kernel, and the characters table below
(`src/ArcaneCore.Data/Characters/Playerbots/PlayerbotPopulationDataModule.cs`).

## The control loop

Every `IntervalSeconds` of **world time** (the `Updated` event's elapsed time, so a manual test clock drives it too) the world
thread starts a pass, if the previous one has finished:

1. **Capture, on the world thread.** The facts of every running managed bot (level, team, zone, whether it is autonomous, whether
   it is busy, whether a real player stands in its zone, whether a placement veto owns it, and whether it is dead or a ghost,
   resurrection-sick (spell 15007) or stalled (a stall or death loop is reported)) and the tick p95 and sample count. Nothing is
   awaited here.
2. **Decide and act, on one pool task.** The task reads the membership table and the bot registry, calls
   `PopulationBudgetController.Decide`, then adds, parks and relocates through `IPlayerbotService` (start, create, stop) and
   `world.InvokeAsync` (boost and teleport). It never blocks the world thread, and the world thread never waits on it. One pass
   runs at a time; a pass that is still running when the next interval comes is not doubled.
3. **Reset.** After any start, creation or park the task asks the world thread to `Reset()` the tick source, so the
   next decision sees only ticks measured after the change, and forgets the confirmed windows. A relocation alone does neither: it
   changes no count of running bots, and a levelling population that moves a misfit every pass would otherwise never see
   `ConfirmWindows` quiet windows in a row and never add a bot. A confirmation window needs `WindowTicks` ticks that no counted window
   used (the tick source's running total), so passes that come faster than a window fills (slow ticks, short interval) do not count
   one burst twice.
4. **Publish.** `PlayerbotPopulationFeature.Status` is replaced by an immutable `PopulationStatus` (readable from any thread; the
   command prints it).
5. **Log.** A pass that parked, started, created or moved bots adds that to the action log, which writes one information line at
   most every `ActionLogSeconds` of world time (default 60; 0 = every such pass), adding up the passes in between:

   ```
   Playerbot population: parked 3 (dead 1, sick 2), started 0, created 1, moved 0 in 2 passes; state over-budget: tick p95 101.2 ms
   is over the 95 ms budget; operator bots running 280, population share 720; bands 1-10 want 120 have 49, 11-20 want 120 have 0, ...
   ```

   The park reasons are those of `PopulationParking.Order` (below). A pass that changes nothing writes nothing, but hands out what
   earlier passes added once the interval is up.

### The decision, in order

`PopulationBudgetController.Decide(samples, p95Ms, runningAll, ownedRunning, effectiveTarget, options, windows)`:

| State | When | Action |
|---|---|---|
| `settling` | fewer than `WindowTicks` ticks measured since the last change | hold |
| `over-target` | more managed bots run than the effective target and the population owns a running one | park `min(surplus, MaxParksPerInterval, ownedRunning)`; this is checked before the budget, so a lowered target is honoured even on a quiet world |
| `over-budget-confirming` | over budget, `ParkWhenOverBudget`, an owned bot runs, but fewer than `ConfirmWindows` consecutive windows over it | hold |
| `over-budget` | tick p95 above `TickP95BudgetMs` in `ConfirmWindows` consecutive windows and `ParkWhenOverBudget` | park `min(MaxParksPerInterval, ownedRunning)` |
| `over-budget-hold` | over budget and `ParkWhenOverBudget` is off | hold (stop adding) |
| `over-budget-no-owned` | over budget and the population owns no running bot | hold; it never stops an operator's bot |
| `hysteresis` | p95 between `ResumeBelowPct` of the budget and the budget | hold |
| `under-budget-confirming` | p95 below the resume line and running < effective target, but in fewer than `ConfirmWindows` consecutive windows | hold |
| `under-budget` | p95 below `ResumeBelowPct` of the budget in `ConfirmWindows` consecutive windows and running < effective target | add `min(target - running, MaxStartsPerInterval)` |
| `at-target` | p95 below the resume line and running >= effective target | hold |

The effective target is `min(TargetOnline, MaxBots)` (`World:Playerbots:MaxBots` is the running cap of the whole feature). The
totals count every managed bot running, the operator's included; the bands count only the population's own (see "Bands" below).

### Hysteresis

Four things stop it adding and parking in turns. **The band:** adding resumes only below `ResumeBelowPct` (default 80) of the
budget and parking starts only above the budget, so with the default 60 ms budget a p95 between 48 and 60 ms changes nothing.
**The reset:** the tick source is emptied after every change and the next decision needs `WindowTicks` (default 200, about ten
seconds at 50 ms) fresh ticks, so a bot's own login cost cannot be read as the world's load, and the p95 never reflects ticks
from before the last start, creation or park (a relocation resets nothing: it changes no bot count, so its teleport cost stays in
the window). **The confirmation:** a park for the budget needs the p95 over it in `ConfirmWindows`
(default 2) consecutive fresh windows, and an add needs as many under the resume line (`PopulationBudgetWindows`: a settling pass
is no evidence and changes nothing; a window between the two lines, one on the other side, or a change starts the count again).
With the defaults (`IntervalSeconds` 10, `WindowTicks` 200) a pass is often not fresh, since 200 ticks of 50 ms take as long as the
interval or longer, so confirming takes about two intervals per window.
A lowered target is honoured at once. **The minimum run time:** a bot the population started is not parked for the budget for
`MinRunSeconds` (default 300) of world time.

Why the last two (2026-10-11, live w25-r2, budget 95 ms, 10 starts and 2 parks a pass): the log showed a batch of ten starts every
one to four minutes while the running count stayed near 84, i.e. ten bots added on one quiet window and taken back two at a time on
the slow windows after it; 1,687 starts for 269 bots in six hours (`_wf/analysis/bot-xp-analysis.md`). Each start is a fresh brain
that has lost its set-asides and danger memory. The p95 of a single 200-tick window is the tenth-slowest tick, so one burst of
slow ticks (a save, a GC pause) was enough to park.

All of it is pure and unit tested (`PopulationBudgetControllerTests`); the end-to-end sequence with a fake tick source is
`PlayerbotPopulationFeatureTests`.

The p95 is the nearest rank of the last `WindowTicks` tick body durations from `WorldRuntime.TickCompleted`. `WorldRuntime.Stats`
keeps a 4096-tick ring too, but its snapshot copies and sorts all of it (about 3.4 minutes at 50 ms, too slow and too
allocating to steer a ten second loop, and it cannot be reset), so the population keeps its own ring: `Add` is one array write
on the world thread and allocates nothing.

## Adding a bot

Per pass, up to the decision's count:

1. **Restart the population's own stopped bots in rotation**: the one parked longest ago first, whatever its band
   (`PopulationParking.RestartOrder`; a bot not parked by this process, stopped before a restart or by an operator, counts as
   parked longest; then the older membership row, then the lower `BotId`). Before 2026-10-11 the band with the largest deficit
   came first, which, with operator bots counted in the bands, restarted the band 1-10 bots last: they did not run for hours. A
   faulted bot (quarantined or disabled) is not started here: an operator start would clear its fault history, and the managed
   feature retries a quarantined bot itself. Three refused starts end the pass's starting.
2. **Create new bots** for a band that its own bots, running **or parked**, cannot fill: creation is planned against the
   population's own bots running plus those waiting to be restarted, so a parked bot is never replaced by a new one. While parked
   bots wait as well, such a band gets at most half the pass's slots first and the rotation the rest; creation takes any slots
   the rotation leaves. Each new bot is planned (`PopulationBandPlanner.Plan`), created through
   `IPlayerbotService.CreateAsync` (the ordinary `CMSG_CHAR_CREATE` rules; a refused name is retried with the next candidate, at
   most five times), recorded in `playerbot_population_member`, started, boosted to its level and teleported to its place. The
   zone, level, graveyard and race faction are chosen together before anything is created (`PopulationZoneAtlas.PickSafe`), so
   an unsafe zone is skipped and a refused plan creates nothing. Creation does not run at all when the teleport service is
   missing (`TeleportAvailable`, "the teleport service is not available"). If the placement is still refused after the bot was
   created (for example a content reload between plan and teleport), the unplaced level-1 bot is **deleted**, not parked: see
   "Failure behaviour".

**Caps, enforced only while adding** (a pass that hits one says so in its status line, `held back: ...`):

| Cap | Refuses creation when |
|---|---|
| `MaxCreatePerMinute` | that many characters were created in the last minute of world time (a queue of creation times; 0 creates none and only restarts parked bots) |
| `World:Playerbots:MaxRegisteredBots` | the registry (running or not) is full |
| `World:Playerbots:MaxBots` | the running bots plus this pass's starts and creations reach it; a restart refused with `playerbot-capacity` ends the pass the same way |
| `MaxStartsPerInterval` | the pass's count (starts plus creations) is used up |

## Bands, levels and the boost

The target is split over `Bands` (default six bands of ten levels, equal weights; at most 20, no overlap, at least one weight
above 0) by weight with the **largest remainder** method, so the counts always add up to the target. The next bot goes to the
band with the largest deficit (desired minus the population's own bots there; the lower band on a tie) that a zone can take.

**What is split, and what a band counts (the design choice, 2026-10-11).** Operator bots reduce the total target, never a band's
share. `PopulationCounts`: the operator bots running (every managed bot running without a `playerbot_population_member` row) are
taken off the top, `population share = max(0, effective target - operator bots running)`, and only that share is split over the
bands; a band's `have` counts only the population's own running bots. The control loop's totals are unchanged (`TargetOnline` and
`MaxBots` still bound the whole server: running against the effective target), so the operator fleet still costs the population
its places, but evenly over the bands by weight. The alternative, `TargetOnline` counting owned bots only, would have let the
population add a full `TargetOnline` on top of any operator fleet, and `MaxBots` would then be the only bound.

Before, every running managed bot counted in its band. Live 2026-10-11 (`_wf/analysis/bot-xp-analysis.md`, section 3): 201
operator bots (`Wfbot*`, `Stressbot*`) at levels 11-20 held that band above its share of 167, so no 11-20 bot was ever created;
78 more at levels 1-10 made that band look closest to full, so its 49 population bots were parked first and restarted last.

A created bot starts at level 1; a higher level is `CharacterBoost.Run`, the engine of `.character boost`, run as the bot itself
(so no GM message is sent). **Without the boost content** (`BoostEnvironment.Resolve` fails; the status line says `Boost missing:
...`) only a band that contains level 1 is eligible and every new bot is level 1.

## Zones: the data source and the slack design choice

The atlas is built from the area table (`WorldMaps.Areas`, the 1.12.1 `AreaTable.dbc` as `AreaTemplate`) and the graveyard
catalog (`WorldGraveyards.Catalog`), and rebuilt when either is replaced (a content reload) or `AllowedMaps` / `ExcludedZones`
change. A zone qualifies when it is a zone (no parent: vmangos `AreaEntry::IsZone`), lies on a map in `AllowedMaps`, is not in
`ExcludedZones`, is not flagged capital or city (a bot is never placed in a city), and has at least one sub-area with a level.

**Where the level comes from.** The 5875 `AreaTable.dbc` zone row carries `AreaLevel` 0 except for capitals; the level sits on
the **sub-areas** (Elwynn Forest 5-10, Westfall 9-18, Durotar 5-10, The Barrens 10-33, Duskwood 10-30, Stranglethorn Vale
30-50; layout `DBCfmt.h:25`, `Map.h:107-118`). A zone's range is therefore the lowest and highest sub-area level.

**Where a bot is placed.** The zone's safe locations are the graveyards linked to the zone or any of its sub-areas on the zone's
map (`game_graveyard_zone`), split by team: link team 0 serves both factions, 469 the Alliance, 67 the Horde (vmangos
`ObjectMgr::LoadGraveyardZones`). A zone with no graveyard for a team is not offered to it. The zone with the **fewest bots
assigned** wins, ties broken at random, then one of its graveyards for the bot's team that takes the bot's level.

**Where the graveyard really stands.** `game_graveyard_zone` links a graveyard to the zones whose ghosts it serves, not to the zone
it stands in, so the atlas asks the terrain (`Map.GetZoneAndAreaId` of the graveyard's position, through `WorldMaps.Terrain`) and:

* **leaves out** a linked graveyard whose terrain zone is known and is not the zone (`PopulationZoneAtlas.Dropped`). Examples
  from the r5 world DB: 829 "Alterac Valley, Alliance Exit" is linked to Western Plaguelands but stands in Alterac Mountains
  beside level 61 Stormpike Battleguards (issue #178); Duskwood's Horde graveyard is Lakeshire in Redridge; Alterac Mountains'
  two graveyards are Tarren Mill and Southshore in Hillsbrad. On that DB 15 of the links (90 to 75 zone locations) are left out,
  and Alterac Mountains is offered to no one;
* gives each kept graveyard the **level of the sub-area it stands in** (`LocationLevels`). A placement's floor is that level, never
  below the zone's lowest (`FloorOf`). Duskwood's lowest sub-area is The Darkened Bank (10), but its graveyards stand in Raven
  Hill (24) and Darkshire (25): band 1-10 used to fit Duskwood with levels 6-10, and those bots died in a loop at Raven Hill
  (issue #177).

* surveys the **creatures near each kept graveyard** (`PopulationCreatureSurvey`, the same background job): for each team, the
  lower median template `MinLevel` of the database creature spawns within `CreatureSurveyRadius` yards (default 100, measured
  flat) and within 15 yards above or below the graveyard that are hostile to the team (the FactionTemplate.dbc relation of the
  creature's template to template 1 Human or 2 Orc). Left out: civilians, creatures that never aggro on sight, invisible (trigger)
  and not-selectable creatures, and spawns of positive game events (absent while no event runs; the AQ war effort silithids at
  Camp Mojache and Gadgetzan). The height bound matters at Stonard: the graveyard stands at z 22, and the flat circle takes in 23
  level 46-47 Nethergarde Miners and 3 others in the valley at z -13 to +7, which would have raised the Horde's lowest level there
  from 37 to 44. Fewer than 3 hostile creatures give no level.
  **Caveat: no reputation.** Hostility is the template relation only, not the reputation a real character has (a faction at war,
  forced reactions, contested-guard flags): a creature whose reaction to a player depends on reputation counts as the template says.
  It is a placement heuristic, not the combat rule. The level is kept per (graveyard, team) as
  `CreatureLevels`; the Dire Maul graveyard in Feralas gets 43 (level 43-46 howlers, bears, screechers), Morgan's Plot in Deadwind
  Pass 59 (level 58-60 undead). The height bound also leaves out the Wildhammer sentries of Aerie Peak (hostile to the Horde,
  17-19 yards below the graveyard), so Aerie Peak has no survey level and a Horde bot of 41 or more may still land there; no bounded
  height difference separates those sentries from the Stonard miners (the nearest at 15.4-20 yards). Without creature
  content or faction templates, or with `CreatureSurveyRadius` 0, nothing is surveyed.

The lookup never runs on the world thread: a terrain tile not in memory is read from disk synchronously and the terrain clean-up
frees unreferenced tiles after a minute, so looking up some 90 graveyards inside a tick would be some 90 file reads on every
build. The feature starts a background task (`TerrainManager.ReadZoneAndAreaIdDetached`, which reads the tile files itself and
never touches the world thread's tile cache) when the atlas's sources change: the first pass, a content reload, an `AllowedMaps` or
`ExcludedZones` change, new creature content, new creature definitions (`.reload creature_template`, through
`CreatureContent.DefinitionsVersion`) or a `CreatureSurveyRadius` change. The result is posted back with `WorldRuntime.Post`, and the next pass rebuilds the atlas with it. Until it
is in, and always without terrain data (`World:Maps:DataDirectory` unset), nothing is known: every link is kept and the floor is the
zone's lowest sub-area level. A lookup for sources that change in the meantime is cancelled (each lookup has its own token source,
linked to the feature's stop), and a result that still arrives is dropped, so repeated reloads never stack lookups. A lookup that
fails is logged and keeps the fallback until the sources change again; it is not retried. The atlas logs its zone and location
counts, the number of surveyed (graveyard, team) pairs and the links it left out each time it is built.

**Band coverage.** Once the located atlas is in, and again whenever the atlas, the bands, the level rule or `Factions` change, the
feature checks that every band with a weight has a zone for every faction it creates bots for
(`PopulationZoneAtlas.UncoveredBands`) and logs a warning naming each band and faction that has none (`no zone takes a bot of
levels 41-50 for the Horde under the level rule ...`): such a band's deficit can never be filled by a new bot. On the w24 live data
every default band has zones for both factions under the default rule (see the table below).

**Creation and relocation wait for the lookup.** While a lookup for the current sources is running (started, its result not yet
delivered to the world thread), a pass creates no bot and relocates none, and the status says `held back: waiting for the
graveyard terrain lookup`; restarting parked bots and parking go on. Otherwise the first pass after every start or reload would
place bots from the fallback, which is the Duskwood bug again. After 6 such passes (`MaxTerrainHoldPasses`) creation and
relocation go ahead on the fallback and a warning is logged, so a hung read cannot stall the population. A failed lookup ends the
wait at once. The wait also applies **without terrain data** when the creature survey runs (creature content and faction templates
loaded, `CreatureSurveyRadius` above 0): the background job then only surveys, and the first pass after a start or a reload holds
creation and relocation until it is in (usually the next pass).

**Creation and relocation wait for the placement safety data.** The spawn index (creatures near a site) and the terrain facts
(liquid status and area level of every safe location) are built on a background task, never on the world thread, and published as
one immutable snapshot (`PopulationPlacementFacts`). Until the snapshot for the creature content, area table and atlas in force is
in, a pass creates and relocates nobody and says `held back: waiting for the placement safety data`. It is rebuilt after a content,
area or atlas reload. Unlike the terrain lookup hold this one has **no pass limit** (placing without the data would put the index
build and the tile reads back inside a tick). A build that throws is logged, recorded in the fault ledger, and retried every 10
passes; meanwhile the status says `waiting for the placement safety data (the last build failed; retrying)`. A population rush holds
on the plain wait but **halts** on the failed-build wait, so a build that keeps failing cannot leave a rush running with nothing
created. A site checked against a snapshot built from a replaced content or area table is refused with the code
`placement-data-stale`; that refusal does not count toward the three-refusals pause below, and a relocation that meets it skips the
bot silently (the next pass checks again against the fresh snapshot).

Known difference from the old on-thread lookup: the area level of a site comes from the terrain tile's area cell only. The live
path (`TerrainInfo.GetAreaFlag`) prefers the WMO group area when model data is loaded, so a site inside a building can get a
different sub-area level than before. Liquid status and everything else agree with the live lookups.

**The detached reader and a missing tile.** `ReadZoneAndAreaIdDetached` returns (0, 0) for a position whose tile file is missing,
unreadable or outside the map, so that graveyard's link is kept with no sub-area level. The cached path
(`TerrainInfo.GetZoneAndAreaId`) instead reads a missing tile as area flag 0, which `AreaTable` resolves to the map's
`linked_zone` area when the map has one. The two agree wherever a tile file exists.

**The level rule (an ArcaneCore choice, not a game rule; `PopulationLevelRule`).** A placement at a graveyard takes a bot of
level L when `lowest <= L <= MaxLevel`, where

```
lowest = max(1, floor - slack(floor), creatureLevel(team) - CreatureLevelMargin)
slack(floor) = ZoneLevelSlack                                                      when floor <= ZoneLevelSlackMaxFloor
             = max(0, ZoneLevelSlack - (floor - ZoneLevelSlackMaxFloor + 1) / 2)   above it
```

With the defaults (4, 12, 3) the slack is 4 up to floor 12, 3 at 13-14, 2 at 15-16, 1 at 17-18 and 0 from 19. The lowest sub-area
of a starter zone is level 4 or 5 and its graveyards stand in level 4-7 areas, so without slack a level 1 bot would fit nowhere and
a server without the boost content could never populate; the whole slack stays there. Above the starter zones it is wrong: under
the flat slack of 4 (PR #188, live on w24) a level 38 bot fitted Aerie Peak (41) in the Hinterlands and Feathermoon (41) or Camp
Mojache (42) in Feralas, and a level 40 bot the Dire Maul graveyard (44), and those bots died in loops to level 43-48 creatures
(issues #183, #193, #195); a level 49 bot (Grosan) fitted Deadwind Pass (50) beside level 58-60 undead. The creature term keeps
a bot away from a graveyard where most hostile creatures are more than `CreatureLevelMargin` levels above it. Above a zone's
highest level a bot is out-levelled; below every placement of its zone for its team (`LowestLevelFor`, the same rule) it is
under-levelled (see relocation). `ZoneLevelSlackMaxFloor = 60` gives back the flat slack; `ZoneLevelSlack = 0` is the strict reading
of the table.

The w24 live data (r5 world DB, 5875 terrain; 40 zones, 75 locations), placement floor and survey per graveyard of the zones the
issues name:

| Zone (levels) | Graveyard (teams) | Stands in (level) | Floor | Survey A / H | Lowest, flat 4 | Lowest, default A / H |
|---|---|---|---|---|---|---|
| The Hinterlands 47 (41-49) | 349 Aerie Peak (both) | Aerie Peak 348 (41) | 41 | - / - | 37 | 41 / 41 |
| | 789 The Overlook Cliffs (both) | The Overlook Cliffs 307 (48) | 48 | - / - | 44 | 48 / 48 |
| Feralas 357 (41-60) | 309 Feathermoon Stronghold (A) | Feathermoon Stronghold 1116 (0) | 41 | - / - | 37 | 41 / - |
| | 310 Camp Mojache (H) | Camp Mojache 1099 (42) | 42 | - / 40 | 38 | - / 42 |
| | 849 Dire Maul (both) | Dire Maul 2577 (44) | 44 | 43 / 43 | 40 | 44 / 44 |
| Deadwind Pass 41 (50-60) | 36 Morgan's Plot (both) | Morgan's Plot 2563 (0) | 50 | 59 / 59 | 46 | 56 / 56 |
| Swamp of Sorrows 8 (36-43) | 108 Stonard (H) | Stonard 75 (37) | 37 | - / - (47 without the height bound) | 33 | - / 37 (44 without it) |

Zones per default band on the same data (`FittingZones`), flat 4 before and the default rule after; no band is left without a
zone for either faction:

| Band | Alliance before / after | Horde before / after |
|---|---|---|
| 1-10 | 5 / 5 | 5 / 5 |
| 11-20 | 13 / 9 | 11 / 8 |
| 21-30 | 9 / 9 | 10 / 10 |
| 31-40 | 12 / 8 | 14 / 10 |
| 41-50 | 15 / 13 | 15 / 13 |
| 51-60 | 13 / 13 | 12 / 12 |

Before teleporting the actual boosted bot, population checks the chosen graveyard and the other linked graveyards of that
zone. A known local sub-area level more than three above the bot, or a hostile creature spawn more than three levels above it
within 90 yards plus its wander radius, disqualifies that location. It tries another linked location for the bot's team; with
none, the placement is refused as `placement-unsafe`. This uses the loaded terrain area levels, creature spawns and
FactionTemplate.dbc hostility, rather than treating the broad zone level range or a neutral graveyard link as proof that a
particular graveyard is safe. The 90-yard placement buffer is an ArcaneCore choice; live roaming and player reputation can
still change the fight after placement, so the bot's route hazards remain necessary.

**The slack (an ArcaneCore choice, not a game rule).** A zone accepts a bot of level L when `MinLevel - ZoneLevelSlack <= L <=
MaxLevel`. The lowest sub-area of a starter zone is level 4 or 5, so with no slack a level 1 bot would fit nowhere and a server
without the boost content could never populate. The default of 4 lets a level 1 bot start in any starter zone while a level 1
bot still never lands in a level 20 zone. `0` is the strict reading of the table. The slack applies only to the low end: a bot
above a zone's highest level is out-levelled (see relocation).

## Factions

`Factions` is `Both`, `Alliance` or `Horde`. With `Both`, `AllianceSharePct` (default 50) is the target share of Alliance bots:
the next bot is of the faction furthest below its share of the bots that would run after it (Alliance on a tie), counting every
running bot, so the split follows the setting from the first bot on and recovers after parking. A faction is the race's: Alliance
is human, dwarf, night elf and gnome, Horde is orc, undead, tauren and troll (`Player.Team`). The race and class pairs come from
`IWorldDataStore.IsValidRaceClassAsync`, the same check `CMSG_CHAR_CREATE` uses, for the configured `Classes` (a class with no
valid race of the chosen faction is skipped). A zone whose area team is the other faction's (`AreaTeams.Ally` 2, `Horde` 4) is
never offered; contested zones (team 0) are offered to both.

The (race, class) cache is built on a pass, from a store resolved in **that pass's service scope** (`IWorldDataStore` is a scoped
service over a DbContext) and awaited inside it; only the resulting value is kept, keyed by the class list.

## Ownership and parking

**Ownership is a row.** Only a bot the population created has a row in `playerbot_population_member`; a bot an operator made with
`.playerbot create` has none. The population **counts** every running managed bot towards the total target, but only its own
bots in a band (above), and it only ever parks or relocates bots it owns, so an operator's bot is never stopped here, and above
the target only owned bots are parked.

**Parking is `IPlayerbotService.StopAsync`**: the bot logs out and saves like any managed bot, stays in the registry and in the
table, and is restarted first when the loop wants bots again. `PopulationParking.Order` chooses the least useful bot among the
owned running bots that are:

* **autonomous** - not driven by a real player's party (`IsPartyDriven`), not scripted (`IsScripted`), not driven by a bot-led
  group (`PlayerbotGroupCoordinator.Drives`); and
* **not busy** - in the world, out of combat, not being teleported. A dead bot or a ghost qualifies: it logs out like any bot
  (a server shutdown does the same) and comes back as it was, and it earns nothing meanwhile.

Order, **least productive first**: a dead bot or ghost, then a resurrection-sick one, then a stalled one, then the rest; within
each the band with the largest surplus (a bot in no band counts as the largest), then a bot with no real player in its zone, then
the bot that has run longest since the population started it (one started before this process or by an operator counts as
longest: a rotation), then the lower `BotId`. Band fullness only breaks ties between equally productive bots. The surplus is
recounted after each pick so a band is not drained below its share while another still has a surplus. Each pick carries its
reason for the status and the log: `dead`, `sick`, `stalled`, `surplus` (its band holds more than its share) or `rotation`.

**Anti-churn.** A bot the population started less than `MinRunSeconds` ago is not parked for the budget at all, and is parked
last over the target (a lowered target is the operator's limit). Before 2026-10-11 the most recently started bot was parked
first, so a restart was the next park.

The facts are seconds old by the time of the stop, so the world thread re-checks `autonomous` and `not busy` right before each
stop. A dead bot is never relocated.

**Housekeeping.** A row whose bot or character is gone (the character delete cleanup removes the row; a registry loss leaves it
behind) is deleted at the next pass.

## Relocation

With `Relocation = Teleport` and `MaxRelocationsPerInterval > 0`, on a pass that otherwise holds (`under-budget`, `at-target`,
`hysteresis`), owned bots that are autonomous, idle and not vetoed are looked at, the worst misfit first. A bot whose level is at
most its zone's highest level plus `OutlevelMargin` (default 2) and not under-levelled (`PopulationZoneAtlas.UnderLevelled`: below
the lowest placement level of its zone's graveyards for its team under the level rule, `LowestLevelFor`) stays; the row only records the zone it walked to.
Otherwise it is teleported to a graveyard of the least populated zone that fits its level and team. This also re-homes bots an
older build placed badly (a level 9 bot at Raven Hill) and bots that walked over a border into a harder zone, on the first pass
that sees them alive and idle. The zone checked is the one the bot stands in at that pass, whatever it is doing: a bot travelling
on its own (a travel goal, a road through a harder zone) that is under- or out-levelled for the zone
it is crossing is teleported too, mid-goal, and its goal is cut short. That is deliberate: a low-level bot crossing Duskwood is as
likely to die there as one placed there. Dead, in-combat, teleporting, party-driven, scripted, group-driven and vetoed bots are
never moved. `Off` leaves the bot to its own goals. No bot is relocated on a parking pass, and only bots with a
`playerbot_population_member` row are ever moved: never a real player, never an operator's bot.

### The `IPopulationPlacementVeto` seam

```csharp
public interface IPopulationPlacementVeto { bool OwnsPlacement(Guid botId); }
```

A feature that decides where a bot lives (the bot-home lane's home area) registers an implementation
(`services.AddSingleton<IPopulationPlacementVeto, ...>()`). The capture reads every registered implementation on the world
thread, and **a bot one of them owns is never relocated**. The population still parks and restarts such a bot and still places a
bot it creates. This lane registers none; with none registered nothing is vetoed.

### Zone capacity (`SpawnsPerBot`)

A small zone must not take more bots than its creatures can feed. Deadwind Pass (zone 41, level 50-60, one graveyard) has 62 creature
spawns, 15 of them in an underground crypt on a 600 s respawn; 127 level 60 bots were placed in it, every creature was always dead
and the bots stalled. The atlas now caps each zone: **a zone takes no new or relocated bot while its assigned bots (the running
bots standing in it) are at least the level-fitting surface creature spawns divided by `SpawnsPerBot`** (default 3; 0 turns the cap
off). It applies in `PopulationZoneAtlas.Pick` and `PickSafe`, so creation and relocation both skip a full zone; when every
fitting zone is full no bot is placed rather than overfilling one. Bots already standing in an over-cap zone are not evicted.

* The count is per zone and team (`PopulationSpawnCapacity`): the database spawns hostile to the team under the survey's rule
  (`PopulationCreatureSurvey.CountSurface`: no civilians, no-aggro, invisible or not-selectable creatures, no positive game event
  spawns), whose zone is the one the terrain tile puts them in, and that stand **outdoors on the surface**, the test of
  `PlayerbotNavigation.IsOutdoorsOnSurface` (outside every indoor WMO group and not more than 8 yards under the terrain). Crypts,
  caves and building interiors do not count. The comparison uses the team's count against all bots assigned to the zone.
* A spawn *fits* a bot level range when its template `MinLevel` is within 5 levels below the lowest and 3 above the highest level of
  the range (`PopulationSpawnCapacity.LevelsBelow` / `LevelsAbove`; the 3 is the placement safety gap). Level 20 rats do not
  feed a level 60 bot.
* The survey runs on a background task (`PlayerbotPopulationFeature.EnsureCapacity`): the zone and ground height of each spawn come
  from the terrain tile file (`TerrainManager.ReadTileDetached`, spawns ordered by tile), and a spawn more than 8 yards under the
  ground is dropped there, off the world thread. Only the indoor test (a spawn inside an indoor WMO group, `MapCollision.IsOutdoors`)
  needs the world thread: it hops once per batch of at most 128 spawns lying in **one tile** (`PopulationCreatureSurvey.SurfaceBatch`),
  loading at most that tile's **vmap** (`ICollisionTileDemand` of the line-of-sight service; never the navigation mesh tile, which
  would evict the mesh cache the bots' routes use). A tile nobody held is released again after its batch
  (`VMapManager.ReleaseOnDemandTile`, never a tile a grid holds), and every 16th hop and at the end the cached models no loaded tile
  uses are dropped (`VMapManager.TrimUnusedModels`; the model cache otherwise only grows), so the survey does not leave its models behind. The trim drops every unused model server-wide, so it may drop more than the survey brought in, and a grid tile that reloads later parses such a model again, on the world thread. Measured on the real vmaps (`PopulationSurveyRealTerrainTests`, 8 spawns on Stormwind, Ironforge, Deathknell and Goldshire
  tiles): 227 models cached before and after, 244 brought in and dropped. The first parse of a tile's models still runs on the world
  thread, once per hop. The survey's duration, hop count and the model cache before/after are logged with its result. It never holds
  creation back: until the result is posted (and attached to the atlas, `PopulationZoneAtlas.Capacity`) the previous capacity, or none,
  applies, and without terrain data, creature content or faction templates there is no survey and no cap. A survey that located no
  spawn at all is discarded rather than capping every zone at 0 (and never replaces a capacity already in force). Sources: `AllowedMaps`,
  the area table and the creature definitions version. `SpawnsPerBot` is **not** part of the atlas key:
  the atlas reads the divisor live, so a change neither restarts the terrain lookup nor holds creation, and the survey does not
  depend on it. The old capacity is kept while a new survey runs unless the area table changed.
* Diagnostics: the survey log lists the bots each weighted band can hold (the sum of the caps of the zones that fit it) and every zone
  capped under 20; `.playerbot population status` prints `Zone capacity N` per band and `(SHORT of want)` when the band's want
  exceeds it.
* Size the population against it: the caps of the zones that fit a band must add up to the bots wanted there, or the band is
  left short ("no safe zone").

### Dead-end escape veto

When a bot escapes a dead-end pin or a navigation-mesh island (`PlayerbotBrain.EscapeIsland` teleports it home or to its bind
point), the brain remembers the zone it left (`TakeEscapedZones`). The next capture of the population records it in
`PopulationZoneVetoes` (in memory, per bot and zone, not persisted: a restart forgets it): the zone is vetoed for **60 minutes,
doubling with every repeat escape from the same zone by the same bot (2 h, 4 h, 8 h), at most 8 hours**; the strikes are forgotten 24 h
after a veto ended. The brain keeps the zones of all escapes between two captures (also the bind teleports of a bot stalled exploring
where nothing fits its level and of a revived bot among creatures too strong for it). `RelocateAsync` passes the
bot's vetoes to `PickSafe`, which skips a vetoed zone for that bot only. Without it, the bind point (a low zone) counted the bot as
out-levelled and the next pass relocated it straight back into the dead end.

## The command

`.playerbot population [status|enable|disable|target #count]`, Administrator, an ArcaneCore operator command (not a vmangos one),
added under the `.playerbot` root.

| Subcommand | Does |
|---|---|
| `status` (the default) | The state and why (a park lists its reasons), tick p95 against the budget, the target (and the effective target), running, owned and parked bots, creations in the last minute, the operator bots running and the population's own share they leave, the boost, the zone counts per faction, and want against have per band (the population's own bots). |
| `enable` / `disable` | Sets `World:Playerbots:Population:Enabled` on the live options. `disable` stops the loop; bots it started keep running. |
| `adopt` | Takes over the homes an operator set (for example the `.playerbot send ... SentinelHill` fleet) for the bots of the population, over the next home sweeps, until none is left: a level 1 bot goes back onto the start-area flow, a graduate gets its town homes (see "Town progression"). Bots an operator created and homes an operator cleared are left alone. `AdoptOperatorHomes` does the same permanently. |
| `rush $count [$perSecond]` / `rush stop` | Create and start `count` new bots at a rate, with a tick-p95 brake (see "Fresh launch, rush and fleet reset"). |
| `target #count` | Sets `World:Playerbots:Population:TargetOnline` (0..5000, `PlayerbotOptions.MaxBotsCeiling`); a value out of range is refused and nothing changes. A malformed argument answers with the syntax. |

A value set by command lasts until the next `.reload config` (which re-applies the file) or a restart: the permanent value
belongs in the configuration file. While `Enabled` is false (or `World:Playerbots:Enabled` is), nothing is created, started,
parked or moved, and the status says `disabled`.

## Fresh launch, rush and fleet reset (release simulation)

Three operator tools simulate a server release: every bot back to a new level-1 character, then many bots logging in at once.
All are off or idle by default.

**`.playerbot fleet reset`** (Administrator, console allowed; `PlayerbotFleetCommands`, `ManagedPlayerbotFeature.Reset`).
Two steps. `.playerbot fleet reset` only prints the census (bots to stop, bot characters to delete, bot accounts, bots grouped with
real players, bots trading) and a six-character confirm code valid for 60 seconds, bound to the invoker, single use, three wrong
tries and it is void. `.playerbot fleet reset confirm <code>` executes: the population stands still, trades are cancelled, every
bot is stopped gracefully (batches of 32), then every character of a bot account is deleted through the same path as
CMSG_CHAR_DELETE (`CharacterDeletion.TryDeleteAsync`: the delete hooks, then every characters-database module's
`ICharacterDataCleanup` in one transaction). Guild leadership is handed on (or the guild disbanded) first, a bot in a group with
real players leaves it, and the registry, population member, home and set-aside rows go too. The emptied bot accounts are kept and reused for the next bots created in the same server run (after a restart they stay as unused empty accounts).
**Turn `FreshLaunch` on (and `.reload config`) before you confirm:** with the population enabled and `FreshLaunch` off it refills the fleet at once with boosted bots at their usual levels, not level 1; the preview prints a warning in that case.
Ownership comes only from the managed-bot registry confirmed against the account store (the account is the registered bot
account, has no session key, is not a staff account); name patterns are never used. If any character on a bot account is not the
registered bot's, or the registry points at a real account, the reset refuses and logs, and deletes nothing. A real player's
characters are never selected. Progress and a final summary (characters deleted and failed, bots stopped and failed, groups left,
trades cancelled) are replied and logged at Information with counts. While it runs, `create`/`start` and the population refuse.

**`FreshLaunch`** (`World:Playerbots:Population:FreshLaunch`, live). The population creates level-1 characters at their race's
native start (`playercreateinfo`) with the default creation kit: no boost, no teleport, no zone placement. Race and class are
balanced across both factions over the legal race/class pairs (`PopulationBandPlanner.PlanFresh`: the least-represented
faction, race and class first). The level bands and the zone atlas are bypassed, and relocation (the parking-to-zone move) never
moves a fresh bot out of its starting zone; the status says `fresh launch`.

**`.playerbot population rush <count> [perSecond]`** and **`rush stop`** (Administrator; default 5 per second, at most 50; count
1..5000, `PlayerbotOptions.MaxBotsCeiling`). Creates and starts `count` new population bots at that rate, bypassing the add-budget confirmation windows and the
`MaxCreatePerMinute` limit, with the same account creation as `.playerbot create` and the existing `MaxBots` and
`MaxRegisteredBots` limits (a cap ends the rush as `halted` with the reason). One rush at a time. The brake runs in real time: a window is
`RushBrakeWindowSeconds` (5) seconds, and if the tick p95 of a window is over the ceiling in `RushBrakeWindows` (3) consecutive
windows (about 15 s) the rush pauses and logs a warning; the flag is also checked between bots inside a pass, so creation stops
at once. It resumes after as many windows under `ResumeBelowPct` of the ceiling. The ceiling is `RushBrakeP95Ms`, by default
(0) 1.5 times `TickP95BudgetMs`. While a rush runs or is paused the interval pass still runs to shed: it parks owned bots when the
p95 is over `TickP95BudgetMs` (`ParkWhenOverBudget`), and never adds, moves or parks for the target. These shed-only passes run during a rush even when `Population.Enabled` is `false`. `.playerbot population status` adds a `Rush <state>` line with created/failed,
requested and achieved rate, the tick p95 and the brake windows seen. A fleet reset stops a running rush. Note: bots above
`TargetOnline` are parked by the ordinary passes after the rush, so set `TargetOnline` to the rush size when the bots should stay
online.

## Configuration

`World:Playerbots:Population:*`, all live through `.reload config`; the full table with ranges and defaults is generated into
`docs/reference/configuration.md`. A list (`Classes`, `Bands`, `ExcludedZones`) replaces its default as a whole, and is compared
and swapped as one value on reload.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Run the manager. |
| `TargetOnline` | `0` | Managed bots to keep running (0..5000; capped by `MaxBots`); operator bots running take their places off the top, the rest is split over the bands. |
| `IntervalSeconds` | `10` | World seconds between passes (2..600). |
| `TickP95BudgetMs` | `60` | The p95 tick budget (5..1000). |
| `ResumeBelowPct` | `80` | Add only below this percent of the budget (10..99). |
| `WindowTicks` | `200` | Fresh ticks needed after a change (20..4096). |
| `ParkWhenOverBudget` | `true` | Park owned bots when over budget; off: only stop adding. |
| `ConfirmWindows` | `2` | Consecutive fresh windows over the budget before a park, or under the resume line before an add (1..20). |
| `MinRunSeconds` | `300` | A bot the population started is not parked for the budget before this many world seconds (0..3600). |
| `ActionLogSeconds` | `60` | At most one information line per this many world seconds for the passes that changed something (0..3600; 0 = every one). |
| `MaxCreatePerMinute` | `4` | New characters per minute of world time (0..600). |
| `MaxStartsPerInterval` / `MaxParksPerInterval` | `4` / `2` | Starts plus creations, and parks, per pass (1..500). |
| `Factions` / `AllianceSharePct` | `Both` / `50` | Which factions; the Alliance share with `Both`. |
| `Classes` | all nine | The 1.12 class ids new bots may have. |
| `Bands` | six of ten levels | `MinLevel`, `MaxLevel`, `Weight` per band. |
| `ZoneLevelSlack` | `4` | Levels below a placement's floor a bot may be, up to `ZoneLevelSlackMaxFloor` (0..10). |
| `ZoneLevelSlackMaxFloor` | `12` | The highest floor with the whole slack; above it one level less per two floor levels (1..60; 60 = flat). |
| `CreatureLevelMargin` | `3` | No placement where most hostile creatures near the graveyard are more than this above the bot (0..10). |
| `CreatureSurveyRadius` | `100` | Yards around each graveyard the creature survey counts (0..300; 0 = no survey). |
| `SpawnsPerBot` | `3` | Zone capacity: a zone takes bots while assigned bots < level-fitting surface creature spawns / this (0..100; 0 = no cap). |
| `OutlevelMargin` | `2` | Levels above a zone's highest before relocation (0..10). |
| `Relocation` / `MaxRelocationsPerInterval` | `Teleport` / `2` | What to do with an out-levelled bot; moves per pass (0..50). |
| `ExcludedZones` | `[]` | Zone ids bots are never placed in (at most 256). |
| `FreshLaunch` | `false` | New population bots are level 1 at their race's native start, with the default kit, no boost and no teleport (see "Fresh launch"). |
| `TownProgression` | `true` | From `FreshReleaseLevel` a bot has town homes (see "Town progression"). Off: no home past the release level. |
| `TownProgressionTeleport` | `true` | A town on another continent, across the water of Teldrassil or farther than `TownWalkYards` is reached by the one return teleport. Off: such a town is not assigned (the bot takes a town it can walk to; with none it has no town home) and one Info anomaly per reason says so; the progression never teleports then, only the Home options' returns of a displaced bot do. |
| `TownHomeRadius` / `CapitalHomeRadius` | `4500` / `500` | Radius of a level-band town home (zone-clipped; covers the zone's quests) and of a capital home (the city). 50..5000. |
| `TownCap` / `TownCaps` | `60` / empty | Most bots a level-band town holds as its secondary (0 = no cap) and per-town overrides `key=cap,key=cap`. When every town of a band is full the least populated still takes the bot. |
| `CapitalMajorSharePct` | `65` | Percent of each faction whose master capital is Stormwind or Orgrimmar; the rest go to the other two capitals, the bot's own race's first. |
| `SecondaryStayMinMinutes` / `SecondaryStayMaxMinutes` | `30` / `90` | Stay at the level-band town before heading to the capital; fixed per bot (by its id) inside the range. |
| `CapitalStayMinMinutes` / `CapitalStayMaxMinutes` | `5` / `15` | Stay at the capital (errands, then idling at the inn) before heading back out. |
| `TownBandMargin` | `0` | Levels a bot stays at its town past the band's maximum before the next town takes it (0..10). |
| `TownWalkYards` | `4500` | The longest trip a bot walks to a town home on its continent (100..20000). |
| `AdoptOperatorHomes` | `false` | The sweep also replaces operator homes of population bots with the home of their level. |
| `RushBrakeP95Ms` | `0` | The tick p95 above which a `rush` pauses (50..10000); 0 = 1.5 x `TickP95BudgetMs`. |
| `RushBrakeWindows` | `3` | Consecutive windows over the ceiling before the pause, and under `ResumeBelowPct` of it before the resume (1..20). |
| `RushBrakeWindowSeconds` | `5` | The real-time length of one brake window (1..60). |

## Town progression (levelling towns and capitals)

`PopulationTownProgression.cs`, `PlayerbotTownIdle.cs`, `PlayerbotHomeTravel.cs`. After the start flow (start valley, then the start zone) a bot of
the fresh launch has **two homes** and lives at one at a time:

- the **master**, its faction capital: Stormwind or Orgrimmar for `CapitalMajorSharePct` percent (by a stable hash of the bot id), otherwise
  Ironforge/Darnassus or Undercity/Thunder Bluff, the bot's own race's first (race is only the tiebreak inside the minor capitals);
- the **secondary**, the town of its level band from the table `PopulationTownProgression.Rows` (46 rows: the six capitals and 40 level-band
  towns, classic 1.12 zone level ranges from level 10 up; two or more towns per band where they exist: Kharanos, Sentinel Hill/Thelsamar,
  Lakeshire, Darkshire/Menethil, Southshore, Refuge Pointe, Booty Bay, Aerie Peak, Nethergarde/Thorium Point, Chillwind/Light's Hope; Brill,
  The Sepulcher, Tarren Mill, Hammerfall/Grom'gol, Stonard/Kargath, Revantusk; Auberdine, Astranaar, Theramore, Feathermoon; the
  Crossroads/Camp Taurajo, Splintertree/Sun Rock, Freewind Post, Shadowprey, Brackenwall, Camp Mojache; neutral Ratchet, Gadgetzan,
  Marshal's Refuge, Everlook, Cenarion Hold). There are no rows for the 5-10 start-zone hubs (Goldshire, Dolanaar, Razor Hill, Bloodhoof):
  the town stage begins at `FreshReleaseLevel` 10, where a band ending at 10 no longer serves, so such a row could never be picked at the default `FreshReleaseLevel`.

The town centres are not coordinates: each row names the innkeeper (or, where the data has none, the flight master) whose first spawn on
the town's map is the centre, read from the creature content when the sweep first needs it (`Resolve`). A town that does not resolve is
skipped and reported in **one** `bot-population/town-unresolved` anomaly; a bot with no resolvable capital gets no home (the old behaviour).

The stored home (`managed_playerbot_home`, no schema change) is the **active** one; its place text carries all three:
`town:<active>;m=<master>;s=<secondary>`. The sweep alternates: a stay at the secondary (`SecondaryStayMin/MaxMinutes`), a stay at the capital
(`CapitalStayMin/MaxMinutes`), and again; a graduate starts at its capital. The stay clock is in memory only: after a restart the stored place
text says where the bot is and a **fresh stay** starts there, so nothing flaps. The secondary changes only when the bot's level reaches the
maximum of its town's band (plus `TownBandMargin`), or when the explore atlas has had no creature of the bot's level round the town for two
sweeps in a row (an over-levelled zone; one sweep is not enough, so a mid-band bot does not hop on a single level); at the capital the new secondary waits for the stay to end, at the secondary the bot goes straight to it. Spread:
the least populated town of the band under `TownCap`/`TownCaps`. A capital home (radius `CapitalHomeRadius`, zone-clipped to the city)
contains the trainers, auction house, bank and vendors of the city, which is where the existing town goals (and the economy and trainer
destinations, which stay inside the active home) do their errands; the level-band town's home (radius `TownHomeRadius`) covers the zone's
quests. Leave-home quests (a turn-in outside the home, at most `LeaveTurnInYards` = 3000 yards from its centre: the same zone or the next
band's town) are allowed only from the level-band town and the start zone, never from a capital (`AllowsLeaveQuests`).

A bot with a reachable capital but **no reachable secondary** (with `TownProgressionTeleport` off, a bot on Teldrassil: every town of its
level is across the water) gets no town home: a capital-only home would hold it at the inn for good. A town home it already had is dropped,
a start, zone or operator home stays, and the bot explores like an unhomed one. A bot with **no reachable capital at all** (for example one
inside a dungeon, with the teleport off) keeps whatever home it has, a town home included. The one Info anomaly (`town-unreachable`) is
reported once per process for each combination of where the bot stands (a continent, or another map) and which of those two outcomes
applied, and its text says which. A bot younger than the first town of its own continent (only with a `FreshReleaseLevel` below 10, for
example a Kalimdor Horde bot at level 6 to 9) gets no town rather than one on another continent. Capital-only place text, whenever one is
read, has no stay and so no idle rule.

**Sweep details.** The home sweep resumes where the previous one stopped (round robin), so bots whose homes change on a cycle never starve
those still waiting for their first. `.playerbot population adopt` is a request generation: a sweep retires only the generation it began with,
and only when every operator home was taken over (towns not loaded yet, a town the bot cannot reach, or a failure leave it pending; the
operator home of a zone-level bot that has left its start country is cleared, as a release). An
unresolved town and a town on a map `AllowedMaps` refuses are each reported once per process (`town-unresolved`, `town-map-not-allowed`). The
town state of a bot that is no longer in the population is dropped.

**Idle.** A bot with a town home that has nothing to do goes back to the inn of the town it stays in and waits there
(`PlayerbotTownIdle`): at the capital always, at the level-band town for two minutes in turn with four minutes of normal exploring. At the
inn it binds its hearthstone through the server's own path (CMSG_BINDER_ACTIVATE to the innkeeper), so a bind or hearth teleport no longer
sends a graduate back to its start valley. If the spot beside the innkeeper cannot be reached (a counter, an upper floor, Undercity), the walk
is given up after `WalkStallMs` (20 s) without getting a yard closer and the bot stands where it is for `StallDeferMs` (3 min); it then tries
again, so an unreachable spot costs one 20 s attempt every 3 min 20 s, never a re-plan on every think. Standing is only for a bot within
`HoldYards` (30) of the town centre, and the stall watch does not count that wait (live 2026-10-17: a bot lingering at its inn was reported
stalled every two minutes, 1 253 times at the Stormwind inn alone). A bot whose walk was given up farther out explores for the give-up
(hunts, or wanders inward) instead of standing in the open. The progress window restarts
only on an explicit interruption: the brain calls `PlayerbotTownIdle.Interrupted()` after any think that ran a goal other than Explore (a
quest, a fight, an errand), or an Explore think that made no town idle decision, and the idle decision itself restarts the window when the
last decision was not a walk (the explore window). There is no wall-clock gap rule: a think gap of 10 to 16 s on a busy server, or a think
skipped for want of plan budget, does not reset a walk. A town walk is never chained onto a forward wander leg (it would carry the bot past
the innkeeper). It binds only when an innkeeper is within
interaction distance (5 yards plus the bounding radii) of the bot, so a stalled bot far from the inn does not bind. The pull back to the home goes to the active home only.

**Travel.** A trip within `TownWalkYards` on the same side of the water is walked (`PlayerbotHomeArea.WalkRangeYards`); a longer one, one
across the water of Teldrassil (Darnassus from Darkshore), or one to another continent takes the existing return teleport with its
5-minute cooldown (`PlayerbotHomeReturn.TryTeleport`). With `TownProgressionTeleport` off such a town is not assigned (one Info
anomaly per reason, naming the real one: across water, or the bot stands on a dungeon or battleground map), and the progression's own moves
never teleport (the Danger rule is not applied to the way to a town home). What still teleports are the Home options' returns of a displaced
bot, as for any home: another map (`OtherMap`), a walk set aside as blocked (`WalkBlocked`), `ReturnMode` Teleport, an unsafe spirit-healer
landing and stalled ground. All of it goes through one seam, `IPlayerbotHomeTravel` (`CanReachWithoutTeleport`, `Decide`). The daemon
registers authentic travel's `PlayerbotTravelHomeTravel` (docs/areas/playbots.md, Travel): with `World:Playerbots:Travel` on and a return
mode other than Walk, a town across the sea counts as reachable when a boat or zeppelin route joins the two (Teldrassil by the Auberdine
boat), and the brain travels there by boat, zeppelin, flight path or portal before the return rules (the teleport) are asked. In Walk mode,
with travel off, or without such a route it answers as `DefaultHomeTravel` does, and a Teldrassil bot with the option off has no town
secondary.

**Release level.** `FreshReleaseLevel` is now 10 (was 12): the bot is in the town of the next zone as it finishes its start zone. A level
reset to 1 puts a bot with a town home back on the start-area flow.

## The characters table (schema 57)

`playerbot_population_member` (`PlayerbotPopulationDataModule`, characters version **57**; written as 56, the next after main's 55 when the lane
was written; the w19 train renumbered it because the bot-home lane took 56 - the module is self-contained and only its `Version` constant,
the `IntegratedSchemaTests` expectation and `docs/reference/schema.md` change).

| Column | Meaning |
|---|---|
| `BotId` (key) | The managed playerbot (`ManagedPlayerbot.BotId`). |
| `CharacterId` (unique) | The bot's character; the row goes with the character (`ICharacterDataCleanup`). |
| `ZoneId` | The zone the population last placed the bot in (0: not placed yet). |
| `Band` | The index of the band it was created for (informational; bands are matched by level), or **255** (`PendingDeleteBand`): the bot could not be placed and is being deleted. A pending member is never restarted and never counted in a band or in the owned totals. |
| `CreatedUnix`, `UpdatedUnix` | Unix seconds. |

`EfPlayerbotPopulationStore.UpsertAsync` inserts a row or updates `ZoneId`, `Band` and `UpdatedUnix` of the row with the same bot
**and** character; the identity never changes, and a different character for a known bot (or a known character for another bot) is
refused. `DeleteAsync` returns whether a row went. Registered scoped; the feature opens one scope per pass.

## Failure behaviour

Fail closed: an error in capture or in a pass is logged, published as `state error` with the reason (and kept as `Last problem`),
and the next interval tries again; nothing throws into the world thread. A refusal of a single bot (start, creation, boost,
teleport, park) is recorded as the last problem and ends only that bot's step. A shutdown cancels the pass and waits up to thirty
seconds for it.

**A bot that cannot be placed is deleted.** When the placement is refused after creation, the member row is first marked
pending (`Band` = 255, written even during a shutdown), then the bot is stopped and deleted (its character through the ordinary
character deletion, then its registry row; the owner account stays), and only then is the member row removed. The bot is deleted
before the member row, so a deletion that fails (a refusing delete hook, the feature stopping, a cancelled shutdown, a crash)
leaves the marked row, never an unowned stray. At the start of every pass the pending members are taken out of the counts and
their deletion is retried, on a growing interval (after 1, 2 ... at most 8 passes) so that a permanently refused delete does not
overwrite the last problem every pass. A pending bot that a restart restored as running is stopped and deleted by that first pass
and is not counted in any band.

**Creation pauses after three refusals in a row.** Three placements refused one pass after another (counted from the refusal, reset
by any successful placement) pause creation: passes 4 to 13 report "creation paused: 3 placements refused in a row" and create
nothing, and pass 14 plans again. Restarting parked members and parking are not affected. A `placement-data-stale` refusal is not
counted: it means the snapshot is being rebuilt, not that the placements are bad.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Population/`: the pure helpers (`PopulationBudgetControllerTests`,
`PopulationBandPlannerTests`, `PopulationZoneAtlasTests`, `PopulationLevelRuleTests` (the slack taper, the creature survey, band
coverage and no relocation ping-pong, on the live w24 floors of the Hinterlands, Feralas and Deadwind Pass), `PopulationZoneCapacityTests` (the zone cap, indoor spawns not counting, the band capacity, the escape veto and its TTL) and `PopulationZoneWiringTests` (the feature: a recorded escape keeps a bot out of the zone; the capacity survey superseded, cancelled and discarded), `PopulationTickWindowTests`, `PopulationNamesTests`,
`PlayerbotPopulationOptionsTests`), `PopulationParkingTests` (who may be parked and in which order: dead, sick, stalled first;
the anti-churn minimum; the restart rotation), `PlayerbotPopulationFeatureTests` (a real world and managed bots with a fake tick
source: add, hold, park, restart and the window reset; the creation caps; owned-only parking; operator bots in a band not
suppressing its creation; a dead bot parked first; the rotation; the confirmation windows; the anti-churn minimum; the action log
line; the pass-scoped store; enable and disable on the timer) and
`PlayerbotPopulationCommandTests`. The membership table: `tests/ArcaneCore.Data.Tests/PlayerbotPopulationStoreTests.cs`.
