# MILESTONE M6 — Session essentials, chat, GM commands

Status: **implemented** (automated loopback + multi-engine tests, CI); **client acceptance
pending** (`docs/M6_ACCEPTANCE.md`).

M6 turns "a character can stand in the world and move" into "a player can play a session":
log out properly, talk, see who is online, keep their UI settings, action bars and
tutorial progress, and be administered by staff.

## What was built

| Area | Behaviour |
|---|---|
| Login sequence | The full vmangos order: verify world, account data hashes, friend/ignore lists, MOTD lines, rest start, bind point, tutorials, spells, action buttons, reputations, time speed, self create, world states |
| Logout | `CMSG_LOGOUT_REQUEST` → 20 s countdown (sit, root, stun, LOGGING_OUT flag) or instant (resting, taxi, staff ≥ `InstantLogoutSecurity`), refused in combat or mid-air; cancel undoes it; completion saves, removes the player and returns the client to the character screen (`SMSG_LOGOUT_COMPLETE`) — the session stays connected |
| Movement orders | `SMSG_FORCE_MOVE_ROOT/UNROOT` with a movement counter; the client's acks are applied and relayed as `MSG_MOVE_ROOT/UNROOT`; `CMSG_MOVE_TIME_SKIPPED` relayed as `MSG_MOVE_TIME_SKIPPED` |
| Chat | `/say` (25 yd), `/yell` (300 yd), `/e` (25 yd, own faction unless two-side chat), whispers with inform + AFK/DND auto-replies, AFK/DND toggles, language checks (race languages; GMs speak Universal), cross-faction whisper refusal (`SMSG_CHAT_WRONG_FACTION`), unknown target (`SMSG_CHAT_PLAYER_NOT_FOUND`), GM chat badge |
| Emotes | Text emotes with the target's name to everyone in range; the two client-initiated animations (none, wave) |
| /who | vmangos filtering: faction (unless `AllowTwoSideWhoList`), staff above `GmLevelInWhoList` hidden from players, level range, race/class masks, zones, name/guild/search strings; 49-entry cap |
| Queries | Name query from an in-memory character directory (online and offline characters, like vmangos' player cache), query time, played time |
| Account settings | `CMSG_UPDATE/REQUEST_ACCOUNT_DATA` (8 zlib blobs, stored as exact bytes), MD5 hashes at login, tutorial flags (set/clear/reset), all per account and persisted |
| Character state | Money, action bar (120 slots, validated types), visible action bars, played time per level, hearthstone bind point — persisted; legacy characters get their start position as bind point |
| Small requests | Stand state, selection (also `UNIT_FIELD_TARGET`), zone updates (+ world states), active mover, GM ticket / next mail / raid info polls answered with "none" |
| Character creation | vmangos name rules: normalized capitalization, 2–12 letters from one script (extended Latin, Cyrillic or East Asian), `CHAR_NAME_*` codes; 10 characters per realm |
| GM commands | `.help`, `.commands`, `.save`, `.server info/motd` (everyone); `.gps`, `.announce`, `.notify`, `.gm [on/off]`, `.gm chat`, `.saveall`, `.modify money` (moderator); `.kick` (game master). Prefix and abbreviation rules as vmangos; commands above the caller's level do not exist for them |
| Accounts | GM level per account (auth schema v2); `arcane-account set-gmlevel <user> <0-3/name>` |
| Schemas | auth v2 (`account.Security`), characters v2 (8 columns + `character_action`, `account_data`, `account_tutorial`), upgraded in place from M5 databases |

### Bugs fixed on the way

1. **No `SMSG_ACCOUNT_DATA_MD5` at login (M2–M5).** gtker documents (as
   `SMSG_ACCOUNT_DATA_TIMES`) that without it the chat frame is an unusable white box. Now
   sent, with real hashes.
2. **Character names (M3–M5)** were taken verbatim (any 1–12 characters, digits and spaces
   included, no capitalization); the per-realm character limit was not enforced.
3. **Schema test fixture (M5).** "Pre-M5 adoption" built its legacy database from the
   *current* model, which stopped being a pre-M5 shape the moment the model grew. The data
   tests now carry exact copies of the M5 (v1) mappings and upgrade real v1 databases.

## Verified against (charter §1.1 / §4)

| Detail | Reference |
|---|---|
| Login packet order | vmangos `WorldSession::HandlePlayerLogin`, `Player::SendInitialPacketsBeforeAddToMap` / `AfterAddToMap` |
| `SMSG_ACCOUNT_DATA_MD5`: 8 × MD5, zeros when empty | vmangos `SendAccountDataTimes`, `MD5::CreateEmpty`; gtker `smsg_account_data_times.wowm` (u32[32]) |
| `CMSG_UPDATE_ACCOUNT_DATA` = u32 type, u32 size, zlib; size 0 erases; > 0xFFFF rejected; Adler-32 optional | vmangos `Misc::UpdateAccountData`, `HandleUpdateAccountData`; gtker `cmsg_update_account_data.wowm` |
| `SMSG_UPDATE_ACCOUNT_DATA` = u32 type, u32 size, zlib (empty: type + 0) | vmangos `UpdateAccountDataResponse`, cmangos-classic `HandleRequestAccountData` |
| Friend/ignore list = u8 count (+ entries) | vmangos `Social::FriendList/IgnoreList`, gtker |
| `SMSG_SET_REST_START` u32 0; `SMSG_BINDPOINTUPDATE` x, y, z, map, area | vmangos `Misc::SetRestStart`, `BindpointUpdate`; gtker |
| `SMSG_TUTORIAL_FLAGS` 8 × u32; flag = word / bit; clear = all ones, reset = zeros | vmangos `SendTutorialsData`, `HandleTutorial*` |
| `SMSG_ACTION_BUTTONS` 120 × u32 (action \| type << 24); settable types spell/macro/cmacro/item | vmangos `SendInitialActionButtons`, `HandleSetActionButtonOpcode`, `ActionButtonType`; gtker u32[120] |
| `SMSG_INITIALIZE_FACTIONS` u32 64 + 64 × (u8, i32) | vmangos `InitializeFactions`, `ReputationMgr::SendInitialReputations` |
| `SMSG_INIT_WORLD_STATES` map, zone, u16 count, (u32, i32) pairs | vmangos `Misc::InitWorldStates` (> 1.11.2), gtker; empty list as cmangos-classic outside BG/outdoor-PvP zones |
| Logout: refusals (combat 1, jumping/falling 3), instant conditions, sit/root/stun/flag, cancel | vmangos `HandleLogoutRequestOpcode` / `HandleLogoutCancelOpcode`; `SMSG_LOGOUT_RESPONSE` u32 + u8 |
| `PLAYER_FIELD_BYTE_LOGGING_OUT` 0x04 in `PLAYER_FIELD_BYTES` byte 0 | vmangos `Player.h` |
| `SMSG_STANDSTATE_UPDATE` u8 on every stand-state change | vmangos `Unit::SetStandState` |
| Root order = packed GUID + u32 counter; ack = u64 GUID, u32 counter, MovementInfo; observers get `MSG_MOVE_ROOT` + packed GUID + MovementInfo | vmangos `MovementPacketSender`, `HandleMoveRootAck`; cmangos-classic `Unit::SetRoot`; mangoszero `Player::SetRoot` |
| `CMSG_MOVE_TIME_SKIPPED` u64 + u32 → `MSG_MOVE_TIME_SKIPPED` packed GUID + u32 to observers | vmangos + cmangos-classic + mangoszero (read), vmangos + cmangos-classic (relay) |
| Chat packet layouts (`SMSG_MESSAGECHAT` per type, sized strings, tag) | vmangos `ChatHandler::BuildChatPacket`; gtker `smsg_messagechat.wowm` (1.7–1.12) |
| `CMSG_MESSAGECHAT` = u32 type, u32 language, target for whisper/channel, message | vmangos `Chat::ChatMessage`, gtker |
| Chat types, languages, chat tags | gtker `social_common.wowm` + vmangos `SharedDefines.h` (agree) |
| Language rules, GM → Universal, two-side chat → Universal, addon only on group chat, Universal only for AFK/DND | vmangos `HandleChatMessageOpcode`, `IsLanguageAllowedForChatType` |
| Race starting languages | classic-db `playercreateinfo_spell` (language spells), vmangos `lang_description` |
| Say range = min(say, yell); 3D distance + bounding radii; emotes own team only | vmangos `Player::Say/Yell/TextEmote`, `MessageDistDeliverer`, `WorldObject::IsWithinDist` |
| Whisper: inform with receiver's tag, DND before AFK reply, always Universal, faction check between players | vmangos `MasterPlayer::Whisper`, whisper case in `HandleChatMessageOpcode` |
| Chat tag precedence GM badge → DND → AFK | vmangos `Player::GetChatTag`, `IsGMChat` (≥ moderator) |
| AFK/DND toggle rules | vmangos `HandleChatMessageOpcode` (AFK/DND cases), `Player::ToggleAFK/DND` |
| Text emote = u64, u32, u32, sized name or single NUL; emote = u32 + u64; only emotes 0/3 from `CMSG_EMOTE` | vmangos `EmoteChatBuilder`, `HandleEmoteOpcode`, `Unit::HandleEmoteCommand`; gtker |
| /who request/response and filtering, 49 cap, online count | vmangos `Misc::Who`, `WhoListClientQueryTask`; gtker `cmsg_who`/`smsg_who` |
| Name query response (u64, name, realm name, u32 race/gender/class) | vmangos `NameQueryResponse` (1.12.1), gtker `smsg_name_query_response.wowm` |
| Query time u32; played time u32 + u32 | vmangos `QueryTimeResponse`, `Misc::PlayedTime` |
| GM ticket "none" = u32 0x0A; next mail −86400 f32; raid info u32 0 | vmangos `TicketMgr::SendTicket`, `HandleQueryNextMailTime`, `Player::SendRaidInfo` |
| Stand states the client may pick (stand/sit/sleep/kneel) | vmangos `HandleStandStateChangeOpcode` |
| Selection also sets `UNIT_FIELD_TARGET` | vmangos `Player::SetSelectionGuid` |
| Name rules and codes | vmangos `normalizePlayerName`, `ObjectMgr::CheckPlayerName`, `Util.h` character classes, `HandleCharCreateOpcode` order; gtker/vmangos `CHAR_NAME_*` 0x45–0x4F agree |
| Characters per realm 10 | vmangos `CharactersPerRealm` |
| GM mode: `PLAYER_FLAGS_GM`, faction template 35, race faction on off | vmangos `Player::SetGameMaster` |
| Command prefix rules, abbreviations, unavailable = unknown | vmangos `ChatHandler::ParseCommands`, `FindCommand`/`hasStringAbbr` |
| Command security levels | cmangos-classic `Chat.cpp` command table |
| Money cap `0x7FFFFFFF − 1` | vmangos/cmangos `MAX_MONEY_AMOUNT` |
| Option defaults (ranges 25/300/25, two-side off, InstantLogout moderator, GM.InWhoList administrator, PlayerCommands on, Motd split on '@') | vmangos and cmangos-classic `World.cpp` |

### Reference discrepancies found

1. **`SMSG_FORCE_MOVE_ROOT` GUID.** gtker lists a full u64 GUID for 1.12 (packed only for
   2.4.3+); vmangos, cmangos-classic and mangoszero all send the packed GUID for 1.12.
   The three servers win.
2. **Opcode 0x209.** vmangos `SMSG_ACCOUNT_DATA_MD5` (8 digests), gtker
   `SMSG_ACCOUNT_DATA_TIMES` (u32[32]) — the same 128 bytes; vmangos' meaning is used.
3. **Security scale.** vmangos 0–7 (adds ticket master, basic admin, developer);
   cmangos-classic and TrinityCore 0–3. ArcaneCore uses 0–3; config defaults are carried
   over by meaning (InstantLogout = moderator, GM.InWhoList.Level = administrator).
4. **Say/emote range.** 25 yd in vmangos' and cmangos' code defaults and cmangos' shipped
   config; vmangos' shipped config raises both to 40. 25 is used.
5. **`SMSG_UPDATE_ACCOUNT_DATA`.** gtker only describes the TBC+ layout (GUID, time …);
   vmangos and cmangos-classic agree on the 1.12 one (type, size, zlib), which is used.
6. **`CHAR_NAME` 0x48** is `ONLY_LETTERS` in gtker and `INVALID_CHARACTER` in vmangos —
   same value, same meaning.

### Deliberate differences from vmangos

1. **Staff cannot act on higher accounts** (`.kick`, `.modify money`): cmangos/vmangos
   allow it unless `GM.LowerSecurity` is set; ArcaneCore always checks.
2. **`CMSG_LOGOUT_CANCEL`** only undoes a logout that is in progress (vmangos also stands
   a merely sitting player up).
3. **Logout delay** is an option (`LogoutDelayMs`, default 20 000); vmangos hard-codes 20 s.
4. **Account data** is accepted in every authenticated state (vmangos: logged in or
   recently logged out); it belongs to the account either way.
5. **Players may always whisper staff** — GM whisper acceptance (`.whispers`) is not
   implemented yet.
6. **A faulty command** replies with an error and is logged; it never drops the GM's
   connection.

## Automated tests

| Project | Tests | What |
|---|---|---|
| ArcaneCore.Game.Tests | 31 (+17) | chat tag order, GM mode, race languages, logout orders and timer (incl. clock wrap), action-bar validation and snapshots, stand state, ranged broadcast (3D, radii, team, map-wide) |
| ArcaneCore.Data.Tests | 30 (+12) | M5 → v2 upgrades of auth and characters, pre-M5 adoption + upgrade, action buttons / bind point / money round trips, exact-byte account data, tutorials, GM level — × SQLite, MariaDB 10.11, PostgreSQL 16 |
| ArcaneCore.World.Tests | 84 (+65) | login sequence contents, name rules, account data across sessions (with/without Adler-32, wrong sizes), tutorials, action-bar persistence, name/time/played queries and polls, say/yell ranges, faction rules, whispers with AFK/DND, languages, GM badge, text emotes, animations, /who filters, logout countdown/cancel/instant/refusals, root acks, time skips, zone updates, every command and its security level, prefix/abbreviation rules, zlib edge cases, character directory |

The world suite was run repeatedly and under full CPU contention without a failure.

## Decisions & limitations

1. **No channels, party, raid, guild or battleground chat** — they need channels, groups
   and guilds (M14); such messages are dropped, as vmangos drops them for a player outside
   any group or guild. Addon messages likewise.
2. **No mute, chat flood control, reserved names or whisper restrictions** yet.
3. **Content-dependent pieces wait for M8:** text-emote animations (EmotesText.dbc), the
   reputation list (Faction.dbc; sent empty), first-login cinematics (ChrRaces.dbc), area
   names in /who search strings (AreaTable.dbc), and the zone, which still comes from the
   client until terrain data exists.
4. **GM mode and the GM chat badge are not persisted** across logins; GM invisibility does
   not exist yet.
5. **A GM level change applies from the account's next world login**; it is changed with
   `arcane-account set-gmlevel`, not in game.
6. **Action-bar actions are not checked against spells/items** until those exist (M9/M12).

## Client build verified against

Pending — `docs/M6_ACCEPTANCE.md` with WoW **1.12.1 (5875)** clients.
