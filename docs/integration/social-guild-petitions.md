# Social wave 3: guild charters, tabard/emblem, guild parity, chat gate

Branch `claude/vw4-social-guild-petitions`, based on `claude/vw-integration` at `41babaf`. Reference
precedence as in [areas/social.md](../areas/social.md): vmangos first (`D:\refs\vmangos`), then
cmangos-classic, gtker wow_messages, classic-db. Every rule in code cites the vmangos file and line it follows;
nothing was copied from the references.

## Delivered

| Slice | What | Commit subject |
|---|---|---|
| Foundation | `GuildOptions` (World:Guild), `CharterNameRules` + `ICharterNameBlacklist`, `GuildManager.MemberJoined/MemberLeft`, public `QuestNpcServices.TryCharge` | Guild foundation |
| /who | the member's guild name, guild filter and search strings | /who reports guild names |
| Guild parity | offline add stores level/zone and `LogoutTime = now`; rank 0 keeps every right at load; dead ranks read `<unknown>` with no rights; oversized text disconnects and counts code points; `CMSG_GUILD_CREATE` gated | Guild rule parity |
| Petition store | `petition`/`petition_sign`, `EfPetitionStore`, atomic `CompletePetitionAsync`, deletion cleanup + conditional purge, petition writes on the one ordered social write queue | Petition persistence |
| Petition engine | buy, show list, show signatures, query, rename, sign, decline, offer, turn in, join cleanup | Petition engine |
| Petition wire | nine opcode handlers, gossip option, load after guilds, login heal, character-delete hook | Petition wire |
| Tabard/emblem | `MSG_TABARDVENDOR_ACTIVATE`, `MSG_SAVE_GUILD_EMBLEM`, gossip option 11 | Tabard designer |
| Chat gate | account-keyed mute + anti-flood service and an `IChatMessageHandler` | Chat mute and anti-flood gate |
| Pins | friend/ignore edge cases | Friend and ignore edge-case pins |

### Not delivered (recorded, not stubbed)

- **Persistent `.mute` / `.unmute` and the `account_mute` table.** wave-2 GM commands may register the `mute`
  root (`ICommandGroup` duplicates throw at startup) and live-ban-enforcement/security hardening own account
  state. `ChatRestrictionService.Mute/Unmute` is the extension point; vmangos stores the command mute in the
  auth database (`account.mutetime`, AccountCommands.cpp:1136) and warns the account (`WarnAccount`).
- **Chat gate before command parsing and for emotes** (`ChatHandlers.HandleMessageChat` call site,
  `HandleEmote`, `HandleTextEmote`): `ChatHandlers.cs` belongs to the chat-languages-channels lane, which will
  almost certainly restructure it. Until then a muted speaker can still run `.` commands, commands are not
  counted by the flood gate and emotes are not gated (vmangos: ChatHandler.cpp:221-236, :663-668, :716-722).
- **Guild snapshot coalescing in `SocialWriteQueue`** (every change and every member logout writes the whole
  guild): the wave-2 ops/perf queue may land coalescing; not started.

## Schema

One new characters module: `PetitionDataModule.Version = 16` (the next free version in this tree; **the one
constant the integrator renumbers**). Tables `petition` (id PK never generated, owner_id UNIQUE, charter_item_id,
name varchar(24)) and `petition_sign` (PK petition_id+player_id, index on player_id, account_id). No database
uniqueness on the name: collations differ per engine; the world thread checks it case-insensitively in memory.
`IntegratedSchemaTests` lists the module by constant; tests use `Schema.CurrentVersion` / the module constant,
never a literal.

## Options (all default to the retail/vmangos value; restart-only)

