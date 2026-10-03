# Area: chat languages, gates and channel rules

WoW 1.12.1 (build 5875). Lane `chat-languages-channels` (branch `claude/vw4-chat-languages-channels`).
Channel join/leave/moderation, the channel packets, say/yell/emote/whisper and AFK/DND were already
delivered by the social and M6 work (see [social.md](social.md)); this lane added the gates that
vmangos puts around them and removed the vmangos-only channel names from the default behaviour.

Reference precedence: **vmangos (`D:\refs\vmangos\src\game`) > mangos-classic > wow_messages**.
Retail first: every vmangos-only behaviour is behind an option that defaults to retail, and every
option names the reference value it follows. No reference code or data is copied into the repository.

## Delivered

`HandleMessageChat` (`World/Handlers/ChatHandlers.cs`) now follows the vmangos order
(`Handlers/ChatHandler.cpp`, `WorldSession::HandleChatMessageOpcode`):

1. type `>= 0x5E` is dropped (`SharedDefines.h:1303` `MAX_CHAT_MSG_TYPE`);
2. `IsLanguageAllowedForChatType` (`ChatHandler.cpp:78-108`, unchanged);
3. addon language: dropped when `AddonChannel` is off, otherwise offered to the features with no further checks;
4. a language the character does not know: `SMSG_NOTIFICATION` "You don't know that language" (classic-db
   `mangos_string` 806, no full stop; the old text had one);
5. GM mode speaks Universal; otherwise two-side chat turns Common/Orcish into Universal, then
   `SPELL_AURA_MOD_LANGUAGE` forces its misc value as the language (the first aura, `ChatHandler.cpp`
   "overwrite it by SPELL_AURA_MOD_LANGUAGE auras"; `Game/Chat/ChatLanguageQueries.cs`, the public
   wrapper over the internal aura query);
6. non-AFK/DND messages: mute check (not for whispers), then the flood counter, then empty-message
   check, invisible-character stripping and the link check, then commands, then the features and
   the core say/yell/emote/whisper handling.

| Rule | Code | Reference |
|---|---|---|
| Flood counter: staff exempt; a message inside the delay window counts; the count reaching `FloodMessageCount` mutes for `FloodMuteSeconds` (never shortens a mute). The message that trips the mute is still delivered because the mute check precedes the counter, so with the defaults the 11th message in one window trips it and the 12th is refused | `World/Chat/ChatFeature.UpdateSpeakTime` | vmangos `Chat/MasterPlayerChat.cpp:10-35`, `ChatHandler.cpp:221-236`; options `mangosd.conf.dist.in:1666-1668` |
| Mute notice "You must wait 10 Seconds. before speaking again." (the reference time formatter keeps the trailing spaces, the capital plural and the full stop; 0 and 1 are "Second.") | `World/Chat/ChatText.SecsToTimeString`, `ChatFeature.MuteNotice` | `shared/Util.cpp:197-248`, classic-db `mangos_string` 705 |
| A muted player cannot emote or text-emote; can only whisper staff | `ChatHandlers.RejectMuted`, `Whisper` | `ChatHandler.cpp:417-428`, `:660-668`, `:713-721` |
| AFK/DND and addon messages are neither counted nor blocked | `HandleMessageChat` | `ChatHandler.cpp:158-236` |
| `ChatFakeMessagePreventing`: runs of space/tab/bell/newline become one space | `World/Chat/ChatSanitizer.StripInvisibleChars` | `shared/Util.cpp:134-163`, `ChatHandler.cpp:44-53` |
| `ChatStrictLinkChecking.Severity` 1 and 2 (pipe commands `c H h r |`, order `c H h h r`, 255 bytes), `.Kick` | `ChatSanitizer.IsValidChatMessage` | `Chat/Chat.cpp:2165-2208`, `ChatHandler.cpp:55-61` |
| Staff hidden from plain players unless accepting whispers or having whispered them first; `.whispers [ON/OFF]`; the allowed list is cleared by `.whispers OFF` | `World/Chat/ChatFeature`, `WhisperCommands.cs`, `ChatHandlers.Whisper` | `ChatHandler.cpp:405-415`, `Chat/MasterPlayer.h:98-102`, `MasterPlayerChat.cpp:55-72`, `Commands/CharacterCommands.cpp:1283-1313`, `Chat/Chat.cpp:1285`, `Player.cpp:133-135`; strings 259/284-286 |
| `World` and `China` custom channels are ordinary custom channels (retail has no special flags) unless `VmangosChannelExtensions` | `Game/Channels/Channel.cs` | vmangos `Chat/Channel.cpp:63-71` |
| Channel flag values, member flags and built-in channel flags pinned | `tests/.../ChannelFlagConstantsTests.cs` | vmangos `Chat/Channel.h:74-127`, mangos-classic `Channel.h:121-123` |

