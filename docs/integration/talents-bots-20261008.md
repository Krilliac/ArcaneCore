# Talents and bots integration (2026-10-08)

Branch `claude/integrate-20261007`, base `92966fdd` (main, live since the wave-4 deploy from `D:/ArcaneCore-lanes/_deploy/integrated-w4b`).
Seven reviewed lanes and four fix branches were merged with `--no-ff` in the order below, then the `World:Playerbots:MovementPackets`
option and one merge fix. Logs: `D:/ArcaneCore-lanes/_logs/w5-talents-bots/`. Nothing was pushed or deployed. The live servers, the
live profile and its databases were only read, to copy them.

## Merges (first-parent order)

| Commit | Merge | Tip | Conflicts and how they were resolved |
|---|---|---|---|
| 88f5bb18 | tb-t1-talent-lifecycle | e961e1da | - |
| 86ba02be | tb-t2-talent-procs-coverage | cb684126 | - (review item applied in ace8f1cc, below) |
| 6281a1b7 | tb-t3-talent-class-scripts-pets | 31368473 | - |
| 28e12a95 | tb-b1-party-master-control | 20e15a8e | - (its reference docs regenerated in 0885ad65) |
| 9db7c7ed | tb-b2-class-combat-rotations | b8cf36f1 | - |
| 5ef7370b | tb-b3-bot-progression | e35a62bc | - |
| 5a910831 | tb-b4-dungeon-movement-recovery | 41004590 | - (semantic interaction with b1: fixed in 6b9e80f5) |
| 9bced288 | fix-gmrename-flake | b0fcc773 | - |
| 8f7f9183 | fix-world-flakes-3 | d05d1ff2 | `ClassScriptScenarioTests.cs` |
| c95cf69f | fix-scenario-wait | 4a9a16d7 | - |
| 960e10bf | fix-destinations-flake | 6978f8dd | - |

The one textual conflict came from t3 and fix-world-flakes-3 fixing the same flake in different ways. In the Seal of Righteousness /
Judgement duel, Judgement of Righteousness can crit. t3 let the scenario accept either amount, using its own hit-info decoder.
world-flakes-3 pins the crit roll (`FixedSpellCritRules`) and runs one test for each outcome, each asserting an exact amount and the
crit flag (`SpellDamageView.HitInfo`/`Critical`). The merge keeps t3's Swiftmend scenario and its `Druid` using, and takes
world-flakes-3's pinned version of the seal duel. t3's `IsCritical` helper was dropped because `ScenarioClassDecoders.SpellDamage` now
carries the hit info. `2e8569ee` is only the blank line the resolution lost.

## Integration commits

* `ace8f1cc` Docs: t2's review item. `docs/areas/aura-engine.md` counts now read Handler 139, Referenced 26, Unsupported 27,
  NotAnAura 1. Rows 65 (`ModCastingSpeedNotStack`, consumer `BuiltInProcHandlers.cs`) and 183 (`ModCriticalThreat`, consumer
  `SpellThreatModifiers.cs`) say Referenced, as `AuraSupportBaseline.cs` does.
* `7b12ecce` Playerbots: `World:Playerbots:MovementPackets` (below).
* `0885ad65` Docs regeneration (`ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests -c Release --filter Docs`, 76 passed):
  `configuration.md` gains the six `World:Playerbots:Party:*` rows (b1) and the `MovementPackets` row (live). `gm-commands.md` gains
  `.playerbot invite` (GameMaster 123, Administrator 181). No line-ending-only change was left.
* `6b9e80f5` A merge-caused failure, b1 x b4. `PlayerbotPartyLootAndDeathTests.AStayingBot_WalksOverToReleaseItsLoot_ThenBackToItsPlace`
  killed the bot's round-robin corpse 12 yards east of the human start. Since b4, a bot reports area triggers like a client. The walk
  crosses MapTestData's "Test shortcut" teleport sphere (10 yards east, radius 3), and the bot is teleported away before it reaches the
  corpse. Evidence: it failed 6 of 6 at the integration tip and at the b4 merge 5a910831, and passed at the b3 merge 5ef7370b. The corpses
  now lie 12 yards south (`KillGoldCreature` takes a `yOffset`). The party tests then passed 50 of 50 on five runs. The product
  behaviour is right: a real client walking into that sphere is teleported too.