| Key | Default | Reference | Meaning |
|---|---|---|---|
| `World:Guild:AllowClientGuildCreate` | true | GuildHandler.cpp:47-72 honours CMSG_GUILD_CREATE (default is vmangos; false is an operator opt-out) | honour `CMSG_GUILD_CREATE` |
| `World:Guild:MinPetitionSigns` | 9 | mangosd.conf.dist.in:1341, World.cpp:666 (0..9) | signatures that complete a charter (`==`, GuildMgr.h:124) |
| `World:Guild:MinCharterNameLength` | 2 | :1299, World.cpp:625 (2..24) | |
| `World:Guild:StrictCharterNames` | 0 | :1296 | 0 = one script for the whole name; bit 1 = basic Latin (bit 2, realm-zone language, is not supported) |
| `World:Guild:DeleteRankMovesMembers` | false | Guild.cpp:696-723 | true moves members of a deleted rank (deviation) |
| `World:Guild:KickOnOversizedText` | true | GuildHandler.cpp:58-62,470-474,518-522,554-558,580-584,600-604 | disconnect on over-long name/MOTD/info/note/rank text |
| `World:Chat:FloodMessageCount` | 10 | :1666 | 0 disables flood protection |
| `World:Chat:FloodMessageDelaySeconds` | 1 | :1667 | |
| `World:Chat:FloodMuteSeconds` | 10 | :1668 | |

`GuildOptions` is deliberately not a `SocialOptions` member: the `WorldConfigKeys` guard on the integration
branch demands every `SocialOptions` member be classified live or restart-only, and every wave-3 lane would
collide there. An integrator may add `.reload config` keys for `World:Guild` / `World:Chat` after merge.

## Shared-file edits (all additive)

`GuildManager.cs` (partial, `Options`, `MemberJoined/MemberLeft`, length checks, rank rules, offline add),
`Guild.cs` (`RankName`), `SocialContext.cs` (`CharacterInfo` Level/ZoneId, `Petitions`), `CharacterRecord.cs`
(`CharacterIdentity` Level/ZoneId, optional trailing), `EfCharacterStore.cs` (projection), `EfSocialStore.cs`
(`StageGuildAsync` extracted), `SocialWriteQueue.cs` (generic scoped work + `IPetitionPersistence`),
`SocialFeature.cs` (`World:Guild` binding, directory refresh at logout), `CharacterLookup.cs`,
`CharacterHandlers.cs` (creation entry), `GuildHandlers.cs`, `ChatHandlers.cs` (`HandleWho` body only),
`QuestNpcServices.Charge.cs` (new partial), `IntegratedSchemaTests.cs`, `SocialFixture.cs`,
`SocialTestServices.cs`, `InMemoryWorldStores.cs`.

## Seams used

`QuestNpcServices.ForeignOptionSelected` (Petitioner and TabardDesigner options), `QuestNpcServices.InteractableNpc`
and the new `TryCharge`, `PlayerInventory.ItemCountChanged` (charter destroyed), `Item.EnchantmentId`/`ToData`
(the petition id round-trips in enchantment slot 0), `IChatMessageHandler`, `ICharacterDeleteHook`,
`ICharacterDataCleanup`, `IDataModule`. `SocialPetitionFeature` and `ChatRestrictionFeature` are named so they
attach/are offered messages in the right order (features sort by full type name).

## Behaviors worth knowing

- The petition id is the charter item's enchantment slot 0 (as vmangos). Ids are reused after the highest petition
  is deleted, so every use also binds the charter item's guid (stricter than vmangos, no client-visible change).
- Buy stores the item first, then charges (the vendor order), so the character save the charge triggers already
  holds the charter; vmangos charges first. Same end state and packets.
- A join (accept, GM invite, turn-in) removes the joiner's signatures on other petitions and his own petition
  from memory AND storage (vmangos forgets the owned petition in memory); the turn-in passes the completing
  petition id so its signers' signatures are not stripped mid-iteration.
