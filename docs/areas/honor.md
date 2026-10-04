# Honor and PvP ranks

The vanilla 1.12 honor system after vmangos (primary reference, `D:\refs\vmangos`), with mangos-classic, wow_messages, classic-db and
the vmangos wiki used for the differences and the data. Reference files are read for verification only; no code or data is copied.
Code: `src/ArcaneCore.Game/Honor/` (rules and world-thread state), `src/ArcaneCore.Kernel/Honor/` (store contract),
`src/ArcaneCore.Data/Honor/` (characters schema), `src/ArcaneCore.World/Honor/` (daemon features, handlers, commands). Shared-file edits and the
schema renumbering note: [integration/honor.md](../integration/honor.md).

Evidence: automated tests against the references cited below, loopback clients through the real world host, and SQLite for the stores.
**Not verified against a real 1.12.1 client** (charter 1.3); the checklist at the end is what a client pass has to confirm.
**MariaDB and PostgreSQL were not exercised on this machine** (no server): the store tests are provider theories that run on hosted CI, and
the SQLite results do not prove the other two (see "Hosted CI").

## Delivered scope

| Part | Where | Reference |
|---|---|---|
| Rank thresholds (negative ranks, positive ranks 5-18, internal vs visual rank), rank bar byte | `HonorRanks` | vmangos `HonorMgr.cpp:909-913, 942-1033` |
| Honorable kill points (level factor, same-victim diminishing returns 10 % per kill, victim rank exponent, float/double promotion reproduced), dishonorable points, racial leader 488 | `HonorKillPoints` | `Formulas.h:179-224`, `HonorMgr.cpp:1035-1063`, `HonorMgr.h:145-152` |
| Weekly standing maths: 1.12 break-point table, curve (BRK/FX/FY), earning, 20 % decay, level caps | `HonorStandings` | `HonorMgr.cpp:35-46, 468-615` |
| Weekly planner: ranked vs inactive split (15 honorable kills, account), per faction ranking, highest-rank rule, outstanding-period loop, last maintenance weekday, HCR report text | `HonorMaintenancePlanner` | `HonorMgr.cpp:104-183, 238-326, 328-466, 617-669`, `World.h:735-741` |
| Per-player state, `Add`/`Update`/`Reset`, honor tab update fields (today/yesterday/this week/last week/lifetime, rank, highest rank, rank bar, City Protector byte), SMSG_PVP_CREDIT | `HonorService`, `HonorState`, `HonorPackets` | `HonorMgr.cpp:671-940, 1065-1097` |
| Kill credit from the damage ledger (killing blow included), group pooling and split with the group rate, gray level, other team, living and in reach; creature honor (civilian dishonor, racial leader), Honorless Target aura check | `PvpDamageLedger`, `HonorKillRewards`, `MapCombat.DamageTaken` | `Player.cpp:19943-19957, 21810-21919`, `Unit.cpp:342-344, 762-829`, `Group.cpp:2295-2300` |
| `SPELL_EFFECT_ADD_HONOR` (45) and the Honorless Target aura (159) | `HonorSpellEffects` | `SpellEffects.cpp:2979-2988` |
| Equip rank gate (highest rank) | `HonorItemRequirements` | `Player.cpp:10078-10081` |
| Vendor rank gate (current rank and required level), honor discount (vendor and flight master), additive with Honored | `QuestNpcServices.Vendor.cs`, `HonorPriceDiscount` | `Player.cpp:18431-18439, 19470-19510` |
| WorldDefense channel speech (internal rank 15) and the rank byte in channel messages; the PvP_RANK condition (internal rank) | `HonorHooks.InternalRank`, `Channel.Say`, `ConditionFeature` | `Channel.cpp:636-648, 670`; classic-db / mangos-classic condition type 11 |
| Persistence: characters schema (state, contribution rows, maintenance row), deletion cleanup, ordered write queue with retention, login barrier, reuse-of-id cleanup | `CharacterHonorDataModule`, `EfHonorStore`, `HonorWriteQueue`, `HonorFeature`, `HonorCharacterDeleteHook` | `CharacterHandler.cpp:82,90`, `HonorMgr.cpp:638-790` |
| PvP flag and desire persisted at logout, restored at login | `HonorService.CapturePvpFlags/RestorePvpFlags` | `Player.cpp:14674-14677, 16337` |
| Weekly job: startup only (default) or minute tick (Live), one DML transaction per week, online players updated in memory, optional City Protector and HCR file | `HonorMaintenanceRunner`, `HonorMaintenanceFeature` | `HonorMgr.cpp:238-326, 617-669`, `World.cpp:2121-2127` |
| `CMSG_INSPECT`, `MSG_INSPECT_HONOR_STATS` (50 bytes) | `InspectHandlers`, `HonorPackets.InspectHonorStats` | `MiscHandler.cpp:943-1036`, wow_messages `msg_inspect_honor_stats_server.wowm` |
| `.honor add|addkill|show|setrp|reset`, `.modify honor` | `HonorCommands` | `CharacterCommands.cpp:2321-2570`, `Chat.cpp:467-475, 596` |