## World:Playerbots:MovementPackets

The default `true` keeps today's path. A bot's MSG_MOVE_START_FORWARD, heartbeats and STOP are encoded, dispatched through the opcode
table (`TryManagedAction`) and handled by `MovementHandlers` like any client's.

With `false`, `PlayerbotMotion.Send` calls `WorldSession.TryManagedMovement`, which calls `MovementHandlers.MoveManagedBot`:

* the same session gate and action budget;
* the same admission (no move while teleported or on a taxi, a valid block, the bot is its own mover);
* the block normalised the way the decode would leave it, with fields its flags do not announce set to zero;
* the same `ApplyObserved` (the locomotion observers: falls, under-map, flag authority, hazards, transports);
* the same relay (`RelayOwnMovement`, now shared with `HandleMovement`).

It skips the opcode dispatch and the encode/decode. Acknowledgements and `CMSG_AREATRIGGER` stay client packets in both modes.
`PlayerbotMotion` reads the live `PlayerbotOptions` at every move. `.reload config` changes that object in place: `WorldConfigView`
gained a `Playerbots` side and `WorldConfigKeys` has `World:Playerbots:MovementPackets` as its only live playerbot key. Running bots
switch at their next move.

Tests (`PlayerbotMovementPacketsTests`, `PlayerbotMovementPacketsOffScenarioTests`, `PlayerbotMovementWireTests`, `ConfigReloadTests`):

* RED, before the change: 5 failed of 24.
* Two bots walk one route from one spot in the same world-thread call, one in each mode. The observer receives the same opcodes and
  byte-identical movement blocks from both, and they end at the same position.
* In off mode `ManagedDispatchObserver` (a new test tap in `TryManagedAction`) sees no relayable movement opcode for the bot. The
  on-mode control sees one for each observed packet.
* Turning the option off mid-route takes effect at the next packet.
* The client-consumption wire checks pass with the packets off.
* The key is live and classified, and removing it from the file restores `true`.
* With the packets off, three groups of scenarios pass: dungeon ghost run, stalled corpse run to the spirit healer, and entrance;
  combat-warrior, -hunter and -mage; and progression-quest. These are not vacuous. With the off path stubbed to refuse every move, 4 of
  the 5 fail (the hunter fights without moving).

## Builds and tests

