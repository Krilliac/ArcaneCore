# Observed play protocol follow-up

The original build-5875 client sandbox session authenticated, created a character, entered the world and exercised movement, chat, commands and UI. Its Realm log also closed two reconnect command `0x02` connections, and World logged unhandled ticket status/create requests. This follow-up addresses those concrete protocol gaps. A full content/client acceptance claim remains pending.

Reconnect follows pinned vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `src/realmd/AuthSocket.cpp:874-982`: a fresh 16-byte server challenge, the existing session key, and SHA1 over username, client R1, server challenge and key in that order. Successful proof uses the selected vmangos two-byte opcode/result response. CMaNGOS uses a four-byte success variant; this slice does not claim both framing contracts.

Challenge/proof state is retired on new exchanges and every proof revokes prior authentication before validation. Reconnect retains normal name/build/locale, account status, account-ban and IP-ban gates. Only a stored 40-byte session key is eligible; invalid or out-of-order proofs close. Account has no expiry field, and strict R3 client-integrity validation has no current configuration/source seam, so neither is claimed.

Ticket status reports a disabled queue, and a source-bounded create envelope receives the unavailable result. Handler discovery wires the two observed opcodes automatically. Ticket text is never decoded or persisted. Other ticket operations and a durable support queue remain pending.

Root review corrected a reversed proof order mirrored by the initial tests, active-ban result mapping, suspended-account result mapping, and unbounded test teardown. Compile/fixture failures remain preserved. Tests cover successful reconnect and realm list, one-shot replay, bad/out-of-order proof, inactive/missing/wrong-sized key, account/IP bans, and actual World ticket replies/malformed envelopes. Verification uses separate artifacts so the previous qualified live server DLLs remain intact.

The empty observed world is explained by absent imported content and extracted DBC inputs. A later private content preparation phase will use the selected classic-db z2815 snapshot and original client DBCs. Starting actions already have a mapped importer; starting skills and outfit-derived items need separate contracts. Private assets and session databases are excluded from source exports.

Qualification: 14,747 passed, six existing skips, zero failures; 85 focused checks, native59, clean Release. Frozen-source, isolated-index replay and ZIP checks verified locally. Original-client reconnect acceptance and later local provider CI remain pending.
