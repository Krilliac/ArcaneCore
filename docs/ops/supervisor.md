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
from the process environment, so processes the child starts (a `gh` or crash-dump tool) and the host's `IConfiguration` do not inherit it. Every
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
