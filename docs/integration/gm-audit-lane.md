# GM audit lane: pinfo, mute, tickets, staff announcements, `.arcane`

Lane branch `ccr-build/gm-audit`. Source of the scope: `docs/integration/gm-command-matrix-raw.md` (lanes "GM audit, tickets and bans" and
"Server ops", commands that three or more cores implement and ArcaneCore lacked). The reference cores are EVIDENCE only: names, levels and
syntax below are ArcaneCore's decision. `BanCommands.cs` and `Honor/` are untouched (another lane).

## What was added

| Command | Level | Does |
|---|---|---|
| `.pinfo [name]` | GameMaster | Account id, security (online only), level, played time, money, position, GM mode, chat-mute state and open ticket of an online or offline character, the selection, or yourself |
| `.mute [name] <duration> [reason]` | Moderator | Disables the chat of the target's ACCOUNT. Target must be online, of strictly lower security |
| `.unmute [name]` | Moderator | Lifts it. The character may be offline |
| `.gmannounce <text>` | Moderator | Chat line to every staff member online |
| `.gmnotify <text>` | Moderator | Screen notification to every staff member online |
| `.gm ingame` | Moderator | Online players with GM mode on, and whether they accept whispers |
| `.gm list` | Administrator | Every staff member online with security level (GM mode on or off) |
| `.ticket` | GameMaster | Count of open tickets |
| `.ticket list` / `onlinelist` / `show <id>` / `respond <id> <text>` / `close <id> [text]` | GameMaster | See below |
| `.ticket delete <id>` | Administrator | Removes an open ticket and its row |
| `.arcane bancheck account\|character\|ip <value>` | GameMaster | Read-only: is it blocked from logging in right now (ban row or status override), by whom, until when, and the account's chat-mute state |
| `.arcane mutes` | GameMaster | Read-only: chat mutes in force |
| `.arcane gmlog [count]` | Administrator | Read-only: tail of the GM command audit log (default 20, at most 100) |
| `.arcane queues` | Administrator | Read-only: pending writes of the write-behind queues, and writes retained after failed attempts |

Player side: `CMSG_GMTICKET_GETTICKET`, `_CREATE`, `_UPDATETEXT` and `_DELETETICKET` are handled by `GmTicketHandlers` (the old
"always no ticket" stub of `PlayerHandlers` moved there; with no ticket the answer is unchanged). Staff with GameMaster or higher are told in
chat when a ticket is created or its text actually changes (the same text sent again is answered "updated" but neither written nor announced).

The three mutations are rate limited per account, ArcaneCore's own (no reference core limits them): `World:GmCommands:TicketMutationsPerMinute`
(default 10) is the most create, update-text and delete packets one account may send in a fixed minute from its first; the rest are refused
before the payload is read and the player gets a system line. A refused create or update answers with its error code (3 / 5); a refused
delete answers with the ticket's real state (the status answer that normally follows a delete) and never the "deleted" code, since nothing
was deleted (UNVERIFIED on a retail client, as the layouts below). Fail-closed: 0 refuses every ticket mutation, a negative value falls
back to the default, and the limit holds the staff notices to the same bound.

### Why these names and levels

* **Levels follow the stored four-level scale.** `Moderator` 1, `GameMaster` 2, `Administrator` 3 are the same numbers mangos-zero and
  acore use (their `mute` 1/2, `pinfo` 2, `ticket` 2, `ticket delete` 3), so the matrix levels carry over; the retail (vmangos) level a
  command maps to comes from `World:GmCommands:SecurityMap` as for every other command.
* **`.gm ingame` is Moderator, not open to players** (mangos-zero and acore: 0, TrinityCore: 1). A player-level child would put `gm ...`
  into every player's `.commands` list; showing GMs to players is a policy choice an operator should make deliberately.