## Not delivered (limits)

- **Battlegrounds and their bonus honor.** The seam is `IHonorAwards.Add(player, cp, HonorKind.Bonus, source)` (implemented by `HonorService`); the
  battlegrounds lane calls it.
- **PvP flag completion** (the `overriding` form of `UpdatePvP`, propagation to pets/totems, group status, the contested flag and its 30 s timer, assist
  pulses, `ForcePvp` for PvP-type quests) and **taxi and Honorless Target rules** (flight start clears the flag, landing in an enforced area casts spell
  2479): not done. They edit `MapCombat.Death.cs`/`Melee.cs` regions other wave-4 lanes (threat, aura engine, graveyards) also touch. The existing flag logic
  is unchanged; only its two persisted bits are new. The teleport-arrival Honorless cast additionally needs an arrival hook `TeleportService` does not have.
- **Hall of Legends / Champions' Hall rank gate** (areatriggers 2527, 2532, visual rank 6): present in mangos-classic, absent in vmangos, and there is no
  area-trigger requirement framework on the base. A later lane can read `IPlayerHonor.VisualRank`.
- **Creature honor follows the tap, with a simplified tap.** `HonorKillRewards` taps a non-pet creature on the first damage from a player (or a player's pet
  or totem, credited to the controlling player), as `Unit::DealDamage` does (`Unit.cpp:804-807`), and `Unit::Kill` rewards that tapper or its group whoever lands
  the blow (`Unit.cpp:988-1001, 1076-1079`). Not modelled: `IsLootAllowedDueToDamageOrigin` (the share of damage done by players), the pet-to-owner tap swap
  (`Unit.cpp:809-820`), and the tap's group id (the tapper's current group is used while it still contains the tapper, otherwise the group as it was at the
  first hit). An evade or respawn clears the tap in vmangos (`CreatureAI.cpp:344`, `Creature.cpp:2306`); damage dealt to a creature at full health starts a new
  tap here, so a creature healed to full mid-fight also loses its tap. A creature nobody tapped rewards the killer's controlling player (vmangos's pPlayerTap fallback).
  The loot and experience paths still use the killing-blow player (the combat layer has no tap list); that is theirs to change, honor does not depend on it.
- **`IsHonorOrXPTarget`** implements the gray level, totem and pet terms only. The `xp_multiplier == 0` and `UNIT_STATE_NO_KILL_REWARD` terms have no data here
  (`CreatureContent` carries neither). The pet and totem terms are exercised only through the code path, no test builds a summoned totem.
