# Pet kill credit continuation — 2026-10-11

Worktree: `claude/w22-pet-xp`, starting at WIP `32ea04ff`; reviewed predecessor `f514b7dc`.
Reference checkout: `<refs-root>/vmangos`, revision `0e3ff01` (read only).
Nothing committed or pushed. No live databases, profile files, or server ports were used.

## Reference rules and root causes

* `src/game/Objects/Unit.cpp`, `Unit::DealDamage`, lines 788–819: count damage before the lethal check; player ownership/charmer GUID or self-damage determines player damage. The first eligible damage taps the creature. A player who owns the provisional pet tap, or belongs to its recorded group, takes over that tap on a hit.
* `src/game/Objects/Creature.h`, `CountDamageTaken`, `IsLootAllowedDueToDamageOrigin`, and `GetXPModifierDueToDamageOrigin`, lines 534–566: eligibility is **strictly greater than 35%** of total counted damage (`65 * playerDamage > 35 * nonPlayerDamage`). Eligible XP is multiplied by the player damage fraction. The raid-corpse static flag bypasses this check and multiplier. The WIP already implemented these counters and their kill snapshot; this continuation retains and verifies them.
* `src/game/Objects/Creature.cpp`, `Creature::SetLootRecipient`, lines 1583–1612: an active pet stores its own GUID provisionally; other controlled units store their player's GUID. `Creature::IsTappedBy`, immediately following, recognizes the viewer's active pet as its tap.
* `src/game/Objects/Unit.cpp`, `Unit::Kill`, lines 981–1098: a still-provisional pet tap clears `pPlayerTap`; the outer `allowLoot && pPlayerTap && pPlayerTap != pVictim` block gates **both** loot generation and group rewards. The WIP incorrectly retained group rewards when no player tapper existed. Its old test encoded that error. Pet-only damage therefore rewards nobody even with the owner present, until a player takes over the tap; an absent owner cannot claim it either.
* `src/game/AI/CreatureAI.cpp`, `CreatureAI::EnterEvadeMode`: explicitly clear the loot recipient. Honor's independent tap snapshot previously inferred reset from full health. A real evade without full healing retained the previous player's honor tap; a heal to full incorrectly dropped it. A reset revision now invalidates the honor snapshot, and evade uses the common clear method.
* `src/game/Objects/Player.cpp`, `Player::RewardHonorOnDeath`, lines 21848–21919: use original player teams and damage history, irrespective of charm faction. Environmental self-damage does not replace the damaging player's ledger contribution. Existing honor behavior passed new tests for enemy and same-team charmed victims, through both combat and environmental death.

## Changes from the starting WIP

* `src/ArcaneCore.Game/Progression/KillRewards.cs`: require a player tapper before returning group credit; require an actual pet object for provisional active-pet taps.
* `src/ArcaneCore.Game/Loot/LootService.cs`: suppress corpse loot for a provisional pet tap, including grouped owners; display the owner's provisional pet tap correctly.
* `src/ArcaneCore.Game/Creatures/Creature.LootRecipient.cs` and `CreatureMapSystem.Evade.cs`: increment the reset revision and route evade clears through it while resetting damage counters. Honor removes the snapshot on death; the inherited respawn path resets damage and the kill snapshot.
* `src/ArcaneCore.Game/Honor/HonorKillRewards.cs`: respect the player-tapper gate and explicit resets, while preserving the reference's killing-player fallback for an unavailable player recipient.
* `src/ArcaneCore.Game/Reputation/ReputationKillCredit.cs`: correct the provisional-tap documentation; reputation and quest rewards already consume the shared resolver.
* `tests/ArcaneCore.World.Tests/Progression/OwnedKillerRewardTests.cs`: real pet GUID/type fixture; group takeover/no-takeover, 35% boundary, exact 40%-share XP, melee/spell/periodic damage sink paths, owner gone, tap visibility, and evade counters.
* `tests/ArcaneCore.Game.Tests/Honor/HonorKillRewardsTests.cs`: grouped provisional pet tap, evade without a full heal, healing without evade, and charmed-player combat/environmental honor. The old fake-evade test now invokes actual evade rather than treating a heal as an evade; its reward assertions remain unchanged.

