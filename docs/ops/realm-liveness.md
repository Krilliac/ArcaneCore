# Realm list liveness, states and character counts

What the realm-list screen shows for a realm, and how the world and logon daemons agree on it. The client has three
visible things per realm: a name colour (grey and not selectable when the realm is offline, red, green, or gold), a
population text, and a character count. This page covers the first and the last; the population is
`World:Realm:PlayerLimit` ([installation](../guide/installation.md) section 4).

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
the lock is shown as offline. The lock compares `AllowedSecurityLevel` with the account-wide GM level, not a per-realm level as vmangos's `GetSecurityOn` does (AuthSocket.cpp:1088). There is no command to set the lock yet: set the column with SQL.

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
  and the population set to 0.
* Countdown changes arrive on the world thread, which only records the new kind; the timer writes the state within a
  second. A state write carries the last beat, not "now", because changing a state is not proof the loop is ticking.
* A failed write is logged (fault site `realm.liveness-write`, throttled) and the next pass tries again.
* A clean stop (`BeforeWorldStop`) stops the timer, then writes Offline and `Stopped`.

Not written: with `World:Realm:Id` 0, with no auth database, and in gateway worker mode (`Cluster:Worker:Enabled`),
where an active and a standby world share the realm and the gateway owns the client-facing address; the operator owns
the row there ([gateway](../areas/gateway.md)).

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