- **CHARACTER_FLAG_HAS_PVP_RANK** in the character list is not delivered (ArcaneCore has no `character_flags` column).
- **The honor tab refreshes on `Add`, login, GM commands and the weekly job**, not at the game-day rollover or before every autosave (the same as vmangos,
  which refreshes only on those events and before a save). The derived fields are not persisted.
- **Contribution rows are written at least once, not exactly once.** A batch is one transaction; if a commit succeeds but its acknowledgement is lost, the
  retry appends the batch again. A write queued while the weekly calculation runs can overwrite an online player's new numbers with older ones (the job
  re-queues the post-maintenance state to narrow that window).
- `.reset honor` (SEC_DEVELOPER, a separate vmangos command) is not provided; `.honor reset` is the same `HonorMgr::Reset`.

## Schema

Characters schema **version 23** (allocated as 21 by the lane; renumbered at wave-4 integration) (`CharacterHonorDataModule.Version`, the next free number in this tree; the integrator renumbers it, tests use the constant).
Three new tables, nothing existing changes:

| Table | Columns | Replaces (vmangos) |
|---|---|---|
| `character_honor` | `character_id` PK, `rank_points` float, `highest_rank`, `standing`, `last_week_hk`, `last_week_cp`, `stored_hk`, `stored_dk`, `pvp_flags`, `city_protector` | `characters.honor_*`, `extra_flags` bit 0x0400 |
| `character_honor_cp` | `id` bigint identity PK, `character_id`, `victim_type`, `victim_id`, `cp` float, `date`, `type`; indexes `(character_id, date)` and `(date)` | `character_honor_cp` |
| `honor_maintenance` | `id` PK (always 1), `last_day`, `next_day`, `marker` | `saved_variables.honor_*` |

Contribution points and rank points are stored with one decimal, as vmangos writes them with `%.1f`; an exact tie (x.25, x.75) rounds to even.
Every column has an explicit snake_case name (PostgreSQL folds unquoted identifiers). The module implements `ICharacterDataCleanup`, so character deletion
removes its rows in the deletion transaction; a queued, id-conditional removal after the deletion and an explicit removal at creation cover reused ids.

## Configuration (`World:Honor`)

Every default is the retail 1.12 value; a different value is a deliberate deviation. Restart-only.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Off: nothing loads, every consumer treats players as unranked (the behaviour before this lane) |
| `DishonorableKills` | `true` | Civilian kills below gray cost honor (vmangos `CONFIG_BOOL_ENABLE_DK`) |
| `MinHonorKills` | `0` | Honorable kills a week to be ranked; 0 selects 15 (`MIN_HONOR_KILLS_POST_1_10`) |
| `RpDecay` | `0.2` | Weekly rank point decay, clamped to 0..1 |
| `MaintenanceDay` | `3` | Weekday of the weekly calculation (Sunday 0). vmangos code defaults to 4, its configuration text says 3 (Wednesday in EU); the documented value is used, see open questions |
| `TimeZoneOffsetHours` | `0` | One offset drives both the game day and the weekday (vmangos mixes `localtime()` and `TimeZoneOffset`; identical on a UTC server) |
| `PoolSizePerFaction` | `0` | Standing pool size; 0 uses the number of ranked players |
| `CityProtector` | `false` | City Protector titles (vmangos default off); assigned from the standings just calculated, vmangos reads the previous ones |
| `RacialLeaderExcludedEntries` | empty | Creature entries that are never racial leaders (classic-db flags Kaldorei Infantry 15423, 30 spawns, as a leader) |
| `MaintenanceMode` | `Startup` | `Startup`: only at process start, like vmangos (which flags the work and restarts, `HonorMgr.cpp:617-633`). `Live`: in-process, checked every minute (deviation, opt-in) |
| `ReportDirectory` | empty | Writes the vmangos "HCR" calculation report there |

## Deviations from vmangos (all deliberate and documented)

