# Area: Character persistence under load

Lane `claude/w19-persistence`. How character snapshots reach the database when a couple of hundred players (or bots) are online, what
the queue guarantees, and which knobs and log lines tell an operator that it is falling behind. Everything here is ArcaneCore's own
persistence design: vmangos saves players synchronously through its async-query worker threads and has no equivalent queue, so nothing
in this page claims retail fidelity. The charter rule it serves is "never lose a save".

Code: `src/ArcaneCore.World/Persistence/CharacterSaveQueue.cs`, `src/ArcaneCore.World/Spells/SpellbookCache.cs`,
`src/ArcaneCore.Data/Resilience/SqliteConnectionPragmaInterceptor.cs`, `src/ArcaneCore.Data/Stores/EfCharacterStore.cs` (the
action-button diff), `src/ArcaneCore.Game/Maps/WorldRuntime.cs` (`Autosave`), `src/ArcaneCore.World/Npc/QuestNpcFeature.Rewards.cs`
(settlement timing). Tests: `tests/ArcaneCore.World.Tests/Persistence/CharacterSaveQueueTests.cs`,
`SaveQueueLoadBenchmarkTests.cs`, `tests/ArcaneCore.World.Tests/Spells/SpellbookCacheBatchingTests.cs`,
`tests/ArcaneCore.Game.Tests/Maps/WorldRuntimeAutosaveTests.cs`, `tests/ArcaneCore.Data.Tests/Resilience/SqliteConnectionPragmaTests.cs`,
`tests/ArcaneCore.Data.Tests/CharacterStoreActionButtonDiffTests.cs`, `tests/ArcaneCore.MockClient.Tests/QuestSettlementResponsivenessTests.cs`.
The diagnostics half of the lane (hang detail in the tick watchdog) is in [watchdog.md](../ops/watchdog.md).

## What went wrong under 200 live bots

A 200-bot run showed the character save queue 19 s or more deep, 71 quest reward settlements failing with `TaskCanceledException` while
saving their pre-reward snapshot, 39 more refused with "settlement capacity reached", and world ticks stalling for 2-9 s with nothing in
the log to say where. The causes, each of which this lane removes or exposes:

- The queue was one consumer over an unbounded FIFO with no per-character coalescing, and the periodic autosave queued a snapshot of every
  online player every `World:AutosaveIntervalMs` whether or not the previous one had been written. Level-ups and boosts queued more.
  A full snapshot costs well under a millisecond on disk, so the backlog was copies and waiting, not I/O.
- A settlement's pre-reward save sat behind that FIFO and its 5 s budget token expired before the save started; the cancellation
  surfaced as a "saving character failed" error although nothing had failed.
- About fifteen independent background writers share `characters.db` (save queue, spellbook, skills, talents, reputation, quests,
  explored zones, rest, taxi, honor, spell state, pets, playbot checkpoints, settlements). `Microsoft.Data.Sqlite` reacts to
  `SQLITE_BUSY` with its own retry loop that sleeps a fixed 150 ms per attempt, so a writer is admitted up to 150 ms after the lock
  frees and the writers starve each other.
- Each learned spell was its own transaction.

## Save queue

`CharacterSaveQueue` is still one consumer writing through a scoped `ICharacterStore`; what changed is what it holds. State lives in one
`Slot` per character under a single lock that is never held across an `await` or a store call. `Pending` counts characters with a
snapshot waiting or being written (the gauge `arcanecore.db.save_queue_depth`).

### Per-character coalescing

- **Latest wins.** `Enqueue` copies the snapshot on the calling (world) thread. If a snapshot of the same character is still waiting, the
  new one replaces it atomically; the queue holds at most one waiting snapshot per character, so its depth is bounded by the number of
  characters, not by the number of events.
- **Optional sections are inherited.** `CharacterState` has optional sections (action buttons, skills, talents, ... the same set
  `Merge` already handled for a retained failed save). A newer snapshot that leaves a section `null` takes that section from the
  superseded one, so superseding never loses a section that only the older snapshot carried. The same merge applies to a retained
  failure when the slot is written.
- **A write in flight is never replaced.** When the consumer takes a slot it moves the snapshot out of the slot and marks the slot in
  flight. A snapshot arriving meanwhile becomes the next waiting one and follows the write. A character's saves therefore never overtake
  each other, and the snapshot that is on disk is never older than one already confirmed.
