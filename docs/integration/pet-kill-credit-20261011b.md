# Kill credit escalation implementation — 2026-10-11

This supersedes the outstanding behavior limitations and completion claim in
`pet-kill-credit-20261011.md` for the changes below. Base: `51c731b2` on `claude/w22-pet-xp`.
Contract: `<lanes-root>/_wf/specs/kill-credit.md`; reference: vmangos `0e3ff01`.
Logs: `<lanes-root>/_codex/w6/runs/pet-xp-r2/`.

## Coordinator action before review #3

**Merge `ebdd4a37` into the lane after reviewing/committing this diff.** The user explicitly prohibited a merge commit by this
worker and directed implementation on the current tree. No merge, rebase, commit, push, stash, or Git configuration change was
performed. A read-only `git diff --name-only 9bb054e5..ebdd4a37 -- <the eight production/test paths changed here>` returned no
overlapping paths. These results qualify the current lane tree, not a merged main tree.

## Root causes and production changes

1. **Failed damage-share checks restored group credit and created loot.** vmangos `Unit::Kill`
   (`src/game/Objects/Unit.cpp:981–1008`) assigns `pGroupTap` only inside the allowed-share branch. Failure retains only the
   killing player's `pPlayerTap`; `Player::RewardSinglePlayerAtKill` (`Player.cpp:19959–19982`) grants solo reputation, quest
   credit, and applicable racial-leader/civilian honor, but zero XP (`Formulas.h:152`, `Creature.h:556–566`).
   `KillRewards.ResolveTap` now returns `(killingPlayer, null)` immediately on failure. Its progression, reputation, and quest
   consumers were read and verified to use the single-player branch; they were not refactored.
2. **Loot access and skinning survived a failed share check.** `LootService.OnCreatureKilled` now returns without generating a bag.
   vmangos still fills a bag (`Unit.cpp:1082–1091`), but the cleared recipient makes it inaccessible
   (`Player::IsAllowedToLoot`, `Player.cpp:15253–15278`) and masks LOOTABLE for viewers (`Object.cpp:797–798`). No recipient means
   no Skinnable flag (`Creature.cpp:2274–2277`). Generating no bag preserves those observations and avoids the empty-bag
   `CreatureLootedOut` shortcut that would shorten normal corpse decay. **BG-group caveat:** vmangos's special battleground
   group loot allowance (`Player.cpp:15273–15274`) is outside this lane; the no-bag choice does not implement that exception.
3. **Honor independently restored the current group.** `HonorKillRewards.OnUnitKilled` now selects the spec's four explicit
   cases: failed share → solo killing player; untapped allowed kill → killer/current group; provisional pet tap → nobody;
   player tap → original player and tap-time group, falling back to current group only without a tap group. The private reward
   method consumes the chosen group without resolving it again. The unused public overload and nullable-tapper branch were removed.
   References: `Unit.cpp:989–999, 1007–1008, 1020, 1094–1097`; `Group.cpp:2295–2300`.
4. **Forced despawn bypassed the death event, leaving a stale honor tap.** Respawn formerly cleared GUID/group fields directly,
   leaving `LootTapRevision` unchanged. It now calls `ClearLootRecipient`, resets damage-origin state, and explicitly clears
   Skinnable, matching `Creature::SetDeathState(JUST_ALIVED)` (`Creature.cpp:2306, 2324`). The revision invalidates the old honor
   snapshot even when `UnitKilled` never fired. `RecordTap` is unchanged.
5. **Direct `MapCombat.Kill` health counting remains unchanged**, as explicitly decided by the spec. Its remarks now document
   the deviation: vmangos `Unit::Kill` counts no damage; ArcaneCore treats a direct call with a killer as a lethal remaining-health
   blow. For Twin Emperors (`boss_twinemperors.cpp:316`), this can allow the brother's rewards when players dealt 35% or less of
   his earlier damage, whereas vmangos would deny them. No runtime logic in MapCombat changed.

## Tests first: observed RED

