# Realm list liveness, states and character counts

What the realm-list screen shows for a realm, and how the world and logon daemons agree on it. The client has three
visible things per realm: a name colour (grey and not selectable when the realm is offline, red, green, or gold), a
population text, and a character count. This page covers the first and the last; the population is
`World:Realm:PlayerLimit` ([installation](../guide/installation.md) section 4). The population hint is 2 x sessions / `PlayerLimit`
where sessions is every session in the session registry, playerbot sessions included, so a realm full of bots reads as busy
(`RealmStatusFeature`, vmangos `World::AddSession_`, World.cpp:340-353).

Reference: vmangos `realmd/AuthSocket.cpp:1050-1135` (`LoadRealmlistAndWriteIntoBuffer`), `shared/Common.h:149-160`
(`RealmFlags`), `mangosd/Master.cpp:215,330`. The colours and the status text come from the client's own
`Interface/GlueXML/RealmList.lua`, read for ground truth and not shipped here.

## 1. What the client does with the flags

| Wire flag | Meaning | Client result |
|---|---|---|
| `0x02` Offline | the realm is down | name grey, row cannot be selected |
| `0x01` Invalid | vmangos names it `REALM_FLAG_INVALID` | name red, row stays selectable |
| none, character count above 0 | healthy, the account has characters here | name green |
| none, character count 0 | healthy | name gold |

`0x02` is verified in the client script. `0x01` as red comes from other emulators (WCell, MangosSharp, arcemu) and is
**not verified in the 1.12.1 client binary**; it is therefore behind `Realm:Liveness:RestartingAs` (section 3), and
`Offline` is the verified alternative.

## 2. States

The world writes a state and a heartbeat into its `realmlist` row (auth schema 7):

| State | Written when | The logon daemon sends |
|---|---|---|
| `Unknown` (0) | a row nobody manages (default for existing rows) | the stored flags; no heartbeat is enforced |
| `Starting` (1) | the process begins starting, before the world loads | stored flags plus Offline |
| `Online` (2) | the listener is bound **and** the world loop has started a tick | stored flags, if the heartbeat is fresh |
| `Restarting` (3) | a `.server restart` countdown is running and the world still serves | red (`Invalid`) or Offline, if the heartbeat is fresh |
| `ShuttingDown` (4) | a `.server shutdown` countdown is running | stored flags, if the heartbeat is fresh (shown normal) |
| `Stopped` (5) | a clean stop | stored flags plus Offline |

A heartbeat older than `Realm:Liveness:HeartbeatStaleSeconds` (60) turns any running state into Offline, which is how a
crashed, killed or hung world disappears from the list: nobody has to write "offline" for a process that can no longer
write. The gap after a restart countdown ends and before the new process is ready is Offline (`Stopped`, then
`Starting`).

A realm locked above the account's security level (`realmlist.AllowedSecurityLevel` greater than the account's GM level)
is also sent Offline, in every state (vmangos AuthSocket.cpp:1088-1090). The 1.x client has no locked display, so
the lock is shown as offline. The lock compares `AllowedSecurityLevel` with the account-wide GM level, not a per-realm level as vmangos's `GetSecurityOn` does (AuthSocket.cpp:1088). `.server plimit` sets the lock (Administrator, or the console): a security name (`gamemaster`) or a negative number locks the realm to accounts of at least that level and writes `allowedSecurityLevel` to the row off the world thread; `.server plimit <count>` limits the players online, `.server plimit reset` restores `World:Realm:PlayerLimit`, and the command alone prints both. A world refuses a login below the lock, or a plain player once `PlayerLimit` other accounts are online, with AUTH_UNAVAILABLE (vmangos queues the second case; ArcaneCore has no queue). Staff accounts skip the limit. A start writes the lock in force (none) to the row, so a lock never outlives the process that set it. A failed write is logged and the next change or start writes it again; the realm console `realms` listing prints the stored `lock`.

## 3. Configuration

