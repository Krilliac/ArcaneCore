# Native mock-client implementation checks

This is automated protocol validation against owned, isolated ArcaneCore
instances. Real WoW 1.12.1 build 5875 client compatibility remains a separate,
later acceptance run with developer-supplied content and terrain.

## Executable procedure

1. Build the solution in Release with warnings as errors.
2. Run `dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test`.
3. Require exit zero and a structured JSON report whose checks pass. The scenario
   must authenticate through the actual realm server, verify its SRP proof,
   follow the loopback realm address, and authenticate the world session using
   the session key produced by that exchange.
4. Require an empty initial character list, successful synthetic character
   creation, exact character-list decoding, player login with journal fields,
   matching quest content, ping echo, logout, realm/world reconnect with the
   same saved character, and final deletion/empty list.
   The same executable must query a visible synthetic questgiver's exact status
   and details, accept quest 900002 into the second journal slot, persist it
   across fresh authentication/relogin, abandon it with the one-byte slot
   request, and verify the cleared slot after another relog. Quest 900001's
   original objective progress must survive every stage.
   After those original checks, require quest 900003 to use the live guide as
   starter and ender and two distinct, actually visible creature spawns 900021
   and 900022 of template 900030. Each exact eight-byte `CMSG_ATTACKSWING` must
   kill its target through the production map combat/death path. Independently
   decoded objective packets, journal update masks and real store reads must
   show incomplete count one, then complete count two.
   Require separate twelve-byte `CMSG_QUESTGIVER_COMPLETE_QUEST` and
   `CMSG_QUESTGIVER_REQUEST_REWARD` exchanges to produce fully decoded offers,
   including the fixed reward, two choices, displays, copper and the complete
   emote/flags/spell suffix. Send a sixteen-byte choice request with zero-based
   slot one. Opcode 401 must contain quest id, type three, zero XP, money 1234,
   one fixed reward and its item/count pair, with no GUID or choice reward.
   Player coinage, cleared journal slot, backpack GUID fields and item create
   fields must agree with the real character, item and quest stores: one item
   900040, one item 900042, no item 900041, and rewarded history recording item
   entry 900042. Repeat the choice packet and require no second completion or
   item grant. A fresh realm/world handshake must restore those rewards and
   the cleared reward slot while preserving quest 900001's original progress.
5. Run the mock-client tests and full existing solution tests. Wrong credentials,
   build/version errors, invalid session states, malformed owned-instance
   requests, foreign character ownership and hostile realm redirects must have
   explicit bounded outcomes. Crypto and packet fixtures must use independent
   verified values rather than only server/client round trips.

All endpoints are ephemeral loopback listeners; all accounts and content are
synthetic and all databases are unique disposable SQLite files. Session tasks,
feature saves, listeners and contexts must stop before fixture cleanup finishes.
Operation and scenario deadlines keep failures finite. No proprietary client or
game assets are needed for these checks.

The combat/reward Release build passed with zero warnings/errors; all 8,760
native tests passed without failures/skips. The standalone executable passed
41 checks across 122 received frames in 5.024 seconds, retaining all original
checks. Its 16 adverse reward cases include failed commits, full inventory,
lost acknowledgement and unreadable-outcome quarantine followed by fresh login.
Four deterministic social-load regressions and queue recovery tests passed. The
45-second scenario and 60-second CLI limits remain. Only synthetic quest 900003
opts into ordinary rewards before the host starts. Offer and completion bytes
are pinned to vmangos/core `4b3d241cffe245a1f68da11380bce96c23db48c0`, with
independent literal vectors, bounded counts, truncation and trailing-byte checks.

## Limits

Mock success verifies the implemented wire exchange and server behaviour. It
does not verify client rendering, UI, terrain, animation, or acceptance by an
actual build 5875 executable. The mock covers bounded ordinary NPC journal
interactions and a configured ordinary creature-kill quest with fixed/choice
item and money rewards. Source items/spells, reputation, repeatables, timed or
scripted objectives and broader NPC services require their actual adapters and
separate acceptance.
