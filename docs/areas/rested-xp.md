# Rested experience

Lane `L10-rested-xp-and-char-lifecycle` (the rest half; the rename half is [character-rename](character-rename.md)). It merges the
rested-XP gaps that the world-instances-pvp and progression-ops lanes each listed. Behaviour is taken from the mangos reference core
(`Player::SetRestType`, `ComputeRest`, `GetXPRestBonus`, `SetRestBonus`, the rest block of `Player::Update`, `HandleAreaTriggerOpcode`,
`PlayerLoad.cpp`, `PlayerSave.cpp`); the comments in the code cite the files and lines. The vmangos source is not available on the
build machine, so every number below is the mangos one; where vmangos may differ it is marked **UNVERIFIED**.

## What it does

A character gains a **rested pool** while it rests, and while it is logged out. The pool is capped at one and a half levels of experience
as the client shows it: the server keeps half of that (`next-level XP * 1.5 / 2`, 300 at level 1 with 400 XP to the next level) because
the client doubles the value it is sent. A kill takes from the pool up to the experience the kill gave and adds it, so kill experience is
at most doubled; quest and exploration experience never touch the pool. The pool is written to the database and survives a logout.

| Piece | Where |
|---|---|
| The pool, its cap, the rest state byte, `PLAYER_REST_STATE_EXPERIENCE`, the doubling of kill XP | `PlayerProgression` (`SetRestBonus`, `RestBonus`, `GiveXp`), unchanged apart from comments |
| Where and how fast the pool fills, offline gain, what is persisted | `src/ArcaneCore.Game/Progression/RestService.cs`, `RestOptions.cs` |
| The daemon wiring: events, tick, persistence | `src/ArcaneCore.World/Progression/RestFeature.cs`, `RestWriteQueue.cs`, `TavernTriggers.cs` |
| The inn table and its reload | `src/ArcaneCore.Data/World/Rest/AreaTriggerTavernDataModule.cs`, `src/ArcaneCore.World/Reload/TavernContentReloadable.cs` |
| The stored state | `src/ArcaneCore.Data/Characters/Life/CharacterRestDataModule.cs` |

### Where a character rests