Baseline build passed with zero warnings/errors. Before adding tests, the original classes passed **18 World / 33 Game**
(`build-baseline.log`, `world-baseline.log`, `game-baseline.log`). After adding only the two test-file changes,
`git diff --exit-code -- src` confirmed production was unchanged; the RED build passed with zero warnings/errors.

The fixture now registers item **783**, copied from `Skills/SkinningFidelityWorldTests.cs`, through `ItemTestContent.Use`, and
provides a 100%-chance skinning row under loot entry `960204`. The existing 40%-share guard-finish control asserts **Skinnable=true**
and passed on unchanged production. Thus negative skinning assertions exercise a real loaded skinning template.

`world-red.log`: **20 passed, 7 failed** out of 27.

| Case | Observed on unchanged production |
|---|---|
| Solo player/pet finish at 30% and 35% (four cases) | XP 0, reputation +10, quest credit true; **bag recipient Sharesolo, Skinnable=true, dynamic flags=0x1**, despite shareAllowed=false and XP modifier=0. |
| Grouped player finish at 30% and 35% (two cases) | Both Sharelead and Sharemem got **+10 reputation and quest credit**; XP 0 for both; **bag recipients Sharelead,Sharemem; Skinnable=true; flags=0x1**. |
| Exactly 35% boundary (failing first half) | Shareedge got an accessible bag and Skinnable=true; shareAllowed=false and XP modifier=0. The 36% half was not reached in RED. |
| Controls | Grouped 40% kill, forced-despawn/respawn World credit, and the 40% Skinnable positive control all passed. |

`game-red.log`: **33 passed, 4 failed** out of 37.

| Case | Observed on unchanged production |
|---|---|
| Player finishing at 35% | Killer honor **488**, group member honor **488** (expected member 0). |
| Owned-unit finishing at 35% | Owner honor **488**, group member honor **488** (expected member 0). |
| ForcedDespawn → ForceRespawn → new killer | Tap revision **0 → 0**; old tapper honor **488**, new killer **0**. |
| Tapper changes groups | Tapper **488**, original group member **0**, new group member **488** (expected original 488, new 0). |

The general Game fixture defaults to 55 HP; the new threshold tests explicitly use 100 HP, avoiding integer rounding that
would otherwise turn the intended 35% case into a passing share. The owned-unit helper also sets the actual owner GUID so
damage provenance uses the production owner field. Its diagnostic counters print zero **after death**, because combat stop
resets them; the submitted hits are 65 and 35 and the tests independently assert `KillCreditAllowed == false`.
Every expected-RED case failed; no assertion was weakened or timeout lengthened.

## Section 7 verification

* **Skinnable setter/enum:** compiled successfully with zero warnings/errors. The World respawn control asserts that a deliberately
  stale Skinnable flag is cleared, and the CreatureRespawn filter passed 55 tests.
* **ForcedDespawn/ForceRespawn APIs:** observed Dead → Alive in both the World control and Game regression on the existing
  DB-spawn objects. No timed-update fallback was needed; the objects remain available for `ForceRespawn`.
* **Skinning item exists:** ID 783 is explicitly registered in the fixture's item store, using the reference test's registration
  pattern. The skinning row is loaded and the positive-control flag passed both before and after the fix.
* **DB-spawn position/range:** the World forced-respawn control passed at `(-8940, -132, 83.5)`, including B's XP, reputation,
  quest credit, and loot-recipient assertions, with A receiving none. No coordinate substitution was needed.
* **Source locations/main overlap:** references and consumers were re-read at the actual `51c731b2` tree. The targeted main
  diff is empty. The merge remains a coordinator action, as required by the user; no merged-tree claim is made.

## GREEN verification

All section 7 items above are resolved. No C# files were edited after the final build began.

* Final Release build: **0 warnings, 0 errors, exit 0** (`build-green.log`).
* Artifact check (`artifact-freshness.log`): latest C# edit `2026-10-11T17:19:03.9498916Z`; Game DLL and both copies
  `17:19:37.2969833Z`; Game.Tests DLL `17:20:09.9781588Z`; World.Tests DLL `17:20:44.1575494Z`. Both copies match the built
  Game DLL's SHA-256 `23AEFFEF22A775D5668C2FEE0D0D7F2ED85682CD09FEB58A7CFBEF5F18A3D92B`.
