# Playerbot QA: findings and GitHub issues

The bots play the game all day and keep running into the same few problems: a quest whose target has no spawn, a place where bots
die over and over, a recovery that throws. They already log each one as a warning. Bot QA reads those warnings, folds the many
repeats into a few ranked findings, writes a report a person can read, and (only when asked) opens one GitHub issue per finding.
The bots themselves are untouched: QA listens to what they log. Configuration: `World:Playerbots:Qa` (read once at start; a change
needs a restart). Commands: `.playerbot qa` (GameMaster). Everything lives in `src/ArcaneCore.Playerbots/Qa/`.

## What it watches (signals)

A logger provider (`PlayerbotQaLogTap`) is registered next to the normal ones. It answers for two log categories only,
`ManagedPlayerbotFeature` (the bots) and `CreatureWorldFeature` (the creature scripts), only at Warning and above, and gives every
other category a null logger. A log call parses the formatted line (`PlayerbotQaSignalParser`) and does one non-blocking write to a
bounded queue (`PlayerbotQaSignalBus`, `SignalQueueCapacity`; a full queue drops the oldest and counts it). It does no I/O, takes no
lock beyond the queue's and never throws, so it cannot slow the world thread. One background task drains the queue; `.playerbot qa` and `.playerbot qa top` also fold in at most 512 queued signals on the calling thread before they list (the rest waits for the background pass), so a finding just met is not shown as stale.

| Kind | The warning it comes from |
|---|---|
| stall | `Playerbot ... stalled 120s: goal=Quest target=N quest=Q at x,y,z map M; it gives up that goal` (a homed bot outside its home: `... it gives up that goal outside its home`) |
| shared set-aside | `Playerbots: N bots stalled on quest Q for 30 min; every bot sets it aside (stalls at x,y,z map M; ...)` (the positions since 2026-10-11; the first is the signal's position, shown in the report; the classifier's spawn check of a set-aside still looks at every map, since the bots behind it may have stalled elsewhere) |
| death loop | `Playerbot ... death loop: died N times in Ns within 60 yd of x,y,z map M attackers=a/b/c` |
| action fault | `Playerbot ... action failed (InvalidOperationException: playerbot-recovery-...)` |
| quarantine, disabled | `Playerbot ... quarantined for N s (fault N of N)`, `... disabled after N faults ...` |
| relay data / relay script | `relay script N uses unsupported MOVEMENT waypoint path N missing for entry N`, `relay script N does not exist` |

The parser formats are the real log templates; the contract tests build the real strings and parse them, so rewording one of those
warnings fails a test instead of silently blinding QA.

### Direct signals (no log line)

Any feature can report without a log line through `IPlayerbotQaSink` (DI singleton `PlayerbotQaSink`): `Report(signal)` does one
non-blocking write to the same signal queue; `ReportFor(unit, signal)` fills the bot key and class only when the unit is a managed
playerbot (a player of a managed session that is not the console: `WorldSession.IsPlayerbot`) and reports nothing for a real player or a
creature. With QA disabled `Enabled` is false and both return at once. A signal carries optional ids next to the old fields: `Spell`,
`SpellName`, `SpellRank`, `EffectIndex` / `EffectName` / `EffectValue`, `Aura`, `Reason` (a code: the gap of a spell, the cast result of
a refusal), `Item`, `Npc`, `GameObject`, `BotClass`, and `FromCoverage` (a static fact: it marks a finding without counting an
occurrence, a sample or a bot). A direct signal's bot key is made from the character guid (not the bot id the log lines carry; the two
never meet in one finding). Findings keep the ids (and the classes that met them) in `findings.json`; older files load unchanged.

Every kind has one row in `QaKinds` (label, GitHub label slug, severity weight, bot-independent, one-occurrence, spell kind). A new kind
needs its row and its cases in the fingerprint, the subject and the classifier; `PlayerbotQaKindExhaustivenessTests` runs every kind
through every switch (and the publisher), and an unknown kind throws in the fingerprint and the classifier instead of passing as a stall.

### Spell signals

`PlayerbotQaSpellMonitor` is an `ISpellCastObserver` on the world's spell system, registered while QA runs. Each callback first checks
that the caster is a managed playerbot (two type checks) and returns for anyone else, so **real players are never reported**. The common
path of a bot's cast allocates nothing (a test measures `GC.GetAllocatedBytesForCurrentThread` over a thousand calls): the facts of a
spell are computed on its first cast and cached by id, the counters are value-keyed, a signal is built only when one is reported, and the
same (kind, spell, detail) is reported at most once a minute.

