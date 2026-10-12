# Wiring audit

A feature that is written but not connected looks like a feature that works until someone uses it. The wiring audit reports, from the running
world daemon, what is declared but not wired. It never changes behaviour.

## Where it runs

* Once after the host has started: one log line from `WiringAuditReporter`. Information `wiring audit: clean (N opcode handlers, N config checks, N IOptions consumers)`,
  or a Warning `wiring audit: K finding(s): <category> <count>, ... (.server wiring lists them)`.
* On demand: `.server wiring` (Administrator, also at the server console). It runs the audit again and lists up to 40 findings.

Unhandled opcodes only appear once clients have connected, so the start-up line covers the static categories and `.server wiring` the live ones.

## Categories

| Category | Meaning |
|---|---|
| `unhandled-opcode` | A client sent this opcode and no handler is registered, so the packet was dropped (the connection stays open). Counted per opcode since start. |
| `options-unbound` | A class asks for `IOptions<T>`, `IOptionsSnapshot<T>` or `IOptionsMonitor<T>`, the options type declares a `SectionName` that is in the `.conf` catalog (so operators are told they can set it), but nothing in the container configures `T`: the class always sees defaults. |
| `config-check-not-run` | A concrete `IConfigCheck` in the world assembly or one it references that `check-config` and the start-up validation do not run (it needs constructor arguments the daemon does not supply, or lives in an assembly the daemon does not scan). |
| `handler-for-server-opcode` | A handler is registered for an `SMSG_*` opcode, which a client never sends. |

Not audited: a world feature whose `Attach` threw (that aborts start-up, so there is no skipped state) and optional `GetService`-only dependencies that resolved to null (no cheap, non-speculative signal).
Options that a feature reads straight from `IConfiguration` are not options consumers; the source scan in the Docs tests covers keys read but not documented.

## Unhandled opcodes

`WorldSession` counts each unhandled opcode in `UnhandledOpcodeTracker` (one atomic increment, no allocation; bounded by the opcode space, values without a reference
name share the `unknown` bucket). The first time an opcode is seen in the process it logs one Warning:
`client sent <OPCODE> (0x...) which has no handler; further occurrences are counted`. The metric is `arcanecore.net.opcode.unhandled` (Prometheus `arcanecore_net_opcode_unhandled_total`),
tagged `protocol=world` and `opcode=<name>` (docs/ops/metrics.md).

## Config checks are discovered

`check-config` and the start-up validation of both daemons run every concrete `IConfigCheck` of the Kernel assembly and the daemon's own assembly, found by reflection and
ordered by type name (`ConfigCheckDiscovery`), plus the `.conf` key check which the daemon supplies itself. A new check needs no list entry. A check with a public or non-public
parameterless constructor is created automatically; one that needs arguments must be added to `OpsCli.CreateChecks` / `RealmOpsCli.CreateChecks`, or the audit reports it.

## CI guards

`tests/ArcaneCore.World.Tests/Ops/WiringAuditTests.cs`: the whole production registration (`WorldProductionServices.AddWorldProductionServices`, which `Program.cs` calls) is built with
`ValidateOnBuild` and `ValidateScopes` and must contain what each `Add*` call adds; every concrete `IConfigCheck` is run; the audit of the production container has no static findings except the
allowlisted ones (each with a reason); the unhandled-opcode counter, warn-once and metric; `.server wiring` output. `tests/ArcaneCore.Realm.Tests/Ops/RealmWiringTests.cs` does the same for the realm.