* **Capital city.** The zone listener (`IPlayerLocationListener`, the zone tracker's own events) starts a city rest when the zone entry's
  flags contain `AreaFlags.Capital` (0x100), and ends any rest that is not an inn's when the character enters another zone
  (`Player::UpdateZone`, PlayerZone.cpp:343-349). A zone with no area entry (client-zone mode, no area data) is not a capital.
* **Inn.** `CMSG_AREATRIGGER` for a trigger listed in `areatrigger_tavern` starts a tavern rest, unless the character is resting in a city
  (a city rest is not overwritten). The area-trigger handler only calls listeners for a trigger that exists and that the character is
  inside (with the client delta), which is the check vmangos makes when it loads the table (a row naming no trigger is ignored).
* **Leaving an inn.** vmangos checks the position whenever the character moves outdoors. Here the rest feature polls once a second for
  characters in a tavern rest: when the character is outdoors (the map's collision data, `IsOutdoors`) and outside the volume of the
  trigger it entered by, the rest ends. Indoors it never ends (an inn is a building). A trigger that was reloaded away ends the rest at
  the next poll. **Deviation:** a poll of resting characters instead of a check on movement, same result up to one second.
* Setting a rest type again restarts the gain clock (the reference does the same, so the up-to-10-second remainder of the clock is lost
  at each capital-city zone entry).

### Gain while resting

Every world tick `RestService.Update` visits only the resting characters (a hash set; no allocation). Once 10 seconds have passed since
the last gain (`Rest:AccrualIntervalSeconds`) the elapsed time is converted: `seconds * nextLevelXp / 1152000 * Rate.Rest.InGame`. At level 1
(400 XP to the next level) 8 hours are worth 10 points of pool (5% of the level, halved for the client doubling), 20 such "bubbles" fill the
bar. The pool never exceeds the cap whatever the interval.

### Gain while logged out, and persistence

`character_rest` (characters schema) holds `rest_bonus`, `logout_time` (Unix seconds) and `is_logout_resting`. At login, after the
level and its next-level XP are known, the stored pool plus `offline seconds * nextLevelXp / 1152000` is applied: times
`Rate.Rest.Offline.InTavernOrCity` when the character logged out resting, otherwise times `Rate.Rest.Offline.InWilderness / 4`
(`Player::ComputeRest`, offline branch). A stored second in the future (the clock was moved back) gives no gain; a stored `NaN` or negative
pool counts as 0; a character never saved has no pool.

The state is written by a retained-write queue (`RestWriteQueue`, the pattern of the explored-zones queue):

* at **logout** (`PlayerLoggingOut`), at **shutdown** (`StopAsync` captures the characters still online, because the world's final save
  does not raise the logout event), and **periodically** for every online character (`Rest:SaveIntervalSeconds`, default 300; the time
  is what offline accrual counts from after a crash);
* off the world thread, in order, latest state wins; a write that fails three times is **retained**, never dropped, and is retried by the
  next change, by the login barrier, by a logout and by shutdown;
* **login fails closed** (`CHAR_LOGIN_FAILED`) while an earlier write of the same character is not durable, rather than entering the world
  with a stale pool; shutdown throws naming the characters that are still not durable.

A created character clears a row left by an earlier character with the same id; a deleted character's row is removed with it
(`ICharacterDataCleanup`) and its retained writes are forgotten (`RestDeleteHook`).

### `areatrigger_tavern` and `.reload areatrigger_tavern`

World table `areatrigger_tavern(id)`, loaded at start into `TavernTriggers`, an immutable set behind a holder that a reload swaps whole.
`.reload areatrigger_tavern` (in `.reload all`, as vmangos `all_area` does) reads the table off the world thread, skips rows that name no
known area trigger (and says how many in the result), and replaces the set; an empty table empties it (vmangos clears first) unless
`HotReload:EmptyTables = KeepLoaded`. A character already resting in an inn keeps resting until the next poll finds its trigger gone.

## Options

Configuration section `Rest` (the generated page [configuration reference](../reference/configuration.md) lists them once regenerated):

| Key | Default | Meaning |
|---|---|---|
| `Rest:RateInGame` | 1 | `Rate.Rest.InGame` |
| `Rest:RateOfflineInTavernOrCity` | 1 | `Rate.Rest.Offline.InTavernOrCity` |
| `Rest:RateOfflineInWilderness` | 1 | `Rate.Rest.Offline.InWilderness` (a quarter of the resting gain at 1) |
| `Rest:AccrualIntervalSeconds` | 10 | seconds of resting between two gains (the reference's 10) |
| `Rest:SaveIntervalSeconds` | 300 | seconds between periodic writes of every online character; 0 = logout and shutdown only |

A negative or non-finite rate counts as 0 (no gain). The cap is not an option.

## Schema

* Characters version **26**: `character_rest` (`CharacterRestDataModule.Version`). Its own file; the integrator renumbers the constant.
* World version **29**: `areatrigger_tavern` (`AreaTriggerTavernDataModule.Version`). Its own file; the integrator renumbers the constant.

## Known gaps

* **Content import.** The content importer has no `areatrigger_tavern` table spec (`ContentTableSpecs.cs` is not part of this lane), so the
  table is empty until it is filled by SQL. With it empty, only capital cities give rest.
* **Login inside an inn.** The rest type is not restored at login (the reference does not restore it either: it restores the flag but not
  the type and the trigger, so the gain stays frozen). A character that logs in inside an inn rests again when it re-enters the trigger or
  enters a capital; the offline gain still uses the stored resting flag.
* **Position poll** instead of a check on movement (above), and the outdoor test uses the map's collision data: without it every position is
  outdoors, so an inn's rest ends as soon as the character is outside the trigger volume.
* **Cluster transfers.** A character moving between servers keeps its pool in memory on neither side; it is saved at logout of the first
  and loaded at login of the second (no special handling was added).
* **Config limits.** `Rest:*` binds at start; there is no live reload for it (`.reload config` does not list it).

## Unverified

* The 1.12.1 client's own rendering of the rested state (the resting icon from `PLAYER_FLAGS_RESTING`, the bar from
  `PLAYER_REST_STATE_EXPERIENCE` and `PLAYER_BYTES_2` byte 3) is taken from the reference and was not observed with a client.
* That the client doubles the value it is sent is the reference's comment (`Player::ComputeRest`), not observed.
* vmangos' own rest numbers, rates and offline rule (this lane follows mangos): the vmangos source was not available.

## Tests

`tests/ArcaneCore.Game.Tests/Progression/RestServiceTests.cs`, `KillRewardsTests.cs` (kill XP doubled from the pool);
`tests/ArcaneCore.World.Tests/Progression/RestFeatureTests.cs`, `RestWriteQueueTests.cs`, `Reload/TavernReloadTests.cs`;
`tests/ArcaneCore.Data.Tests/CharacterRestAndRenameStoreTests.cs` (every engine).
