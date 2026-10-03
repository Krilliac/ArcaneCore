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

## Limits

Mock success verifies the implemented wire exchange and server behaviour. It
does not verify client rendering, UI, terrain, animation, or acceptance by an
actual build 5875 executable. The mock covers bounded ordinary NPC journal
interactions; objective event integration, source items/spells, reputation,
repeatables, rewards and broader NPC services require their actual adapters.