Release build `ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors (DLL timestamps checked after every build).

| Suite | Result (commit) |
|---|---|
| Kernel | 26 passed (0885ad65) |
| Cryptography | 8019 passed (0885ad65) |
| Realm | 276 passed (0885ad65) |
| MockClient | 348 passed (0885ad65) |
| Data | 1291 passed, 10 skipped (0885ad65) |
| Game | 7171 passed, 11 skipped (0885ad65) |
| World | 2862 passed, 1 failed, 7 skipped (0885ad65: the b1 x b4 failure above); then 2863 passed, 7 skipped, twice (6b9e80f5) |
| MockClient self-test | passed, 59 checks (0885ad65 and 6b9e80f5) |

Logs: `tests/run1` (all suites), `tests/run2-world` and `tests/run3-world`. After 0885ad65 only a World test file changed.

## Deploy candidate

`D:/ArcaneCore-lanes/_deploy/integrated-w5`, built with
`dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false --artifacts-path D:/ArcaneCore-lanes/_deploy/integrated-w5` at
6b9e80f5 (0 warnings, 0 errors), with `tools/content` copied in and a `SOURCE.txt`. This document is the only later commit.

## Rehearsal (on copies)

The method is wave 4's, with scripts in `D:/ArcaneCore-lanes/_logs/w5-talents-bots/rehearsal/`.

1. `snapshot.py` copied the live auth and characters databases through the SQLite backup API from mode=ro connections. The live world
   database had no -wal or -shm (nobody held it open), so it was copied as a file (the source's mtime, size and sidecars were unchanged,
   the SHA matched and the integrity check was ok), as `deploy_w4.py`'s own snapshot does in that case.
2. `make_profile.py` built a profile on ports 18085/13724.
3. `start_old.py` started the live build, `integrated-w4b`, on the copies, and all 4 enabled bots started.
4. `deploy_w4.py --artifacts D:\ArcaneCore-lanes\_deploy\integrated-w5 --world-port 18085 --auth-port 13724` ran against that profile
   (`deploy-run1`). It shut the old World down gracefully through the operator, stopped the Realm, ran the arcane-db
   status/upgrade/check, refreshed the content, then started Realm and World from integrated-w5.
   * Result: healthy, every expected log line present, `unknown target map` 0, `not existing zone id` 0, ERROR 0, FATAL 0, WARN 10
     (the same 10 as the wave-4 deploy).
   * Characters 85 and managed bot rows 10, identical. The only per-character row loss is one expiring aura on Duststalker, a running
     bot.
   * Talents load: "Loaded 432 talents in 27 tabs. Talent effects: 1285 of 1357 rank spells are fully handled by the spell system."
   * The 4 enabled bots (Ironwander, Graveweaver, Dawnrover, Duststalker) started.
5. Mirthblade (`DesiredEnabled` 0, `disabled after 3 faults: action: playerbot-recovery-stalled`, a ghost 359.1 yards from its corpse)
   was started with `.playerbot start Mirthblade` through the rehearsal copy of the operator account (`gm_rehearsal.py`, port 13724).
   "Playerbot ok: started". Within 50 seconds it was alive (hp 164/164, `corpse=none`, no fault, `DesiredEnabled` 1).
   * It did not revive at its body. The ghost moved away from the corpse (at +64 s it was 491 yards from it) and came back to life near
     the Coldridge Valley graveyard (-6189, 234). This is b4's stalled-corpse-run path, which uses the spirit healer. Its corpse row was
     consumed.
   * After that it stood still for at least 6 minutes with `goal=Quest target=1718` (Rockjaw Raider) for quest 179.
6. `stop_new.py` stopped the rehearsal World ("World saved and stopped") and Realm. Afterwards nothing listened on 18085/13724, and the
   live 8085/3724 processes were the same ones as before.

## Still open

* Bots that stand still. On the rehearsal, Ironwander (`Vendor 829`), Graveweaver (`Explore 1512`), Dawnrover (`Quest 253`, after a short
  walk) and Mirthblade (`Quest 1718`, after its revive) kept the same position across inspects 45 seconds or more apart, without a fault.
  Only Duststalker kept fighting. The live snapshot already has Ironwander, Graveweaver and Dawnrover at the same goals and positions as
  the wave-4 deploy's status at 06:40, and the old build on the copies did not move them either. So this predates this integration and is
  not caused by it, but it is not fixed by it. It needs a look at why those goals neither progress nor give up (route planning on real
  terrain is the first suspect).
* Mirthblade recovers, but through the spirit healer, not at its body. Why the 359-yard corpse run counts as stalled on the real
  Dun Morogh terrain was not investigated.
* `.playerbot start` gives a bot that was disabled after faults a clean history. If its stall comes back, it will be quarantined again.

Update (branch `claude/bot-stall`, commits 0939f6e5 and b1de410b): the first two items are root-caused and fixed. Replaying
`rehearsal/orig-characters.db` on the real terrain gave one cause per bot: Graveweaver, route corners re-tested with a line of
sight at floor height (the crypt floor blocks it); Dawnrover, the motion's straight line of sight across a route corner (a pillar
in the inn); Ironwander, a vendor refusing to buy a damaged gray item without a repair price, re-sold every 1.5 s; Mirthblade,
512 search nodes (vmangos 2048) to the Rockjaw Raiders, and far goals cut to an unreachable straight-line point, which also
judged its corpse run stuck. Ghosts also walked instead of running. A bot without progress for `World:Playerbots:StallSeconds`
now reports `stalled ...` in `.playerbot list` and gives up that goal. On the replay, after the fixes, the four bots travelled
1240 to 4450 yards in 10 minutes of game time; before, 0 to 504 yards, standing still for 485 to 599 seconds.
Mirthblade's ghost now walks to its body (39 yards) but still takes the spirit healer 60 seconds later: two Frostmane Troll
Whelps (spawns 927 and 931) stand within 25 yards of where the reclaim would revive it, the camped-body rule, unchanged.
