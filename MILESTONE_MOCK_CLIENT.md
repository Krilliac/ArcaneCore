# Native mock-client lifecycle

The `arcane-mock self-test` executable authenticates a synthetic account through
the production logon session, verifies the SRP server proof, decodes the realm
list, then authenticates a production world session with that realm-derived key.
It creates and fully enumerates a character, logs in, decodes the initial player
and saved quest journal, queries quest content, checks ping/logout, repeats realm
authentication and relogin, and deletes the character.
It also queries a nearby live synthetic NPC, decodes status and full details,
accepts a second ordinary quest, verifies its fields and saved state across a
fresh realm/world exchange, abandons it, and confirms the cleared slot after
another relog. The original saved journal retains its objective progress.

The next slice adds distinct quest 900003 after those existing journal checks.
Its two real target spawns are visible in the player's map. Independently encoded
eight-byte client attack requests drive the production melee swing and death
path, first to partial progress and then to the complete journal state. The
client sends separate complete and request-reward packets, decodes the entire
offer, selects zero-based choice slot one, and checks the completion body,
coinage, backpack slots and item fields. Real character, inventory and quest
stores must agree on 1234 copper, fixed item 900040, chosen item 900042 and
rewarded history; item 900041 must be absent. Duplicate choice requests and a
fresh realm/world relog must retain that result and the original journal.

Only this synthetic ordinary quest is configured in
`Quests:OrdinaryRewardQuestIds` before startup. It has one positive creature-kill
requirement of two, one fixed reward and two dense choices, with no XP, maximum-level
money, source/required items, reputation, spells, timers, flags or events. A
fixture combat random source makes the low health targets deterministic while
the production attack, damage, death and objective adapters remain in use.

The fixture uses real EF stores and three unique SQLite files, ephemeral loopback
listeners, and the normal world host and feature lifecycle. It tracks sessions
and drains persistence before disposing contexts and deleting its owned directory.
Startup, shutdown, operations and the complete scenario have bounded deadlines.
Realm redirects outside loopback are rejected. No client assets or server accounts
outside this fixture are required.

Client SRP arithmetic, M1/M2, world proof, header cipher and packet encodings are
independent of the server implementation. Tests include pinned numerical vectors,
literal bytes, fragmented reads, EOF, deadlines, reader concurrency, wrong
credentials/builds/proofs, pre-authentication packets, foreign character ownership
and malformed quest requests. The protocol-vector license and byte-order record
are in [the retained notice](tests/ArcaneCore.MockClient.Tests/PROTOCOL_VECTORS_NOTICE.md).

The fixture exposed a production shutdown gap: social login reads were untracked,
and social/spell writes drained only during service-provider disposal. The features
now participate in the host shutdown seam; social reads are canceled and awaited
before their scopes or persistence dependencies can be disposed.

## Reference decisions