- **Hold, quarantine and failure retention are unchanged.** `Hold` suppresses new ordinary snapshots until `Resume`; `Quarantine` refuses new snapshots
  and drops a waiting one when the consumer reaches it (its settlement waiters fail with "quarantined"); a snapshot that fails after `MaxAttempts` (3) is retained as the authoritative one and merged
  into the next write for that character. The final drain at `StopAsync` still writes every waiting slot.

### Priority lane and the 1-in-8 ordinary pick

`SaveForSettlementAsync` (the pre-reward save of a quest, vendor, loot or economy settlement) and `FlushCharacterAsync` (a barrier) add the
character to a **priority lane**. The consumer takes from that lane first, so a settlement no longer waits behind unrelated
characters' autosaves. To keep the lane from starving ordinary snapshots, **every eighth pick is an ordinary one** (`PriorityBurst` = 7
consecutive priority picks, then one ordinary pick if any is waiting). Ordering is per character only, as before.

- A settlement's snapshot is merged over any snapshot of the same character already waiting (callers capture it on the world thread and
  hold the character in the same critical section, so a waiting one is always older).
- If a settlement's token fires **before its write starts**, the returned task is cancelled, nothing is logged as a failure, and the
  snapshot stays queued as an ordinary one: the newest snapshot is never dropped. Once the write has started, cancellation is
  cooperative as before.
- Several settlements for one character that share a write do not cancel each other: the write runs without a token when more than one
  settlement is attached.
- `QuestNpcFeature.Rewards` now bounds `FlushCharacterAsync` with the same budget token (`WaitAsync(budget.Token)`), so the budget governs
  the whole pre-reward drain and cancellation of the wait never removes retained state.

### Autosave skips characters with a pending snapshot

The periodic save is `WorldRuntime.Autosave()` and runs at the end of the tick body (it used to be `SaveAll()`, which is unchanged and
still used for the final save). It skips a character whose previous snapshot is still **waiting** (`CharacterSaveQueue.IsPending`: a
snapshot in flight does not count, since the next autosave then queues normally) and one whose quest settlement is pending. The queue is
behind in exactly that case, and another snapshot would only supersede the waiting one at the cost of a copy. The tick records the time
the pass took and the number of snapshots it queued (`TickPhases.AutosaveMicros` / `AutosaveSnapshots`, shown in the slow-tick detail).

### Backlog warnings

The consumer (and `Enqueue`) check the backlog at most once per second and warn through the log:

| Key | Default | Meaning |
|---|---|---|
| `World:SaveQueueWarnDepth` | `1000` | Warn while this many characters or more have a snapshot waiting or being written. 0 disables the depth check. |
| `World:SaveQueueWarnAgeMs` | `5000` | Warn while the oldest waiting or in-flight snapshot has waited this long. 0 disables the age check. The default is the quest settlement budget: a pre-reward save older than that is already failing its settlement. |

The warning is `character save backlog: N characters pending, oldest waiting M ms (warns at ...)`, repeated at most every 30 s while
the backlog persists, followed by one `character save backlog cleared: ...` line (Information) when it ends. An idle queue wakes once a
second so the cleared line is not delayed until the next save. Both keys are checked by `check-config` (integer, 0 or more) and listed in
the generated [configuration reference](../reference/configuration.md). The age is also exported as the gauge
`arcanecore.db.save_queue_oldest_age` ([metrics](../ops/metrics.md)) and appended to every watchdog hang line. Both keys are live: `.reload config` applies them at the next once-a-second check.

## SQLite busy timeout

`SqliteConnectionPragmaInterceptor` (added to the SQLite case of `DataServiceCollectionExtensions.ConfigureProvider` only) runs
`PRAGMA busy_timeout = N` on every connection EF opens. SQLite's own busy handler then waits inside the call and retries within a few
milliseconds, instead of reporting busy at once and leaving the wait to `Microsoft.Data.Sqlite`'s fixed 150 ms sleeps.

| Key | Default | Meaning |
|---|---|---|
| `Database:SqliteBusyTimeoutMs` | `5000` | 0 to 60000 ms. 0 leaves SQLite's "fail at once" behaviour. |
| `Database:<Component>:SqliteBusyTimeoutMs` | unset | Per component (Auth, Characters, World); unset inherits the shared value. |

