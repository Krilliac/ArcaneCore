# Area: social (friends/ignore, groups/raids, guilds, chat channels)

WoW 1.12.1 (build 5875). Branch `feat/social`. Integration details (schema version, seams,
shared-file edits) are in [docs/integration/social.md](../integration/social.md).

Reference precedence: **vmangos > cmangos-classic > gtker/wow_messages**. vmangos is the
primary source (its `SUPPORTED_CLIENT_BUILD` branches were read for build 5875); cmangos-classic
was used to confirm and for the few cases vmangos leaves open; gtker `.wowm` definitions were
used for client payload layouts and as a third check. Where they disagree the servers win and
the difference is listed under [Discrepancies](#discrepancies).

File names below are upstream source files (vmangos `src/game/...`, cmangos-classic
`src/game/...`, gtker `wow_message_parser/wowm/world/...`).

## Verified against

| Feature | Code | vmangos | cmangos-classic | gtker |
|---|---|---|---|---|
| Friend / ignore lists (SMSG_FRIEND_LIST, SMSG_IGNORE_LIST) | `Game/Social/SocialPackets.cs`, `FriendsService.cs` | `Server/Packets/Social.cpp` (FriendList, IgnoreList), `Social/SocialMgr.cpp/.h` | `Social/SocialMgr.cpp/.h` | `social/smsg_friend_list.wowm`, `smsg_ignore_list.wowm`, `social_common.wowm` |
| Add/del friend and ignore, SMSG_FRIEND_STATUS results | `FriendsService.cs`, `World/Social/SocialHandlers.cs` | `SocialMgr.cpp` (HandleAddFriendOpcode path, SendFriendStatus), `Server/Packets/Social.cpp` (FriendStatus) | `SocialMgr.cpp` SendFriendStatus, `Server/WorldSession.h` | `cmsg_add_friend`, `cmsg_del_friend`, `cmsg_add_ignore`, `cmsg_del_ignore`, `smsg_friend_status.wowm` |
| Online/offline notifications, GM visibility | `FriendsService.BroadcastPresence`, `SocialContext.CanSeeOnline` | `SocialMgr.cpp` BroadcastToFriendListers / GetFriendInfo | `SocialMgr.cpp` | — |
| Social table (character_social) | `Data/Social/SocialDataModule.cs`, `EfSocialStore.cs` | `SocialMgr.cpp` LoadFromDB (guid, friend, flags) | — | — |
| Party invite / accept / decline / uninvite / leave / disband | `Game/Groups/GroupManager.cs`, `Group.cs`, `World/Social/GroupHandlers.cs` | `Handlers/GroupHandler.cpp`, `Group/Group.cpp/.h` | `Groups/GroupHandler.cpp`, `Groups/Group.cpp/.h` | `cmsg_group_invite`, `cmsg_group_accept`, `cmsg_group_decline`, `cmsg_group_uninvite(_guid)`, `cmsg_group_disband`, `smsg_group_invite`, `smsg_group_decline`, `smsg_group_uninvite`, `smsg_group_destroyed` |
| SMSG_PARTY_COMMAND_RESULT | `GroupPackets.BuildCommandResult` | `Server/Packets/Group.cpp` PartyCommandResult | `GroupHandler.cpp` SendPartyResult | `smsg_party_command_result.wowm` |
| Leader, loot method/threshold, assistants, subgroups | `GroupManager.cs` | `GroupHandler.cpp`, `Group.cpp` | `GroupHandler.cpp`, `Group.cpp` | `cmsg_group_set_leader`, `smsg_group_set_leader`, `cmsg_loot_method`, `cmsg_group_assistant_leader`, `cmsg_group_change_sub_group`, `cmsg_group_swap_sub_group` |
| SMSG_GROUP_LIST | `GroupPackets.BuildGroupList` | `Group.cpp` SendUpdate, `Server/Packets/Group.cpp` GroupList | `Group.cpp` SendUpdate | `smsg_group_list.wowm` |
| SMSG_PARTY_MEMBER_STATS (+ request) | `GroupPackets.BuildMemberStats` | `WorldSession` BuildPartyMemberStatsPacket, HandleRequestPartyMemberStatsOpcode | `GroupHandler.cpp` | `smsg_party_member_stats(_full)`, `cmsg_request_party_member_stats` |
| Raid convert, ready check, raid targets, minimap ping, random roll | `GroupManager.cs`, `GroupPackets.cs` | `GroupHandler.cpp`, `Server/Packets/Group.cpp` | `GroupHandler.cpp` | `cmsg_group_raid_convert`, `msg_minimap_ping_*`, `msg_party_assignment`, `smsg_raid_group_only` |
| Party / raid / raid warning chat | `World/Social/SocialFeature.cs` | `Handlers/ChatHandler.cpp`, `Chat/Chat.cpp` BuildChatPacket | — | `chat/smsg_messagechat.wowm` |
| Guild create (GM), invite, accept, decline | `Game/Guilds/GuildManager.cs`, `World/Social/GuildCommands.cs`, `GuildHandlers.cs` | `Guild/Guild.cpp/.h`, `Guild/GuildMgr.cpp/.h`, `Handlers/GuildHandler.cpp`, `Server/Packets/Guild.cpp`, Level2/Level3 `.guild` commands | `Guilds/Guild.cpp/.h`, `Guilds/GuildHandler.cpp`, `Guilds/GuildMgr.cpp` | — |
| Promote, demote, remove, leave, disband, leader | `GuildManager.cs` | `GuildHandler.cpp` | `GuildHandler.cpp` | — |
| MOTD, info text, public/officer notes, ranks and rights | `GuildManager.cs`, `GuildTypes.cs` | `Guild.cpp/.h` (GuildRankRights, HasRankRight, CreateDefaultGuildRanks), `GuildHandler.cpp` | `Guild.cpp/.h` | — |
| SMSG_GUILD_ROSTER | `GuildPackets.BuildRoster` | `Server/Packets/Guild.cpp` GuildRoster, `Guild.cpp` SendGuildRoster (GUILD_ROSTER_MAX_LENGTH) | `Guild.cpp` Roster | `guild/smsg_guild_roster.wowm` |
| SMSG_GUILD_EVENT | `GuildPackets.BuildEvent` | `Server/Packets/Guild.cpp` GuildEvent | `Guild.cpp` BroadcastEvent | `guild/smsg_guild_event.wowm` |
| SMSG_GUILD_INFO, SMSG_GUILD_QUERY_RESPONSE, SMSG_GUILD_COMMAND_RESULT | `GuildPackets.cs` | `Server/Packets/Guild.cpp` | `GuildHandler.cpp` | `guild/smsg_guild_info.wowm`, `queries/smsg_guild_query_response.wowm`, `guild/smsg_guild_command_result.wowm` |
| Guild / officer chat | `SocialFeature.cs` | `ChatHandler.cpp`, `Guild.cpp` BroadcastToGuild/BroadcastToOfficers | — | `smsg_messagechat.wowm` |
| Guild tables (guild, guild_rank, guild_member) | `SocialDataModule.cs`, `EfSocialStore.cs` | `Guild.cpp` LoadGuildFromDB / LoadRanksFromDB / LoadMembersFromDB | — | — |
| Guild charters: show list, buy, show signatures, query, rename, sign, decline, offer, turn in | `Game/Guilds/PetitionManager.cs`, `PetitionPackets.cs`, `PetitionTypes.cs`, `GuildManager.Petitions.cs`, `World/Social/PetitionHandlers.cs`, `SocialPetitionFeature.cs` | `Handlers/PetitionsHandler.cpp:41-507`, `Server/Packets/Petition.cpp:3-186`, `Guild/GuildMgr.cpp:168-546` (Petition), `Guild/Guild.cpp:104-119,197-278` | `Entities/PetitionsHandler.cpp` (same constants, `MinPetitionSigns`) | `guild/cmsg_petition_buy`, `smsg_petition_showlist`, `smsg_petition_show_signatures`, `smsg_petition_sign_results`, `cmsg_offer_petition`, `cmsg_turn_in_petition`, `smsg_turn_in_petition_results`, `msg_petition_rename`, `msg_petition_decline`, `queries/smsg_petition_query_response.wowm` (client 1.12) |
| Petition tables (petition, petition_sign) and character deletion | `Data/Social/PetitionDataModule.cs`, `Kernel/Social/PetitionRecords.cs` | `GuildMgr.cpp:168-246` (LoadPetitions), `Objects/Player.cpp:4353-4354,17796-17806` (RemovePetitionsAndSigns) | — | — |
| Charter names | `Game/Guilds/CharterNameRules.cs` | `ObjectMgr.cpp:9496-9616` (IsReservedName, isValidString, IsValidCharterName), `shared/Util.h:115-231` | — | — |
| Tabard designer and guild emblem | `Game/Guilds/GuildManager.Emblem.cs`, `GuildEmblemPackets.cs`, `World/Social/TabardHandlers.cs` | `Handlers/GuildHandler.cpp:684-735`, `Handlers/NPCHandler.cpp:49-69`, `Objects/Player.cpp:12268-12271`, `Guild/Guild.cpp:883-892` | `Guilds/GuildHandler.cpp:716-767` (same 10 gold and results) | `guild/msg_save_guild_emblem_client/server`, `msg_tabardvendor_activate` |
| Chat mute and anti-flood | `Game/Social/ChatRestrictionService.cs`, `World/Social/ChatRestrictionFeature.cs` | `Handlers/ChatHandler.cpp:221-247,417-430`, `Chat/MasterPlayerChat.cpp:10-37`, `shared/Util.cpp:197-250`, `mangosd.conf.dist.in:1666-1668`, classic-db `mangos_string` 705 | — | — |
| /who guild name and guild filter | `World/Handlers/ChatHandlers.cs` (HandleWho) | `Handlers/MiscHandler.cpp:147-158,180-196,201-203` | — | `cmsg_who`, `smsg_who` |
| Channel join / leave, password, built-ins (General, Trade, LocalDefense, WorldDefense, LookingForGroup, GuildRecruitment) | `Game/Channels/ChannelManager.cs`, `Channel.cs`, `ChannelTypes.cs` | `Chat/Channel.cpp/.h`, `Chat/ChannelMgr.cpp/.h`, `Handlers/ChannelHandler.cpp`, `DBCStores.cpp`/`DBCStructure.h` (ChatChannelsEntry) | `Chat/Channel.cpp/.h`, `Chat/ChannelMgr.cpp`, `Chat/ChannelHandler.cpp` | `chat/cmsg_join_channel.wowm` |
| SMSG_CHANNEL_NOTIFY and the moderation commands (owner, moderator, mute, kick, ban, announce, moderate, invite) | `Channel.cs`, `ChannelPackets.cs` | `Channel.cpp` Make* | `Channel.cpp` | `chat/smsg_channel_notify.wowm` |
| SMSG_CHANNEL_LIST | `ChannelPackets.BuildList` | `Channel.cpp` List, `Server/Packets/Channel.cpp` | `Channel.cpp` List | `chat/smsg_channel_list.wowm` |
| Channel chat (SMSG_MESSAGECHAT CHAT_MSG_CHANNEL) | `ChannelPackets.BuildChannelMessage` | `Chat.cpp` BuildChatPacket (CHAT_MSG_CHANNEL), `Channel.cpp` Say | `Channel.cpp` Say | `smsg_messagechat.wowm` |

## Discrepancies

| Topic | vmangos / cmangos | gtker | Chosen |
|---|---|---|---|
| SMSG_FRIEND_STATUS online fields | vmangos (builds after 1.8.4) and cmangos-classic append u8 status, u32 area, u32 level, u32 class for ADDED_ONLINE and ONLINE | lists only result + guid | servers |
| SMSG_GROUP_LIST trailing byte | vmangos and cmangos-classic write a trailing u8 (difficulty, always 0 in 1.12) after the loot fields | absent | servers |
| SMSG_GUILD_EVENT affected guid | vmangos GuildEvent appends the u64 guid when set (e.g. SIGNED_ON/OFF) | event, count, strings only | servers |
| SMSG_CHANNEL_NOTIFY payload | vmangos Make* append guids, names or flags per notice type | modelled as type + channel name only (the per-type tail is not described) | servers |
| Adding an unknown name as a friend | vmangos sends nothing | — | cmangos-classic: FRIEND_NOT_FOUND, so the client gets feedback |

## Limitations and deviations

- **ChatChannels.dbc rows are not verified.** The built-in channel ids, flags and name patterns
  in `ChannelTypes.cs` are transcribed from the vmangos ChatChannelsEntry handling; no DBC file
  was read.
- **Groups are in memory only.** They are not persisted and do not survive a restart
  (vmangos stores groups in `groups`/`group_member`).
- **Lists after login.** `LoginSequence` (seam) sends empty friend/ignore lists; the stored
  lists are resent after `PlayerLoggedIn`, and only when non-empty.
- **Guild MOTD order.** The MOTD and SIGNED_ON events go out after the player is added to the
  map (vmangos sends them during HandlePlayerLogin before the map add).
- **Character deletion** is handled: `SocialCharacterDeleteHook` and `SocialDataModule` remove
  friend/ignore rows and guild membership, `SocialPetitionFeature` and `PetitionDataModule` the
  character's petition and signatures (docs/integration/character-delete.md).
- **Options.** `SocialFeature.Options` (`World:Social`: two-side friend/group/guild/channel) is bound at
  startup and reloadable; the guild, charter and chat options are bound from `World:Guild` and `World:Chat`
  and are restart-only (see [social-guild-petitions](../integration/social-guild-petitions.md)).
- **WorldDefense is muted.** vmangos `Channel::Say` lets only honor rank 15 and up speak there;
  honor ranks are not implemented (always 0), so nobody can talk in WorldDefense.
- **Client guild create.** `CMSG_GUILD_CREATE` is honoured as in vmangos (`GuildHandler.cpp:47-72`);
  `World:Guild:AllowClientGuildCreate=false` ignores it (charter-only), an operator opt-out.
- **Deleting a rank (DelRank).** vmangos `Guild::DelRank` drops the lowest rank (never below
  the minimum of 5) and leaves its members with an out-of-range rank id (name `<unknown>`, no
  rights) until the next load clamps them (`Guild.cpp:696-723,458-460`). That is the default;
  `World:Guild:DeleteRankMovesMembers=true` moves them to the new lowest rank instead (a deliberate,
  not retail-proven deviation).
- **Roster broadcast omits officer notes.** A roster sent to one viewer includes officer notes
  when that viewer has the view-officer-note right; the roster broadcast to the whole guild
  after a change never includes them.
- **Channels:** no GM join of the opposite faction's built-in channels and no silent GM join
  configuration.
- **Whisper ignore is client-side.** The server still delivers whispers from ignored players;
  the 1.12 client drops them and sends `CMSG_CHAT_IGNORED`, and the whisperer then gets
  CHAT_MSG_IGNORED (vmangos HandleChatIgnoredOpcode).
- **/who** shows the member's guild and matches the guild filter and the search strings against it
  (`MiscHandler.cpp:147-158,180-196`). The search strings still do not match area names (needs
  AreaTable.dbc, `MiscHandler.cpp:115-130`).
- **Charters, tabard, mute and flood: see the limits list in
  [social-guild-petitions](../integration/social-guild-petitions.md#limits)**: no antispam name filter,
  the Undercity guild master has no gossip option rows in classic-db, no `GE_TABARDCHANGE`, no emblem range
  validation, no mute aura and no persistent `.mute`, commands are not counted by the flood gate.
- **GM cross-faction group invites** need GM mode (`.gm on`), matching vmangos
  `IsGameMaster()`; the account level alone is not enough.

## Tests

- `tests/ArcaneCore.Game.Tests/Social/` — friends, groups, guilds and channels against the
  managers with fake characters and persistence (71 tests).
- `tests/ArcaneCore.World.Tests/Social/SocialEndToEndTests.cs` — over the world socket: friend
  add, offline notice and persistence across a relog; group invite/accept, group list and party
  chat; a GM-founded guild with guild chat and roster; channel join notices, channel chat and
  member list.
- `tests/ArcaneCore.Data.Tests/SocialStoreTests.cs` — the social tables on SQLite, MariaDB
  and PostgreSQL.
- Petitions, emblem, parity and chat gate (see the integration doc for the full list): Game
  `PetitionManagerTests`, `GuildEmblemTests`, `GuildParityTests`, `CharterNameRulesTests`,
  `ChatRestrictionServiceTests`, `FriendsParityTests`; World `PetitionEndToEndTests`,
  `WhoGuildTests`, `GuildCreateGateTests`, `ChatRestrictionEndToEndTests`; Data `PetitionStoreTests`,
  `PetitionSchemaUpgradeTests`, `GuildRankRightsStoreTests` (provider theories: only SQLite executes locally).
