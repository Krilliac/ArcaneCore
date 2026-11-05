# Supervisor protocol: child side (kernel)

How a child process launched by the cluster host supervisor ([clustering design](../CLUSTERING_DESIGN.md), M15.4) talks to it. The
code is `SupervisedChildDrain`, `ConsoleInputSource` and `SupervisorProtocol` under `src/ArcaneCore.Kernel/Ops/Console`.

## Drain and stop

The supervisor sets `ARCANECORE_SUPERVISED_CHILD`, redirects the child's stdin and drains it by writing the line `drain` and closing
stdin. The child stops through the host's graceful path on `drain` or on end of input (the supervisor died). With the line protocol on
(`ARCANECORE_SUPERVISOR_PROTOCOL=1`) and a token, the child writes `lifecycle stopping token=... intent=... code=...` to stdout when the
host starts stopping. The write happens on a dedicated thread; `StopAsync` waits at most 2 s for it and also returns as soon as the host's
shutdown token is cancelled.

## The per-run token

`ARCANECORE_SUPERVISOR_TOKEN` is the supervisor's per-run secret. A supervised daemon takes it out of the environment once, at the very start of
`Main` (`SupervisorProtocol.CaptureProcessToken()`, before the host's configuration builder copies the environment), and the variable is removed
from the process environment, so processes the child starts through `Process.Start` (a `gh` tool) and the host's `IConfiguration` do not inherit it. On Linux, `Environment.SetEnvironmentVariable` is not known to change the initial environment block that `/proc/<pid>/environ` shows or that a natively spawned tool (a crash-dump tool the runtime starts itself) reads, so the removal is only claimed for managed children and the configuration. Every
reader in the process takes the token from that captured copy: `ConsoleInputSource.Default()`, the heartbeat writer and the World's
`SupervisorProtocolChannel.Process` all go through `SupervisorProtocol.ProcessEnvironment`, so removing the variable disables nothing (cross-realm
`.server restart|shutdown` still works under supervision). The Gateway captures it in the same way. A process that is not supervised has no token and
nothing happens.

## Authentication

Every line the supervisor acts on carries the token: the `lifecycle` lines (requests, results and the `stopping` announcement), the `dumping`
heartbeat a child writes before its in-process crash dump, and every plain heartbeat, `heartbeat <UTC> pid=<pid> ready|alive|stopping token=<token>`.
Child output also carries logged text that a player or GM can influence. If such text contains a bare LF, the part after it becomes a line of its
own and could match a line shape. The parser rejects any line with a control character, and the supervisor treats a line without the right
token as ordinary output: a forged heartbeat cannot make a hung child look alive, arm the hang watch or delay hang detection, and a forged
`lifecycle` line cannot drain, stop or restart a child.

## The supervisor process itself

- **Free memory.** A start refused only because free memory is below `MinFreeMemoryReserveMb` does not end the supervisor: it logs a warning, waits 30 s and plans again until the plan reaches `MinClusterProcesses`, a stop signal ends the wait, or a child launched meanwhile took the memory the plan counted on (the rest are launched once memory is free). Any other refusal (process ceiling, total allocation, map ownership), a mixed plan where memory cannot make up the difference, and a reserve that is not below the memory the host (or its container limit) can offer, are configuration errors: the supervisor exits 1 with the reason on stderr. `tools/ops/live/start-cluster.ps1` sees a waiting supervisor as a started process.
- **Exit line.** Every way the supervisor ends writes `exiting with code <n>: <reason>` to its log and to stderr (the start refused, the stop failed, a stop signal, below the minimum). An unhandled exception and an end from outside (console closed, termination signal) are written by hooks that flush the log first; for a crash the runtime chooses the exit code, so that line names none. A hard kill (`TerminateProcess`, the OOM killer) can be seen by no hook, and a refusal at exit 78 or a failure while the host is being built happens before the hooks and is written to stderr only.
