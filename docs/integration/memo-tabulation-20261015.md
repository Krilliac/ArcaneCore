# Hot-path memoization and tabulation pass (2026-10-15)

Base: `89397354` (origin/main at branch time), branch `claude/memo-tabulation`. A survey of pure or deterministic work that the
world thread recomputes per tick, per think, per hit or per packet, the wins taken, and the candidates measured and dropped.
All numbers below come from BenchmarkDotNet 0.15.8 (in-process emit toolchain, default job) on the shared development machine
(Ryzen 9 9900X3D; other agent sessions were building and testing, CPU load not controlled). The before and after columns were run
back to back in one session from two worktrees: `89397354` plus only the benchmark project, and this branch. Nothing here was measured
on the live server.

Real data: the 5875 client DBCs (`ARCANECORE_TEST_DBC_DIR=<data-root>\client-dbc-5875`, 22,357 spells) and an imported world
database snapshot (`ARCANECORE_TEST_WORLD_DB`, the `managed84-cadence-r2-before-world.db` copy of 2026-10-07, world schema 27, opened
read-only and immutable; the live `world.db` files were empty when this ran). The equivalence tests and the benchmarks read its loot and
item tables raw, so the older schema does not matter to them.

## Results

| Win | Benchmark | Before (mean, alloc) | After (mean, alloc) |
|---|---|---|---|
| Spellbook snapshot | `GetSpells(p).Count`, 250-spell book | 1,308 ns, 3.26 KB | 12.3 ns, 0 B |
| | iterate `GetSpells(p)` | 1,441 ns, 3.3 KB | 141 ns, 32 B |
| Loot plans | one roll of each of the 4,296 real creature loot templates (per roll) | 1,122 ns, 1.92 KB | 310 ns, 197 B |
| Ghost visibility | living viewer, living ungrouped target | 8.50 ns, 64 B | 1.02 ns, 0 B |
| Consumable classification | per item, the 999 real class-0 items, real Spell.dbc | 22.1 ns (sd 3.7), 22 B | 14.2 ns, 0 B |
| World tick dispatch | `RunTick`, 10 `Updated` + 10 `WorldTick` no-op handlers | 1.50 us, 368 B | 1.43 us, 48 B |
| | same, 100 + 100 handlers | 13.50 us, 1,808 B | 12.81 us, 48 B |

The tick-dispatch time difference is within run-to-run noise; its effect is the allocation (the invocation lists and their casts per
tick), not the time. The consumable baseline was noisy (an earlier baseline run of the same code measured 17.2 ns): a small time win
and the 22 bytes per call gone.

Run them: `dotnet run -c Release --project tools/perf/ArcaneCore.Benchmarks -- --filter '*'` with both variables set (a benchmark that
needs real data throws without it, it never falls back to stand-in data).

## What changed, and when each table is invalidated

| Table | Where | Key | Invalidated by | Memory | Threads |
|---|---|---|---|---|---|
| Sorted spellbook snapshot (`ReadOnlyCollection<uint>`) | `SpellbookCache._sorted` | character id | every change of that book: `Load` (all), `LoadCharacter`, `LearnSpell`, `AdoptCommitted`, `ForgetSpell`, `EnsureDefaults`, `DeleteCharacter` | one array per cached book (about 1 KB for a level-60 book), only for books read since their last change | under the cache's existing lock (session tasks and the world thread) |
| Loot plan (rows, groups split into explicit-chance and equal-chance pools) | `LootGenerator._plans` | (table kind, entry) | never stale: `LootContent` is read-only and fixed per generator; a reload (`LootService.ReplaceContent`) builds a new generator | one plan per template rolled, at most the templates of the content (8,104 in the snapshot); empty templates are not kept | the generator was already single-threaded (one per world thread, one for the auction bot) |
| Consumable kind | `PlayerbotConsumables.s_classified` | (`SpellStore`, `ItemTemplate`) by reference, both weak | a spell reload installs a new `SpellStore`, an item reload new templates: they miss and the old entries are collected with the old objects | one small record per (store, template) classified | `ConditionalWeakTable` is thread-safe; a race computes the same answer twice |
| Handler table of `Updated` / `WorldTick` | `WorldRuntime._updatedHandlers`, `_worldTickHandlers` | the multicast delegate instance | any subscribe or unsubscribe (a delegate is immutable, so the event holds a new instance) | one array per event | world thread only, as before |

The ghost visibility change is a reordering, not a table: a living viewer and a target that is not a ghost were always answered `true`
(the raid lookup could only return `true` too), so that pair now returns before the raid lookup, which built the target's member array on
every visibility check of nearly every pair.

`GetSpells` hands out the same read-only snapshot until the book changes. A caller that learns or forgets spells while iterating a result
keeps iterating the old snapshot, exactly as it iterated the fresh copy before; a snapshot cannot be written through (as the copy built by
the collection expression could not).

## Equivalence tests

`tests/ArcaneCore.Game.Tests/Memo/MemoEquivalenceTests.cs` and `tests/ArcaneCore.World.Tests/Memo/MemoEquivalenceWorldTests.cs`. Each
compares against the original computation kept verbatim (`ReferenceLootGenerator`, the raid-first ghost rule, `ComputeClassify`, a model
spellbook):

