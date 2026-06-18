# MILESTONE M3 — Character Lifecycle

Status: **implementation + automated (loopback) verification complete; awaiting
real-client acceptance** (`docs/M3_ACCEPTANCE.md`). The world-entry object update is the
part most likely to need real-client iteration (see risks below).

## What was built

- `ArcaneCore.Game` (new) — the entity/object-model layer: `ObjectGuid` (+ packed GUID),
  `UpdateFields` (build-5875 indices), `UpdateMask`, `PlayerObject`, and
  `ObjectUpdateBuilder` (the SMSG_UPDATE_OBJECT create block).
- `ArcaneCore.Data` — `CharacterDbContext` with the `characters` table and the
  **DB-driven world data** tables `player_create_info`, `race_info`, `class_info`,
  seeded by `CharacterDbInitializer`. `EfCharacterStore` + `EfWorldDataStore`.
- `ArcaneCore.Kernel` — `CharacterRecord` + `ICharacterStore`; world-data records +
  `IWorldDataStore`.
- `ArcaneCore.World` — handlers for char enumerate / create / delete and player login
  (the world-entry packet sequence + object update).

## DB-driven "DBC" design (per request)

All race/class/appearance/start data lives in seeded, tunable database tables — **no
client DBC extraction is required**. `player_create_info` (race/class → start position),
`race_info` (race/gender → display id + faction), and `class_info` (class → base
health/mana + power type) are seeded with sane vanilla defaults and can be fine-tuned in
the DB. Optional validation against extracted DBCs can be layered on later without
changing the runtime, which only reads these tables.

## Verified against (Charter §1.1 / §4)

| Detail | Reference |
|--------|-----------|
| `SMSG_CHAR_ENUM` per-character block | vmangos `Player::BuildEnumData` |
| Char create/delete + login opcodes (54/55/56/58/59/60/61) | vmangos `Opcodes_1_12_1.h` |
| Char result codes (create 0x2E, delete 0x39, …) | gtker vanilla `world_result` |
| Object-update envelope + create block | vmangos `Object::BuildCreateUpdateBlockForPlayer`, `UpdateData::BuildPacket` |
| Movement block (LIVING + speeds + ALL) | vmangos `Object::BuildMovementUpdate`, `MovementInfo::Write` |
| UpdateField indices (OBJECT/UNIT/PLAYER) | vmangos `UpdateFields_1_12_1.h` (symbolic arithmetic) |
| TYPEID/TYPEMASK, UPDATETYPE/UPDATEFLAG | vmangos `ObjectGuid.h`, `UpdateData.h`, `Unit.cpp` |

### Automated tests (8 world tests total)
- Full lifecycle over loopback: create → enumerate (name verified) → player login
  (verify-world / tutorial / time-speed / initial-spells / **update-object**) → delete →
  empty list. Plus duplicate-name and invalid-race/class rejection.

## Decisions & discrepancies

1. **Object update is the highest-risk, client-pending area.** Unlike SRP6 (deterministic
   KATs) and the header cipher (inverse-verified), the UpdateFields create block has no
   known-answer vector here. Its structure is loopback-tested; exact field correctness
   needs a real client. The set of fields written is a faithful minimal-but-complete
   subset; fields may need adding/adjusting during client testing.
2. **UpdateFields header comments are stale.** vmangos `UpdateFields_1_12_1.h` inline hex
   comments for PLAYER_* are 6 too low; the symbolic `UNIT_END + offset` arithmetic is
   authoritative and is what ArcaneCore reproduces (`UNIT_END = 188`, `PLAYER_END = 1282`).
3. **Single database for M3.** Characters + world data share the configured database with
   auth (a second `CharacterDbContext`, separate tables). Real cores split realm/character
   DBs; splitting is a later change and does not affect the seams.
4. **Power values by type.** Rage → 0/1000, Energy/Focus → 100/100, Mana → base/base
   (derived from `class_info.PowerType`).
5. **Schema via `EnsureCreated`** (consistent with M1) rather than migrations.

## Client build verified against

Pending — to be filled in by the developer after running `docs/M3_ACCEPTANCE.md`
against a real WoW **1.12.1 (build 5875)** client.