* **Mute is per account** (vmangos `account.mutetime`), in `account_mute` with who set it, their stored security and the reason. The
  existing chat gates (`ChatFeature`, vmangos HandleChatMessageOpcode order) already took `IChatMuteSource`s; `GmAuditFeature` is one, so
  nothing in the chat handlers changed. Strong security check as vmangos `HasLowerSecurity(strong)`: equal security is refused too.
  A mute set by higher staff cannot be shortened or lifted by lower staff (`MutedBySecurity`); that is what lets `.unmute` work on an
  offline account without loading its security. The check is against the mute's AUTHOR whoever the target is, the invoker included:
  `CanActOn` lets staff target themselves and a muted staff member can still reach the command table (a whisper is not gated by the
  mute), so a self-target gets no exemption. `.unmute` also clears `ChatFeature`'s own flood mute (vmangos `.unmute` sets `m_muteTime`
  to 0, the field the flood mute shares) and says "already enabled" only when neither mute was in force; `.pinfo` shows a flood mute as
  `muted for ... (anti-flood)`.
* **Duration** is minutes when a bare number (mangos-zero, vmangos), else `1d2h30m` groups as `.ban`. Unlike vmangos' 32-bit
  `TimeStringToSecs` the arithmetic is checked, so `4294967297s` is refused instead of wrapping into one second; zero is refused and
  there is no permanent mute (use a ban); maximum 365 days. A word with a digit is a duration, otherwise a name, so `.mute 30m` mutes the selection.
* **`.arcane` is a non-retail root.** No reference core has it, `RetailCommandOrder` does not list it, so a future retail table can never
  collide with it. Everything under it only reads.
* **Tickets are per character, one open at a time** (vmangos `character_ticket`). `respond` answers and leaves the ticket open; `close`
  ends it (optionally with a last answer) and keeps the row as history; `delete` removes the row. Only open tickets are loaded and listed.

### Reference versus ours

Levels are each core's own scale (mangos-zero 0-4 PLAYER..CONSOLE, acore/TrinityCore 0-3, ArcEmu permission letters), from the matrix.

