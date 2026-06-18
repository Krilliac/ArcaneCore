# MILESTONE M2 — World Handshake

Status: **implementation + automated verification complete; awaiting real-client
acceptance** (`docs/M2_ACCEPTANCE.md` is the developer's gate).

## What was built

- `ArcaneCore.Protocol` — world-stream infrastructure: `WorldOpcode` (build-5875 values),
  the vanilla `WorldHeaderCrypt` (rolling header cipher), and packet read/write helpers.
- `ArcaneCore.World` — the world daemon (TCP 8085): `SMSG_AUTH_CHALLENGE` →
  `CMSG_AUTH_SESSION` digest validation → header encryption → `SMSG_AUTH_RESPONSE` →
  `SMSG_ADDON_INFO`, plus `CMSG_PING`/`SMSG_PONG` and an empty `CMSG_CHAR_ENUM` →
  `SMSG_CHAR_ENUM` to reach character select.
- `ArcaneCore.Kernel` — added `WorldOptions`.

The session key continuity is the realm↔world seam: the realm daemon stores `K` on the
account at logon (M1); the world daemon reads it back through `IAccountStore` to validate
the world digest. No direct coupling between the daemons.

## Verified against (Charter §1.1 / §4)

| Detail | Reference |
|--------|-----------|
| Vanilla header cipher (rolling, raw K key — **not** ARC4/HMAC) | vmangos `src/shared/Auth/AuthCrypt.cpp` |
| Header sizes (SMSG 4, CMSG 6), big-endian size + LE opcode | vmangos `WorldSocket.cpp` `ServerPktHeader` |
| `CMSG_AUTH_SESSION` layout + auth digest | vmangos `WorldSocket::_HandleAuthSession` |
| `SMSG_AUTH_CHALLENGE` / `SMSG_AUTH_RESPONSE` (1-byte vanilla result) | vmangos `WorldSocket.cpp`, `WorldSession.cpp` |
| Opcode values (492/493/494, 476/477, 55/59, 751) | vmangos `Opcodes_1_12_1.h` |
| `AUTH_OK` = 0x0C | vmangos `SharedDefines.h` (enum ResponseCodes) |
| `SMSG_ADDON_INFO` build + public-key blob | vmangos `src/game/Handlers/AddonHandler.cpp` |

### Automated tests
- **3** header-cipher tests: server-encrypted SMSG headers decrypt on a peer client and
  vice versa across 50 packets (rolling state stays in lock-step); no-op before init.
- **2** loopback handshake tests driving the real `WorldSession`: full challenge → session
  → encrypted AUTH_OK → addon info → empty char list; and digest rejection.

## Decisions & discrepancies

1. **Auth digest hashes the full 40-byte session key.** vmangos hashes the minimal
   big-number form of `K`; differs only if `K`'s most-significant byte is zero (~1/256).
   ArcaneCore hashes the stored 40 bytes (the client-correct width). To confirm against
   the real client.
2. **Encryption boundary.** `SMSG_AUTH_CHALLENGE` and the inbound `CMSG_AUTH_SESSION` use
   plaintext headers; encryption engages immediately after a successful digest, so
   `SMSG_AUTH_RESPONSE` onward have encrypted headers (matches vmangos ordering).
3. **`SMSG_AUTH_RESPONSE` is a single result byte** in vanilla (billing fields are TBC+).
4. **Addon info** is parsed from the client's zlib block and answered per vmangos
   (Blizzard addons hidden, custom addons visible). A malformed/absent block yields an
   empty response; the client still proceeds.
5. **No `REALM_SPLIT`** — that opcode is TBC+, so the vanilla client does not send it.

## Client build verified against

Pending — to be filled in by the developer after running `docs/M2_ACCEPTANCE.md`
against a real WoW **1.12.1 (build 5875)** client.