## Options (`World:Chat`, `World:Social`)

| Option | Default | Follows |
|---|---|---|
| `World:Chat:AddonChannel` | true | vmangos / mangos-classic `AddonChannel = 1` |
| `World:Chat:FloodMessageCount` / `FloodMessageDelaySeconds` / `FloodMuteSeconds` | 10 / 1 / 10 (0 count = off) | `ChatFlood.*`, both servers |
| `World:Chat:FakeMessagePreventing` | true | vmangos `ChatFakeMessagePreventing = 1` (mangos-classic defaults it to 0, `World.cpp:681`) |
| `World:Chat:StrictLinkSeverity` | 2 | vmangos `ChatStrictLinkChecking.Severity = 2` (mangos-classic 0, `World.cpp:683`); 3 behaves as 2 |
| `World:Chat:StrictLinkKick` | false | `ChatStrictLinkChecking.Kick = 0` |
| `World:Chat:GmWhisperingTo` | 0 | `GM.WhisperingTo` 0/1 (vmangos default 2 is a limit, below) |
| `World:Social:VmangosChannelExtensions` | false | none: vmangos-only names, off = retail |

`ListenRange.Say/Yell/TextEmote` stay 25/300/25: that is the code default of both servers
(`vmangos World.cpp:556-558`, `mangos-classic World.cpp:464-466`); vmangos' shipped conf raises
Say and TextEmote to 40 (`mangosd.conf.dist.in:1559-1561`).

## Seams

* `IChatMuteSource` (DI): lane-external account mutes (`.mute`, ban tables) return a unix end time;
  the gates take the latest of it and the in-memory flood mute. `ChatFeature.MuteUntil` lets a
  command mute a player directly.
* `Player.KnowsLanguage` (wave 1 skills): language knowledge was already done and checked against
  classic-db `playercreateinfo_spell`; nothing was added here.
* A zone auto-join of General/Trade/LocalDefense is deliberately **not** done: `Player::UpdateLocalChannels`
  is empty in vmangos (`Objects/Player.cpp:5058`, "Updated client-side"); the 1.12 client joins them itself.

## Limits and open reference questions

* **ChatChannels.dbc.** No DBC exists under the references and there is no DBC reader. The six built-in
  rows match the vmangos `Channel.h` comment table for the English name patterns only; vmangos matches
  all locale patterns (`DBCStores.cpp:530-552`), so a non-English client's General/Trade channel
  would be created as a custom channel. Needs the client MPQ data.
* **Persisted mute and `GM.WhisperingTo = 2`.** The flood mute and the whisper-acceptance state are in
  memory and end with the session (vmangos' flood mute does too; its account mute and the saved GM state
  are persisted). Persisting either is a Characters/Auth schema change that belongs to the live-ban lane.
  No store was touched by this lane, so there are no provider theories.
* **Link check level 3** (item, enchant and spell links against the catalogs, `Chat.cpp:2210-2535`)
  needs the item, enchant and spell catalogs inside the chat handlers; it is treated as level 2.
* **Group/guild Universal conversion order.** vmangos converts party/raid/guild chat to Universal
  before the `MOD_LANGUAGE` override; here `SocialFeature.TryHandle` converts afterwards, so with
  `AllowTwoSideGroup` and a language aura the group message is Universal. Non-default realm setting only.
* **Emote interrupts.** vmangos' emote opcodes also remove `ANIM_CANCELS` auras; not done.
* **vmangos channel restrictions not reproduced** (all non-retail): level-restricted channels, GM
  public-channel ban, strict-Latin, world-channel cooldown, GM-only channels (Warden etc.),
  `GM.JoinOppositeFactionChannels`, `Channel.SilentlyGMJoin`.
* **WorldDefense** stays muted until honor ranks exist (`Channel.cs` keeps the rank-0 seam).
* **Member flag conflict.** wow_messages `smsg_channel_list.wowm` lists MODERATOR 0x04, VOICED 0x08, MUTED
  0x10; vmangos and mangos-classic use 0x02/0x04/0x08. The servers are followed (pinned by test); no
  client capture is available to arbitrate.
* **`mangos-classic` extras not taken:** it replies "Unknown language" (string 805) to an unknown
  language id; vmangos does not, and neither does this server.
* **Hot reload.** `World:Chat` is read at attach and is not in the `.reload config` registry
  (restart to change); `VmangosChannelExtensions` is registered live and applies to new channels.
