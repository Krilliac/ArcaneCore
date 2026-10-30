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

`ARCANECORE_SUPERVISOR_TOKEN` is the supervisor's per-run secret. `ConsoleInputSource.Default()` reads it once, keeps it on the source, and
removes the variable from the process environment so children of the child do not inherit it. The host's environment configuration
provider copies the environment when the builder is built; `Default()` runs later (from a DI singleton factory), so the configuration
still holds a copy under the raw variable name. Nothing binds that key.

## Authentication limits

`lifecycle` lines (requests, results and the `stopping` announcement) carry the token. A `heartbeat` line does not: it is
`heartbeat <UTC> pid=<pid> ready|alive|stopping` and nothing else.

Child output also carries logged text that a player or GM can influence. If such text contains a bare LF, the part after it becomes a line
of its own and could match the heartbeat shape. The parser rejects any line with a control character, so a forged line cannot smuggle a
second line inside it, but a clean forged line is accepted. The effect is bounded to liveness: a forged heartbeat can make a hung child
look alive and so delay hang detection. It cannot drain, stop or restart a child, because those paths need the token.
