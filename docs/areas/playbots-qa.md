# Playerbot QA: findings and GitHub issues

The bots play the game all day and keep running into the same few problems: a quest whose target has no spawn, a place where bots
die over and over, a recovery that throws. They already log each one as a warning. Bot QA reads those warnings, folds the many
repeats into a few ranked findings, writes a report a person can read, and (only when asked) opens one GitHub issue per finding.
The bots themselves are untouched: QA listens to what they log. Configuration: `World:Playerbots:Qa` (read once at start; a change
needs a restart). Commands: `.playerbot qa` (GameMaster). Everything lives in `src/ArcaneCore.World/Playerbots/Qa/`.

## What it watches (signals)

A logger provider (`PlayerbotQaLogTap`) is registered next to the normal ones. It answers for two log categories only,
`ManagedPlayerbotFeature` (the bots) and `CreatureWorldFeature` (the creature scripts), only at Warning and above, and gives every
other category a null logger. A log call parses the formatted line (`PlayerbotQaSignalParser`) and does one non-blocking write to a
bounded queue (`PlayerbotQaSignalBus`, `SignalQueueCapacity`; a full queue drops the oldest and counts it). It does no I/O, takes no
lock beyond the queue's and never throws, so it cannot slow the world thread. One background task drains the queue.

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
locally (GitHub is never touched). Ranking is severity, then count, then last seen (newest first), then id, so equal findings always
list the same way. Every chat line is at most 220 characters and redacted.

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
  `gh issue list --repo R --label L --state all --json number,state,body,url --limit N` (N = `MaxListPages` x 100), `gh issue view N`,
  `gh issue create --repo R --title T --body-file F --label L`, `gh issue comment N --repo R --body-file F` and `gh issue reopen N`.
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
stalled; the fingerprint, and so the issue marker, is unchanged).

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
token in use, IPv4 and IPv6 addresses, e-mail addresses, guids, `C:\Users\<name>`-style folders, and the names of all current bots and
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
