# Clustered runtime design — M15

Status: researched design and implementation plan, requested 2026-10-03;
module/process direction approved by the developer on 2026-10-03.
The delivered state is tracked in the [roadmap](ROADMAP.md#delivered).
This tranche changes documentation; no cluster processes or transport are shipped.
The inventory below was checked against integration source
`e06d4e468118454d906c1d4be8914a3522106d1b`. The constraint bullets on sessions,
maps and instances, ID allocators, write queues and far transfer were re-read
against `c3dea16292756e146df0ddd0783f97fee734dd5c` for M15.0 (see the
[M15.0 scope record](integration/cluster-m15-0.md) and the
[transfer-state inventory](integration/cluster-transfer-state.md)); the rest remain at
the older pin. The [auxiliary services](#auxiliary-services) section (requested
2026-10-10) and the importer row of the component table were checked against
`db9df29420b7ddf844b48ee7d459a8912e8924da`.

## Decision and reference comparison

Build an independent .NET 10/gRPC design with stable client gateways, one owner
per map/instance, realm-wide social services and durable ownership fencing.
Keep the current local realm/world mode and use the same simulation and protocol
implementations in both modes. This combines the useful parts of the references
without inheriting their incomplete guarantees or copying their implementation.

| Pinned reference | Useful evidence | Qualification and ArcaneCore consequence |
|---|---|---|
| [Krilliac/MangosSharp, cluster-proxy branch](https://github.com/Krilliac/MangosSharp/tree/8ead94388a1c3fa9de6bc16982214c804b9eeeb2) | Separated world/proxy topology, supervisor, federation and shard contracts | Disconnect handling and foreign-shard routing remain incomplete; reconnecting to another realm is not transparent handoff. Source review also suggests startup can await a registration reply before the receiving loop starts. That ordering concern is an inference, not an executed failure. Require actual separated-process registration and readiness tests. |
| [Krilliac/server-Zero](https://github.com/Krilliac/server-Zero/tree/e051334e4cccb6c712ce5b6feda7ad2aefb8e81a) | `src/ipc` and standalone `src/modules/AhWorker`: bounded queues, world-thread application, per-spawn generation fencing, readiness/health, recovery and idempotency | This is supervised auction-service offload of one child, not distributed map ownership. Apply its lifecycle principles to every ArcaneCore worker and durable mutation. |
| [SparkEngine active release-readiness source](https://github.com/Krilliac/SparkEngine/tree/b656f435025ff3896b7eba5337e4202e421f4579) | Separate server, gateway, daemon, orchestrator, collaboration, cooker, worker and automation roles; `AreaHandoffDispatcher` and `TFHandoffParticipant` capture/suspend/install/retire gameplay and fence residency | Active online-services specification retains same-host/same-user gateway and single-host daemon boundaries, with production services unsupported. `TFDatabase` uses an atomic JSON file and same-host locks. Preserve actual handoff phases, and implement distributed ownership/storage explicitly. UDP ticket binding is unrelated to the unchanged 5875 TCP client protocol. |
| [SparkEngine default Working source](https://github.com/Krilliac/SparkEngine/tree/2b03dc7971488b003beb97b965207c1b5d3cde49) | Earlier executable and local area-control split | Its control path persists session/epoch/phase without full gameplay transfer; older conceptual migration and wiki balancing claims cannot establish active-branch capability. Launch health and stop signals do not establish gameplay readiness or completed drain. |

The selected foundation is ArcaneCore's own authoritative tick and data seams.
Sharp informs topology; Zero informs lifecycle and idempotency; Spark's active
branch informs process roles and transactional handoff phases. A wholesale port
would leave the hard ownership, persistence and recovery problems unresolved.
GPL references supply behavior and architecture observations only.

The primary comparison paths are Sharp's
[supervisor](https://github.com/Krilliac/MangosSharp/blob/8ead94388a1c3fa9de6bc16982214c804b9eeeb2/src/server/Mangos.Cluster/Supervision/WorldSupervisor.cs)
and [shard registry](https://github.com/Krilliac/MangosSharp/blob/8ead94388a1c3fa9de6bc16982214c804b9eeeb2/src/server/Mangos.Cluster/Federation/ShardRegistry.cs),
Zero's [worker supervisor](https://github.com/Krilliac/server-Zero/blob/e051334e4cccb6c712ce5b6feda7ad2aefb8e81a/src/ipc/WorkerSupervisor.cpp)
and [durable custody](https://github.com/Krilliac/server-Zero/blob/e051334e4cccb6c712ce5b6feda7ad2aefb8e81a/src/game/AuctionHouseBot/CustodyService.h),
and Spark's active [online-services specification](https://github.com/Krilliac/SparkEngine/blob/b656f435025ff3896b7eba5337e4202e421f4579/docs/specs/online-services.md),
[handoff contract](https://github.com/Krilliac/SparkEngine/blob/b656f435025ff3896b7eba5337e4202e421f4579/SparkEngine/Source/Engine/Networking/AreaHandoffParticipant.h),
[game participant](https://github.com/Krilliac/SparkEngine/blob/b656f435025ff3896b7eba5337e4202e421f4579/GameModules/SparkGameMMOFPS/Source/Net/TFHandoffParticipant.cpp)
and [migration test](https://github.com/Krilliac/SparkEngine/blob/b656f435025ff3896b7eba5337e4202e421f4579/Tests/TestTF120Migration.cpp).
That Spark test uses actual participants/storage with a pawn capture/install
test double; it is not live cross-host acceptance. Zero map threads remain local
and its AH service is a single loopback child. Sharp map routing does not supply
complete instance/epoch authority. Same-realm clustering and federation are
separate: shared auth or cross-realm discovery does not move a character or its
client transparently between realms. Federation is outside M15's first phases.

Internal transport will use versioned protobuf contracts over ASP.NET Core gRPC.
Its [HTTP/2 and TLS requirements](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-10.0)
and [deadline behavior](https://grpc.io/docs/guides/deadlines/) inform deployment
and cancellation tests. Package versions are selected and pinned when the first
transport phase is implemented. Local mode retains an in-process implementation
of the same semantic contracts.

## Current executable and state boundaries

| Existing component | Actual responsibility | Planned cluster role |
|---|---|---|
| `ArcaneCore.Realm` | SRP6 and realm-list TCP daemon; successful proof stores the account session key | Replicated realm frontend; each SRP connection remains on one replica until complete; advertise stable gateway addresses |
| `ArcaneCore.World` | World authentication, character screen, socket/cipher, all simulation/features and persistence queues | Keep combined local mode; extract gateway and game-worker hosting around shared implementation |
| Kernel / Cryptography / Protocol / Game / Data | Shared assemblies and store/simulation seams | Remain shared libraries; process separation does not turn each library into a network service |
| `ArcaneCore.AccountTool` | Short-lived create/password/security/list operations against auth storage | Administrative job with the same CLI; coordinate credential/session invalidation and schema readiness |
| `tools/spell-import` | Short-lived DBC-to-world replacement operation | Exclusive content-maintenance job that publishes an immutable content revision |
| `ArcaneCore.MockClient` | Synthetic owned-loopback acceptance executable using actual realm/world services | Test/job role; extend to launch and validate owned subprocesses as cluster phases land |
| `tools/codegen/gen_wow_tables.py` | Offline generated protocol/update-field tables | Build job producing reviewed, versioned artifacts; never a live simulation service |
| `arcane-content-importer` (`tools/ArcaneCore.ContentImporter`) | Short-lived `plan`/`import`/`refresh`/`verify` operations against the world database (`ContentImporterCli.cs:38-39`); `CreatureDumpImporter` and the other dump importers are its data libraries | Exclusive content-maintenance job under the same rules as `tools/spell-import`; never a long-running service |

Relevant existing boundaries are [world session](../src/ArcaneCore.World/Net/WorldSession.cs),
[player session seam](../src/ArcaneCore.Game/Maps/IPlayerSession.cs),
[runtime](../src/ArcaneCore.Game/Maps/WorldRuntime.cs),
[database split](../src/ArcaneCore.Data/DatabaseOptions.cs), and
[feature lifecycle](../src/ArcaneCore.World/Features/WorldFeatures.cs).
The public realm/world TCP defaults remain 3724/8085.

Important current constraints:

- `WorldSession` combines TCP/cipher, scoped stores, settings, runtime and Player.
  Duplicate-account replacement in `SessionRegistry` and character online checks
  are process-local. The world-auth server-ID field is currently discarded;
  it cannot be trusted as a new routing authority.
- Maps are keyed by `(map ID, instance ID)` (`WorldRuntime._maps`,
  `WorldRuntime.cs:20,191-201`) and all run on one `WorldRuntime` thread. Mutable
  visibility, grids, objects and queues contain direct references. `InstanceRegistry`
  no longer exists: `InstanceManager` gives each instance its own Map. Instance IDs
  come from a process-local counter, group binds and instance contents (deaths,
  respawns, boss state) are memory-only, and only instance saves and character binds
  are durable ([instances.md](integration/instances.md)). Durable instance IDs and
  binds must be implemented before placing instances on other workers.
- Auth storage is shared across realms; characters storage is explicitly one
  database per realm; world content is read-only during runtime. Character and
  social rows have no realm discriminator. Keep per-realm characters databases
  initially and carry an explicit realm identity in configuration and contracts.
- Item GUIDs are seeded from database maximum then allocated locally; guild
  IDs use local maximum-plus-one, group IDs a local counter from 1, instance IDs a
  local counter from 101, and mail/auction/item-text IDs are seeded from stored
  maxima at attach. Concurrent allocators require durable authority or reserved
  disjoint ranges.
- Core state/inventory save together, but quest, spellbook, spell-state, social,
  reputation and instance writes each use a separate queue or barrier. Local
  revisions and FIFO ordering provide no distributed fencing. The core and quest
  queues retain a snapshot that fails three attempts and report it at flush, login or
  stop, but the caller of an ordinary enqueue is never told. The reputation queue now
  retains a failed write per character and retries it at the next write, login barrier
  or logout ([reputation.md](integration/reputation.md)); the social and instance
  queues still drop a write after three failed attempts
  ([table](integration/cluster-transfer-state.md#1-what-reaches-storage-and-when)).
  Recoverable acknowledgements and a complete handoff barrier are prerequisites.
- Social context, name cache, groups, guilds and channels refer to local players.
  A fleet must have one realm-wide social authority and routed presence.
- Far transfer retains direct source-map references and has no distributed
  deadline/recovery journal. Spell auras/casts survive local detachment without
  full durable persistence. Cooldowns, corpses and respawns use process-local
  clocks. `Player.CreateSnapshot` is not a complete handoff serialization.

## Target roles and placement

The names below describe proposed process roles, not projects already present.

```mermaid
flowchart LR
    C[5875 client] --> R[Realm replicas]
    C --> G[World gateways]
    G <--> W[Game workers: whole maps / instances]
    G <--> S[Realm social and directory authority]
    W <--> S
    O[Coordinator: membership / placement / drain] <--> G
    O <--> W
    O <--> S
    H[Host daemon: supervised processes and jobs] --> O
    H --> J[Account / import / codegen / acceptance jobs]
    O <--> D[(Durable leases / transfers / operations)]
    W --> P[(Per-realm characters storage)]
    R --> A[(Shared auth storage)]
    G --> A
    F[Immutable content and asset revision] --> W
```

| Authority / classification | Owns | Placement rule |
|---|---|---|
| Realm frontend — replica | SRP exchange and realm list; no live Player or exclusive durable partition | Multiple replicas sharing auth; connection-local SRP state requires affinity until completion |
| World gateway — partitioned single writer | Client TCP, rolling cipher, connection generation, account settings, character-screen flow and one ordered client-output stream | Stable address per realm; multiple gateways claim account sessions durably |
| Game worker — partitioned single writer | Map/instance tick, visibility, combat, creatures, movement, spells, inventory and quest state for its resident players | One writer per `(realm, map, instance)`; initially whole maps and statically assigned instances |
| Realm social/directory — partitioned single writer | Names, online presence, whisper/group/guild/channel routing and social mutation order | One fenced active authority per realm initially, recoverable from durable state |
| Coordinator — elected coordinator | Membership incarnations, readiness, placement, lease/transfer decisions and draining | One elected lease holder backed by transactional storage; replacement coordinators reconstruct decisions |
| Host daemon — per-host supervisor | Launch/monitor/drain approved role executables and short-lived jobs; resource limits | One supervisor per host; host-local process control, network control-plane client |
| Account/import/codegen/mock tools — distributed jobs or offline tools | Existing bounded CLI operations and reviewed result artifacts | Scheduled against compatible hosts with explicit job ownership; retain direct offline CLI usage |

A worker may own several maps on its existing tick thread. Placement considers
tick latency, queued work, players, memory, content availability and configured
host capacity; a hashing choice cannot override capacity or compatibility.
Start with explicit assignment, then automatic placement of new/empty maps.
Live map relocation follows complete snapshot/recovery tests. Splitting a
continent into cells requires a separate design for border interactions and
visibility; it is outside the first clustering phases.

Combat, movement, AI, spells and visibility stay together inside the map owner.
Do not create an RPC per gameplay subsystem or per object. Additional economy
services need a real consumer and custody/transaction design before extraction.
The services that do qualify (Warden placement, bot chat, bot planning, the
auction house bot) are decided in [auxiliary services](#auxiliary-services).

### Subsystem separation and alternatives

Every subsystem keeps a clean module boundary. The chosen executable boundary
follows mutable-state ownership. Approval of this planning direction keeps the
implementation phases and deployment gates below intact.

| Subsystem | Recommended execution | Reason and later extraction condition |
|---|---|---|
| Maps, movement and visibility | Same authoritative map/instance worker | A move changes grid membership, observers, range checks and update fields in one tick; remote ownership requires an explicit border/transfer protocol |
| Spells and combat | Same worker as their entities | Casts read position/targets and change health, threat, auras and death; per-cast RPC adds latency and uncertain partial effects |
| NPC processing and immediate AI | Same map worker, separate modules | Selection, interaction range, faction reactions, combat and inventory/quest effects must use current authoritative state |
| Say/yell/proximity emotes | Same map worker | Delivery depends on current visibility, position and faction rules |
| Whisper, guild, party/raid and channels | Realm social authority with routed presence/output | Cross-worker membership and delivery require one mutation order and deduplicated events; group combat calculations consume versioned membership on the map owner |
| Heavy path planning or model inference | Optional bounded compute worker after a measured consumer exists | Return proposals tagged with request, map/instance, owner epoch, entity state version, content/navigation revision and deadline; owner validates current state and applies or discards them |
| Economy operations | Optional justified service after transactional custody is designed | Preserve item/currency custody and idempotent operation outcomes; ordinary NPC gameplay ownership stays on the map |

Keeping one combined World executable is simplest to operate and remains the
compatibility mode, but cannot spread map CPU across hosts. Whole-map workers
preserve deterministic interactions and failure isolation, at the cost of
handoff and realm-wide routing. A movement/spell/NPC/AI executable split offers
independent scheduling but puts RPC on nearly every interaction, multiplying
tick latency, snapshots and partial-failure recovery. Use the whole-map design
first; split expensive pure computations only when profiling justifies it.
Compute responses never mutate world state directly, block the tick, or retain
authority after a worker/owner generation change. Timeout/backpressure falls
back to a documented local behavior or skips the proposal safely.

All roles publish realm, node incarnation, protocol/contract version, schema
compatibility and content/DBC/terrain manifest revision. Admission rejects a
mismatch. Replicas use the existing database engines; database replication and
multi-region gameplay are separate projects. Bound connection pools, worker
count and job concurrency per host before enabling automatic placement.

## Internal contracts and routing

Implement contract families only in the phase with their first consumer:

| Family | Required semantics |
|---|---|
| Membership/readiness/drain | Versioned role registration; unique incarnation; heartbeat distinct from gameplay readiness; stop admission, drain acknowledgement and final stop |
| Placement/ownership | Explicit realm/map/instance and character/account scopes; atomic claim/renew/release; monotonically increasing fencing epoch; authoritative owner lookup |
| Session input/output | Connection/session generation, character identity, owner epoch and ordered sequence; bounded plaintext opcode/payload; precise close/logout; backpressure and cancellation |
| Transfer | Unique transfer ID; source/target epochs; captured state revision; prepare/install/commit/retire/abort results; durable status lookup after ambiguous responses |
| Persistence | Operation ID, character/realm, owner epoch and save sequence; durable acknowledgement; idempotent retry and feature-complete flush result |
| Social/directory | Membership/presence version; sender authorization; deduplicated events and routed output; name creation/deletion invalidation |
| Jobs/content | Approved executable/job type, input manifest, bounded resource budget, status/result artifact and content-maintenance lease |

The gateway decrypts client headers and forwards plaintext messages. A worker's
session adapter posts input onto the authoritative tick and returns packets to
the gateway; game code never runs in an RPC callback. Keep one output sequencer
and cipher writer per client. Output from social and simulation must carry its
session generation and preserve logout/transfer causality. Old workers cannot
send to a replacement session. Never serialize a socket, DI scope, Player,
Map reference or cipher state into a handoff.

Keep existing opcode payload bounds, session-state validation, client ownership
checks and slow-client limits. Add independent bounds for internal messages,
mailboxes, in-flight RPCs and retained retries. Use unary calls for bounded
control/acknowledgements and ordered streams for session traffic. Set deadlines;
retry only operations with an idempotency key or a read-only result. Preserve
uncertain mutation outcomes until their durable operation status is known.
Authenticate internal roles and authorize their realm/role scope. Cross-host
endpoints use TLS; any loopback development exception is explicit configuration.

Start the receiving loop before registration can await a reply. Mark a role
ready only after schema validation, current leases, matching content manifests,
required feature queues and directories, and a real command roundtrip through
the running simulation. Health reports liveness separately. A dropped internal
transport reconnects with a new stream generation and reconciles durable
ownership; process restart creates a new incarnation. A stale stream cannot
resume by assumption.

## Persistence, handoff and failure recovery

Use transactional storage as the initial ownership authority. Every durable
mutation checks the current owner epoch in the same transaction as its write;
sequence checks reject old saves. A lease alone is insufficient if storage
still accepts an old owner's queued snapshots. Local monotonic lease budgets
stop admission/output when renewal becomes uncertain; durable epochs and
gateway routing reject stale authority. Test clock skew, partitions and expired
leases. Do not operate a partitioned worker indefinitely in autonomous mode.

Keep realm map/character/social ownership and transfer records in that realm's
characters database, alongside the writes they fence. Auth generation belongs
in the shared auth database. A coordinator's global view is a projection of
these authorities; it cannot replace an atomic per-realm ownership check.
Changes spanning auth and characters storage require the documented durable
operation/outbox and reconciliation, rather than an assumed cross-database
transaction. Node identity is stable configuration; incarnation changes on a
process restart and stream generation changes on reconnect.

Reserve item/group/guild ID ranges durably with overflow checks. Preserve current
client GUID layouts. Creature/instance routing includes the map-instance scope;
persisted IDs and instance bindings need stable allocation across restarts.
Give account session replacement a durable generation so two gateways cannot
both activate the same account/character in a realm. Credential changes invalidate the
defined generations consistently across realm and gateway replicas.

Refactor persistence barriers to cover core/inventory, quests, spellbooks and
social state relevant to the player. Save results report durable success or a
recoverable failure. For cross-feature updates in one characters database,
commit the required bundle atomically; use a durable operation/outbox where an
update spans authorities. Local caches remain valid only for their owner epoch.
Restart recovery reads persisted revisions, unresolved operations and transfers
before advertising readiness. State acknowledged as durable must survive
process loss; transient movement after the last checkpoint has a measured,
documented recovery window rather than an implied zero-loss guarantee.

Cross-worker map transfer preserves the existing vanilla far-teleport packets:

1. Reserve compatible destination capacity and persist a unique transfer record.
2. At a source tick boundary, suspend the player and fence further mutation.
   Capture a versioned complete state bundle and durably flush accepted changes.
3. Install a passive target using the transfer ID. Validate state/content and
   rebase remaining-duration timers; target simulation/output stays inactive.
4. Gateway emits existing transfer/new-world staging once and routes the matching
   world-port acknowledgement through the transfer, with bounded input buffering.
5. Atomically commit residency/epoch and the transfer decision. Target activation
   and gateway route change use that committed decision; retire source authority.
6. Rebuild visibility and send the existing before/after-add packet sequence.
   A timeout or failed reply queries durable status; abort only while the source
   remains authoritative. A committed transfer cannot roll back into two owners.

The state bundle includes identity/security generation, movement and counters,
inventory, journal/objective/timer state, spellbook/cooldowns/auras and supported
casts, played time, logout/transfer state, home bind and relevant social versions.
Preserve source-map corpse/reclaim ownership and vanilla combat-detachment rules.
Encode timer durations/reference timestamps explicitly, covering offline quest
expiry and process clock differences. A feature without capture/install/recovery
support blocks remote handoff; unsupported state cannot be silently discarded.
Introduce transfer participation alongside the first actual handoff consumer;
login/logout hooks alone do not cover it.

On worker loss, gateways stop routing, reject late outputs and disconnect clients
whose transient state cannot be recovered safely. Reconnect uses the current
durable epoch/checkpoint, resolves transfers, and retains acknowledged quest/item
operations. Gateway loss requires a normal client reconnect: a new gateway does
not inherit a live TCP connection or rolling cipher. Coordinator loss permits
only existing still-valid leases; replacement reconstructs placement and pending
transfers. Social loss invalidates presence and recovers the fenced authority.
No failure path acknowledges a dropped save as successful.

Graceful drain is a separate protocol: stop new placement/login/jobs; complete or
resolve transfers; evacuate supported residents or perform normal logout; flush
all feature queues; release leases; acknowledge drain; then terminate the owned
process. A launch, heartbeat, or signal alone is not readiness or completed drain.

### Host supervisor and console layout (M15.4)

A single clustered host runs **3 executables** across **6 to 9 OS processes**:
`ArcaneCore.Realm`, `ArcaneCore.World` (selected by role: `combined | gateway | worker | social | coordinator`),
and the host supervisor (`ClusterHostSupervisor` in `src/ArcaneCore.World/Cluster/Supervision/`).
Bots remain in-process on the `worker` that owns their map; there is no separate bot-world executable.

- **Default merged console (`Merged`).** By default, the supervisor spawns every child windowless (`CreateNoWindow = true`, redirected `stdout`/`stderr`), writes every line to that child's per-process log file, and multiplexes output into the single supervisor console prefixed with its `[<name>]` role tag (for example `[realm]`, `[gateway:1]`, `[worker:EK]`, `[worker:Kalimdor]`, `[worker:Instances]`, `[social]`, `[coordinator]`).
- **Per-process console switch.** Each supervised child configures `Cluster:Supervisor:Processes:<name>:Console` as `merged` (default), `window` (own OS console window with `CreateNoWindow = false`), or `none` (silent background process, log file only).
- **Unconditional error/exception visibility.** Regardless of whether a child is configured for `merged`, `window`, or `none`, any `stderr` output or error/exception line (`ERROR`, `FATAL`, `CRIT`, `[ERR]`, `[FTL]`, `Unhandled exception`, `Exception:`) is always surfaced in the supervisor's merged console alongside its `[<name>]` prefix and written to the child's log file so crashes or faults are never hidden.
- **Lifecycle, backoff, and capacity admission.** Unexpected child exits trigger capped exponential restart backoff (`1 s -> 2 s -> 4 s -> ... -> 60 s` maximum) and increment the child's incarnation and restart counters. Before launch, `CheckCapacityAdmission` enforces `MaxConcurrentProcesses`, `MaxTotalAllocatedMemoryMb`, `MinFreeMemoryReserveMb`, and single-writer map ownership across workers. `DrainAndStopAsync` drains and terminates only supervisor-owned child processes and never restarts a drained child.

## Auxiliary services

Status: planned design, requested 2026-10-10; nothing in this section is built.
It decides which subsystems outside the map simulation may run as separate server
processes. Current-state claims cite `db9df29420b7ddf844b48ee7d459a8912e8924da`;
paths are under `src/ArcaneCore.World/` unless they say otherwise.

Every auxiliary service follows the compute-worker rule above: it owns no world
state, it sends intents, proposals or verdicts, and the authoritative owner
validates and applies them on its own tick. Common rules (planned):

- **Switch.** `Cluster:Services:<Service>:Mode` is `Local` (the default) or `Remote`.
  `<Service>` is `BotChat`, `BotPlanner` or `AuctionHouseBot`, with an endpoint key
  beside it. These keys are planned and not in the configuration catalog. Local
  mode keeps today's in-process code behind the same contract interface, as the
  charter's seam rule requires (`ARCANECORE_CHARTER.md:135-139`). The combined
  World executable always runs Local.
- **Transport.** The M15.1 gRPC transport, with the membership/readiness family:
  versioned registration, a unique service incarnation, mutual authentication
  scoped to realm and role, deadlines, and bounded in-flight requests on both sides.
  Retry only requests that carry an idempotency key.
- **Fail closed.** The world never waits on a service inside a tick. When a
  service is down or not ready, its feature pauses: no new intents are applied
  and nothing is sent outside the process. Work already started settles on the
  world side. The host daemon (M15.4) restarts the service with capped backoff,
  as server-Zero's `WorkerSupervisor` does (`src/ipc/WorkerSupervisor.h:51-53`
  at the pinned reference: 1 to 60 s).
- **Not a database writer.** A service never writes the characters or world
  databases. Item, money and ban effects go through the owner's existing
  transactional paths.

| Candidate | Decision | Rank by value |
|---|---|---|
| Warden | Runs on the connection owner (the gateway in cluster mode); not a separate process | 1: needed for Warden under M15.1 without shipping session keys |
| Playerbot chat / LLM | Optional `Remote` service | 2 |
| Playerbot planning | No service: the goal policy scores in process with each map's bots (the Ollama planner was removed) | n/a |
| Auction house bot | Optional `Remote` planner; custody stays with the auction owner | 4 |
| Metrics exporter | No service: every role exports its own metrics | n/a |
| Broadcast and server mail | No service: fixed placement on existing roles | n/a |
| Content importer and other CLIs | M15.5 jobs, not services | n/a |

### Warden: placed on the connection owner

1. **Boundary.** Warden is per connection and reads no world state. The session's
   two RC4 streams are derived from the 40-byte session key K
   (`Warden/WardenSession.cs:53-78`, `Warden/WardenCrypto.cs`). The handshake
   state machine, deadlines, scan batches and checksum checks
   (`WardenSession.cs:4-13`, `:209-299`) run where the client connection and K
   already live: the World session today (`Net/WorldSession.cs:833-841`), and the
   gateway in cluster mode (the gateway owns the cipher, above). A separate Warden
   process would need K or both RC4 states, and every CMSG/SMSG_WARDEN_DATA body,
   on a second process. That widens the session-key exposure without any payoff:
   a scan is one request every 30-60 s with up to 3 checks
   (`Warden/WardenOptions.cs:113-119`). Decision: there is no `Remote` mode.
   Managed playerbots have no client connection and are exempt by construction
   (`Warden/WardenFeature.cs:115`).
2. **Contract.** The gateway hosts Warden and keeps the `warden_checks` set loaded
   from the world database (`WardenFeature.cs:90-107`). A verdict is advisory to
   the game: the gateway applies it through existing authorities. Log stays
   local. Kick requests the normal session close from the worker, so logout and
   saves keep their order, and then closes the client. Ban writes through
   `IBanStore` to shared auth storage, as today (`WardenFeature.cs:164-181`),
   then kicks. The gateway reports `WardenVerdict(session generation, account,
   reason, action, check id)` to operators. Idempotency: a verdict belongs to
   one session generation, so a stale generation is ignored.
3. **Failure.** A Warden failure is the session's protocol failure, and
   `ProtocolAction` applies (never above Kick, `WardenSession.cs:305-306`).
   Today, Warden enabled with a missing or corrupt module logs an error and runs
   unscanned (`WardenFeature.cs:47-51`). In cluster mode (planned), a gateway with
   `Warden:Enabled` true reports not ready unless its module is usable. All
   gateways of a realm must publish the same Warden manifest (module digest,
   options, check revision), so a client cannot pick an unscanned gateway. The
   check-catalogue load failure keeps today's fallback to the configured checks
   (`WardenFeature.cs:103-106`). Admission is as today: play is not held for the
   handshake. Gating world entry on a completed handshake is a separate decision.
4. **Value and cost.** Nothing to scale: the CPU per session is negligible at
   the default cadence. The value is correctness: Warden keeps working under
   M15.1 without sending K to map workers.
5. **Dependencies.** M15.1 (the gateway owns world auth, K and the cipher; the
   session close contract). There is no M15.2 dependency.

### Playerbot chat / LLM service (optional Remote)

1. **Boundary.** Today the world thread hands a line to `PlayerbotChat.TryAsk`.
   That method does cheap checks and a bounded queue, never I/O
   (`Playerbots/Chat/PlayerbotChat.cs:116-145`). Two background workers call the
   providers (`:59`, `:228-233`), and the world thread drains replies and mutes
   (`ManagedPlayerbotFeature.cs:705-712`). The Remote service takes the worker
   side: provider calls, keys read from its own environment
   (`PlayerbotChat.cs:48`), moderation, the safety filter and its strike, cut-off
   and choice state, spend and hourly caps, and conversation memory. The world
   keeps the bot, the act of saying a reply through the bot's own CMSG_MESSAGECHAT,
   applying mutes, and the built-in template provider.
2. **Contract.** World to service: `Ask(ask_id, realm, bot_id, bot guid, persona,
   sender guid/name/account, channel, text, from_master, invite_allowed, login_id)`
   (today's `BotChatAsk`, `Playerbots/Chat/BotChatModels.cs:29-39`). The service
   answers `Accepted(ask_id, disclosure?)` or `Declined(ask_id, reason)`, using
   today's `BotChatAdmission` reasons (`PlayerbotChat.cs:10-31`). It sends
   `Reply(ask_id, bot_id, sender, channel, text, provider)` and an advisory
   `Mute(account, player, minutes)` that the world applies or ignores. The world
   owns bots and presence. The service owns its ledgers and never sees an object
   reference. `ask_id` is the world incarnation, bot and a sequence. The service
   deduplicates by `ask_id`, and the world says a reply at most once. A reply for
   a bot that stopped or left the world is dropped, as today
   (`ManagedPlayerbotFeature.cs:709-711`). At-most-once is correct here: a lost
   reply only means no reply (`PlayerbotChat.cs:45-46`).
3. **Failure.** When the service is unavailable, the world falls back to the
   built-in provider, the existing last link of the provider chain
   (`Playerbots/Chat/PlayerbotChatOptions.cs:23-26`). No line leaves the realm
   unscreened, because screening runs before any provider (playbots.md, Safety
   and provider policies). Strikes and cut-offs are memory-only today: a restart
   clears them (`Playerbots/Chat/PlayerbotChatSafety.cs:36-39`). A service
   restart keeps that semantics unless a durable table is added.
4. **Value and cost.** Provider API keys and outbound internet egress leave the
   world and gateway hosts. Provider latency and moderation I/O get their own
   process. With bots on several workers (M15.3), each in-process chat would keep
   its own spend cap and safety state. The `MaxDailySpendUsd` default of $1
   (playbots.md, Cost control) would multiply, and a player could avoid strikes by
   talking to bots on another worker. One service per realm keeps one ledger.
   The CPU saving is small (two workers, at most 256 queued asks, `PlayerbotChat.cs:231`).
   Cost: one more role, and the service must be realm-scoped.
5. **Dependencies.** M15.1 (transport and membership). M15.2 directory routing
   (a reply goes to the worker that owns the bot; a player sender may be on
   another worker or gateway).

### Playerbot planning and the bot host decision

1. **Boundary.** A managed bot is a Player with a socketless
   `ManagedWorldSession` on a map, and every action is a real CMSG through the
   world handler (playbots.md, Managed playerbots; `Net/ManagedWorldSession.cs:87`).
   Movement pumping, party intake, the group coordinator and the think rotation
   run on the world tick (`Playerbots/ManagedPlayerbotFeature.cs:544-596`), and
   the brain reads live Map and Player references
   (for example `Playerbots/PlayerbotBrain.cs:302`, `:437`). All of that stays on
   the map worker that owns the bot. The local-model (Ollama) planner this
   section once proposed to move was removed: the goal policy that replaced it
   scores each map's bots in process, on the CPU, off the world thread
   (`Playerbots/Policy/PlayerbotPolicyEngine.cs`; playbots.md, Goal policy).
2. **No Remote planner.** A bot costs at most a few thousand multiply-adds per
   snapshot, far less than one RPC, so the policy stays with its map and moves
   with it when maps move. With no scores, or late ones, the rule ladder decides,
   as it does with the policy off.
   **No separate playerbot host.** A process that runs bots away from their maps
   would need a replica of map state, or an RPC per decision. The subsystem table
   above rejects both. The measured cost does not justify it: think time is
   already capped at 8 ms and `MaxActionsPerTick` per tick
   (`ManagedPlayerbotFeature.cs:582-590`). The bot feature averaged 9.6 ms per tick
   (EMA) at 1000 running bots in a fixture with no terrain, creatures or clients
   ([perf limits](integration/perf-limits-20261008.md), measurement table). Bots
   scale out with their maps when M15.3 places maps on several workers. That
   requires transfer participation for managed sessions and brain state (or a
   documented stop and restart of the bot on transfer). Out-of-process bots
   already exist as protocol clients for load tests (`arcane-mock playbot`).
5. **Dependencies.** M15.1 (transport), and M15.0 owner epochs for discarding
   stale choices. The bot-host decision depends on M15.3.

### Auction house bot (optional Remote planner)

1. **Boundary.** Today a timer posts one bot action to the world thread every
   `UpdateIntervalSeconds` (default 20, `src/ArcaneCore.Game/Economy/AuctionBot/AuctionBotOptions.cs:24`;
   `Economy/AuctionBotFeature.cs:115`). The pure part can move: the item pool, loot
   source rolls, pricing and sell/buy planning (`AuctionBotPlanner.PlanSell` and
   `PlanBuy`, `src/ArcaneCore.Game/Economy/AuctionBot/AuctionBotPlanner.cs:66`, `:185`).
   Custody stays with the auction owner (the world today): auction id and item
   GUID allocation (`AuctionBotFeature.cs:404-405`), the custody ledger and its
   durable `ahbot_custody` rows (areas/auction-house-bot.md, Custody), minting
   only inside the listing transaction (`Economy/EconomyFeature.AuctionBot.cs:62`),
   reconciliation of Reserved rows, standing-bid review, daily budgets, and the
   audit that halts the bot (`AuctionBotFeature.cs:596-645`). The `.ahbot`
   commands stay on the world. `reload` and `rebuild` push options or a refill
   request to the service.
2. **Contract.** This follows server-Zero's intent channel (`src/ipc/AuctionIntents.h:64-68`
   and `src/ipc/IpcOpcodes.h:50-54` at the pinned reference). Service to owner:
   `ProposeListing(intent_id, house, entry, count, start_bid, buyout, hours)` and
   `ProposeBuy(intent_id, house, auction_id, bid|buyout, price, observed bid)`.
   Owner to service: `IntentResult(intent_id, Ok | Rejected(reason) | Duplicate,
   ledger key)`, `QueueFull`, and a read-only `MarketSnapshot(house, version,
   open auctions)` for buy planning. `intent_id` is the service incarnation and a
   sequence. The owner records it durably with the custody row it reserved
   (planned column), so a retried intent returns `Duplicate` and the original
   outcome. Buys are also protected by today's one-live-row rule per auction.
   The owner revalidates every intent against current state: price bounds
   (`EconomyFeature.AuctionBot.cs:41-46`), the overrides and blacklist, and the
   daily budgets enforced by the ledger. A faulty service can therefore spend at
   most the daily budgets, and it never mints, moves or deletes an item itself.
3. **Failure.** When the service is down, no new intents arrive. In-flight
   economy operations settle, and Reserved rows are reconciled against
   `economy_operation` on the owner, so no item or copper is lost or duplicated.
   After reconnect, the service asks the owner for the results of its
   outstanding `intent_id`s before it plans again. An audit failure still halts
   the bot on the owner.
4. **Value and cost.** The value is low. One action per 20 s, with an item pool
   rebuilt only when the content changes (`AuctionBotFeature.cs:647-680`), costs
   little world CPU. The gains are crash isolation of planner bugs and, later,
   colocating the planner with a realm economy owner. Cost: a durable intent
   mapping, a market snapshot stream and one more role. Recommendation: stay
   Local until a realm economy owner exists.
5. **Dependencies.** M15.0 (durable auction and item ID allocation, and custody
   writes fenced by owner epoch). M15.1 (transport). It also needs one fenced
   auction and mail owner per realm once more than one worker runs. No phase from
   M15.0 to M15.4 assigns that owner yet, so the open item is part of M15.6.

### Considered and kept in existing roles

- **Metrics exporter.** No service. Each daemon already runs its own
  `MetricsHost` (`src/ArcaneCore.Kernel/Ops/Metrics/MetricsHost.cs:15`) with
  Prometheus pull and/or OTLP push, and an exporter failure never stops the daemon
  ([metrics](ops/metrics.md)). In a cluster every role exports its own series,
  and aggregation belongs to the operator's collector. Planned: `role`, node and
  incarnation labels next to `realm`. Metrics never feed readiness.
- **AutoBroadcast.** No service, but one owner. It is a world-tick timer that
  sends to every player (`ServerMail/AutoBroadcastFeature.cs:51-54`). Run on
  every worker, it would send each message once per worker. In cluster mode it
  runs on the realm social and directory authority (M15.2), which already owns
  realm-wide routed output. GM announcements route the same way.
- **Server mail.** No service. Delivery starts at login (`ServerMail/ServerMailFeature.cs:53`)
  and its conditions read the live Player (`:128-142`), so it stays with the
  player's map owner. Current gap: the letters commit in an economy operation
  with no operation id (`Economy/EconomyFeature.ServerMail.cs:43`). The
  `mail_server_character` record is written afterwards, in a separate write
  (`ServerMailFeature.cs:111-121`). A crash or a failed record between the two,
  or a relog before the record lands, sends the reward again. Make them one
  transaction, or derive the operation id from character and template, before
  M15.3 makes a relog on another worker routine.
- **Movement anticheat.** It stays on the map worker: it scores every movement
  packet against authoritative position ([anticheat](areas/anticheat.md)).
- **Content importer, `arcane-db`, `arcane-account`, `arcane-spell-import`.** These
  are short-lived jobs under M15.5 (component table above), not services.

## Phased implementation and acceptance

The roadmap owns phase status. The technical closure criteria are:

| Phase / lane owner | Concrete implementation | Required closure |
|---|---|---|
| M15.0 — integration/data owner | Inventory feature transfer state; separate connection/simulation responsibilities around existing seams; design and implement durable epochs, ID reservation, save acknowledgement/recovery and coordinated schema initialization | Existing local behavior and mock remain green; concurrent claims, old-owner saves, ID allocation and exhausted write retries have meaningful tests on SQLite/MariaDB/PostgreSQL |
| M15.1 — transport/session owner | Versioned contracts, gateway hosting and one separate game worker; retain combined World hosting and a local contract adapter; realm replicas and account-generation claims | Real owned subprocess registration/receive loop/readiness; actual SRP/M2/world cipher; two gateways replace duplicate sessions consistently; one-worker mock lifecycle remains equivalent |
| M15.2 — realm services/game owner | Routed identity/presence/social authority; one fenced realm auction/mail authority; scoped feature registration; true distinct Map instances and durable instance bindings | Cross-gateway social delivery and logout ordering, concurrent names/group/guild IDs, single auction/mail scheduling and custody ownership, isolated instances, restart recovery and all existing feature tests |
| M15.3 — transfer/game/data owners | Complete feature state capture/install, durable transfer decisions, fenced residency, deadlines and acknowledgement routing; static multiple-worker map placement | Seamless 5875 TCP connection across workers; quests/timers, inventory, casts/auras/cooldowns and corpse semantics preserved; duplicate/lost/reordered/late messages and crashes at every handoff stage cannot activate two owners |
| M15.4 — operations/recovery owner | Host daemon/coordinator supervision, distinct readiness/drain, capacity admission, restart reconciliation and placement of new/empty maps; live map relocation only after complete map snapshots | Kill only fixture-owned subprocesses at defined stages; coordinator/worker/gateway/social/storage partitions, cold starts, drains and constrained-host overload recover with bounded resources and measured data loss policy |
| M15.5 — tooling/compatibility owner | Job contracts for existing CLIs/import/codegen/mock, content revision rollout, mixed-version policy, upgrade/rollback runbook and cluster diagnostics | Offline tools retain CLI use; live content replacement is refused or coordinated; Windows/Linux and private cross-host tests; deterministic output artifacts; compatible rollout and safe local-mode rollback |
| M15.6 — auxiliary services owner | In value order: Warden hosted on the gateway with a realm-wide Warden manifest; the optional Remote bot chat service; the optional Remote bot planner; the optional Remote auction house bot planner with durable intent ids, plus one fenced auction and mail owner per realm. Also the AutoBroadcast placement, cluster metrics labels and the server-mail idempotency fix ([auxiliary services](#auxiliary-services)) | Every Remote service runs as a real owned subprocess and is also tested in Local mode; Local mode stays equivalent to today. Killing a service at each intent stage leaves the custody audit clean, with no lost or duplicated item or copper; a replayed intent returns Duplicate. A down chat service sends nothing to providers and the built-in replies continue. A gateway with Warden enabled and an unusable module is not ready. Stale-epoch plans and verdicts are discarded |

An integrator owns the shared contract/schema changes and final candidate.
Workers own bounded domains and submit reviewable commits; no concurrent edits
to protocol contracts, migration allocation or registration ownership. Implement
one phase with complete tests before scheduling the next. First code tranche is
M15.0, after the current mock milestone, rather than a fleet of empty executables.

## Compatibility, migration and operational evidence

Keep the build-5875 realm/world wire, generated tables, SRP and header cipher
unchanged. One realm advertises gateways, not map workers or a realm per shard.
First deployments keep one characters database per realm. Multiple realms
sharing a characters database would require a separate discriminator/uniqueness
migration across all feature tables before admission.

Current schema lineage is auth v2, characters/world v6. Allocate new contiguous
module versions only when their migrations are implemented and integrated.
One maintenance authority applies upgrades; serving nodes validate compatible
versions without racing DDL. Test fresh schemas, historical upgrades, preserved
rows and idempotence against all existing provider engines. Back up existing
databases and inspect conflicting draft lineages before any upgrade.

Roll out local ownership/persistence semantics first, then one gateway/worker in
an owned fixture, then multiworker static placement, then supervised private-host
acceptance. Keep old combined hosting deployable throughout. Rollback drains
cluster ownership and verifies no pending transfer/writes before local mode
claims the realm; additive schema compatibility must be explicit. Unknown schema
or contract versions fail admission. Never run old unfenced writers alongside
new cluster writers against the same characters database.

Automated evidence must include the existing full suite and mock, actual
separate-process startup, multi-gateway duplicate replacement, cross-worker
transfer, exhaustive transfer crash points, stale incarnation/epoch rejection,
durable mutation retries, social ordering, drained shutdown, content mismatch,
bounded queues and migration/provider cases. Record throughput, p95/p99 tick
time, gateway latency, queue bytes, database pool use, restart time and checkpoint
recovery window before setting placement thresholds. Logs correlate realm,
session generation, worker incarnation, transfer ID, epoch and operation ID;
credentials/session keys stay out of logs.

Later real-client acceptance uses two actual build-5875 clients for visibility,
movement, combat, social interaction, map transfer and reconnect. The current
user request defers that testing; synthetic CI establishes implementation
evidence and cannot mark the runtime playable or cluster acceptance complete.