| Key | Daemon | Default | Meaning |
|---|---|---|---|
| `World:Realm:HeartbeatIntervalSeconds` | World | 10 | seconds between heartbeats |
| `Realm:Liveness:HeartbeatStaleSeconds` | Realm | 60 | offline after this long without a heartbeat; 0 turns the check off |
| `Realm:Liveness:RestartingAs` | Realm | `Invalid` | `Invalid` (red) or `Offline` while a restart countdown runs |

Start-up refuses a stale threshold below three times the interval (a missed beat or two must never flip the realm) and
at or below five times `Ops:Watchdog:TickMonitor:HangMs` in seconds (a tick the watchdog calls a hang must not nearly take
the realm offline). Each daemon checks the file it reads and the other's keys at their defaults.

## 4. How the world writes it

`RealmLivenessService` is a hosted service registered **before** the world host, so the row says `Starting` (Offline)
from the moment the process starts, long before the world accepts a connection (vmangos clears Offline only after
start-up, Master.cpp:215, and a client has no "starting" state, so a starting realm is offline).

* The world loop does one thing for liveness: it counts the ticks it starts (`WorldRuntime.TicksStarted`, a plain add and
  a volatile store; no database, no allocation).
* A background timer, **never the world thread**, wakes once a second. Every `HeartbeatIntervalSeconds` it writes
  `HeartbeatUtc` **only if** the tick counter advanced since the last write; otherwise it withholds the beat and logs a
  rate-limited warning. A hung or deadlocked loop therefore stops beating while the process is up, and the realm goes
  offline once the last beat is older than the stale threshold. A slow world (one tick every few seconds) or a stall
  shorter than the threshold never flips the realm. When ticks resume, beats resume and the realm returns by itself.
* Online needs two facts: the listener is bound (`ApplicationStarted`) and the loop has ticked. Offline is then cleared
  and the population set to 0; a population hint written before that moment is written again right after it, so the row does not stay at 0.
  The population hint counts every session in the session registry, bot sessions included.
* Countdown changes arrive on the world thread, which only records the new kind; the timer writes the state within a
  second. A state write carries the last beat, not "now", because changing a state is not proof the loop is ticking.
* A failed write is logged (fault site `realm.liveness-write`, throttled) and the next pass tries again.
* A clean stop (`BeforeWorldStop`) stops the timer, then writes Offline and `Stopped`.

Not written by the world: with `World:Realm:Id` 0, with no auth database, and in gateway worker mode (`Cluster:Worker:Enabled`),
where an active and a standby world share the realm and the gateway owns the client-facing address. There the gateway writes
the row (section 7).

Startup refusal (exit 78): under the host supervisor, a child labelled `social` or `coordinator` is a whole World executable, so its own
`Arguments` must carry `--World:Realm:Id=0` in the exact `--Key=value` form, or the supervisor's config check refuses to start. Any other form is refused
too (space form, slash form, a mention inside a quoted value, a mention right after a `--Key` that has no `=`), as is any non-zero value among
several mentions (the message names the offending one). A labelled child that also sets `Cluster:Worker:Enabled=true` is still refused until it carries `--World:Realm:Id=0`.
Accepted limit: the check covers only the `social` and `coordinator` labels. A second `combined` child, or a `worker` child that does not set
`Cluster:Worker:Enabled=true`, is not refused for the realm row and, with `World:Realm:Id` above 0, writes it beside the world that serves the realm:
give such a child `--World:Realm:Id=0` yourself (tests: `ClusterSupervisorConfigChecksTests`).

## 5. How the logon daemon reads it

`RealmListProjection` (pure, `src/ArcaneCore.Realm/Protocol`) turns the row, the heartbeat age and the account's GM level
into the wire flags for each realm-list request. The stored row is never changed. The realm daemon's console `realmlist`
command prints the state, the heartbeat age, the flags a player would be sent (`wire`) and the lock level.

**Clocks.** The heartbeat is the world's UTC clock; its age is judged by the realm daemon's clock. Daemons on one host
agree. On split hosts, run NTP on both. A heartbeat in the future counts as fresh (its age is
read as zero), so drift never takes a live realm offline; when it is more than 5 s ahead the logon daemon logs one warning per five
minutes naming the cause. Consequence: a world whose clock runs ahead keeps showing online for that long after it hangs.

