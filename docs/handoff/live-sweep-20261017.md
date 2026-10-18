# ArcaneCore live sweep: handoff, Oct 17, 2026 (written 22:40 CT)

Branch `grok/live-sweep` (Krilliac/ArcaneCore). Sweep started 18:20 CT at 9b39adcf. This handoff was written after 8b46a2a7, which is 144 commits later. Work was coordinated in `D:\ArcaneCore-sweep\` on Nate's PC: LEAD-LOG.md, ISSUES.md, CLAIMS.md, and per-lane `_<lane>` tool folders. Nate stopped the sweep at 22:10 CT. Live keeps running.

## 1. Live state (final redeploy #7, 22:30 CT)

- Deployed **8b46a2a7** (`D:\ArcaneCore-live\bin\arcanecore-deploy.json`). Snapshot `bin-20261017-2230`. The previous run stopped cleanly ("World saved and stopped").
- Realm PID 51108, World PID 43124. The world was listening at 22:31:34 CT on 127.0.0.1:8085 (305 opcode handlers). Debug build, unoptimized.
- **Remote admin: on** at 127.0.0.1:3443 (loopback only). The token for `@token` login is in `D:\ArcaneCore-live\bin\ra-token.txt`; it is regenerated at each start and deleted at shutdown.
- **Health at 22:36 CT, 6 minutes after the start:**
  - No crash loop and no dumps.
  - Apart from under-map anomalies (LS-004), the only FAIL, EROR or CRIT lines are 2 Conditions startup lines.
  - No ObjectDisposedException, "second operation" or fence errors from the pooled DbContext (43d72744).
  - About 1000 bots. World working set 2.0 GB. Host memory 67%.
  - Ticks: 1190 per minute (972 on #6).
  - Serial gate: about 207k entries per minute with 9k contended, down from 1.1M with 550k contended on #6, thanks to 6a569c79. Owner violations: 0.
- **botctl smoke test over RA (22:33 CT): PASS.** Command: `python tools/botctl/ra.py --token-file D:\ArcaneCore-live\bin\ra-token.txt ...`
  - `.botctl list` → `OK list (1608/1608 bots ok): 1608 bots, 0 controlled` (running and stopped bots).
  - `.botctl take Araefaek` → `ok controlled (passive); its own AI is paused`.
  - `.botctl state Araefaek --json` → full JSON:
    - map 0, zone 38, level 15 gnome warrior, hp 284/319
    - in combat with Forest Lurker 1195
    - path idle, owner `RA:Console`
  - `.botctl release Araefaek` → `ok its own AI drives it again`.
  - Minor cosmetic issue for the botctl lane: some `list` entries print several `botId:` lines under one name, for example `Zoththiarn`.
- Profile, which holds the live appsettings: `C:\Users\Nathan\Documents\Codex\2026-10-04\c\work\ArcaneCore-playable-standard-20261005-r3\appsettings.json`. Backups are `.bak-20261017-HHMM`. The live config was not changed in #6 or #7.

### Redeploy recipe
1. `cd D:\ArcaneCore-sweep\lead`, then `git pull --rebase`.
2. Build all three projects through `D:\ArcaneCore-sweep\build-slot.ps1 build src\<P>\<P>.csproj -c Debug`, where P is ArcaneCore.World, ArcaneCore.Realm and **ArcaneCore.Playerbots**. Without the Playerbots build the pack is stale (LS-016).
3. Set `ARCANECORE_LIVE_HOME=D:\ArcaneCore-live` and `ARCANECORE_LIVE_PROFILE=<profile above>`.
4. Run `pwsh -NoProfile -File tools\ops\live\redeploy.ps1 -ForceRestart`.

To monitor, run `powershell -NoProfile -File D:\ArcaneCore-sweep\cycle.ps1 -Minutes N`.

## 2. Multithreading settings and status

| Setting | Live | Status |
|---|---|---|
| SQLite single writer (W1, mt-db) | ON. It is the default with no switch (005f8bfe); only `Database:Sqlite:SingleWriter:StatsIntervalMs=60000` is in live appsettings. | Validated. Since 20:33 CT: 0 "database is locked" (246 in the 24 h before), 0 backlog, 0 timeouts, about 1.5–2.7k writes/min, wait ≤0.1 ms mean. Remove StatsIntervalMs from live once monitoring is no longer needed. MT-D1: "released a hold ... ended unseen" is a benign DEBUG line, about 10/min. |
| Pooled CharacterDbContext (W9, mt-gc 43d72744) | ON (code, no setting) | New in #7. Clean in the first minutes. Watch for ObjectDisposedException, "A second operation was started" and ownership fence refusals. |
| `Creatures:Movement:PathJobs=true`, `PathJobThreads=4` (W5, mt-jobs) | ON since #3 (20:08 CT) | No nav.job faults or NavMesh exceptions. MT-J1, the in-flight cap pinned at 512, was fixed in 680f3cf8; the cap is now a constant (c0da1866). |
| `Creatures:Movement:PathJobsFollow` (d5f13a78) | **NOT applied** | Enable request from 20:32 CT, with tests passing. Next MT change candidate. Then verify pet lag and nav.job faults. Proposal from mt-maps: Formation followers onto the job path; FormationMovementGenerator.Step sync FindPath is 9.5% of map work. |
| `World:Parallel:MapUpdateThreads=4` (W2/W3 stage 1, mt-maps) | ON since #5 (21:11 CT) | MT-M1: no crash, 0 owner violations. Tick p50/p99 is about the same as single-threaded with the current pack (#4: p50 ~80/p99 92–168; #5/#6: p50 54–68/p99 111–143). Overlap is still x1.0 because creature work is gated. Kept at 4 per mt-maps. Rollback: `MapUpdateThreads=1`. |
| Stage 2: `World:Parallel:CreatureSplinePass=true` + `World:Parallel:Ungated=CreatureMapSystem,GameObjectMapSystem` | **NOT applied** | Requested for #8, with 2926 Game tests passing including the 1-vs-N determinism test. Expect overlap above x1.0. Watch for ownership violations, FaultLedger UpdateCreatureMotion/SplinePass, and LockRecursionException. Rollback: remove the keys. |
| `World:Parallel:OwnerGuard` | Report (default) | Validation knob. GateAllStages, Ungated and Gated are also validation knobs; delete each once its stage is audited. |
| Remote admin `World:RemoteAdmin:*` | ON by default, loopback | Not MT. New in #7. |

Rules: one MT config change per redeploy. MT switches stay until validated on live. Any other fix ships on by default with no switch (Nate, 20:44 CT). Live stays Debug.

## 3. Open issues (full table in `D:\ArcaneCore-sweep\ISSUES.md`)

- **LS-001** (S2, perf/memory): not a leak. The 20:05 gcdump showed 778 MB live of a 2.75 GB heap, and the LOH (~810 MB) is static navmesh and vmap geometry. Bot think costs 88 ms per tick at worst. Do not take gcdumps casually; each one causes a 2 s TickHang.
- **LS-004 / F1-1** (S3): under-map false positives in Ironforge and Undercity, thousands per hour. Polys sit 1–3 yd sideways of the convicted bots. The candidate fix (3 yd sideways tolerance) conflicts with WmoUnderMapRealTerrainTests and needs a decision.
- **LS-005**: WSG flag events. Awaiting a live flag pickup to verify.
- **LS-008**: slow single path searches of 120–560 ms under host load. Instrumented.
- **LS-009** (partial): Undercity lift upper landing at 1596,292 has no route. Ironforge and Undercity battlemaster routes are refused (QA-7).
- **Still open: LS-013, LS-014, LS-015.**
  - LS-013: slow bot think phases; town paths hit the 32768 node cap.
  - LS-014: Grizzled Den wendigos sink under the cave mesh.
  - LS-015: bots walk to vendors gated behind inactive game events.
- **LS-017 / LS-022** (instrumented): evade-loop reasons, and pets in combat-never-left at fixed Barrens spots. Root-cause from the new report fields.
- **LS-016b**: spell 11939 summon failed. Probably fixed by a0740ae5 ("silent summon with a pet"); verify.
- **QA lane:**
  - Still open:
    - QA-3/QA-11: #774, cooking spell focus unreachable.
    - QA-7: battlemasters. QA-10: #834, phantom combat.
    - QA-13/13b: retreat-died is still about 51–53% of retreats. The dbbdbe0c5 diagnostics are live from #7; read the "last seen attackers" field.
    - QA-14: Explore stalls, off-mesh.
    - QA-20 stall groups: Ironforge wander-refused #693, Teldrassil q475 #618/#352, DM grind chase #570.
  - QA-12: watch only.
  - Open bot-qa issues: about 61. Summary in #778. The server files bot-QA issues itself (`World:Playerbots:Qa:GitHub` Publish=true, Nate's config).
- **MT-M1, MT-D1**: see section 2.
- **S-22** (partial): vmangos generic dummy and script effects. Remaining items are listed in the spells handoff in CLAIMS.md and `_spells\gap-now.txt`.
- **Pre-existing, waiting on Nate since Oct 14–15 (main CI, not approved):**
  - Playerbot quest-goal filter (63ea173b).
  - Realm listener "connection refused" tests.
  - `bot` help-list expectations.
  - QaSpellKit fixture expecting an Unsupported aura type, which no longer exists after 69f0044b.
- PR #440 (the earlier handoff) is still unmerged.

## 4. Remaining work per lane (from the CLAIMS.md handoffs)

- **mt-maps:**
  1. Watch #7: gate entries already dropped about 5x, so look for faults and hangs.
  2. Stage 2 at #8.
  3. Then MapCombat per-unit, GameObjectMapSystem.UpdateObjects, and more of the creature walk outside the gate.
  - Tools: `_mt-maps\mtwatch.py`, `summ.py`, `topslow.py`. Status doc: docs/research/parallel-maps-status-20261017.md.
- **mt-jobs:**
  1. PathJobsFollow live check.
  2. Formation (and Targeted?) movement onto path jobs.
  3. The remaining O(N) `_creatures.Values` scans in CreatureMapSystem.
  4. EventAiEngine MoveInLineOfSight and NavMeshQuery corridor allocations.
  5. W6 bot activation radius.
  - Bot A* is 29% of the world thread: AsyncNavigationEnabled is off.
- **mt-db:** done. Measure with `_mtdb\measure.ps1`. Drop StatsIntervalMs once monitoring ends.
- **mt-gc:**
  1. Re-trace after #7 to confirm the pooled-DbContext effect.
  2. EF per-save cost (CharacterOwnershipGuard).
  3. Next top allocators.
  - Baseline on #6: about 36 MB/s allocation, LOH 67 MB/min, 0 full blocking GCs. Tool: box `/workspace/mtgc/allocview`.
- **qa:** item (1) of its handoff: read the retreat-died attacker lines on #7. If "new" attackers dominate, route retreats away from hazards. If "fled" with clock >12, look at the leash in SelectHostileTarget. Then the stall groups listed above.
- **fix1:** LS-004 decision, LS-009 Undercity lift, then root-causing LS-017 and LS-022 from the new instrumentation.
- **spells:** batch 6 pushed (a0740ae5). Left: the rest of the vmgap0 scan (`_spells\vmgap0.py`), plus spell_group, single-target auras, AURA_BOUNCED and spell_area, which need world-DB data. Verify hunter taming on live (9b9f84da plus bot taming in e3cffacc).
- **content / world-scripts:** priority list done (batches 7–22, Restless Soul, Deeprun Rat Roundup). C'Thun reset is still skipped on purpose. Verify the Short John chest position at game event 16 ("Let the Bloodletting Begin!"). Next: further unported scripted_event_id / dungeon scripts (rescan against cmangos ScriptNames) and new faults from live logs.
- **cmds:** batches pushed. `.debug`, `.inspect`, `.alter`, `.ai`, `.creature clone/spawnmany/despawntemp`, `.cheat` and `.tele group` are done. Idea list and box pipeline: `D:\ArcaneCore-sweep\_cmds\HANDOFF.md`. Note that `.set` resolves to retail `.setskill`, which is why the setters are named `.alter`.
- **botctl:** pushed 4ddce276 with docs/ops/botctl.md, docs/ops/remote-admin.md, tools/botctl/ra.py and RaClient.ps1. Basic RA smoke passed on live (above). Next:
  - A longer scripted scenario: move, cast, quest, loot, and `.botctl wait`.
  - The duplicate botId lines in `list`.

## 5. Commit summary (9b39adcf..8b46a2a7, 144 commits)

- **Multithreading / perf:**
  - SQLite single writer and its follow-ups: 7783aa91, 3d18705e, 0c14390f, fe2a3052, da87a807, 0f65e435, 625c511e, 0b2f2345, bd508007, 005f8bfe.
  - Path-job pool: fb99a12c, 680f3cf8, d5f13a78, c0da1866.
  - Parallel maps: ff188d9f, 9bbae7ab, 4967d819, df2e68f5, d3bdea02, 6a569c79, 261db539, 07506272, df3023ae.
  - Navmesh / A*: 3dfd657f, 46a5adaf, 77f85f78.
  - Allocation / GC: 1922f395, c62fe2ac, a32c31b3, 179d392b, 56e94270, 74ae4aaf, d41e9fa6, 43d72744.
  - Plan doc: 9f8ccee8.
- **Bots / bot QA:** 561ee994, c2f3484f, bef91a2b, 81505665, 12d327ac, fa4e3a7c, 71d3fb02, 8e1cce6e, 3101b1cf, 72da4736, 0f1dc0b4, ab618532, 379b7037, 50bcf3ac, f4c19da7, faaf4acf, df322580, dbbdbe0c, e3cffacc, f56e46de, e8529db4.
- **Creatures / combat / collision:** bea409ae, 3e8e9a69, 0c33be21, 5884d811, 5d36b66a, 3be98df1, 8fa02f28, a35b271c, 4013d12e, b5b155a5, 121add77, 2cbf8e46. Collision change ff363fee was reverted by 70305917.
- **Spells (about 35 commits):**
  - Talents and set procs.
  - Exclusive auras, aura limits, CC heartbeat resist (always on).
  - BG-only spells, target 8 and target 53.
  - Tame Beast fix.
  - vmangos dummy batches 1–6: engineering, raid, world, holiday.
  - Buff rank by level, party aura slots.
- **Content:** zone and dungeon script batches 7–22 plus follow-ups (8ad8ac1f … dbeddaf9, 7c3cc42f, 3c48c50d). Highlights:
  - Gizelton caravans.
  - Malfurion in Sunken Temple (quest 8733).
  - Dire Maul Dreadsteed ritual (quest 7631).
  - ZG Zulian Prowler.
  - Eastern Kingdoms world map script.
  - Stranglethorn Fever, Gurubashi arena.
  - Garments quests.
  - Restless Soul.
- **Pets / systems:** hunter pet stable (0e8fceea, schema v63), 14d259ae, 2a4d13b8.
- **GM commands:** 89517b47, d7f1ad36, ae23433f, 7ebcbcfa, c6c11143, d065f379, 1a880004, 118820d3, d305d72f, 1b7cc837, dfb0f3e2, cdb81f3e, 7993f902, b80c0916, 39f0175d, d1756225, b450210e, 8b46a2a7.
- **botctl + RA:** 4ddce276.
- **Ops / tests:** e3777dd2 (redeploy refuses a stale pack), 315c42f9, b4cfae64, 37e9a50e, f136bd83, a39ba8a3.