- Turn-in stores the guild, its ranks and members and deletes the petition in ONE `SaveChanges`; the in-memory
  guild is created synchronously. A write dropped after three attempts (existing queue behavior) loses the guild
  while the charter is already destroyed; load-time validation (petitions of unknown or guilded owners are
  removed) and the login heal (an owner without a bound charter loses the petition) bound the damage.
- Error codes follow vmangos even where they look odd: a duplicate signature tells the online owner
  `ALREADY_INVITED_TO_GUILD_S` (PetitionsHandler.cpp:281-286); offer errors name the offerer.
- `SMSG_PETITION_QUERY_RESPONSE` sends 9/9 signatures whatever `MinPetitionSigns` is (vmangos :182-183).

## Limits

- No antispam name filter (`AntispamInterface::filterMessage`) and no reserved names: classic-db `reserved_name`
  has 0 rows; `ICharterNameBlacklist` is empty by default.
- Undercity guild master (creature 4613) has gossip menu text but no `gossip_menu_option` rows in classic-db, so
  charters cannot be offered through gossip there. Data gap, not invented; the direct `CMSG_PETITION_SHOWLIST`
  still works. `option_id 10` also appears once for an arena team (menu 8494, TBC content): not wired.
- No `GE_TABARDCHANGE` broadcast and no emblem range validation (neither vmangos nor cmangos does either;
  result 1 `INVALID_TABARD_COLORS` is never sent). Retail behavior from another source is unknown.
- `SMSG_GUILD_COMMAND_RESULT` 0x13 `IGNORING_YOU_S` is kept as vmangos sends it; the 1.12 enum in wow_messages
  stops at 0x0E. What the 1.12.1 client prints is unverified (needs the real client).
- No mute visual aura (spell 1852, Chat.h:53) and no `CHARACTER_FLAG_SILENCED` in `SMSG_CHAR_ENUM`
  (Player.cpp:16335): the aura and char-enum seams belong to other lanes.
- The chat gate is the session's mute in memory: it does not survive a relog and nothing is persisted.
- `.who` area-name search needs AreaTable.dbc.
- Guild and charter name uniqueness is enforced in memory on the world thread only (a second world process would
  break it). vmangos does not enforce uniqueness between charters either: a second charter with the same name
  gets `NAME_EXISTS_S` only at turn-in, and so does this port.
- Ignore enforcement for duel requests (SpellEffects.cpp:4686) belongs to the duels lane; its extension point is
  `SocialContext.IsIgnoring`.
- Friend add of an unknown name answers `FRIEND_NOT_FOUND` (cmangos) where vmangos stays silent: kept, pinned.
- `World:Guild` / `World:Chat` are restart-only until an integrator adds reload keys.

## Verification status

- Provider theories (`PetitionStoreTests`, `PetitionSchemaUpgradeTests`, `GuildRankRightsStoreTests`) are written
  against real MariaDB/PostgreSQL semantics (non-transactional MariaDB DDL and re-applied steps, transactional
  PostgreSQL DDL and aborted-transaction state, pooled Npgsql connections, unsigned/`bigint`, `VARCHAR` in
  characters, case-insensitive collations, one-transaction turn-in rollback). **Only SQLite executed on the
  machine that wrote them; hosted CI is the first run on the other two engines.**
- Needs the real client: the petition UI flow including the Turn-in button with `MinPetitionSigns` below 9
  (the query response says 9), the tabard designer UI, and what the client does with result 0x13.

## Open questions

1. `AllowClientGuildCreate` defaults to on (vmangos honours the opcode); set it false to found guilds by charter only.
2. `DeleteRankMovesMembers` defaults to the vmangos behavior (members stranded on a dead rank id until restart).
   Retail behavior is unknown; flip the default if the stranded state is judged a vmangos bug.
3. Charter names are not trimmed or collapsed (neither server does it); should leading/trailing/double spaces be
   rejected?
4. Where the persistent mute should live once wave-2 GM commands and the security lane have landed (auth
   `account.mutetime` is the vmangos location).