[MaNGOS WoWFakeClient](https://github.com/MangosServer/MangosSharp/tree/main/src/tools/Mangos.WoWFakeClient)
is a console protocol client for build 5875, including realm/world authentication,
enumeration, login, chat and ping. Its Windows timing dependency, older target
framework and GPL licensing make a native independently written harness a better
fit here. Its files are references only; no implementation was copied or vendored.

The native client was cross-checked with [gtker/wow_messages at the recorded pin](https://github.com/gtker/wow_messages/tree/70abb9deff0bb63440d8aeb4386b820653e8a176/examples/vanilla_client)
and tested against [wow_srp numerical/header vectors](https://github.com/gtker/wow_srp/tree/25ffab6433e1ee5eee629200cf42b592c1f36121).
Upstream examples are useful protocol references, but do not establish ArcaneCore
acceptance. Upstream header fixtures, even where labeled captures, are not treated
as verified evidence from an actual client session in this project.

The [C# SRP alternative](https://github.com/gtker/wow_srp_csharp) and
[C# message-generator successor](https://codeberg.org/gtker/wow_messages_csharp)
remain reference options. No new runtime dependencies were introduced.

## Validation and remaining acceptance

The preceding native journal slice's combined Release build passed with zero warnings/errors and all
8,657 tests passed (8,005 crypto, 59 SQLite data, 367 Game, 46 mock-client,
3 Realm and 177 World), with no failures or skips. The executable passed
all 29 named checks across 75 received frames in approximately two seconds.
The final focused review found and corrected rewarded-history reacceptance;
ordinary and repeatable history now have regression coverage.

The combat/reward Release build passed with zero warnings/errors and all 8,760
native tests passed with zero failures/skips: crypto 8,005, SQLite data 74,
Game 398, mock-client 92, Realm 3 and World 188. The standalone executable
passed all 41 checks across 122 received frames in 5.024 seconds. It retains
the original 29 named checks and adds 12 reward checks, with the same
45-second scenario and 60-second CLI deadlines. New wire vectors independently
pin the offer suffix and fixed-reward-only completion layout to vmangos/core
`4b3d241cffe245a1f68da11380bce96c23db48c0`; the recorded gtker pin agrees on
completion and differs semantically on the zero-valued offer flags/spell words.
All 16 adverse reward cases passed, including precommit failure/retry, capacity, lost
acknowledgement and unreadable-outcome quarantine/relog. Qualification fixed
nearby-create timing, natural combat exit before logout and an existing delayed
social-load overwrite race; four deterministic social regressions passed.

Run the [implementation checks](docs/MOCK_CLIENT_ACCEPTANCE.md) and the full
Release/provider suite. Record exact results and integrated source heads in
[the integration ledger](docs/integration/fleet-20261003.md).

The asynchronous settlement successor passed the full native Release build with
zero warnings/errors and all **8,799 tests**, zero failures/skips: crypto 8,005,
SQLite data 78, Game 416, mock-client 100, Realm 3 and World 197. The standalone
scenario retains **41 checks / 122 frames**, passing in **4.935 seconds**. A real
scoped reward operation held **744.517 ms** while **47 actual map ticks** ran;
maximum gap **16.551 ms**, p95 **16.526 ms**, maximum world command **16.098 ms**,
NPC status **16.591 ms**, and independent player quest acceptance **16.901 ms**.
The fixture uses its configured 5 ms tick and 200 ms response budgets; these are
observed Windows timings, not a claim that every tick meets 5 ms. Eight
held operations retained eight distinct identities; the ninth player stayed
active and retried successfully. Actual database rollback, uncertain commit,
disconnect/relog, stale snapshot and retained dirty-field tests pass.
See the [async contract](docs/integration/quest-settlement-async.md) and the
[bounded real-client handoff](docs/integration/quest-client-acceptance.md).

This demonstrates the implemented exchange against ArcaneCore's own listeners.

The NPC greeting successor retains all 41 checks and adds 18 actual wire checks,
passing **59 checks / 142 frames / 5.041 seconds** on the exact native source
`248accc71acbe70144b92c33761d8f9ae0ab07cc`. It decodes both hello responses,
eligible/mixed menus, selected details and incomplete/completed turn-in phases,
and verifies rewarded history stays excluded across duplicate choice and relog.
Full native Release passed **8,866 tests**, zero failures/skips and zero
warnings/errors. [Full source provider CI](https://github.com/Krilliac/ArcaneCore/actions/runs/37104390069)
passed **8,978 tests** across SQLite/MariaDB 10.11/PostgreSQL 16 and the standalone
scenario before canonical integration. Schema versions remain auth 2/characters 6/world 6.

`arcane-mock client-fixture --directory <new absolute path> --account <name>
--password <disposable password>` writes persistent repository-authored content,
three SQLite databases, server-only faction data, loopback configuration and
provenance. It does not start a daemon or clean up its output. Eleven focused
tests cover transactional rollback, existing-content/output refusal, credentials,
hashes, cancellation and normal world loading with its listener removed. The
[separate UI handoff](docs/integration/quest-ui-acceptance.md) preserves the user's
original f8ae6e8 acceptance target and reserves actual client work for their
selected file-access chat; publishing this successor does not change that pin.

The [user-provided f8ae6e8 baseline report](docs/integration/client-run-f8ae6e8-20261003.md)
records single-client authentication, creation, visible world entry and movement
passes. Its artifacts were not independently inspected by this coding task;
logout/relog and restart persistence remain pending. NPC quest UI, models/icons,
terrain/content fidelity, broader combat and playable quest/reward behavior still
require their own real-client acceptance. No default branch merge, deployment or
release is part of this tranche.

## Inactive queue compatibility and lifecycle evidence

Qualified source `434968a555bfbf15fb4bc6156e34ae277baee572` handles the two
reported empty vanilla polls through normal world-thread ownership. An unqueued
battlefield poll sends nothing; meeting-stone info sends opcode 661 with exact
idle body `00 00 00 00 05`. Queue joining, battlegrounds and matchmaking remain
unsupported. Surplus-byte rejection is an explicit ArcaneCore validation policy.

Production `WorldServer` now observes accepted sessions through handler completion
and async scope disposal before listener shutdown finishes, including a canceled
host budget. Tests prove the prior premature return and the fixed drain. Additional
real SQLite/SRP mock cases verify ordinary countdown logout/save barriers and
durable position, identity and action-bar reload through a fresh host/provider.
Non-cooperative I/O/disposal can extend shutdown until the owned work terminates.

Full source provider CI passed 8,999 tests, zero failures/skips, with Release zero
warnings/errors; native full qualification passed 8,887. The existing standalone
59-check / 142-frame scenario remains intact. See the
[contract and evidence](docs/integration/protocol-lifecycle-compat.md).
Actual f8ae6e8 logout/relog/restart and current NPC UI acceptance remain pending
and separate from this automated evidence. Computer Use is deferred by the user.