- `World:Honor:MaintenanceMode = Live` (opt-in, default `Startup`) runs the weekly calculation in process with players online; the results are the same, only the timing differs from vmangos' flag-and-restart flow. Online players are then updated in memory exactly as the transaction updated the rows; a login is serialised against the transaction (`HonorFeature.WeekGate`) and each loaded state remembers the week begin day it was loaded under, so a week already in a freshly loaded row is never added a second time. A player whose login read the old row but who is not yet in the world when the update runs keeps the old numbers until the next maintenance or restart.
- Honor storage is its own tables instead of columns of `characters`/`saved_variables`.
- `.honor show` names ranks by internal rank (vmangos indexes with the visual rank and prints wrong names; a negative rank wraps and prints "CrashAlert").
- A negative victim visual rank is clamped to 0 when computing kill points (vmangos converts it to an unsigned index and the exponential overflows to
  infinity, an undefined result).
- The rank bar for rank points outside the rank's band is the low byte of the truncated integer (what the C++ produces on x86; the cast is undefined in C++).
  Reachable only through GM commands.
- Standing ties are broken by character id (vmangos `std::sort` is unstable).
- WorldDefense: vmangos needs internal rank 15, mangos-classic visual rank 11; these are the same rank under two numberings, so there is no difference to choose.

## Hosted CI

Every store and schema theory runs over `TestDatabases.AvailableProviders()`. On this machine only SQLite exists. The tests are written against real
provider semantics: the weekly transaction is DML only (rolls back on MariaDB/InnoDB and PostgreSQL), no advisory lock is taken (Npgsql pooling returns the
same physical connection, which would make one re-entrant), the table creation is re-runnable after a half-applied MariaDB upgrade (a test drops tables
to simulate it), unsigned values above `int.MaxValue` round-trip, the identity key and the indexes are checked in the catalog on a fresh and an upgraded
database. **They have not been run on MariaDB or PostgreSQL.**

## Open questions

- `MaintenanceDay`: 3 (documented) or 4 (vmangos code)? Retail vanilla ran the weekly reset on the maintenance day (Tuesday US, Wednesday EU).
- Did retail 1.12 award City Protector titles? (vmangos default is off.)
- Keep Kaldorei Infantry (15423) as a racial leader (classic-db data, 488 honor each) or add it to the exclusion list?
- Vendor prices: the NPC services floor the discounted price, vmangos (`Player.cpp:18443`) adds 0.5 before truncating. Existing behaviour from an earlier
  wave, noticed here; not changed by this lane.

## Real-client checklist

Honor tab fields (the four `*_KILLS` fields are written as full u32 as vmangos does; wow_messages types them as two u16, identical below 65536) and the rank
bar; the SMSG_PVP_CREDIT chat text including the "at least Scout" substitution for an unranked victim; the rank name in `MSG_INSPECT_HONOR_STATS`; the
WorldDefense speaker rank display; rank-gated vendor lists and purchases.

## Tests

Game: `HonorRanksTests`, `HonorKillPointsTests`, `HonorStandingsTests`, `HonorMaintenancePlannerTests` (golden vectors from the references),
`HonorServiceTests`, `HonorPacketsTests`, `HonorItemRequirementsTests`, `PvpDamageLedgerTests`, `HonorKillRewardsTests`, `HonorSpellEffectsTests`,
`DamageTakenEventTests` (the killing blow is in the history; with the event restricted to non-lethal damage eleven tests failed), `VendorHonorGateTests`,
`HonorPriceDiscountTests`, `WorldDefenseRankTests`. Data: `HonorStoreTests`, `HonorMaintenanceStoreTests`, `HonorSchemaUpgradeTests`, the integrated schema list.
World: `HonorWorldTests`, `HonorWriteQueueTests`, `HonorMaintenanceRunnerTests`, `HonorConsumersTests`, `InspectHandlerTests`, `HonorCommandTests`,
`HonorEndToEndTests`. The persistence, queue and job tests were repeated 12-20 times with no failure; none waits on a fixed timer.
