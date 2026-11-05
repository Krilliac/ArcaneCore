# Native mock client

`arcane-mock` (`tools/ArcaneCore.MockClient`) is a build-5875 protocol client written independently of the server. It drives the production realm and world sessions over
ephemeral loopback listeners, with synthetic accounts and content in unique disposable SQLite files. It is automated protocol validation against ArcaneCore's own listeners.
It does not show that a real 1.12.1 executable renders, animates or accepts anything: that is a separate run, see [real-client acceptance](../guide/client-acceptance.md).
No client files, DBCs or game assets are needed.

## Running it

```bash
dotnet build ArcaneCore.slnx -c Release
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

`self-test` prints a structured JSON report with one entry per named check and exits non-zero when any stage fails. The scenario has a 45 second budget and the CLI a 60 second one;
startup, shutdown and every operation have bounded deadlines, so a failure is finite. CI runs it after the test suites. A run that prints no report, or a report with fewer checks than
the previous one, is not a pass.

`arcane-mock client-fixture --directory <new absolute directory> --account <name> --password <disposable password>` writes persistent repository-authored content, three SQLite
databases, server-only faction data, a loopback configuration and provenance, so a real client can be pointed at a throwaway realm. It starts no daemon and does not clean up its
output; it refuses an existing output directory and existing content.

The `playbot` verb is an external protocol client that plays autonomously ([playbots](playbots.md#mockclient-playbot-external-protocol-client)).

## What the self-test covers

- **Logon and world authentication.** A synthetic account authenticates through the production `LogonSession`, the client verifies the server's SRP proof, decodes the realm list, follows the
  loopback realm address and authenticates a production world session with the key that exchange produced. The client's SRP arithmetic, `M1`/`M2`, world digest, header cipher and
  packet encodings are independent of the server's: tests pin numeric vectors from wow_srp and literal bytes, and cover fragmented reads, EOF, deadlines, reader concurrency, wrong
  credentials, builds and proofs, packets sent before authentication, foreign character ownership and malformed quest requests.
- **Character lifecycle.** Empty character list, create, exact character-list decoding, login with the initial player and the saved quest journal, ping, logout, a fresh realm/world exchange with
  relogin, and deletion back to an empty list.
- **NPC quest journal.** Status and full details from a nearby live questgiver, acceptance of a second ordinary quest into the next journal slot, persistence across a fresh authentication,
  abandonment with the one-byte slot request, and the cleared slot after another relog. The original journal's objective progress survives every stage.
- **Combat and rewards.** A distinct ordinary quest whose two real target spawns are visible: eight-byte `CMSG_ATTACKSWING` requests drive the production melee swing and death path to partial and then
  complete progress; separate complete and request-reward packets produce fully decoded offers; a sixteen-byte choice request picks a zero-based slot. The reward (money, one fixed item, the chosen item,
  rewarded history, the unchosen item absent) must agree across the real character, inventory and quest stores, survive a repeated choice request and a relog. Only that synthetic quest opts into
  ordinary rewards before the host starts, and a fixed combat random source makes the low-health targets deterministic while the production attack, damage, death and objective adapters run.
- **NPC greeting.** Both hello responses, eligible and mixed menus, the selected quest's details, and the incomplete and completed turn-in phases, with rewarded history staying excluded across a duplicate choice and a relog.
- **Shutdown and lifecycle.** The fixture tracks sessions, drains persistence, disposes contexts and deletes its own directory. Realm redirects outside loopback are rejected. The world server waits for
  accepted sessions through handler completion and async scope disposal before listener shutdown finishes.

Reward and offer byte layouts are pinned by independent literal vectors, with bounded counts, truncation and trailing-byte checks, to vmangos/core (the pinned revision is recorded in the test sources;
gtker agrees on completion and differs semantically on the zero-valued offer flags and spell words).

## Reference decisions

- [MaNGOS WoWFakeClient](https://github.com/MangosServer/MangosSharp/tree/main/src/tools/Mangos.WoWFakeClient) is a console protocol client for build 5875. Its Windows timing dependency, older target framework and GPL licensing make a
  native, independently written harness a better fit. Its files are references only; nothing was copied or vendored.
- The client was cross-checked with the [gtker/wow_messages vanilla client example](https://github.com/gtker/wow_messages/tree/main/examples/vanilla_client) and tested against
  [wow_srp numeric and header vectors](https://github.com/gtker/wow_srp). Upstream examples are protocol references and do not establish ArcaneCore acceptance; upstream header fixtures, even where
  labelled captures, are not treated as evidence from an actual client session in this project. The vector license and byte-order record is
  [PROTOCOL_VECTORS_NOTICE.md](../../tests/ArcaneCore.MockClient.Tests/PROTOCOL_VECTORS_NOTICE.md).
- The [C# SRP alternative](https://github.com/gtker/wow_srp_csharp) and the [C# message-generator successor](https://codeberg.org/gtker/wow_messages_csharp) remain options. No runtime dependency was added for them.

## Limits

Mock success verifies the implemented wire exchange and server behaviour. It does not verify client rendering, UI, terrain, animation or acceptance by a real executable. The mock covers bounded
ordinary NPC journal interactions and a configured ordinary creature-kill quest with fixed and choice item and money rewards. Source items and spells, reputation, repeatables, timed or scripted
objectives and broader NPC services need their own adapters and their own acceptance. Related records: [quest settlement](../integration/quest-settlement-async.md),
[quest client acceptance](../integration/quest-client-acceptance.md), [protocol lifecycle compatibility](../integration/protocol-lifecycle-compat.md).
