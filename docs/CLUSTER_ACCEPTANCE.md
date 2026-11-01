# Real 1.12.1 client acceptance through the gateway (S6d)

This is the manual acceptance run for the 3a exit criterion and the cross-worker transfer work: one real WoW 1.12.1 (build 5875) client
logs in through `ArcaneCore.Gateway` and plays on a split cluster. Until the results table below is filled in, the gateway and the
transfer path are accepted by the mock client and the in-process tests only ([gateway](areas/gateway.md),
[mock client](MOCK_CLIENT_ACCEPTANCE.md)).

**Who does what.** The operator (Nathan) starts the cluster and the client and fills in the table. Nothing in the repository starts a
server or a client for this run.

## Before you start

1. Cluster up with the split topology and transfers on: `tools/ops/live/start-cluster.ps1 -Topology Split -Transfers` (a real start needs
   PostgreSQL; `-Plan` prints what would start without starting anything). The status window must list the gateway, the realm and the
   three map workers (EK, Kalimdor, instances) as running.
2. The client's `realmlist.wtf` names the realm list; the realm row's address and port point at the **gateway** port
   (`Gateway:Port`, default 8086), not at a world's own port.
3. A test account and a character on an Eastern Kingdoms map with enough level for the dungeon test, a hearthstone bound in
   Kalimdor, and access to a boat or zeppelin dock that leads to Kalimdor. Note the account name, character name and start position
   in the results table (do not commit them: the table in the repository copy stays free of real names, see "Recording results").
4. Start log capture (next section) before the client connects.

## Log capture

The helper described here is a procedure, not a shipped script: open one capture per process before the first login and keep them
running until the last step. Each daemon writes its own log file (`Logging:ArcaneCore:File:Path`, [logging](ops/logging.md); give
every process its own path, which the split-topology launch does) and, with the console mode on, the same lines on stdout.

1. Note the wall-clock start time (UTC, the logs default to `Logging:ArcaneCore:Timestamps = Utc`) and the client build string.
2. Tail the gateway, realm, EK, Kalimdor and instances log files into one folder named for the run, one file per process, keeping the
   process name in the file name. Rolled segments count: copy any `.1`/dated segment that appears during the run.
3. At each checklist step write the step id and the UTC time into a running notes file. A failure is then found by time window across
   all five logs.
4. When a step fails, keep from every log the lines from one minute before the step to one minute after, and search them for
   `gateway-fault`, `LinkLost`, `Transfer ` (every gateway transfer line starts with `Transfer <id>:`, so one id ties the gateway,
   source and target lines together), `worker refused` and `no worker to redirect to`.
5. Keep the client's own `Logs/` folder (`Errors` and `FrameXML` logs) and, for a disconnect, the client's last error text.
6. Do not publish captured logs: they hold account, character and address data. Summarize them in the table's notes column.

## Checklist

Mark each step Pass, Fail or Skipped in the results table. A step passes only when every listed observation holds. Stop at the first
Fail of steps 1 to 3 (later steps depend on them) and capture logs; steps 4 onwards may continue after a Fail.

| Id | Step | Observe (all must hold) |
|---|---|---|
| L1 | Login through the gateway | Realm list shows the realm; character list loads; entering the world completes; no client error dialog; the gateway log shows one `'<account>' authenticated; forwarding to the worker (generation …, epoch …)` line and no `worker refused '<account>'` line |
| L2 | Walking | Walk, run, jump and turn on land for two minutes: no rubber-banding, no disconnect, no snap-back; position survives a short stop and restart of movement |
| L3 | Chat | Say, yell, emote and a whisper to a second account or a bot arrive; a channel (General) join and message works; ping stays stable (no sustained growth) |
| L4 | EK to Kalimdor by boat or zeppelin | Board at an Eastern Kingdoms dock, ride, arrive at the Kalimdor dock; the loading screen appears once at the map change; after it the character is on the right map, not stuck, can move, and chat still works; no disconnect |
| L5 | EK to Kalimdor by hearthstone | Use the hearthstone bound in Kalimdor from Eastern Kingdoms; cast completes; a single loading screen; the character appears at the bound inn on the Kalimdor worker; can move and chat; the hearthstone cooldown starts |
| L6 | Kalimdor back to EK | Any route back (boat, zeppelin or a second hearthstone binding): same observations as L4; this exercises a transfer in the other direction |
| L7 | Dungeon entry and exit | Walk into a dungeon portal from the world; arrival inside the instance (instances worker), mobs and doors present, movement and combat work for five minutes; walk out; arrival at the outside entrance on the owning world worker |
| L8 | Relog | Log out to the character list and enter again from three places: on land in EK, in Kalimdor after L4 or L5, and inside the instance; each time the character appears where it logged out, with inventory, health and quest log unchanged |
| L9 | Hard disconnect and reconnect | Kill the client process during play, log in again within a minute: login is accepted (no stuck "character already in world"), position is where the last save left it |
| L10 | Transfer then relog | Right after L4 or L5, log out and in again: the character is on the destination map, not on the source one |

Not part of this run: killing a worker mid-transfer, a gateway restart and handoff (covered by the multi-process tests, and a restart
drops the client by design; see [gateway](areas/gateway.md#not-done-yet-rest-of-3a-and-beyond)).

## Recording results

Copy the table below into a dated note (for example `docs/handoff/`) or fill it in here, and keep the repository copy free of account,
character and player names, hosts and addresses. Cite the commit of the build under test.

| Date (UTC) | Build commit | Topology flags | Id | Result (Pass/Fail/Skipped) | Notes (log files, times, symptom) |
|---|---|---|---|---|---|
| | | | L1 | | |
| | | | L2 | | |
| | | | L3 | | |
| | | | L4 | | |
| | | | L5 | | |
| | | | L6 | | |
| | | | L7 | | |
| | | | L8 | | |
| | | | L9 | | |
| | | | L10 | | |

Exit criterion: L1 to L8 Pass. Then update [gateway](areas/gateway.md) (status line and "Not done yet") to say that real-client
acceptance ran, with the date and build commit.