The timeout never exceeds the connection's own `Default Timeout`, and when the database guard is on (`Resilience:Database:Enabled`, `QueryTimeoutMs` above 0) it is **clamped, silently, to `Resilience:Database:QueryTimeoutMs`** (the effective value is `min(configured, QueryTimeoutMs)`; a configured value above it is not an error). Reason: the guard abandons a call past its deadline, but the abandoned call keeps waiting in SQLite's busy handler and disposing its scope blocks until it returns, so a longer busy timeout lengthened every refused logon (measured 7.0-7.6 s against a 2 s deadline before the clamp). `DatabaseGuard`'s pessimistic-timeout contract (the outer bound on
a whole operation) is unchanged. MariaDB, MySQL and PostgreSQL ignore the key. Out-of-range values are a `check-config` error and the
interceptor refuses them at start.

### A locked database does not stall the write queues

A writer that holds the SQLite file for a minute (the 2026-10-11 incident) used to hold the single save consumer for ~97 s: each save waited the
connection's 30 s `Default Timeout` and was retried inline three times, while every other character save and quest reward waited behind it.

| Key | Default | Meaning |
|---|---|---|
| `Database:SqliteWriteTimeoutMs` | `5000` | SQLite, characters database only. 0 to 60000 ms (rounded up to whole seconds). The longest any command on a characters connection waits for another connection's write lock. Applied by lowering the connection's `Default Timeout` when it opens (EF's `BEGIN IMMEDIATE` uses that timeout) before `busy_timeout` is derived from it, so `busy_timeout` is clamped to it too. 0 keeps the connection string's timeout. |
| `Database:SlowTransactionWarnMs` | `2000` | All providers. A transaction open longer than this is logged at WARN when it commits, rolls back or fails, naming the DbContext, the code that closed it, and the time held by the application before the end call versus the time inside the commit/rollback call (WAL fsync = disk I/O). A transaction disposed without commit or rollback is logged the same way (via EF's `TransactionDisposed` diagnostic event; EF has no interceptor hook for it) (`SlowTransactionInterceptor`). 0 disables. |
| `Database:SlowCommandWarnMs` | `2000` | All providers. A single command whose execution takes at least this long is logged at WARN with the DbContext, duration, autocommit versus inside a transaction, write versus read, the first 80 characters of the SQL (parameter values are never logged; quoted strings and numbers in the text are masked as `?`) and the calling code (`SlowCommandInterceptor`). It covers what the transaction warning cannot see: autocommit writes (single-command `SaveChanges`, `ExecuteUpdate`/`ExecuteDelete`, raw SQL) and commands that fail as "database is locked". Identical warnings (same caller and SQL prefix) are limited to one per 10 s and the next one states how many were suppressed. 0 disables. |

- **Interaction with `busy_timeout`.** `busy_timeout` makes SQLite wait inside one call; the driver repeats the call until the command timeout has elapsed. Because the write timeout lowers the connection's `Default Timeout` before the pragma is set, `busy_timeout = min(SqliteBusyTimeoutMs, write timeout)` on a characters connection, so with the defaults a locked command fails after about 5 s (previously 30 s). The driver ignores cancellation tokens during the wait, so a timeout is the only bound. Auth and World connections are not bounded.
- **`CharacterSaveQueue`.** A busy/locked failure (SQLite 5/6, also inside `DbUpdateException`) is not retried inline. The snapshot is retained, the whole queue pauses (1 s, 2 s, then 5 s while it keeps happening; reset by the next successful write) and the consumer moves on. Retained snapshots are retried when the queue is idle and at least every 16th pick, by a barrier (`FlushCharacterAsync`, which waits until the snapshot is written or its token fires), or by a newer snapshot of the character (merged over it). Shutdown ignores the pause and retries inline, then names the characters it could not write.
- **Other queues.** Creature respawn and keyed (GM audit, groups, last address) queues no longer repeat a lock wait inline: the write stays retained and their retained-retry timers (30 s) land it. The honor, reputation, taxi, rested-state, explored-zones and social queues keep three inline attempts per write (about 5 s each under the characters write bound) and share `RetainedRetryTimer`: every 30 s while the queue holds a retained write, one sweep marker is posted into the queue's single consumer (never stacked), which makes one attempt per retained character or key and stops at the first locked database, so a lock outlasting the inline attempts costs about one 5 s wait per 30 s and the write lands once the lock clears, with no new trigger and no one online. The sweep runs on the consumer, ordered with every other item, and never replaces a newer value. Shutdown still drains and names whatever is not durable. A taxi save with an abandon fallback keeps the route (it is not abandoned) when the failure is a locked database. The instance write queue keeps its inline retries only (it has no retention).

### Decision: keep `BEGIN IMMEDIATE`, WAL and `synchronous=FULL`

The obvious ways to make a write-heavy SQLite file faster were considered and **rejected**; two tests pin the current behaviour so a later
change has to be deliberate.

- **WAL** (`PRAGMA journal_mode=WAL`, applied by the schema bootstrap): kept. Readers never block the writer and the writer never blocks
  readers. `WalAndSynchronousFullArePinned` asserts it.
- **`synchronous=FULL`** (SQLite's default, value 2): kept. In WAL mode `NORMAL` skips the fsync at commit and can lose the most recent
  committed transactions on power loss or an OS crash. The charter rule is "never lose a save", and the save queue reports a snapshot as
  written once the store call returns, so a commit that can silently vanish would break the retained-failure logic that depends on it.
  It is also not where the time goes: a full snapshot measured about 0.7 ms on disk (`SaveQueueLoadBenchmarkTests`, below).
- **`BEGIN IMMEDIATE`**: kept. EF's `BeginTransaction` on `Microsoft.Data.Sqlite` takes the write lock at `BEGIN`, before any statement
  runs. A writer therefore queues at the lock (now waiting inside SQLite under `busy_timeout`) instead of failing later when a read
  transaction tries to upgrade to a write and gets `SQLITE_BUSY` that no timeout can resolve. `EfTransactionsOnSqliteBeginImmediate`
  asserts it.

The per-snapshot cost was also cut: `EfCharacterStore.SaveStateAsync` writes the action bars as a **diff** (unchanged buttons issue no
statement, removed ones are deleted, new ones inserted) instead of deleting and reinserting every button on every save
(`CharacterStoreActionButtonDiffTests` counts the statements on the wire).

## Spellbook learn batching

`SpellbookCache` writes spell changes on one background task. Each learn used to be its own store call and transaction, so a character
learning 18 spells paid 18 transactions. `EnqueueAdd` now **joins the entry at the tail of the write queue** when that entry is an Add for
the same character that the writer has not taken yet (`Taken` is set under the writes lock the moment the writer dequeues it); the spells
of one tick then reach the store as one call and one transaction whenever the writer is busy or behind.

- It is merged only with the **tail** and only when nothing else sits after it. A forget, a delete, a flush or another character's
  write between two learns ends the run, so every write keeps its position relative to forgets, deletes and flushes. A learn that
  follows a forget is never folded into an Add queued before it.
- An entry the writer already took is not appended to; the next learn starts a new entry.
- `FlushAsync` and `FlushCharacterAsync` still queue behind everything already queued, so a settlement that flushes the spellbook sees
  every spell learned before it.

## Measuring it

`SaveQueueLoadBenchmarkTests` reproduces the live 200-character shape (file-backed WAL SQLite with the live `Pooling=False;Default
Timeout=5` settings, autosave tenths, a level-up burst, quest settlements under a 5 s budget, spell learns) against the real queue and
spellbook. It is **skipped unless `ARCANECORE_SAVE_BENCH=1`**, and a skipped run says so rather than passing silently; set the variable
to run it. The unit tests in `CharacterSaveQueueTests` cover the coalescing, merge, in-flight, priority and burst rules, cancellation
before and after the write starts, hold, quarantine and retained-failure behaviour, and the warning thresholds.

## Not delivered and known limits

- One consumer: coalescing and the priority lane remove the queueing delay, but a single slow store call still delays every other
  character. Parallel writers were not added; a full snapshot costs well under a millisecond on disk, so one consumer is not the limit at 200 characters.
- Other background writers (skills, talents, reputation, quests, ...) keep their own queues and connections; this lane gave them the
  shared busy timeout but did not merge them.
- The backlog thresholds are warnings only; nothing sheds load or refuses a save when they are crossed.