## RED evidence

Logs are in `<lanes-root>/_codex/w6/runs/pet-xp/`.

* `world-red.log`: 18 tests, 17 passed, 1 failed. `AGroupMembersActivePet_RequiresAPlayerToTakeOverTheTap(false)` observed **25 XP** for the group leader when zero was required. This used the rebuilt World.Tests DLL and unmodified WIP production binaries.
* `game-red.log`: 30 tests, 28 passed, 2 failed. `Provisional_pet_tap_does_not_reward_group_honor` and `Evade_without_a_full_heal_releases_the_honor_tap` each observed an unwanted **488 honor** record. The charmed-victim environmental cases passed. The tested Game DLL was the pre-fix WIP binary.
* The first build failed because the new environmental test imported the folder name as a namespace. Corrected to `ArcaneCore.Game.Locomotion`; `build-red-2.log` then recorded a successful build with zero warnings/errors. This compile error is not counted as behavioral RED.
* The inherited damage-share fix was already present at HEAD. No claim is made that this session executed its historical failure at `f514b7dc`.

## Verification

All commands ran from `<lanes-root>/w22-pet-xp` in PowerShell, with:

```powershell
$env:ARCANECORE_TEST_TERRAIN_DIR='<data-root>/terrain-5875'
$env:ARCANECORE_TEST_DBC_DIR='<data-root>/client-dbc-5875'
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --filter FullyQualifiedName~HonorKillRewardsTests
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter FullyQualifiedName~OwnedKillerRewardTests
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~Progression|FullyQualifiedName~Honor|FullyQualifiedName~Loot|FullyQualifiedName~Reputation'
dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build
git diff --check
```

* Final build: **passed, 0 warnings, 0 errors**, exit 0 (`build-final.log`). This followed the last C# edit. Test DLL modification times were checked against all changed C# files; both test output folders contain the newly built Game DLL.
* Focused Game honor class: **33 passed**, exit 0 (`game-focused-green.log`).
* Focused World owned-kill class: **18 passed**, exit 0 (`world-focused-green.log`).
* World requested namespace filter: **270 passed**, exit 0 (`world-namespaces.log`).
* Full Game suite: **8,524 passed, 4 skipped, 0 failed** (`game-all.log`). Skips are the native VMap oracle, the optional ClassicDB Wailing Caverns escort fixture, and the opt-in GC/tick-cadence measurement tests. These were not altered by this lane.
* `git diff --check`: exit 0 (only Git's existing LF/CRLF conversion notices).
* CIM memory telemetry was denied by the sandbox. A read-only `GlobalMemoryStatusEx` probe (`memory_check.py` in the output folder) supplied physical RAM; builds were deferred below 3 GiB and launched only after a passing check. No other lane's processes were stopped.
* No configuration options changed, so generated configuration docs did not require regeneration.

The focused GREEN runs cover the three observed RED failures. Additional boundary/path tests passed: 30% and exactly 35% guard-assisted kills give no credit; 40% gives 20 XP from the level-1 base of 50 plus eligible quest/reputation/loot; pets finish through melee, spell and periodic sinks after a player tap; totems, guardians and charms credit their player; an owner removed from the map receives nothing from a provisional pet tap; evade clears prior damage; charm never substitutes a player's temporary faction for their original honor team.

## Limits

* Spell and periodic tests exercise the production `MapCombatDamageSink` with deterministic damage; they do not assert a DBC spell's hit roll or advance an aura schedule.
* No real-client acceptance was performed.
* Existing map-local original-recipient lookup remains (vmangos can find the original player globally). Honor still uses its existing group snapshot/current-group preference; changing groups during the fight is outside these tests. Creator-specific loot exceptions and map-wide loot-disable policy are not newly implemented.