* Focused World class: **27 passed, exit 0** (`world-green.log`). Failed-share cases now report no bag, Skinnable=false,
  dynamic flags=0, and group member XP=0/reputation=0/quest=false. The 36% boundary case reaches its 18-XP assertion and passes.
  The 40% Skinnable positive control still passes.
* Focused honor class: **37 passed, exit 0** (`game-green.log`). All four observed RED cases are GREEN. Failed-share player/pet
  kills now pay the killer/owner 488 and the group member 0. Respawn increments the revision **0 → 1**, paying the old tapper 0
  and new killer 488. Regrouping pays the original member 488 and the new member 0.
* CreatureRespawn filter: **55 passed, exit 0** (`game-respawn.log`).
* Expanded World filter (Progression/Honor/Loot/Reputation/Quest/Respawn): **559 passed, 5 skipped, exit 0**
  (`world-namespaces.log`). The broad substring filter also selects four opt-in live playerbot chat tests and the imported
  Ashi quest-event fixture; those five were skipped and were not changed by this lane.
* Full Game suite: **8,528 passed, 4 skipped, exit 0** (`game-all.log`). The unchanged skips are the native VMap oracle,
  optional ClassicDB Wailing Caverns escort fixture, and opt-in GC/tick-cadence measurements.
* Source checks: only `Creature.LootRecipient.cs:19` assigns `LootTapPlayerGuid = default`; `RewardCreatureKill` has one
  private definition and one call. `git diff --check` exits 0 (Git's LF/CRLF conversion notices are unchanged).

Exact commands (PowerShell, from the lane worktree):

```powershell
$env:ARCANECORE_TEST_TERRAIN_DIR='<data-root>/terrain-5875'
$env:ARCANECORE_TEST_DBC_DIR='<data-root>/client-dbc-5875'
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter FullyQualifiedName~OwnedKillerRewardTests --logger 'console;verbosity=detailed'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~HonorKillRewardsTests --logger 'console;verbosity=detailed'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~CreatureRespawn
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~Progression|FullyQualifiedName~Honor|FullyQualifiedName~Loot|FullyQualifiedName~Reputation|FullyQualifiedName~Quest|FullyQualifiedName~Respawn'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build
git diff --check
git grep -n 'LootTapPlayerGuid = default' -- src
git grep -n 'RewardCreatureKill(' -- src tests
```

The baseline used the same focused filters without the detailed logger. RED used the detailed commands above on unchanged
production. All three builds passed the read-only Windows physical-memory check before starting (4.10, 7.66, and 8.07 GiB free,
respectively); none was started below the user's 3-GiB floor. No config options changed and no generated config docs were needed.

## Files changed

* `src/ArcaneCore.Game/Progression/KillRewards.cs`
* `src/ArcaneCore.Game/Loot/LootService.cs`
* `src/ArcaneCore.Game/Honor/HonorKillRewards.cs`
* `src/ArcaneCore.Game/Creatures/CreatureMapSystem.Lifecycle.cs`
* `src/ArcaneCore.Game/Creatures/Creature.LootRecipient.cs` (summary only)
* `src/ArcaneCore.Game/Combat/MapCombat.Melee.cs` (remarks only)
* `tests/ArcaneCore.World.Tests/Progression/OwnedKillerRewardTests.cs`
* `tests/ArcaneCore.Game.Tests/Honor/HonorKillRewardsTests.cs`
* This report.

## Decided follow-ups and limits

* Honor retains its own `_taps` snapshot. Unifying tap recording in `MapCombat.DealDamage` with Game-layer group lookup remains
  a separate follow-up. Tap-time group precedence is fixed here.
* `QuestObjectiveAdapter`'s solo-credit alive gate remains unchanged: vmangos's solo reward method has no such check. This
  pre-existing difference is a separate follow-up.
* Original player-recipient lookup is still map-local. Direct-Kill counting and the BG loot exception are documented above.
* Spell/periodic regressions use the production damage sink, not complete DBC spell scheduling. No real-client acceptance or
  live-server/database mutation was performed.
