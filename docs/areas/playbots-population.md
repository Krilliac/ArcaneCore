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

| File (`src/ArcaneCore.World/Playerbots/Population/`) | What it does |
|---|---|
| `PlayerbotPopulationOptions.cs` | `World:Playerbots:Population` (every key live through `.reload config`), the validation and the list bindings. |
| `PopulationBudgetController.cs` | The control loop as a pure function: samples, p95, counts -> hold, add or park, with a stable state code. |
| `PopulationTickWindow.cs` | `IPopulationTickSource` and `WorldTickWindow`, the ring of the last 4096 tick body durations the p95 is read from. |
| `PopulationBandPlanner.cs` | Pure planning: bots per band (largest remainder), the next band, faction, race and class, level; the (race, class) cache. |
| `PopulationZoneAtlas.cs` | The zones a bot may live in, built from the area table and the graveyard catalog; `Fits`, `Pick`. |
| `PopulationProvisioner.cs` | Gives a running bot its level and kit (`CharacterBoost.Run`) and its place (the teleport service). |
| `PopulationNames.cs` | A letters-only syllable generator for the names of new characters. |
| `IPopulationPlacementVeto.cs` | The seam the home-area lane implements (below). |
| `PlayerbotPopulationFeature.cs` | The world feature: capture, one pass at a time, add, park, relocate, the published status; `PopulationParking`. |
| `PlayerbotPopulationCommands.cs` | `.playerbot population [status\|enable\|disable\|target N]`. |

Outside the folder, all additive: `PlayerbotOptions.Population` (and its binding and validation), the live keys in
`WorldConfigKeys`, `IPlayerbotPopulationStore` and `PlayerbotPopulationMember` in the kernel, and the characters table below
(`src/ArcaneCore.Data/Characters/Playerbots/PlayerbotPopulationDataModule.cs`).

## The control loop