**Upgrade order.** The world applies auth schema 7 (`arcane-db upgrade`, or the world's own start-up). A logon daemon
built before it keeps working against the upgraded database and sends the stored flags; start the new logon daemon
after the schema is applied.

## 6. Character counts

The realm list carries a per-realm character count for the asking account; the client colours the realm name green
when it is above 0. vmangos keeps it in `realmcharacters` (realmid, acctid, numchars) in the realmd database; mangosd
adds one when a character is created (CharacterHandler.cpp:312-314) and recounts the account when one is deleted
(Player.cpp:4492, World.cpp:2891-2905), and realmd reads it for every realm (AuthSocket.cpp:1062).

ArcaneCore keeps the same table in the auth database (`realmcharacters`: `RealmId`, `AccountId`, `NumChars`). The world
recounts the account on **create**, **delete** and **character list**, always as a recount from the characters database,
so a count that drifted (a failed write, characters that existed before the table did) repairs itself the next time the
account lists its characters. The write runs on the session task after the client has its answer, never on the world
thread, and a failure is logged without failing the create, the delete or the list. The count on the wire is a byte;
above 255 it is sent as 255. With `World:Realm:Id` 0 or no auth database nothing is written.

## 7. A clustered realm: the gateway writes the row

With a gateway in front of one or more workers (`Cluster:Worker:Enabled`), no world writes the realm row: an active and a
standby world share the realm, and a world going offline after a handoff would hide a realm its promoted standby serves. The
gateway (`GatewayRealmLiveness`) writes it instead, from reports the workers send on the gateway link, so a clustered realm
has the same hang-offline, restart-red and population as a combined-mode world. The row it writes is `Gateway:RealmId`
(it must name the realm's `realmlist` row, as `World:Realm:Id` does for a combined world).

**What a worker sends.** A `WorkerStatus` frame (frame type 23, worker to gateway, link contract version 4): the world loop's
tick counter (`WorldRuntime.TicksStarted`), the registered sessions (bots included), the player limit in force (`.server plimit`
changes it; 0 is none) and a flag byte (Standby = 1, restart countdown = 2, shutdown countdown = 4). It is sent once when the
gateway is admitted and then every 5 s. The bytes are pinned by `GatewaySessionWireTests.WorkerStatus_GoldenAndRoundTrip`. A gateway
of contract version 3 is refused at admission (it would close the link on the unknown frame).

**What the gateway writes.**

| Row | When |
|---|---|
| `Online` | an **active** worker (not standby) has ticked, and its counter changed within the last 60 s |
| `Restarting` / `ShuttingDown` | an alive active worker announces a restart or shutdown countdown (restart wins) |
| `Starting` (Offline set) | no worker is alive, but an active worker is linked and has not ticked yet, or the gateway started less than 60 s ago |
| `Stopped` (Offline set) | no active worker has ticked for 60 s and the gateway is past its start-up grace; also a clean gateway stop |

The heartbeat follows a combined world's rule: written every 10 s while some active worker's counter advanced since the last
beat, so a hung loop stops beating and the logon daemon's own stale check (section 3) agrees with the gateway's. Population is
`2 x (sessions summed over the alive active workers) / player limit`, written when it changes; a worker that stopped ticking no
longer contributes, and neither does a standby.

**A standby's ticks do not count.** A standby (`Cluster:Worker:Standby`) reports the Standby flag until it is promoted, and the
gateway ignores its ticks and sessions: a standby running alone leaves the realm Offline, not Online.

**Failover.**

* The active world dies or hangs: its counter stops. The realm stays Online for up to 60 s (the same slack as a combined world),
  then goes Offline. If a standby is promoted in that time, the promoted world's counter takes over and the row never goes
  Offline.
* Planned handoff: the old active world announces nothing special; once the standby is promoted and reports as active, the
  row stays Online throughout. A restart countdown on either world shows Restarting for as long as it runs.
* The gateway itself stops: it writes Offline and `Stopped`. If it is killed, its beats stop and the logon daemon marks the
  realm offline after the stale threshold, as for any crashed writer.
* Two gateways on one realm would both write the row; run one gateway per realm id.

**Character counts.** `RealmCharacterCounts` does not depend on the liveness writer: whichever worker serves a create, delete or
character list recounts the account from the shared characters database and writes the same `realmcharacters` row, so workers
cannot disagree about a count, and a standby (which admits no session) writes none.

Verified by `GatewayRealmLivenessTests` (ticks stop for 60 s goes Offline, a restart countdown shows Restarting, standby ticks alone
leave the row Offline, population is the sum over the workers).

## 8. Clustered nodes: lease clock skew and lease liveness

The realm-singleton authority leases (`realm_authority_lease`, taken by every non-Combined node) are not the realm-list heartbeat above, but they share its clock rule and
decide whether a clustered realm keeps a writer, so the requirements sit here next to it.

**Clock skew between cluster nodes.** A lease's expiry is written from its holder's clock and judged by the next acquirer's clock. A holder renews every
`Cluster:Node:AuthorityLeaseRenewSeconds` (10) for a TTL of `Cluster:Node:AuthorityLeaseTtlSeconds` (30), so a lease never ages past about one renewal interval, and another node sees it lapsed once its own clock runs ahead of
the holder's by more than TTL minus renewal interval: **20 s with the defaults, less the jitter of a renewal round**. Beyond that a healthy holder is deposed: the new node takes the scope with a higher fencing token, the old holder's
writes behind a commit-time lease check (economy, social, instances, honor maintenance batches) are refused at commit, its next renewal is refused and the keeper stops the process (fail-stop). Stores with no commit check (the worldstate stores, the playerbot ledgers, the honor first-start state) ask only the keeper, whose answer stays true until that next renewal is refused: up to one renewal interval (10 s by default) in which the deposed node writes alongside the new holder. A skewed takeover is therefore an outage of the holder and, for those stores, a short double-writer window. Therefore:

* run NTP (or chrony, or the platform's time service) on every cluster host and keep the nodes within a few seconds of each other, well under TTL minus renewal interval;
* when raising the renewal interval (at most a third of the TTL), the skew budget shrinks; raise the TTL with it;
* a node that shows `lost: the store refused the renewal` right after another node's `Took over` line is the symptom to check the clocks for.

**Taking over under write load.** On PostgreSQL a takeover (and a release) first takes `FOR UPDATE` on the lease row, which waits for the in-flight fenced writers of that scope (they hold `FOR KEY SHARE` until their transaction ends).
The wait is bounded by the lease command timeout (a third of the renewal interval, at least a second), so a steady stream of fenced writers can keep a legitimate takeover waiting past it. `AcquireAtStartAsync` therefore retries
a store or timeout failure of a scope for up to `RealmAuthorityLeaseKeeper.StartAcquireAttempts` (4) attempts in total (3 retries), one second apart, before the start fails; the runtime takeover pass retries on every renewal tick anyway.
On MariaDB and SQLite a call that another node's live lease would refuse is answered from a read-only look first and takes no write lock.

**Renewals during a long WAL stall.** A renewal commits with `synchronous_commit = off`, so a slow WAL flush alone does not delay it, but if the WAL buffers fill (the stall outlasts `wal_buffers`), every commit
waits again. Watch `pg_stat_wal.wal_buffers_full` on the PostgreSQL server: a count that keeps growing during a stall means renewals can miss their deadline and the keeper will stop the node.

**Accepted limits of the fence.** On a store that requires the fence (`RequireFence`) or the held lease (`RequireHeld`), a node that holds no fence is refused, and a boot sweep skips (`LeaseFencedWrite.TryBeginSweep`). Some fenced writes keep null-means-unchecked and have no such refusal: the instance store (save, save instance data, delete, bind save and delete, last-instance save, character delete) and the social store write with no commit check on a node that holds no fence. A node whose keeper marked the lease lost locally lets the commit fence decide,
and a store behind `RequireHeld` (no commit check) can still write briefly after a takeover, both in the WAL-stall-plus-PostgreSQL-crash case described on `LeaseFencedWrite` and after a clock-skew takeover (until its next renewal is refused, one renewal interval).

The auction bot's custody store (`ahbot_custody`) is not behind the commit fence (it is not in the `RequireFence` or `FenceFor` list). A partitioned former Economy holder can therefore still flush custody rows until its next renewal is refused, after a surviving worker that took the Economy lease over at run time has read the ledger. The surviving bot's own custody rows are written under its own keys, the reconciliation against the economy operation ledger (which is fenced) settles any row whose outcome is unknown, and the daily budgets bound what a duplicate could add, but a row written by the former holder in that window is not seen by the survivor until its next restart. The window is the same as for a restart takeover, and a run-time takeover makes it more reachable.

**Social and Economy can split.** The two scopes are taken independently on the renewal tick. If their leases lapse on different ticks, two surviving workers can end up holding one each (AutoBroadcast on one, the auction bot and the economy sweeps on the other). Each scope still has one holder at a time, and the commit fence refuses a deposed one, but the peer-link locator (`PeerHolderLocator.Locate`) then reaches only the Social holder, and the design assumption of one node for both (`docs/CLUSTERING_DESIGN.md` D3) does not hold. No production code assigns the economy holder link yet, so nothing depends on it today.

**What a new holder writes from.** The fence says who may write, not whether what the writer has in memory is current, so a node that gains a scope at run time must read before it writes. For the Social scope:

- GM tickets: a node that did not hold the lease at attach holds every ticket write (it never reaches the store, even with the lease held) until it has reread the queue, off the world thread, after the gain. Storage then replaces the cached queue and the changes held on this node are applied to it, each to the ticket id it was made on. Held changes collect from attach, so they can be old: one is skipped, with a log line, when the stored row's `UpdatedAt` is at or after the moment the change was made (a tie goes to what is stored), and one aimed at a ticket that is no longer open (the previous holder closed or deleted it, or its filing was dropped) does nothing. A ticket filed here gives way to a stored open ticket of the same character. So a change made here and skipped is lost, even in a field the previous holder did not touch: the stored row wins as a whole. The comparison trusts the clocks of the two nodes, within the skew limit above. A failed read is retried and the writes stay held. Writes of a node that never gains the lease stay retained and are named at stop, as before.
- Groups: the first sync after the gain starts a read off the world thread, and the group authority is rebuilt from what it returns. Until then no group is written, and a group command that reaches the table waits in it (at most 4096, oldest dropped) and runs on the new table, in order. One that arrived before that first sync, up to a second after the gain, is lost with the old table. A failed read is retried and the writes stay held.
- Character directory: the directory is per process and is only kept current by that process's own creates, renames and deletes, so a survivor does not know a character created elsewhere since it started, and the load repairs of guilds, petitions and groups delete or trim on a character they cannot find. So each of those reads refreshes the directory from storage, after reading its rows and before installing them (a row can only name a character stored before it), and installs nothing if the refresh fails. A repair at takeover therefore only drops a character whose row is really gone, as at a restart. What this process changed during the read is kept. Between takeovers the directory stays as stale as on any worker, but none of these repairs runs on it then.
- Guilds and petitions: a save writes a whole guild or petition, and a worker's replica holds only what the holder published. So a worker that did not hold the lease at attach does not become the holder with the lease: `PeerGuildHolderLink.IsHolder` stays false until it has reread both, off the world thread, after the gain. Until then the managers act as a replica and store nothing, forwarded requests are not served, and this node's own guild requests are refused at the door, as they were while the old holder was out of reach. A replica also writes no load repairs, at attach or later: those wait for the read, which runs them on what is stored. The rows read replace the replicas, this node's online players get the guild fields of what was read, and the result is published to the workers it knows. A worker that first reaches the new holder later gets a full snapshot, which does not name a guild or petition the previous holder removed: that replica keeps showing it until a restart, but it never stores it, and it rereads if it takes the lease itself. A failed read is retried and the saves stay held.

For the Economy scope the auction cache and the bot's state are reread before the sweep or the bot act, and the character directory is refreshed with that read, because the sweeps treat an unknown character as gone (an unknown seller's unsold item is destroyed). This covers characters stored before the gain. A character another worker creates later is unknown to the economy holder until its next gain or restart, whichever way the node became holder: nothing syncs the directory at run time, so an auction or letter of such a character expires as if the character were gone. Other holder-only features that cache rows at attach are not covered by this section.