- Loot: six generated contents (every row shape: groups, zero, quest, NaN and negative-zero chances, nested and cyclic references,
  maxcount 0-3, conditions) with and without a drop rate and with process budgets 0, 7, 16 and 4096, 10,320 rolls each; one hand-built
  group-reference template, 2,000 rolls; and **every one of the 8,104 templates of the real world.db (229,646 rows), 5 rounds, with and
  without a rate: 55,352 and 77,729 items rolled identically**, same seed on both sides, so every random draw also matches.
- Spellbook: 600 random changes of every kind over three characters, after each one every book equals the sorted model, an unchanged book
  returns the same instance, and every snapshot handed out earlier still holds what it held.
- Consumables: every combination of the effect shapes and slot layouts the classification distinguishes, a spell missing from the store, a
  reloaded store and a reloaded template; and **all 17,718 real items against the real 5875 Spell.dbc** (193 food, 30 drink).
- Ghost visibility: every ordered pair of eight players (living, dying, ghosts near and far, both factions, a game master) under four
  group resolvers; the living pairs make no raid lookup.
- World tick: subscription order across subscribe/unsubscribe/resubscribe, and the statistics names.

Load-bearing check: with the `LearnSpell` invalidation removed and the equal-chance pool reversed, the spellbook test, the real-world.db
loot test and 6 of the 9 Game memo tests failed; the code was then restored.

## Survey: candidates not taken

Ranked by how often they run times what they cost. "Dropped" means measured or reasoned to be no win; "deferred" means a real candidate
left for its own change (not pure, larger, or in files the hardening wave H1 is editing).

| Candidate | Runs | Why not here |
|---|---|---|
| `SpellThreatTable.Find` allocating a `SpellThreatEntry` per hit and heal (`Combat/Threat/SpellThreatTable.cs:33`) | every threat hit and heal | **Dropped, measured**: 2.9 ns and 0 B allocated before any change; the JIT already stack-allocates the non-escaping record. |
| Per-aura proc lookups (`Spells/Procs/SpellSystem.Procs.cs:283-307`) | every swing, hit and periodic tick, per aura | Dropped: a per-spell memo is another dictionary or weak-table lookup in place of the two or three it saves; also an H1 file. |
| Per-unit aura totals by type (`SpellSystem.GetTotalAuraModifier` and its siblings) | ~8 full aura scans per white swing | Deferred: the largest total payoff, but not pure (a per-unit index invalidated on aura add, remove and amount change, as vmangos `m_modAuras`). |
| Per-spell facts: polarity, bonus coefficients, binary/mechanic/diminishing flags, area radius (`SpellInfo.Polarity.cs`, `SpellCoefficients.cs`, `SpellSystem.AreaAuras.cs:65`) | per hit, per DoT/HoT tick, per pet autocast check | Deferred: `SpellInfo` is copied with `with` at runtime (wand school, Judgement of Command, Hammer of Wrath), so facts keyed by id are wrong and a lazy field is copied stale; needs a facts field reset by a copy constructor. |
| Shared values block per (object, visible mask) within one flush (`Maps/Map.Update.cs:561`, `SendValuesUpdate`) | every changed object x every observer | Deferred: viewer-dependent when `ViewerFieldFilter` is set; a larger change. |
| Playerbot crafting spell facts and reagent index (`Playerbots/Progression/PlayerbotCrafting.cs`) | every out-of-combat think, per vendor row | Deferred: H1 is editing `PlayerbotCrafting.cs`; benefits from the spellbook snapshot already. |
| Town vendor row classification (`PlayerbotTownGoals.FindNpc`), trainer quotes (`PlayerbotTrainerDestinations.HasCandidate`), quest candidate tables (`PlayerbotWorldDestinations`, `PlayerbotQuestGoals`) | up to three times per think | Deferred: per-bot money, level and quest state are mixed into the scans; two of the files are H1's. |
| Item score per (template, weights) (`PlayerbotItemScore.Score`) | equipment and vendor checks | Dropped for now: a handful of float operations per stat; a weak-table lookup costs about as much. |
| Faction template reactions (`Combat/FactionCombatHooks.cs:31-75`) | every CanAttack/IsHostile | Deferred: 2-4 frozen-dictionary lookups; the gain is small and the player branch is not pure (reputation). |
| Service lookups per call (`services.GetService<...>()` in 309 World lambdas and properties; movement, chat, loot) | per packet, per condition | Deferred: caching a resolved reference, not a computation; many small edits across handler files. |
| Duplicate-packet hash per receiver (`Net/DuplicatePacketDetector.cs:166`) | every outbound packet, N times per broadcast | Deferred: needs the broadcast caller to pass a precomputed hash. |
| Experience, melee table, stat, resist, rage formulas | per kill, swing, regen | Dropped: a few arithmetic operations each, already cheaper than a table read; the XP table and agility rates are already tables. |

Already cached or tabulated (no action): opcode names, per-opcode metrics slots, the handler table, query response cache, content stores
(frozen dictionaries), `Invariant` messages, every `Regex`, the risk verdicts and status-line memos of the playerbots.

Found on the way and not fixed here: `GameObjectMapSystem._questLootItems` (`GameObjects/GameObjectMapSystem.cs:63`, `:1421-1427`) memoizes
quest loot by loot id and is never cleared by a loot reload, so it can serve stale data after `.reload *_loot_template` (separate change;
`GameObjectMapSystem.cs` is an H1 file).
