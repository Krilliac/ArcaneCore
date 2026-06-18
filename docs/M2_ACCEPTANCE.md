# M2 Acceptance Test — World Handshake

> Defined against a real WoW **1.12.1 (build 5875)** client. Per Charter §6, M2 is "done"
> only when the client clears the world handshake and reaches the (empty) character-select
> screen.

## Scope under test

The ArcaneCore **world daemon** (`ArcaneCore.World`) on TCP **8085**:

- Sends `SMSG_AUTH_CHALLENGE` (server seed) with a plaintext header.
- Receives `CMSG_AUTH_SESSION`, validates the client's digest using the **session key
  produced during M1 logon** (read from the account row — the realm↔world seam).
- Engages **header encryption** — the vanilla rolling byte cipher seeded from the raw
  session key (Charter §3; **not** ARC4).
- Replies `SMSG_AUTH_RESPONSE` (AUTH_OK) and `SMSG_ADDON_INFO`.
- Answers `CMSG_PING` with `SMSG_PONG` and `CMSG_CHAR_ENUM` with an empty `SMSG_CHAR_ENUM`.

Out of scope (M3+): character creation/login, world entry.

## Pre-conditions

1. M1 is running and the account has logged in through the realm daemon at least once, so
   `account.SessionKey` is populated.
2. The realm row's `address` points at the world daemon host:port (e.g. `127.0.0.1:8085`).
3. The world daemon is configured against the **same** auth database as the realm daemon
   and is running on 8085.

## Procedure

1. Log in at the client (M1) and select the ArcaneCore realm.
2. The client connects to the world daemon and performs the auth handshake.

## Expected result (PASS criteria)

- The client does **not** disconnect during the handshake (digest validated; encryption
  engaged correctly — a cipher desync drops the connection immediately).
- `SMSG_AUTH_RESPONSE` returns **AUTH_OK** (`0x0C`).
- The client advances to the **character-select screen**, showing **no characters**.

Reaching the empty character-select screen = **M2 PASS**.

## Evidence to capture

- Screenshot of the empty character-select screen.
- World daemon logs: challenge sent, auth digest validated, char list sent.

## Reference basis

Handshake, digest construction, opcode values, header sizes and the rolling cipher all
verified against vmangos `src/game/Server/WorldSocket.cpp`,
`src/shared/Auth/AuthCrypt.cpp`, and `Opcodes_1_12_1.h`. Discrepancies recorded in
`MILESTONE_M2.md`.
