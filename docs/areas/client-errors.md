# Client error codes (build 5875)

How the 1.12.1 client shows a failure, and which code the server sends when the server itself failed.
The evidence is the clean-room note `<re-research-root>/protocol/client-error-codes.md`. That note records
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
| `InventoryResult`, `MailResult` | doc comments only (values unchanged) | 59 shows `ERR_CANT_BE_DISENCHANTED` (no string in 1.12.1), so it is not "none". Mail 6-13 and 16-19 show the internal mail error. |
| `LootError` | none here (the loot lane owns the file) | Loot 15 and 16 show the default "no permission" text; there is no loot internal-error code. |

Golden tests: `tests/ArcaneCore.World.Tests/ClientErrors/ClientErrorCodeGoldenTests.cs`.

## Telling the client the server failed: `ClientError`

`ArcaneCore.Game.ClientErrors.ClientError` is the one place that turns a server-side failure into the client's own
words for the action that failed. Every call logs the cause at Error (throttled by the fault ledger), records a
fault-ledger entry under the site `client-error/<kind>`, and sends a fixed packet. Neither the cause nor any
exception text ever reaches the packet: the payload depends on the context alone.

```csharp
ClientError.SpellCast(session, spellId, "gather: lock lookup failed", logger, ex); // SMSG_CAST_RESULT failed, 0x1C
ClientError.ItemMove(session, item1, item2, "swap: slot map out of sync", logger);  // SMSG_INVENTORY_CHANGE_FAILURE 40
ClientError.Generic(session, "auction: ...", logger);                                // SMSG_NOTIFICATION "Internal error"
ClientError.Internal(session, ClientErrorContext.Mail(mailId, MailAction.Send), "...", logger, ex); // SMSG_SEND_MAIL_RESULT 6
```

| Context | Packet | Payload |
|---|---|---|
| `SpellCast(spellId)` | SMSG_CAST_RESULT (0x130) | u32 spell, u8 2, u8 0x1C |
| `ItemMove(item1, item2)` | SMSG_INVENTORY_CHANGE_FAILURE (0x112) | u8 40, u64 item1, u64 item2, u8 0 |
| `CharacterCreate` / `CharacterDelete` / `CharacterLogin` | SMSG_CHAR_CREATE / SMSG_CHAR_DELETE / SMSG_CHARACTER_LOGIN_FAILED | u8 0x2F / 0x3A / 0x41 |
| `Mail(mailId, action)` | SMSG_SEND_MAIL_RESULT (0x239) | u32 mail, u32 action, u32 6 |
| `Generic` | SMSG_NOTIFICATION (0x1CB) | "Internal error" |

Without an exception, the ledger entry is a `ServerRejectionException` carrying the cause. It is never thrown, so it
has no stack; the site name (`client-error/<kind>`) and the cause text identify the caller.

**Rate limit.** Unsolicited errors (`Generic`, `SpellCast`, `ItemMove`) are limited per session to
`ClientError.Burst` (3) in a sliding `ClientError.WindowMs` (10 s); past that the fault is still logged and recorded but
nothing is sent. Answers the client is waiting for (character screen, mail) are never limited.

**Handler faults.** An opcode handler that throws something other than the malformed-packet outcome is a server bug
(`WorldSession.HandlerFaults.cs`):

- World handlers (world thread): containment is an **allowlist** (`WorldSession.IsContainableWorldOpcode`). Every
  fault sends the context error for the opcode (CMSG_CAST_SPELL -> spell, the item move/equip/use opcodes -> item, the
  mail opcodes -> mail result, anything else -> notification), logs and records the fault. Then:
  - an opcode on the allowlist (the world-thread queries: guild, pet name, petition, quest-giver status, who,
    inspect, played time; chat; channel operations; friend list read; sheathe) is contained: the session's next packet is handled. The
    `WorldSession.HandlerFaultStorm`-th (5th) contained fault inside `WorldSession.HandlerFaultStormWindowMs` (60 s)
    disconnects;
  - every other opcode, including all economy, inventory, loot, mail, trade, spell and quest-reward handlers, sends
    the error and then disconnects, as every handler fault did before. New handlers default to this. Movement is
    deliberately not on the allowlist either, nor are emotes (they cancel auras and run creature emote scripts),
    stand state (removes food/drink auras) and selection (retargets auto-repeat spells). The name, item, creature,
    gameobject, quest, page-text and time queries run on the session task, not the world thread: in game a fault
    there disconnects (only character-screen faults are contained on the session task).
  A session already being dropped sends nothing and only records the fault. `InvariantViolationException` (the Debug
  stop signal) is never absorbed.
- Session handlers (session task): CMSG_CHAR_CREATE, CMSG_CHAR_DELETE and CMSG_PLAYER_LOGIN faults at the character
  screen are answered with their failure code and the session stays; they count against the same storm limit. Any other session-handler fault, or one after the
  login has begun, still leaves the session.
- A malformed packet still disconnects (docs/ops/netguard.md).

Why an allowlist: a handler can change state before it throws (loot money credits the player before it clears the bag; a
split-stack lowers the source count before it stores the new stack). Absorbing that fault would let the client repeat
the same packet and duplicate each time. So only handlers that read state, or repeat an idempotent or cosmetic change,
are contained; a state-changing handler still disconnects. The guarantee is that, not a time bound: the storm rule only
caps how many repeated contained (read-only) faults a session may generate before it is dropped.

Tests: `ClientErrorTests` (packets, rate limit, no leak, ledger), `HandlerFaultDispatchTests` (a throwing allowlisted query is contained; cast,
item move, loot money and split item reply then disconnect; char-create is answered; the storm), `WorldProtectionTests` (the generic case).
