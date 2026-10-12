# Client error codes (build 5875)

How the 1.12.1 client shows a failure, and which code the server sends when the server itself failed.
The evidence is the clean-room note `D:/WoW-RE-Research/protocol/client-error-codes.md`. That note records
facts only, read from the owned `Wow.exe` (tag **BIN**) and from the client's own `GlobalStrings.lua` /
`GlueStrings.lua` (tag **DATA**). Where emulator sources disagree with the binary, the binary wins.

## The internal-error codes

| Failure | Packet (opcode) | Code | Client string key | Player sees | Evidence |
|---|---|---|---|---|---|
| Spell, gather or open cast | `SMSG_CAST_RESULT` (0x130; also called SMSG_CAST_FAILED) | status 2, result `0x1C` | `SPELL_FAILED_ERROR` | red "Internal error" | BIN + DATA |
| Item move, equip, split or use | `SMSG_INVENTORY_CHANGE_FAILURE` (0x112) | `40` (0x28) | `ERR_INTERNAL_BAG_ERROR` | red "Internal Bag Error" | BIN + DATA |
| Mail | `SMSG_SEND_MAIL_RESULT` (0x239) | result `6` (also 7-13 and >15) | `ERR_MAIL_DATABASE_ERROR` | red "Internal mail database error." | BIN + DATA |
| Trade | `SMSG_TRADE_STATUS` (0x120) | status 12 with InventoryResult 40 | `ERR_INTERNAL_BAG_ERROR` | red "Internal Bag Error" | BIN + DATA |
| Character create | `SMSG_CHAR_CREATE` (0x03A) | `0x2F` | `CHAR_CREATE_ERROR` | "Error creating character" | BIN + DATA |
| Character delete | `SMSG_CHAR_DELETE` (0x03C) | `0x3A` | `CHAR_DELETE_FAILED` | "Character deletion failed" | BIN + DATA |
| Enter world | `SMSG_CHARACTER_LOGIN_FAILED` (0x041) | `0x41` | `CHAR_LOGIN_FAILED` | "Login failed" | BIN + DATA |
| Anything else | `SMSG_NOTIFICATION` (0x1CB) | free text | (none) | red UIErrorsFrame text and a chat line | BIN |

Loot, quests, buy, sell, transfer aborts and area-trigger messages have **no** internal-error code. For a
server fault in those areas, use `SMSG_NOTIFICATION`.

## How the client displays them (BIN)

- Every `ERR_*` error goes through one client table of 465 entries. Each entry has a display type: red
  `UI_ERROR_MESSAGE` text in the UIErrorsFrame, yellow `UI_INFO_MESSAGE` text, or a chat line.
  A server cannot send an index into that table directly. It reaches the table only through the
  per-packet translations listed in the note.
- `SMSG_CAST_RESULT` shows the `SPELL_FAILED_*` text through `ERR_SPELL_FAILED_S` (red). Result `0x17`
  (`DONT_REPORT`) shows nothing. Results above `0x90` show "Unknown reason".
- `SMSG_INVENTORY_CHANGE_FAILURE` result 0 shows nothing. Unknown results show "That bag is full.".
- Character-screen codes `0x00`-`0x52` have a GlueStrings key. Any other code is shown as "(N)".
- `SMSG_CHAT_SERVER_MESSAGE` id 3 is a free-text system chat line (`ServerMessages.dbc` row 3 is `%s`).
  It never reaches the UIErrorsFrame.

## Enum corrections made against the binary

| Enum | Change | Why |
|---|---|---|
| `CharResult` (`ArcaneCore.Protocol`) | `CharNameSuccess` 0x50 -> **0x52**; added `CharNameConsecutiveSpaces` 0x50, `CharCreateServerQueue` 0x36, `CharCreateOnlyExisting` 0x37, `CharDeleteFailedLockedForTransfer` 0x3B, `CharLoginLockedForTransfer` 0x44 | The client's response table puts `CHAR_NAME_CONSECUTIVE_SPACES` at 0x50 and `CHAR_NAME_SUCCESS` at 0x52 (gtker had 0x50; vmangos agrees with the binary). |
| `SpellCastResult` | added `AutotrackInterrupted` 0x08 | The client switch maps 0x08 to `SPELL_FAILED_AUTOTRACK_INTERRUPTED`. |
| `InventoryResult`, `LootError`, `MailResult` | doc comments only (values unchanged) | 59 shows `ERR_CANT_BE_DISENCHANTED` (no string in 1.12.1), so it is not "none". Loot 15 and 16 show the default "no permission" text. Mail 16-19 show the internal mail error. |

Golden tests: `tests/ArcaneCore.World.Tests/ClientErrors/ClientErrorCodeGoldenTests.cs`.