Every `IntervalSeconds` of **world time** (the `Updated` event's elapsed time, so a manual test clock drives it too) the world
thread starts a pass, if the previous one has finished:

1. **Capture, on the world thread.** The facts of every running managed bot (level, team, zone, whether it is autonomous, whether
   it is busy, whether a real player stands in its zone, whether a placement veto owns it) and the tick p95 and sample count.
   Nothing is awaited here.
2. **Decide and act, on one pool task.** The task reads the membership table and the bot registry, calls
   `PopulationBudgetController.Decide`, then adds, parks and relocates through `IPlayerbotService` (start, create, stop) and
   `world.InvokeAsync` (boost and teleport). It never blocks the world thread, and the world thread never waits on it. One pass
   runs at a time; a pass that is still running when the next interval comes is not doubled.
3. **Reset.** After any start, creation, park or relocation the task asks the world thread to `Reset()` the tick source, so the
   next decision sees only ticks measured after the change.
4. **Publish.** `PlayerbotPopulationFeature.Status` is replaced by an immutable `PopulationStatus` (readable from any thread; the
   command prints it).

### The decision, in order

`PopulationBudgetController.Decide(samples, p95Ms, runningAll, ownedRunning, effectiveTarget, options)`:

| State | When | Action |
|---|---|---|
| `settling` | fewer than `WindowTicks` ticks measured since the last change | hold |
| `over-target` | more managed bots run than the effective target and the population owns a running one | park `min(surplus, MaxParksPerInterval, ownedRunning)`; this is checked before the budget, so a lowered target is honoured even on a quiet world |
| `over-budget` | tick p95 above `TickP95BudgetMs` and `ParkWhenOverBudget` | park `min(MaxParksPerInterval, ownedRunning)` |
| `over-budget-hold` | over budget and `ParkWhenOverBudget` is off | hold (stop adding) |
| `over-budget-no-owned` | over budget and the population owns no running bot | hold; it never stops an operator's bot |
| `hysteresis` | p95 between `ResumeBelowPct` of the budget and the budget | hold |
| `under-budget` | p95 below `ResumeBelowPct` of the budget and running < effective target | add `min(target - running, MaxStartsPerInterval)` |
| `at-target` | p95 below the resume line and running >= effective target | hold |

The effective target is `min(TargetOnline, MaxBots)` (`World:Playerbots:MaxBots` is the running cap of the whole feature).

### Hysteresis

Two things stop it adding and parking in turns. **The band:** adding resumes only below `ResumeBelowPct` (default 80) of the
budget and parking starts only above the budget, so with the default 60 ms budget a p95 between 48 and 60 ms changes nothing.
**The reset:** the tick source is emptied after every change and the next decision needs `WindowTicks` (default 200, about ten
seconds at 50 ms) fresh ticks, so a bot's own login or teleport cost cannot be read as the world's load, and the p95 never
reflects ticks from before the last change. Both are pure and unit tested (`PopulationBudgetControllerTests`); the end-to-end
sequence with a fake tick source is `PlayerbotPopulationFeatureTests`.

The p95 is the nearest rank of the last `WindowTicks` tick body durations from `WorldRuntime.TickCompleted`. `WorldRuntime.Stats`
keeps a 4096-tick ring too, but its snapshot copies and sorts all of it (about 3.4 minutes at 50 ms, too slow and too
allocating to steer a ten second loop, and it cannot be reset), so the population keeps its own ring: `Add` is one array write
on the world thread and allocates nothing.

## Adding a bot

Per pass, up to the decision's count, in this order:

1. **Restart the population's own stopped bots first**, those in a band with a deficit before the others. A faulted bot
   (quarantined or disabled) is not started here: an operator start would clear its fault history, and the managed feature
   retries a quarantined bot itself. Three refused starts end the pass's starting.
2. **Create new bots**, within the creation caps below. Each is planned (`PopulationBandPlanner.Plan`), created through
   `IPlayerbotService.CreateAsync` (the ordinary `CMSG_CHAR_CREATE` rules; a refused name is retried with the next candidate, at
   most five times), recorded in `playerbot_population_member`, started, boosted to its level and teleported to its place. If
   boosting or teleporting is refused, the bot stays owned and stopped and the next pass restarts it where it stands (its race's
   starting area).

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
band with the largest deficit (desired minus running; the lower band on a tie) that a zone can take.

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
assigned** wins, ties broken at random, then one of its graveyards for the bot's team.

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
`.playerbot create` has none. The population **counts** every running managed bot towards the target, but it only ever parks or
relocates bots it owns, so an operator's bot is never stopped here, and above the target only owned bots are parked.

**Parking is `IPlayerbotService.StopAsync`**: the bot logs out and saves like any managed bot, stays in the registry and in the
table, and is restarted first when the loop wants bots again. `PopulationParking.Order` chooses the least useful bot among the
owned running bots that are:

* **autonomous** - not driven by a real player's party (`IsPartyDriven`), not scripted (`IsScripted`), not driven by a bot-led
  group (`PlayerbotGroupCoordinator.Drives`); and
* **idle** - in the world, alive, out of combat, not being teleported.

Order: the band with the largest surplus (a bot in no band counts as the largest), then a bot with no real player in its zone,
then the most recently started, then the lower `BotId`. The surplus is recounted after each pick so a band is not drained below
its share while another still has a surplus. The facts are seconds old by the time of the stop, so the world thread re-checks
`autonomous` and `idle` right before each stop.

**Housekeeping.** A row whose bot or character is gone (the character delete cleanup removes the row; a registry loss leaves it
behind) is deleted at the next pass.

## Relocation

With `Relocation = Teleport` and `MaxRelocationsPerInterval > 0`, on a pass that otherwise holds (`under-budget`, `at-target`,
`hysteresis`), owned bots that are autonomous, idle and not vetoed are looked at, the most out-levelled first. A bot whose level
is at most its zone's highest level plus `OutlevelMargin` (default 2) stays; the row only records the zone it walked to. Otherwise
it is teleported to a graveyard of the least populated zone that fits its level and team. `Off` leaves the bot to its own goals.
No bot is relocated on a parking pass.

### The `IPopulationPlacementVeto` seam

```csharp
public interface IPopulationPlacementVeto { bool OwnsPlacement(Guid botId); }
```

A feature that decides where a bot lives (the bot-home lane's home area) registers an implementation
(`services.AddSingleton<IPopulationPlacementVeto, ...>()`). The capture reads every registered implementation on the world
thread, and **a bot one of them owns is never relocated**. The population still parks and restarts such a bot and still places a
bot it creates. This lane registers none; with none registered nothing is vetoed.

## The command

`.playerbot population [status|enable|disable|target #count]`, Administrator, an ArcaneCore operator command (not a vmangos one),
added under the `.playerbot` root.

| Subcommand | Does |
|---|---|
| `status` (the default) | The state and why, tick p95 against the budget, the target (and the effective target), running, owned and parked bots, creations in the last minute, the boost, the zone counts per faction, and want against have per band. |
| `enable` / `disable` | Sets `World:Playerbots:Population:Enabled` on the live options. `disable` stops the loop; bots it started keep running. |
| `target #count` | Sets `World:Playerbots:Population:TargetOnline` (0..1000, `PlayerbotOptions.MaxBotsCeiling`); a value out of range is refused and nothing changes. A malformed argument answers with the syntax. |

A value set by command lasts until the next `.reload config` (which re-applies the file) or a restart: the permanent value
belongs in the configuration file. While `Enabled` is false (or `World:Playerbots:Enabled` is), nothing is created, started,
parked or moved, and the status says `disabled`.

## Configuration

`World:Playerbots:Population:*`, all live through `.reload config`; the full table with ranges and defaults is generated into
`docs/reference/configuration.md`. A list (`Classes`, `Bands`, `ExcludedZones`) replaces its default as a whole, and is compared
and swapped as one value on reload.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Run the manager. |
| `TargetOnline` | `0` | Managed bots to keep running (0..1000; capped by `MaxBots`). |
| `IntervalSeconds` | `10` | World seconds between passes (2..600). |
| `TickP95BudgetMs` | `60` | The p95 tick budget (5..1000). |
| `ResumeBelowPct` | `80` | Add only below this percent of the budget (10..99). |
| `WindowTicks` | `200` | Fresh ticks needed after a change (20..4096). |
| `ParkWhenOverBudget` | `true` | Park owned bots when over budget; off: only stop adding. |
| `MaxCreatePerMinute` | `4` | New characters per minute of world time (0..60). |
| `MaxStartsPerInterval` / `MaxParksPerInterval` | `4` / `2` | Starts plus creations, and parks, per pass (1..100). |
| `Factions` / `AllianceSharePct` | `Both` / `50` | Which factions; the Alliance share with `Both`. |
| `Classes` | all nine | The 1.12 class ids new bots may have. |
| `Bands` | six of ten levels | `MinLevel`, `MaxLevel`, `Weight` per band. |
| `ZoneLevelSlack` | `4` | Levels below a zone's lowest sub-area that it still accepts (0..10). |
| `OutlevelMargin` | `2` | Levels above a zone's highest before relocation (0..10). |
| `Relocation` / `MaxRelocationsPerInterval` | `Teleport` / `2` | What to do with an out-levelled bot; moves per pass (0..50). |
| `ExcludedZones` | `[]` | Zone ids bots are never placed in (at most 256). |

## The characters table (schema 57)

`playerbot_population_member` (`PlayerbotPopulationDataModule`, characters version **57**; written as 56, the next after main's 55 when the lane
was written; the w19 train renumbered it because the bot-home lane took 56 - the module is self-contained and only its `Version` constant,
the `IntegratedSchemaTests` expectation and `docs/reference/schema.md` change).

| Column | Meaning |
|---|---|
| `BotId` (key) | The managed playerbot (`ManagedPlayerbot.BotId`). |
| `CharacterId` (unique) | The bot's character; the row goes with the character (`ICharacterDataCleanup`). |
| `ZoneId` | The zone the population last placed the bot in (0: not placed yet). |
| `Band` | The index of the band it was created for (informational; bands are matched by level). |
| `CreatedUnix`, `UpdatedUnix` | Unix seconds. |

`EfPlayerbotPopulationStore.UpsertAsync` inserts a row or updates `ZoneId`, `Band` and `UpdatedUnix` of the row with the same bot
**and** character; the identity never changes, and a different character for a known bot (or a known character for another bot) is
refused. `DeleteAsync` returns whether a row went. Registered scoped; the feature opens one scope per pass.

## Failure behaviour

Fail closed: an error in capture or in a pass is logged, published as `state error` with the reason (and kept as `Last problem`),
and the next interval tries again; nothing throws into the world thread. A refusal of a single bot (start, creation, boost,
teleport, park) is recorded as the last problem and ends only that bot's step. A shutdown cancels the pass and waits up to thirty
seconds for it.

## Tests

`tests/ArcaneCore.World.Tests/Playerbots/Population/`: the pure helpers (`PopulationBudgetControllerTests`,
`PopulationBandPlannerTests`, `PopulationZoneAtlasTests`, `PopulationTickWindowTests`, `PopulationNamesTests`,
`PlayerbotPopulationOptionsTests`), `PopulationParkingTests` (who may be parked and in which order),
`PlayerbotPopulationFeatureTests` (a real world and managed bots with a fake tick source: add, hold, park, restart and the window
reset; the creation caps; owned-only parking; the pass-scoped store; enable and disable on the timer) and
`PlayerbotPopulationCommandTests`. The membership table: `tests/ArcaneCore.Data.Tests/PlayerbotPopulationStoreTests.cs`.