| Kind | When | Fingerprint | Cause |
|---|---|---|---|
| spell unsupported | A bot's own cast (triggered casts: procs, items, scripts are skipped) that really meets a gap. A missing effect handler, an aura type with no handler that `AuraSupport` marks Unsupported (a Referenced type works) and an implicit target the system cannot select are reported from the failing path itself (the spell system's `OnUnsupported` seam), so a handler the running server has is never reported. The gaps no failing path shows come from `PlayerbotQaSpellGapAnalyzer` on the cast: a DUMMY or OVERRIDE_CLASS_SCRIPTS aura nothing reads (no script, proc script, dummy-aura case, periodic dummy, known reader; the Judgement script reads the seals'), and a DUMMY with no spell script or a SCRIPT_EFFECT with neither a script-effect entry nor a spell script, when it is the spell's only working effect. Bot-independent: **one occurrence is enough**. | spell name (every rank alike), effect index, gap code | NotImplemented |
| spell refused | `SpellHandlers` hands the result of a managed bot's refused CMSG_CAST_SPELL to the monitor. `PlayerbotQaSpellRefusals`: only results no rule of play produces are **suspicious** and reported at once: Error (an enchant row missing), Unknown (a bad power type, enchanting unavailable), NotFound, NotKnown (the book check; the detail says whether the bot's book holds the spell), SpellUnavailable and LevelRequirement (produced by nothing today). Every other result is a rule of play, BadTargets included (Overpower or Execute at a target without combo points or above 20%, a buff on a mounted or shapeshifted target), and is reported only when **one bot gets it more often than its own 4 s refusal backoff allows** (20 within 60 s: a loop that does not learn); a bot that honours its backoff never is. | spell name, cast result | the table's (BehaviourBug for bot logic) |
| spell no effect | 3 hits in a row of one bot's spell that should deal damage, heal or leave an aura (on the effects aimed at that target) and did none. A miss, resist, immunity or reflect is no hit; an overheal of a full target, a target that is dead, the spell's aura found on the target, and a damage hit on a target with an absorb shield (the outcome counts damage after absorbs) count as working. | spell name | NotImplemented |
| server fault | An exception the server caught and survived at one of its catch-and-log sites, from the fault ledger (`FaultLedger`, `.server faults`), polled once per QA pass (`PlayerbotQaFaultFeed`; one signal per changed ledger signature carrying its new occurrences). Bot-independent: `MinOccurrences` applies, `MinDistinctBots` does not. The bot update site (`playerbot`) is not fed (it is already an action fault with the bot's context) and neither is the ledger's overflow bucket. The issue shows site, exception type, ledger id, count, first/last seen, the top ArcaneCore frames, all through `PlayerbotQaRedactor.RedactFault`. The exception message is never published (it can name a player); it stays in the ledger for `.server faults show`. | the ledger id (16 hex) | ServerFault |
| run failed | A dungeon or raid run ended without finishing (`PlayerbotGroupCoordinator.NoteRunEnded`, once per run when its group disbands, straight to `IPlayerbotQaSink`, no log line): a give-up or a step timeout whose reason points at content or code: `no-path-to-entrance`, `no-path-to-boss`, `wiped` (out of `MaxWipes`), `objective-missing` (the boss is not at its spawn), `stalled`, and the step timeouts `gather-timeout`, `travel-timeout`, `enter-refused-or-timeout` (the enter step timed out, whether the entrance was refused or the group was slow), `regroup-timeout` and `run-timeout`. Operator and lifecycle ends are not findings and are never reported (they still count in `arcanecore_playerbot_runs_total`): `leader-stopped` (a GM stop, a population scale-down, a shutdown, a controller taking over, a player leashing the bot), `leader-left`, `leader-gone`, `player-gone`, `too-few`, `invites-unanswered`, `disbanded`, `aborted`. Bot-independent (`MinOccurrences` applies, `MinDistinctBots` does not). The subject reads `<reason> in <dungeon name> at <stage>`; the stage is the run machine's step when it failed (`travel`, `enter`, `clear`, `boss`, ...). Severity weight 24: a give-up is often the dungeon system working as designed (a wipe, a slow walk), so it ranks just under a relay gap and needs repeats at one stage before it becomes an issue. No bot, group or character name is in the signal. | map id, wing, stage, boss entry (only at `clear`/`boss`), reason | RunFailed (cause `ContentDifficulty` for `wiped`, `MissingData` for the no-path reasons, else `Unknown`) |
| tick hang | The world tick ran past the watchdog's hang threshold (`TickMonitor` alert, log event 7102, `TickWatchdogFeature`), reported on the watchdog thread straight to the sink. Bot-independent, weight 50: the only kind that freezes every player, so it outranks the worst bot fault (45). It needs `MinOccurrences` like the other kinds (one hang can still be the host, and a one-off kind would reopen closed issues on every deploy). A hang is not reported to QA when it falls in the start-up warm-up (`TickHangWarmupMinutes`, default 5, counted from the watchdog feature attaching), after shutdown began, when the host-load verdict says `host-starved`, `gc-pauses`, `gc-paging` or `process-stalled` (a `memory-starved` hang is still reported, with `|memory-starved` appended to its work token so it groups apart from code hangs), or when GC pauses took 30% or more of the tick. A hang still running is reported once per (phase, work); a completion alert reports only the hangs no running report stood for (several can end between two checks). For a completed hang the work follows the dominant phase of the hung frame's own tick (the tick that ended it, snapshotted when the next tick starts; `OutsideTick|none` when the time outside the tick body is more than the body): `map` for the maps phase, `commands`, `worldtick-handlers` and `autosave` as fixed tokens, the slowest feature only when the feature phase dominates. | tick phase (`Commands`, `WorldTick`, `Maps`, `Features`, `Autosave`) and the kind of work in it (a feature or handler class, `map`, `commands`, `autosave`, `none`); never a duration or time | TickHang (cause `ServerFault`) |

A think over `SlowThinkLogMs` only because the garbage collector paused the process (think time minus the GC pause under the threshold) is
counted as `bot-brain/think-gc-stall` at Info, below the anomaly feed's Warning minimum, so QA never files it, as a hang under `gc-pauses`
is not reported; it stays visible in `.debug botthink`. Its episode is separate from `think-slow`'s, so a real slow think after it is still reported.

The anomaly feed also admits six world (non-`bot-*`) ledger keys, listed in `PlayerbotQaAnomalyFeed.WorldKeys`, because a bot that uses the world can trigger them and a human player seldom reports them: `loot/gameobject-loot-empty` (a chest opened with a loot template that must have dropped something but generated nothing), `loot/gameobject-loot-no-data` (a chest whose loot template names no rows or only unknown items), `gameobject/use-failed` (a use refused for a server defect, not a player mistake), `gameobject/use-failed-unknown-lock` (a use refused because the object's lock id has no `lock_template` row), `gameobject/open-no-effect` (an open-lock cast on a chest that returned Ok and left no loot window, unless an object script took the open or a dungeon chest's generation is still pending) and `spell/gameobject-effect-skipped` (a cast aimed at a game object whose effect was dropped before it ran). They pass the same Warning minimum and the same count or distinct-subject thresholds as `bot-*` keys, and an Error qualifies on its first occurrence. The classifier files the two content-defect keys, `loot/gameobject-loot-no-data` and `gameobject/use-failed-unknown-lock`, as missing data and every other world anomaly as a behaviour bug; it goes by the key, never by the wording of the last occurrence.

A gap code `no-consumer` (a DUMMY aura nothing known reads) is **advisory**: any rule can read such an aura by spell id, icon or family,
so it is listed and counted but never published.

### Spell coverage (static)

The first pass after the spell table is loaded (computed on the world thread, which owns the spell registries, through one `InvokeAsync` a server lifetime; folded in on the QA task) computes `PlayerbotQaSpellCoverage`: of the spells
bots can cast, which have a gap. The spells bots can cast are the class spell books at trainer level: the class spells class trainers
teach (`npc_trainer`, the LEARN_SPELL targets of the teaching spell or the row's spell), the start spells of every race and class and the
talent rank spells, restricted to the class spell families (an NPC spell of the same name never counts) and without passives; those a
class rotation or an escape names are the rotation spells. It runs again only if the first run saw no trainer rows and they loaded since.
Each gap becomes a `spell unsupported` finding marked as coverage with no occurrence, keyed like a bot's cast of it, so the first bot cast
counts on the same finding. A finding with no occurrence is left out of the ranked list, the chat top and the publisher; `report.md` ends
with a **Spell coverage** section (the summary, the gaps shared by the most spells, every gap) and `report.json` has `SpellCoverage`
(the numbers, `Offenders`, `Gaps`). A coverage finding never evicts a finding of real occurrences at `MaxFindings` (it is evicted first).
`.playerbot qa spells [n]` shows the summary, the five most shared gaps and the top spell findings bots met.

## Fingerprint: one problem is one finding

`PlayerbotQaFingerprint` builds a canonical key from the kind and **content ids only** (quest, target entry, goal, map, a 50-yard
cell of the position, the fault code and a normalised message) and hashes it with SHA-256 to 16 hex characters. Ten bots meeting
the same dead quest give one finding. Bot ids and names are never part of the key. The hash is also the dedup marker in the GitHub
issue, so the key format is versioned (`v1`): changing it would orphan existing issues and must bump the version.

Per finding the aggregator keeps the count, first and last time, up to `MaxSamples` newest evidence samples, and the number of
distinct bots. Bots are counted by a salted SHA-256 of the id (the salt is saved, so counts survive a restart; at most 512 are tracked
per finding, then the count saturates). Nothing keeps a bot name or id.

**Which finding a stall or death loop belongs to is coarser than its key.** The key (and so the id and the issue marker) is that of the
finding's *first* signal and never changes; there is no key version bump and no migration of published findings. What decides whether a
later signal is the same problem is the group, computed in memory (and recomputed on load) from the same content ids plus a 500-yard zone cell:

* Stall, Quest goal: the quest and target on the map, wherever on it they stood. Combat goal: the target entry on the map (a city guard stalling in two capitals is two findings).
  Any other goal (Explore, Retreat, Loot...): the goal in a zone cell; a stall just across a cell border still folds into the finding whose anchor (its first sample,
  which never moves) lies within 160 yards, the nearest first, the lower id on a tie. The outside-home flag keeps its own group.
* Death loop: the map and zone cell (or an anchor within 160 yards) and its attackers. A loop that shares an attacker entry with a finding of the area is that
  finding; a loop that shares entries with several merges them; with no attacker on either side the death source decides. A finding collects at most 8 attacker
  entries from its signals (a real loop has one to three): a bound, so one entry that is common to a whole zone cannot chain every loop of it into one finding.

Signals find their finding through indexes (by group, by group base, by map), not by scanning every finding.

The position stays in the samples and the subject of the first signal. **Store hygiene:** a finding not seen for `StaleHours` (24) that has no issue is dropped on load
and on every pass, except one the publisher is working on at that moment; at `MaxFindings` the lowest-value unpublished finding is evicted (a single occurrence of a single bot
first, then lowest severity, then the oldest), and a finding that has an issue is never evicted or dropped by the cap (its issue number is the only local memory of it).

**Merging never loses an issue.** The survivor of a merge is the finding with an open issue (then one a person closed), the lowest issue number first, else the busiest; it keeps its id and
publication state. Every absorbed id is recorded in the store (`Absorbed`: the id, its survivor, and its issue number if it had one that the survivor's does not replace) and is never reused
for another problem. A publish that was in flight when its finding was absorbed reports its outcome to the survivor (it becomes the survivor's issue when the survivor had none; otherwise it is
recorded as another absorbed issue). The issue of an absorbed published finding can then be closed as a duplicate: a comment ("Duplicate of #N", numbers only, no name or id of any bot), then the close as not planned.
This is `World:Playerbots:Qa:GitHub:CloseAbsorbedDuplicates`, **off by default** so the first start after an upgrade can be checked: the start-up log lists every pending close as
`issue #a -> duplicate of #b` (numbers only) and says whether it will happen. Turned on, an issue is only touched when it is in the labelled issue list and open, and the
surviving issue is in the list and open too (an open issue is never closed in favour of a closed or unknown one; otherwise the entry stays pending and is logged). The comment is recorded
before the close is tried, so a close that fails is retried without commenting again; a comment that itself fails is retried. The closes run after the normal publishing of the pass and
share the hourly comment budget (`MaxCommentsPerHour`) with it, so the comments of findings that are still happening come first and a long list of closes spreads over several hours.
An issue handed over to the survivor (the publish was in flight when the merge happened) is found by its number, not only by marker, so it is commented and never opened again.
The survivor of such an in-flight publish is protected from age-out and eviction like the finding itself; an entry whose survivor no longer exists is dropped (the issue stays open as it was).
Each merge of two issues is logged as `issue #a ... absorbed into #b`; the start-up line counts the merges and the issues left to close.

Turning it on is configuration only (the code is in place): set `World:Playerbots:Qa:GitHub:CloseAbsorbedDuplicates` to `true` in the profile's `appsettings.json`
(`"Playerbots": { "Qa": { "GitHub": { "CloseAbsorbedDuplicates": true } } }` under `World`), or the environment variable
`World__Playerbots__Qa__GitHub__CloseAbsorbedDuplicates=true`, and restart. Check the start-up `issue #a -> duplicate of #b` lines first. Each close costs one comment of
`MaxCommentsPerHour` (10), after the pass's own comments, so a backlog of 65 duplicates takes roughly 7 hours or more. The closes run only in a pass that got that far: while the
`gh` issue list timed out (before 2026-10-17) no pass did, so the switch would have closed nothing.

## Classifier: why it probably happens

`PlayerbotQaClassifier` asks the loaded content (`IQaContentFacts`, read-only) and names a likely cause. It makes no claim while the
content is not loaded.

* **MissingData**: the stall target has no spawn on that map, no `creature_template` row, or names a `ScriptName` the server does not
  have; none of a quest's objective creatures has a spawn; a relay script is missing or its waypoint path does not exist.
* **BehaviourBug**: an action fault, a quarantine or a disabled bot; a repeated environmental or self-inflicted death. Its detail names a recorded fatal source (fall, drowning, fatigue, lava, slime or fire) when known. A death with a player killer, a periodic tick whose caster is unavailable, a null killing-blow source or a missed observer is Unknown rather than asserted to be a bot movement bug. Historical warnings with no source keep their original BehaviourBug classification.
* **ContentDifficulty**: a death loop against an elite, rare or boss, a guard observed hostile to the bot, against attackers three or more levels above the bot (the quest level is used only for old warnings without a bot level), or
  against three or more different attackers.
* **OutsideHome**: a stall logged as made outside the bot's home area (checked first). Expected for a homed bot on its way back or on an
  errand out of its home; it set the goal aside for that bot only. Such a stall is its own finding (its key ends `|outside`; the keys of
  the other stalls are unchanged; a death loop is never marked) and is **never published** to GitHub; the report lists it. A shared set-aside counts only stalls made
  inside the home (docs/areas/playbots.md, Home area), so it is never an outside-home finding.
* **NotImplemented**: a spell gap or a spell with no effect, and the server-side refusals (Error, Unknown, SpellUnavailable).
* **Unknown**: anything else; the detail says what was checked.

A stall's `quest=` is the last quest the bot's brain remembers, not necessarily what it was doing: it stays set after the quest goal
ends. Only a stall whose goal is `Quest` (and a shared set-aside) is checked against the quest's content; any other goal is classified by
its own target, and its Unknown detail reads `no missing content found for goal Explore`. Before 2026-10-11 a non-quest stall with a
remembered quest was checked, titled and explained as that quest (GitHub #154: every line said `goal=Explore target=0 quest=109`, 178 of
179 such bots had already turned 109 in, yet the issue read as a quest-109 problem).

Severity ranks findings: a weight per kind (quarantine/disabled 45, fault 40, set-aside 35, death loop 30, spell unsupported 30, spell no
effect 28, relay data 25, spell refused 20, stall 15),
plus `10 * log2(1 + count)`, plus 5 per distinct bot (at most 20), plus 10 for a finding seen in the last hour.

New stall and death-loop evidence samples include the bot's level, race, team, class, reported goal and quest, and the release graveyard id and straight-line distance when a death's release selected a location on the death's map. A living stall has no release graveyard; a cross-map release has no comparable distance. Death samples retain the source of each death in the loop and each observed creature attacker's actual level, template level range, rank, guard flag and reaction to that bot. The map kill event supplies the actual killer even when it attacked from outside the bot's last combat scan; fatal environmental damage supplies its type. The reporter waits until the release hook has run, including a scheduled release after movement acknowledgment, before logging a death-loop sample. These fields live on samples, not on the fingerprint: one issue can still aggregate several bots with different levels or graveyards.

## Report and exports

`.playerbot qa` lists the top ten, `.playerbot qa top [n]` up to 25, `.playerbot qa show <id>` shows one finding (at least 4 hex
characters of the id) with its samples and a `.go xyz` command to the place, and `.playerbot qa reset [<id>]` forgets findings
locally (GitHub is never touched), `.playerbot qa prune` forgets the stale ones. Ranking is severity, then count, then last seen (newest first), then id, so equal findings always
list the same way. Every chat line is at most 220 characters and redacted.

**Stale findings.** A finding last seen before this process started was not met by the running build, so it may already be fixed
(the findings file keeps them across restarts). `.playerbot qa` and `.playerbot qa top [n]` first fold in the queued warnings, then hide
the stale ones behind one hint line ("N stale findings (not seen since this server started) hidden"); `.playerbot qa top [n] all` lists
them after every current one, marked `(stale)`. A stale finding whose issue is open (Published) is not hidden: it is listed in its place,
marked `(stale)`, and `report.md` keeps it in its main table the same way. Every `top` line shows when the finding was last seen (`<1m ago`, `5m ago`, `2h ago`, `1d ago`). `report.md` lists the stale ones
last under "Stale (not seen since <start>)" (one table row each, no evidence block) and has a "Last seen" column; `report.json` marks them `"Stale": true`.

**Build stamps and the quiet signal.** Every occurrence is stamped with the running build (`PlayerbotQaIssueComposer.BuildVersion()`, the same string the issue
shows). A finding keeps `FirstBuild`, `LastBuild` and the occurrence count of the **current and the previous build only** (`BuildCounts`, newest first, at most
two entries, so it never grows; the counts of older builds are dropped, `FirstBuild` stays). `.playerbot qa show <id>` prints a `builds first A, last B; per build ...;
on this build (X): N` line. `report.json` carries the same per entry (`FirstBuild`, `LastBuild`, `Builds`) plus the signal for "a fix landed and the problem stopped"
versus "nothing ran there": `SeenOnCurrentBuild` and `OccurrencesOnCurrentBuild` say whether the file's `Build` met the finding, and the file's `BuildSinceUtc` is when this
process (so at least that build) started. An external tool can say "published issue #N: 0 occurrences in H hours on build X" from `GeneratedUtc - BuildSinceUtc` when
`SeenOnCurrentBuild` is false. `BuildSinceUtc` is a lower bound (an earlier run of the same build is not tracked). `Stale` keeps its meaning (last seen before this
process started). `ClosedBuild` (the build the finding was first observed `KnownClosed` on) and `ReportedBuild` (the build of the issue's last create, reopen or comment) are
exported too.

`findings.json` stays at format version 1: the build stamps and the build registry (`Builds`: each build this store has run, with the time it was first seen, the newest 32) are additive and nullable. A file from before them loads with every build unknown (null, no counts) and the first occurrence on the running build stamps it; an older build ignores the new members, so a rollback keeps its ledger and its known-closed memory. The registry is what orders builds: build strings do not order, so "newer" means first run by this store after the reference build. A finding stamps the first-seen time of its `ClosedBuild` and `ReportedBuild` when it sets them (`ClosedBuildSeenUtc`, `ReportedBuildSeenUtc`), so the comparison holds after a build leaves the 32-entry registry; builds evicted from it are kept (with their time, newest 512) in `EvictedBuilds`, so a rollback to one is still known to be old. A reference with no stamp and no entry anywhere (old data, or older than both lists) never reopens. A finding whose closed issue GitHub marks `not_planned` is flagged (`NotPlanned`) and never looked up again for regressions.

**Pruning.** `.playerbot qa prune [#hours] [closed] [dry]` forgets the findings not seen for `#hours` (default 12, 1..720); `dry` lists what
would go (at most ten lines) and changes nothing. The reply gives the count by kind, how many remain, and how many old findings were kept.
It is local only (GitHub is never touched) and takes effect on disk at the next save, like `reset`.

- **A finding that holds a GitHub issue is never forgotten by age.** Its issue number is the only local memory of the issue: a marker lookup
  cannot always find it again (an issue handed over by a merged finding, a death-loop variant, the `gh` transport without search).
- **`closed`** also forgets findings whose issue is known closed (`KnownClosed`), whatever their age. It leaves a persisted tombstone (at most 1000,
  oldest first) for the finding and for the ids merged into it, so a recurrence takes the closed issue number back and stays `KnownClosed`: no new issue,
  no comment (unless `ReopenClosed` is on, which does that for any closed finding). A death loop's tombstone also keeps its attackers, so a different
  loop that hashes to the same id never takes its issue.
- A finding without an issue that recurs is created again with the same id; the publisher finds an existing issue by its marker as usual, and if that
  lookup finds nothing, it files a new one like for any new finding.
- `reset` keeps the tombstones (they are only memory of closed issues); a finding that recurs after a `reset` of a pruned one is still `KnownClosed`.
- The `Tombstones` field is additive (format version 1, still read; see Build stamps), an older build ignores the unknown field and drops it on its next save
  (a recurrence is then found by its marker, as before this feature).
- Skipped: a finding the publisher is working on, a finding with a duplicate issue still to close, and the start-up spell-coverage findings (by age).

`World:Playerbots:Qa:TickHangWarmupMinutes` (default 5, 0..1440) is the start-up window, counted from the watchdog feature attaching, in which a tick hang is not reported to QA (cold grids, JIT, loading); 0 reports from the first tick. `World:Playerbots:Qa:AutoPruneHours` (default 0, off) runs the same age prune (without `closed`) on every pass, counted from the later of a
finding's last sighting and the server start, so a restart never prunes anything before the age has passed in this process. It logs the short ids it forgot.

Every `ExportIntervalSeconds` (default 300) one pass saves `qa/findings.json`, writes `qa/report.md` and `qa/report.json` (ranked,
redacted, no bot identity) and runs the publisher. `Directory` is relative to the profile folder or absolute. Writes are atomic (temp
file, then replace). The findings file fails closed: one that cannot be read, or that a newer version wrote, is never overwritten;
persistence is then off for the process (one Error log, shown by `.playerbot qa`) while aggregation continues in memory.

## Reporting to GitHub

Off by default. It is a three-step ladder; each step needs an explicit setting.

1. **Off** (default): `World:Playerbots:Qa:GitHub:Enabled` is false. Findings are local only.
2. **Dry run**: set `World:Playerbots:Qa:GitHub:Enabled` = true and `World:Playerbots:Qa:GitHub:Repository` = `owner/repo`. Nothing is sent and no token is read. Each
   eligible finding is written to `qa/pending-issues/<id>.md` (the exact issue body) and `<id>.json` (title, marker, labels, counts), and
   logged. Read them; this is what would be opened.
3. **Publish**: create a fine-grained personal access token limited to that one repository with **Issues: read and write**, put it in
   the environment variable `ARCANECORE_GITHUB_TOKEN` of the server process (the variable name can be changed with
   `World:Playerbots:Qa:GitHub:TokenEnvironmentVariable`), then set `World:Playerbots:Qa:GitHub:Publish` = true and restart. The token is read from the environment only:
   there is no token key in the configuration, and a `Token` key is warned about at start-up and never read. `.playerbot qa` shows
   the variable as set or not set, never its value. With publishing on and the variable empty, nothing is sent and one warning is logged.

### Transport: REST or the `gh` CLI

`World:Playerbots:Qa:GitHub:Transport` picks how GitHub is reached; both share the dedup marker, the caps, the redaction and the dry run.

* `Rest` (default): the REST API with the token from the environment, as above.
* `GhCli`: the server runs the GitHub CLI (`gh`) with the login of the user it runs as, so no token variable is needed (log in once with
  `gh auth login`). `World:Playerbots:Qa:GitHub:GhPath` is the executable (default `gh`, looked up on `PATH`; or a full path). Every call is
  one process started with an argument list (never a shell), in the temp folder as working directory, with a 30 second timeout (the child is
  killed), stdin closed and `GH_PROMPT_DISABLED=1`. `GITHUB_TOKEN` and `GH_TOKEN` are removed from the child so `gh` uses its own login;
  set `World:Playerbots:Qa:GitHub:GhPassToken` = true to hand it the token from `TokenEnvironmentVariable` as `GH_TOKEN` instead. Issue and
  comment bodies go through a temp file (`--body-file`, deleted afterwards) and never on the command line. The calls are
  `gh api --method GET repos/R/issues?labels=L&state=all&per_page=100&page=P` (the list: one call per page of 100, pages 1.. until a short page or `MaxListPages`,
  each call under its own 30 s timeout), `gh issue view N`, `gh issue create --repo R --title T --body-file F --label L`, `gh issue comment N --repo R --body-file F`,
  `gh issue edit N --repo R --body-file F` (the live status), `gh issue reopen N` and `gh issue close N --reason "not planned"` (a duplicate) or `--reason completed` (a quiet close).
  Before 2026-10-17 the list was one `gh issue list --limit 1000` with every body: at 635 issues (about 1.9 MB) it took 20-40 s, was killed at 30 s on most passes, and the
  pass stopped before publishing, commenting or closing anything.
  There is no search: an issue whose label was removed by hand is not found, and if the list was cut at its limit a finding with no known
  issue stays pending (a warning says so) instead of risking a duplicate. A dry run starts no process and does not look for `gh`.

A finding is eligible once seen `MinOccurrences` times (default 3) by `MinDistinctBots` different bots (default 2; relay data findings
come from creature scripts, not bots, and are exempt from the bot count).

### Dedup and closed issues

Every issue body starts with a hidden marker, `<!-- arcanecore-bot-qa:<fingerprint> -->`. Before creating anything the publisher
looks the marker up in the labelled issues (state `all`, open and closed, up to `MaxListPages` pages of 100), which a pass **reads once** and
indexes by marker and number, not once per finding. Only `Rest` then falls back to GitHub search for an issue whose label was removed by
hand. Pull requests are ignored, and the match is the exact marker text. A per-fingerprint lock makes find-then-create atomic inside the
process.

Calls per pass are bounded: one list; a finding with no known issue is skipped before any lookup while the hourly create cap is used up (a
zero cap makes no call at all); a finding whose issue is known and has nothing due makes no call; and the lookups outside the list (an
issue by number, the search) are capped at 5 per pass, 2 seconds apart, the finding that would need a sixth waits for the next pass.

A finding is eligible once seen `MinOccurrences` (3) times by `MinDistinctBots` (2) different bots, and is not an `OutsideHome` one. A shared
set-aside line carries no bot ids: its bot count is the `BotsToShare` (3) the line names, so it always meets the bot threshold, and its
occurrences are the set-aside's repeats (30, 60, 120 min): three of them mean three separate windows in which three bots stalled. Its subject
reads `quest Q (title) set aside for every bot after 3 stalled` (before 2026-10-11: `set aside by every bot`, which read as if every bot had
stalled; the fingerprint, and so the issue marker, is unchanged). When a stall finding for the same quest or target entry is already tracked,
the set-aside stays in the local report and is not opened as a second GitHub issue (the stall finding carries the per-bot samples and
positions).

A stall's subject always names its goal: `goal Explore map 0 near -10350,1400`, and only for a Quest goal also the quest:
`goal Quest, quest 109 (Report to Gryan Stoutmantle) target 6747 map 0 near ...`. The issue body has a `Goal` row; the quest row is `Quest`
for a Quest goal and `Last quest (not the goal)` otherwise (with no `.lookup quest` step), and `report.md`, `report.json` (`Goal`,
`QuestIsGoal`) and `.playerbot qa show` say the same. `Goal` is set in `report.json` for every kind (a set-aside's is `Quest` or `Entry`,
a fault's the action that threw); `QuestIsGoal` is false only for a stall of a non-quest goal. A quest set-aside is checked against its
quest's content; an entry set-aside is checked as a creature (spawn, template, script), as before. The fingerprint is unchanged (its key already held the goal, and still holds the
remembered quest, so the markers of existing issues still match): a findings file written before this gets each stall's goal and subject
back from its key on load (only a key that rebuilds to itself), and keeps its id and issue. A published issue's title is never edited, so
#154 keeps its old title; its cause text in the local report is recomputed with the finding's next stall.

* **No issue**: create one (title `[bot-qa] kind: subject`, the configured `Labels`).
* **Open issue**: add a comment with the new occurrences, at most once per `CommentIntervalHours` (default 24) per finding.
* **Closed issue**: left closed and remembered as known-closed (no API call on later passes). With `World:Playerbots:Qa:GitHub:ReopenClosed` = true it is
  reopened and commented instead.
* **Regression** (`World:Playerbots:Qa:GitHub:ReopenOnRegression`, default true): a finding whose issue is known closed that **recurs on a NEWER build than the one it was
  first observed closed on** (`ClosedBuild`; for a finding closed behind our back, the build of the issue's last create, reopen or comment; for a file from before build stamps or a
  tombstone, the build that last met it, else the running one) is a regression, provided the running build met it at least `MinOccurrences` times (one for a single-occurrence kind) and is newer than that reference by the registry: a rollback to an older build, or a build the registry does not know, never reopens (nothing reopens on doubt), and neither does an issue GitHub shows as closed `not_planned` (a decision; `ReopenClosed` still does). The issue is reopened and gets **one** comment: the new build, the occurrences on it, the total, the
  distinct bots and the latest sample. A recurrence on the **same** build is a person's won't-fix and is left closed (and counted nowhere else). The reopen and its comment
  are one step under `MaxCommentsPerHour`: with no budget the issue stays closed and the next pass retries, and the comment ignores `CommentIntervalHours` (it is not a routine
  occurrence note). The option only acts while publishing is on, and it never closes anything (closing is the quiet close below, off by default).
  How it interacts with `ReopenClosed`: `ReopenClosed` (default off) reopens a closed issue for **any** recurrence on any build and keeps its old comment rules; with it on,
  `ReopenOnRegression` only chooses the comment, so a regression gets the regression comment (when the comment budget allows) instead of the routine occurrence comment. With
  `ReopenClosed` off, `ReopenOnRegression` is the only way a closed issue is reopened; with both off a closed issue is left alone.

* **Live status** (`World:Playerbots:Qa:GitHub:StatusUpdateMinutes`, default 60; 0 is off): every issue body carries, right under its marker, a status block between
  `<!-- arcanecore-bot-qa-status:begin -->` and `<!-- arcanecore-bot-qa-status:end -->`. Its first line is machine-readable:
  `<!-- arcanecore-bot-qa-status last-seen=2026-10-17T04:58:12Z count=1234 build-count=56 bots=12 -->` (last seen UTC, occurrences in all, on the running build, distinct
  bots); the second says the same in words. Comments come at most once per `CommentIntervalHours` (24), so GitHub's idea of when a finding last happened used to lag by up to a
  day; now, when an open issue's finding has been seen at least `StatusUpdateMinutes` after the time its block shows, the pass rewrites the block in place (one body edit, which
  sends no notification; the rest of the body is kept as it is; an issue opened before this gets the block inserted after its marker). So GitHub lags the finding by at most
  `StatusUpdateMinutes` plus one pass, and a stale-issue sweep can trust `last-seen=` (and the issue's `updated_at`). Edits are capped by `MaxStatusEditsPerHour` (30, saved with
  the other caps); a finding over the cap waits for a later pass. Only issues in the label list are edited. Closed issues are never edited. The status is tried once per
  `StatusUpdateMinutes` of new sightings whatever comes of it (an issue outside the list, a spent lookup budget, a failed edit), so an issue the list does not carry never
  makes later passes list again.
* **Quiet close** (`World:Playerbots:Qa:GitHub:AutoCloseQuietMultiple`, default 0 = off): with N > 0, an open issue is closed as **completed** when its finding has stopped:
  it was met at least twice (so it has a usual gap, `(last seen - first seen) / (count - 1)`), it was last met on a build other than the running one, the running build is
  newer by the registry (first run after it; a rollback never counts) and has not met it once, and it has been quiet for at least N x its usual gap and at least
  `AutoCloseMinQuietHours` (72). Quiet is measured from the later of its last sighting and the reporter's start, so a server that was down does not count as quiet time.
  The close comes with one comment (last seen, on which build, the usual gap, how long the running build went without it) under `MaxCommentsPerHour`, after the pass's other
  comments and the duplicate closes; a close that fails after its comment is retried without a second comment (in this process). The finding becomes known-closed with the build it was **last seen on** as its closed build, so if it happens again on the running build
  (or any newer one) it is a regression and `ReopenOnRegression` reopens it with its one comment. An issue a person closed as not planned is never touched. Quiet is not proof:
  a finding the bots simply stopped visiting looks the same, which is why it is off by default; something like 3 x the gap with the 72 h floor is a reasonable start.

A POST is never sent twice. If a create or comment times out, gets a 5xx or loses the connection, GitHub may already have applied it, so
the call is **not** retried: the finding is marked Failed and the next pass's marker lookup finds what was created (or creates it if it
was not). Only a rate-limit refusal (429, or 403 with `Retry-After` / an exhausted `x-ratelimit-remaining`) is waited out (at most 15
minutes) and resent, since a refused request was not applied. Reads and PATCH back off 1 s, 2 s and give up after 3 attempts. A
transient failure stops the pass (the rest would fail the same way); a refusal (bad permissions) fails that finding and goes on.

### Rate caps

`MaxIssuesPerHour` (default 5) and `MaxCommentsPerHour` (default 10) are rolling-hour limits (0 turns the action off). The times
are saved in `findings.json`, so restarting the server does not reset them.

Spell findings can be numerous (a first deploy meets every unimplemented spell at once), so they have two more caps on top of the hourly
one, counted only for the spell kinds: `MaxSpellIssuesPerPass` (default 2) per publish pass and `MaxSpellIssuesPerDay` (default 10) per
rolling 24 hours (saved with the hourly times). With the defaults a first deploy opens at most 2 spell issues per pass, 5 per hour (the
shared cap) and 10 per day, highest severity first; the rest wait. A gap only the start-up coverage lists is never published on its own
(the report lists them all), so the issues are the spells bots really cast; advisory gaps are never published. Each issue is one spell
effect of every rank (the fingerprint keys the spell by name); its body has a **Spell** table (spell and rank, effect index, name and value, aura type, gap or cast result,
the bots and classes that met it, a `Wowhead classic spell=<id>` reference as text, nothing fetched) and the reproduction
(`.learn <id>` and `.cast <id>` on a character or a selected bot). `World:Playerbots:Qa:GitHub:KindLabels` (off) adds a per-kind label such as
`bot-qa:spell-unsupported`; leave it off with the `gh` transport until the labels exist in the repository (`gh` refuses unknown labels). A finding over a cap stays pending and is picked up in a
later pass, highest severity first. Publishing runs on a task of its own (at most one at a time; a pass that finds the previous one still
running does not start another), so a long `Retry-After` wait or a slow GitHub never stops the bot warnings from being drained. Requests time out after `RequestTimeoutSeconds` (15) and replies over 16 MiB are refused.

## Redaction and its limits

Everything that leaves the process (issue titles and bodies, comments, the dry-run files, the exports, chat) goes through
`PlayerbotQaRedactor`: GitHub tokens (`ghp_`, `github_pat_`, ...), `Bearer` headers, `sk-` keys, `key=value` secrets, the literal
token in use, IPv4 and IPv6 addresses, e-mail addresses, guids, per-user profile folders, and the names of all current bots and
accounts (refreshed every pass; names under 3 characters are ignored). Titles are limited to a plain character set and 120
characters; bodies are cut at a line boundary under 60000 characters and always keep the marker. GitHub error replies are redacted and
cut to 300 characters before they reach an exception or a log.

The limits are real. Redaction is pattern based: a secret with no recognisable shape that the server does not know (anything other
than the configured token) can pass, and a bot name that is a common word is only replaced as a whole word. A name that was never
loaded as a bot is not known. The evidence the issue carries is game data (map, coordinates, entry ids, quest ids) and warning text;
read a dry-run file before the first publish.

## Operating notes

* The token is held only for one publish pass, in a local variable and the pass's HTTP client; the client sets the headers on each
  request itself and never follows a redirect, and a pagination link to another host is refused.
* Nothing in the publisher throws into the world: a failed pass logs and leaves the findings as they were.
* Tests: `tests/ArcaneCore.World.Tests/Playerbots/Qa/` (parser contract, fingerprint, classifier, log tap, redactor, report, store and
  ledger, GitHub client and publisher against a fake `HttpMessageHandler`, the commands on an in-process world; the kind exhaustiveness,
  the sink and the spell caps, the spell monitor on a real spell system with its allocation check and refusal table, each spell kind from a
  bot's cast to the created issue, the coverage). `PlayerbotQaSpellCoverageRealContentTests` prints the coverage of an imported world
  (`ARCANECORE_TEST_WORLD_DB`; `ARCANECORE_QA_COVERAGE_CONFIG` may name a profile's `appsettings.json` whose content sections, the DBC
  paths, let the skills, talents and enchanting features install their handlers) and writes `%TEMP%/arcanecore-qa-spell-coverage.md`.
