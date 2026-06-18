# M3 Acceptance Test — Character Lifecycle

> Defined against a real WoW **1.12.1 (build 5875)** client. Per Charter §6, M3 is "done"
> only when the client creates a character and enters the world, standing still, with no
> disconnect.

## Scope under test

The world daemon's character handling (after the M2 handshake):

- `CMSG_CHAR_ENUM` → `SMSG_CHAR_ENUM` (the account's characters).
- `CMSG_CHAR_CREATE` → `SMSG_CHAR_CREATE` (validation + persistence).
- `CMSG_CHAR_DELETE` → `SMSG_CHAR_DELETE`.
- `CMSG_PLAYER_LOGIN` → the world-entry sequence (`SMSG_LOGIN_VERIFY_WORLD`,
  `SMSG_TUTORIAL_FLAGS`, `SMSG_LOGIN_SETTIMESPEED`, `SMSG_INITIAL_SPELLS`, and the
  `SMSG_UPDATE_OBJECT` that creates the player in the world).

All starting data (positions, display ids, factions, base stats) is **loaded from the
database** (`player_create_info`, `race_info`, `class_info`), seeded with defaults and
fully tunable — no client extraction required.

## Pre-conditions

1. M1 + M2 working; the account has a valid session key.
2. The world daemon has run its character-DB initializer (schema + seeded world data).

## Procedure

1. Reach character-select (M2).
2. Create a character (pick race/class/appearance/name) → it should appear in the list.
3. Enter the world with that character.

## Expected result (PASS criteria)

- Character creation returns success and the character appears in the list with the
  correct name, race, class, appearance and starting zone.
- Selecting "Enter World" loads the starting zone and the character stands there with the
  correct model — **no disconnect**.
- Deleting a character removes it from the list.

Standing in the world with no disconnect = **M3 PASS**.

## Highest-risk area (verify carefully)

The `SMSG_UPDATE_OBJECT` create block (movement block + UpdateFields values + mask) is the
part most sensitive to byte-level errors — any mistake disconnects the client immediately.
The loopback tests confirm its structure, but only a real client confirms the exact field
set/values. Capture the daemon log line "entered the world as '<name>'" and a screenshot of
the character standing in the starting zone.

## Reference basis

Char enum/create/delete formats, the login sequence, opcode values, response codes, and
the object-update/UpdateFields layout verified against vmangos `CharacterHandler.cpp`,
`Player.cpp` (`BuildEnumData`), `Object.cpp` (`BuildCreateUpdateBlockForPlayer`),
`UpdateFields_1_12_1.h`, and gtker vanilla `world_result`. Discrepancies in `MILESTONE_M3.md`.
