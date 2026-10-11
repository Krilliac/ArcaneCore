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

## Fingerprint: one problem is one finding

`PlayerbotQaFingerprint` builds a canonical key from the kind and **content ids only** (quest, target entry, goal, map, a 50-yard
cell of the position, the fault code and a normalised message) and hashes it with SHA-256 to 16 hex characters. Ten bots meeting
the same dead quest give one finding. Bot ids and names are never part of the key. The hash is also the dedup marker in the GitHub
issue, so the key format is versioned (`v1`): changing it would orphan existing issues and must bump the version.

Per finding the aggregator keeps the count, first and last time, up to `MaxSamples` newest evidence samples, and the number of
distinct bots. Bots are counted by a salted SHA-256 of the id (the salt is saved, so counts survive a restart; at most 512 are tracked
per finding, then the count saturates). Nothing keeps a bot name or id. At `MaxFindings` the lowest-severity unpublished finding is
evicted; a finding that has an issue is never evicted (its issue number is the only local memory of it).

## Classifier: why it probably happens

`PlayerbotQaClassifier` asks the loaded content (`IQaContentFacts`, read-only) and names a likely cause. It makes no claim while the
content is not loaded.

* **MissingData**: the stall target has no spawn on that map, no `creature_template` row, or names a `ScriptName` the server does not
  have; none of a quest's objective creatures has a spawn; a relay script is missing or its waypoint path does not exist.
* **BehaviourBug**: an action fault, a quarantine or a disabled bot; a death loop with no attacker.
* **ContentDifficulty**: a death loop against an elite, rare or boss, against attackers three or more levels above the quest, or
  against three or more different attackers.
* **OutsideHome**: a stall logged as made outside the bot's home area (checked first). Expected for a homed bot on its way back or on an
  errand out of its home; it set the goal aside for that bot only. Such a stall is its own finding (its key ends `|outside`; the keys of
  the other stalls are unchanged; a death loop is never marked) and is **never published** to GitHub; the report lists it. A shared set-aside counts only stalls made
  inside the home (docs/areas/playbots.md, Home area), so it is never an outside-home finding.
* **Unknown**: anything else; the detail says what was checked.

Severity ranks findings: a weight per kind (quarantine/disabled 45, fault 40, set-aside 35, death loop 30, relay data 25, stall 15),
plus `10 * log2(1 + count)`, plus 5 per distinct bot (at most 20), plus 10 for a finding seen in the last hour.

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
are saved in `findings.json`, so restarting the server does not reset them. A finding over a cap stays pending and is picked up in a
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
  ledger, GitHub client and publisher against a fake `HttpMessageHandler`, the commands on an in-process world).