| Command | mangos-zero | acore | TrinityCore | ArcEmu / WCell | ArcaneCore |
|---|---|---|---|---|---|
| `pinfo` | 2 | 2 | 2 | - | GameMaster. No e-mail, last IP or latency (not shown in chat); offline form shows no security (account not loaded) |
| `mute` | 1, minutes only, online or offline | 2 | 1 | - | Moderator, minutes or `1d2h`, reason, online target only, reason and duration notice to the target |
| `unmute` | 1 | 2 | 1 | - | Moderator, online or offline, cannot lift a mute of higher staff (not even one's own), clears the flood mute too |
| `gm list` | 3, reads the account table | 3 | 3 | ArcEmu 0 | Administrator, online staff only (see deferred) |
| `gm ingame` | 0 | 0 | 1 | - | Moderator |
| `gm visible` | 1 | 2 | 1 | - | not provided: there is no GM-invisibility state |
| `gmannounce` | - | 2 | 1 | ArcEmu `u` | Moderator |
| `gmnotify` | - | 2 | 1 | - | Moderator |
| `ticket` | 2 | 2 | - | WCell Staff | GameMaster, open-ticket count |
| `ticket list` | 2 | 2 | - | WCell Staff | GameMaster |
| `ticket onlinelist` | 2 | 2 | - | - | GameMaster |
| `ticket show` | 2 | - (`viewid`) | - | WCell Staff | GameMaster |
| `ticket close` | 2 | 2 | - | - | GameMaster, keeps history |
| `ticket delete` | 3 | 3 | - | WCell Staff | Administrator |
| `ticket respond` | 2, mails the answer | `response` family | - | - | GameMaster, in-game line to an online owner only |

Reply texts that have a mangos-zero original cite its `Language.h` id on the member in `GmAuditStrings`; the rest are ArcaneCore's wording
and say so.

## Persistence

* Characters schema **version 26** (`GmAuditDataModule.Version`, next free after the game-event status step at 25; the integrator
  renumbers that one constant), two new tables: `account_mute` (account id key) and `gm_ticket` (id key, index on character and status).
  `ICharacterDataCleanup` removes a deleted character's tickets (open and closed); a mute belongs to the account and stays.
* `GmAuditFeature` loads active mutes and open tickets at startup (fails startup when storage cannot be read: a world that forgot every
  mute is worse than one that does not start) and keeps them in memory. Writes go through `GmAuditWriteQueue`, the retained-write
  pattern of `ExploredZonesWriteQueue` keyed by string (`mute:<account>`, `ticket:<id>`): one consumer, newest write per key replaces a waiting one,
  three attempts, a write that fails them all is RETAINED (never dropped) and retried by the next write of the key, a periodic timer (30 s),
  `RetryRetainedAsync`, and `StopAsync` (a final retry that throws naming the keys still not durable). `FlushAsync` is a pure barrier.
  Staff and the chat gate see the in-memory state at once, so a failing database never un-mutes anyone or hides a ticket.
* `OnCharacterDeletedAsync` drops the character's open ticket from memory and queues its row removal; the store ignores a ticket write for
  a character that no longer exists, so a late write cannot resurrect it.
* The audit tail is in memory (lost on restart): `World:GmCommands:AuditTailSize` (default 200, 0 = off) newest lines of what
  `GmCommandLog` writes (same gate: `World:GmCommands:LogCommands`, commands above level 0). `CommandTable.Execute` feeds it.

## Unverified and deferred (nothing below is guessed into code)

* **UNVERIFIED on a retail 1.12.1 client:** the layouts of `CMSG_GMTICKET_CREATE` (`u8 category, u32 map, 3 x f32, cstring text, cstring
  reserved`), the status-6 `SMSG_GMTICKET_GETTICKET` tail, and the response codes (1 exists, 2 created, 3 error, 4 updated, 5 update error,
  9 deleted) are mangos-zero's (`GMTicketHandler.cpp`, `GMTicketMgr.h:39-46`); no 1.12.1 capture or client disassembly was available. The
  category byte in create is the most doubtful (acore's 3.3.5 layout has it too). Mitigations: a create shorter than the fixed part is
  answered with the create-error code and stores nothing; the position fields are skipped and the server's own position stored. Verify
  with a client capture before relying on tickets. `SMSG_GM_TICKET_STATUS_UPDATE` (0x328) is a later-client opcode and is not sent; closing or
  deleting a ticket clears the owner's window with the status-0x0A answer the existing code already sent.
* **Deferred:** `.gm visible` (needs a GM-invisibility concept in the visibility code); the cores' full-account `.gm list` (needs an
  account-listing method on `IAccountAdmin`, whose lane is also editing the ban seam); `mute` of an offline character (needs the account
  security without loading the account); mailing a ticket answer to an offline owner; `ticket assign/comment/escalate/togglesystem`,
  `SMSG_GMTICKET_SYSTEMSTATUS` and the survey opcodes (one core each); `chatfilter`, `rbac`, `kick account|ip`, `ban playeraccount`
  (not in scope of this lane or in the ban lane's files).
* `.arcane queues` reads the pending counts that the existing queues expose (character saves, social, reputation, instances, creature
  respawns, explored zones) and the retained keys of the GM audit queue only; the others expose retained state per character, not as a
  list. The honor queue is left out because its files belong to another lane.

## Tests

`tests/ArcaneCore.World.Tests/Gm/Audit` (command, packet, queue, startup-load, security-gating, failure-path tests against the real world
host, with an in-memory `IGmAuditStore` that can be told to fail) and `tests/ArcaneCore.Data.Tests/Gm/GmAuditStoreTests.cs` (SQLite,
MariaDB and PostgreSQL). The generated references (`docs/reference/*`: command, configuration and schema pages) are not regenerated here; their
three comparison tests fail until the orchestrator does that after the merge.
