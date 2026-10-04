# Character rename

Lane `L10-rested-xp-and-char-lifecycle` (the rename half; the rest half is [rested-xp](rested-xp.md)). Behaviour is taken from the mangos
reference core (`WorldSession::HandleCharRenameOpcode` and its callback, `CharacterHandlerCustomize.cpp:86-190`;
`HandleCharacterRenameCommand`, `PlayerCommands.cpp:166-200`; `Player::BuildEnumData`); citations are in the code comments.

## What it does

A character carries an **at-login flag** `AT_LOGIN_RENAME` (bit 0x01 of vmangos' `characters.at_login`) when it must choose a new name.
From the character screen the player sends `CMSG_CHAR_RENAME` (u64 guid, CString name); the server answers `SMSG_CHAR_RENAME`.

| Step | Result |
|---|---|
| The name is empty, not valid UTF-8, or longer than 15 code points | `CHAR_NAME_NO_NAME` (0x45) |
| The name breaks the realm's name rules (length 2 to 12, one script, the creation options `MinPlayerName`, `StrictPlayerNames`, `RealmZone`) | the `CHAR_NAME_*` code of the rule (too short 0x46, too long 0x47, mixed languages 0x49, ...) |
| The character is not the account's, is not flagged, does not exist, or another character already has the name (any case) | `CHAR_CREATE_ERROR` (0x2F), nothing changes, the flag stays (the reference's single validation query makes no difference between these) |
| The database fails | `CHAR_CREATE_ERROR`, logged; the session continues |
| Otherwise | one commit changes the name and clears the flag; `SMSG_CHAR_RENAME` = `0, guid, name`; the character directory takes the new name; `SMSG_INVALIDATE_PLAYER` (the guid) goes to every online client so they drop the cached name (`World::InvalidatePlayerDataToAllClient`) |

The name is normalized first (first letter upper case, the rest lower case) like creation. The rename itself is atomic and race-safe: the
unique index on `characters.name` decides when two renames, or a rename and a creation, take the same name together (the loser is
"taken", not a fault).

### `.character rename [$name]`

Under the existing `.character` root (the extension mechanism; the reputation lane owns the root). Level 3 on the vmangos scale
(mangos: `SEC_GAMEMASTER`; vmangos' own level is **UNVERIFIED**). It flags the named character, online or not, or the selected one, or the
caller when nothing is selected, and answers *"Forced rename for player X will be requested at next login."* (offline: *"... X (GUID #n)
..."*; the texts are mangos strings 253 and 254). The target's account must not outrank the caller: online through the shared
`HasLowerSecurity` check, offline through the account that owns the character (character directory, then the account store). The flag is
written off the world thread; a database failure is reported to the caller. Unknown name: *"Player not found!"*.

## Schema and options

* Characters version **27**: `character_at_login(guid, at_login)` (`CharacterRenameDataModule.Version`). Its own file; the integrator
  renumbers the constant. It is a table of its own, not a column of `characters`, so the shared character row and its save are untouched;
  it is removed with the character.
* No options of its own. The name rules come from `CharacterCreation:*`.

## Known gaps

* **The character list does not show the flag yet.** The client only offers the rename when `SMSG_CHAR_ENUM` carries
  `CHARACTER_FLAG_RENAME` (0x4000) in the flags word of the character. `World/Packets/CharacterPackets.cs` (`BuildCharEnum` writes a
  fixed 0 there) and `Handlers/CharacterHandlers.cs` (`HandleCharEnumAsync`) are outside this lane. Everything else is ready:
  `CharacterRename.FlagsOfAccountAsync(session)` returns the flags of the account's characters,
  `CharacterRenamePackets.CharacterFlagRename` is the bit; the list must pass `flags[id] & 1 != 0 ? 0x4000 : 0` to the builder.
  Until then the handler works for a client that sends the packet and the flag can be set and read, but the stock client is not prompted.
* **Invalid names at login.** vmangos flags a character at login whose stored name no longer passes the name rules
  (`Player::LoadFromDB`, PlayerLoad.cpp:233-239) and refuses the login; ArcaneCore does neither.
* **Reserved names.** The `reserved_name` list is not enforced, here or at creation (see [character-creation](character-creation.md)).
* **Other name caches** (guild rosters, mail sender names, friend lists, group members) were not audited; the character directory and the
  client caches (`SMSG_INVALIDATE_PLAYER`) are handled. A renamed character is offline at the character screen, so no live object carries
  its old name.
* **Other `.character` sub-commands** (`level`, `erase`, `deleted`) are not provided.

## Unverified

* The 1.12.1 client's use of `CMSG_CHAR_RENAME` / `SMSG_CHAR_RENAME`: the packet layout and the result codes are the mangos server's
  (`RESPONSE_SUCCESS` = 0 is its first `ResponseCodes` entry, SharedDefines.h:2283); no client or capture was available. Whether the
  stock client shows the rename prompt for flag 0x4000, and what text it shows for `CHAR_CREATE_ERROR`, is untested.
* That `SMSG_INVALIDATE_PLAYER` (796) is honoured by the 1.12.1 client the way the reference expects.

## Tests

`tests/ArcaneCore.Game.Tests/Characters/CharacterRenamePacketsTests.cs`; `tests/ArcaneCore.World.Tests/Characters/CharacterRenameTests.cs`
(handler end to end, name rules, duplicates, ownership, flag clearing, the command online and offline, security);
`tests/ArcaneCore.Data.Tests/CharacterRestAndRenameStoreTests.cs` (the real store on every engine, including the concurrent race).
