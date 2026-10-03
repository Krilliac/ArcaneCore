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

The combined native Release build passed with zero warnings/errors and all
8,657 tests passed (8,005 crypto, 59 SQLite data, 367 Game, 46 mock-client,
3 Realm and 177 World), with no failures or skips. The executable passed
all 29 named checks across 75 received frames in approximately two seconds.
The final focused review found and corrected rewarded-history reacceptance;
ordinary and repeatable history now have regression coverage.

Run the [implementation checks](docs/MOCK_CLIENT_ACCEPTANCE.md) and the full
Release/provider suite. Record exact results and integrated source heads in
[the integration ledger](docs/integration/fleet-20261003.md).

This demonstrates the implemented exchange against ArcaneCore's own listeners.
Rendering, UI, client executable acceptance, terrain/content fidelity, animation,
and a complete playable quest/reward loop still require later work and real-client
acceptance. No default branch merge, deployment or release is part of this tranche.
