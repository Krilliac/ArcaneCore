using ArcaneCore.Game;
using ArcaneCore.Game.Chat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Chat, emotes and /who (vmangos ChatHandler.cpp HandleChatMessageOpcode / HandleEmoteOpcode /
/// HandleTextEmoteOpcode, MiscHandler.cpp HandleWhoOpcode). World thread: chat reaches other
/// players through the map and the online registry. Party, raid, guild, battleground and
/// channel chat are served by world features through <see cref="IChatMessageHandler"/>; a
/// message no feature takes is dropped, as vmangos drops it for a player in no group or guild.
/// </summary>
public sealed class ChatHandlers : IOpcodeHandlerGroup
{
    /// <summary>vmangos DBCEnums.h MAX_LEVEL: a /who upper bound at or above it means "any level".</summary>
    private const uint WhoAnyLevelThreshold = 100;

    /// <summary>vmangos WhoListClientQueryTask stops after 49 entries ("50 is maximum player count sent to client").</summary>
    private const int WhoMaxEntries = 49;

    /// <summary>vmangos SharedDefines.h:1303 MAX_CHAT_MSG_TYPE for a 1.12 client: a CMSG_MESSAGECHAT type at or above it is dropped.</summary>
    private const uint MaxChatMsgType = 0x5E;

    /// <summary>classic-db mangos_string 806 (vmangos LANG_NOT_LEARNED_LANGUAGE), sent as SMSG_NOTIFICATION.</summary>
    private const string LanguageNotLearned = "You don't know that language";

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgMessagechat, HandleMessageChat);
        table.OnWorld(WorldOpcode.CmsgTextEmote, HandleTextEmote);
        table.OnWorld(WorldOpcode.CmsgEmote, HandleEmote);
        table.OnWorld(WorldOpcode.CmsgWho, HandleWho);
    }

    /// <summary>
    /// CMSG_MESSAGECHAT: u32 type, u32 language, a CString target for whispers and channels,
    /// then the CString message (vmangos Chat::ChatMessage, gtker cmsg_messagechat).
    /// </summary>
    private static void HandleMessageChat(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint rawType = reader.ReadUInt32();
        var language = (Language)reader.ReadUInt32();
        if (rawType >= MaxChatMsgType)
        {
            return; // vmangos: "Wrong message type received"
        }

        var type = (ChatType)rawType;
        string target = type is ChatType.Whisper or ChatType.Channel ? reader.ReadCString() : string.Empty;
        string message = reader.ReadCString();

        if (!IsLanguageAllowedForChatType(language, type))
        {
            return;
        }

        WorldRuntimeOptions options = session.World.Options;
        ChatFeature chat = session.Services.GetRequiredService<ChatFeature>();
        if (language == Language.Addon)
        {
            // Disabled addon channel? (vmangos AddonChannel). Addon messages are not touched by the
            // language or flood gates by default, skip command parsing (vmangos HandleChatMessageOpcode /
            // SanitizeChatMessage) and only travel the group, guild, battleground and channel chat
            // that features serve; unserved, they are dropped.
            if (chat.Options.AddonChannel)
            {
                // Optional protection, retaining the reference's default addon exemption:
                // vmangos 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa ChatHandler.cpp:165-236.
                if (chat.Options.AddonMuteAndFloodControl)
                {
                    if (RejectMuted(session, player))
                    {
                        return;
                    }

                    chat.UpdateSpeakTime(player);
                }

                OfferToFeatures(session, player, new ClientChatMessage(type, language, target, message));
            }

            return;
        }

        // Talking in a language the character does not know is cheating (vmangos: notification).
        if (language != Language.Universal && !player.KnowsLanguage(language))
        {
            session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(LanguageNotLearned));
            return;
        }

        if (player.IsGameMaster)
        {
            language = Language.Universal; // GM mode speaks plainly, ignoring spell effects
        }
        else
        {
            // Cross-faction realms speak plainly (vmangos HandleChatMessageOpcode) ...
            if (options.AllowTwoSideChat && language is Language.Common or Language.Orcish)
            {
                language = Language.Universal;
            }

            // ... but SPELL_AURA_MOD_LANGUAGE overwrites it (the first aura, "only single case used").
            if (session.Services.GetService<SpellFeature>() is { } spells && spells.System.ModLanguageOverride(player) is { } forced)
            {
                language = forced;
            }
        }

        if (type is not (ChatType.Afk or ChatType.Dnd))
        {
            // Mute and anti-flood (vmangos: the mute check, then UpdateSpeakTime, before sanitising
            // and command parsing, so command lines count as messages). A whisper is checked later,
            // against its receiver: a muted player may still whisper staff.
            if (type != ChatType.Whisper && chat.MuteNotice(player) is { } notice)
            {
                session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(notice));
                return;
            }

            chat.UpdateSpeakTime(player);

            if (message.Length == 0)
            {
                return; // vmangos SanitizeChatMessage
            }

            // The rest of SanitizeChatMessage (addon messages returned above): invisible-character
            // runs, then the link check, which drops (or kicks for) a malformed message.
            if (chat.Options.FakeMessagePreventing)
            {
                message = ChatSanitizer.StripInvisibleChars(message);
            }

            if (chat.Options.StrictLinkSeverity > 0 && !ChatSanitizer.IsValidChatMessage(message, chat.Options.StrictLinkSeverity))
            {
                session.Logger.LogWarning("[{Endpoint}] {Player} sent a chat message with an invalid link", session.RemoteEndpoint, player.Name);
                if (chat.Options.StrictLinkKick)
                {
                    session.Kick();
                }

                return;
            }

            if (TryRunCommand(session, player, message))
            {
                return;
            }

            // chat_word_filter (World:Chat:WordFilter, off by default; AscEmu WordFilter.cpp): censor or drop after commands, so command
            // lines are never rewritten.
            if (chat.Options.WordFilter && session.Services.GetService<ChatWordFilterFeature>()?.Filter is { ChatRuleCount: > 0 } filter)
            {
                if (filter.Apply(message) is not { } filtered)
                {
                    session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification("Your message was blocked by the chat filter."));
                    return;
                }

                message = filtered;
            }
        }

        if (OfferToFeatures(session, player, new ClientChatMessage(type, language, target, message)))
        {
            return;
        }

        switch (type)
        {
            case ChatType.Say:
                if (player.IsAlive)
                {
                    float say = options.ListenRangeSay;
                    float yell = options.ListenRangeYell;
                    float range = say > 0 && yell > 0 ? Math.Min(say, yell) : say; // vmangos Player::Say: min(say, yell range)
                    player.Map?.BroadcastInRange(player, range, WorldOpcode.SmsgMessagechat,
                        ChatPackets.BuildMessage(ChatType.Say, language, player.Guid, message, player.ChatTag), includeSelf: true);
                }

                break;

            case ChatType.Yell:
                if (player.IsAlive)
                {
                    player.Map?.BroadcastInRange(player, options.ListenRangeYell, WorldOpcode.SmsgMessagechat,
                        ChatPackets.BuildMessage(ChatType.Yell, language, player.Guid, message, player.ChatTag), includeSelf: true);
                }

                break;

            case ChatType.Emote:
                // Custom emotes are plain text, so they stay within the faction unless two-side
                // chat is allowed (vmangos Player::TextEmote, own_team_only).
                if (player.IsAlive)
                {
                    player.Map?.BroadcastInRange(player, options.ListenRangeTextEmote, WorldOpcode.SmsgMessagechat,
                        ChatPackets.BuildMessage(ChatType.Emote, Language.Universal, player.Guid, message, player.ChatTag),
                        includeSelf: true, onlyTeam: options.AllowTwoSideChat ? null : player.Team);
                }

                break;

            case ChatType.Whisper:
                Whisper(session, player, target, message, options, chat);
                break;

            case ChatType.Afk:
                // vmangos: no AFK in combat; a message sets the reply, an empty one (or going
                // AFK) toggles; going AFK ends DND.
                if ((player.UnitFlags & UnitFlags.InCombat) != 0)
                {
                    break;
                }

                if (message.Length > 0 || !player.IsAfk)
                {
                    player.AfkMessage = message;
                }

                if (message.Length == 0 || !player.IsAfk)
                {
                    if (player.ToggleAfk() && player.IsDnd)
                    {
                        player.ToggleDnd();
                    }
                }

                break;

            case ChatType.Dnd:
                if (message.Length > 0 || !player.IsDnd)
                {
                    player.DndMessage = message;
                }

                if (message.Length == 0 || !player.IsDnd)
                {
                    if (player.ToggleDnd() && player.IsAfk)
                    {
                        player.ToggleAfk();
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Offer a message to the <see cref="IChatMessageHandler"/> features (channels, groups,
    /// social lists …) in feature-name order; true when one consumed it.
    /// </summary>
    private static bool OfferToFeatures(WorldSession session, Player player, ClientChatMessage message)
    {
        foreach (IChatMessageHandler handler in session.Services.GetServices<IChatMessageHandler>())
        {
            if (handler.TryHandle(session, player, message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos ChatHandler::ParseCommands for a player: '.'/'!' commands run unless the account
    /// is a plain player and player commands are disabled. A recognised command (or an unknown
    /// one, which gets "no such command") is consumed and never shown as chat.
    /// </summary>
    private static bool TryRunCommand(WorldSession session, Player player, string message)
    {
        if (!CommandTable.TryGetCommandText(message, out string commandText)
            || (session.Security == AccountSecurity.Player && !session.World.Options.PlayerCommands))
        {
            return false;
        }

        CommandTable commands = session.Services.GetRequiredService<CommandTableSource>().Current;
        var context = new CommandContext(session, player, commands);
        try
        {
            commands.Execute(context, commandText);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A faulty command must not take the invoker's connection down with it.
            session.Logger.LogError(ex, "[{Endpoint}] command '{Command}' by {Player} failed", session.RemoteEndpoint, commandText, player.Name);
            context.Reply("The command failed; see the server log.");
        }

        return true;
    }

    /// <summary>
    /// vmangos HandleChatMessageOpcode (CHAT_MSG_WHISPER) and MasterPlayer::Whisper: the target
    /// must be online; between two plain players the factions must match unless two-side chat
    /// is allowed. The receiver gets CHAT_MSG_WHISPER, the sender CHAT_MSG_WHISPER_INFORM, then
    /// the receiver's DND or AFK auto-reply. Whispers are always sent in Universal.
    /// </summary>
    private static void Whisper(WorldSession session, Player sender, string targetName, string message, WorldRuntimeOptions options, ChatFeature chat)
    {
        string name = CharacterNames.Normalize(targetName);
        Player? receiver = name.Length == 0 ? null : session.World.FindOnlinePlayer(name);
        // A plain player cannot see a staff member who does not accept its whispers (vmangos
        // ChatHandler.cpp:411: the same "player not found" notice as for an offline target).
        if (receiver is null
            || (sender.Security == AccountSecurity.Player && receiver.Security > AccountSecurity.Player
                && !chat.AcceptsWhispersFrom(receiver, sender.Guid)))
        {
            session.Send(WorldOpcode.SmsgChatPlayerNotFound, ChatPackets.BuildPlayerNotFound(name));
            return;
        }

        // "Can only whisper GMs while muted" (vmangos ChatHandler.cpp:417-428).
        if (receiver.Security == AccountSecurity.Player && chat.MuteNotice(sender) is { } notice)
        {
            session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(notice));
            return;
        }

        if (sender.Security == AccountSecurity.Player && receiver.Security == AccountSecurity.Player
            && !options.AllowTwoSideChat && sender.Team != receiver.Team)
        {
            session.Send(WorldOpcode.SmsgChatWrongFaction, []);
            return;
        }

        chat.NoteWhisperSent(sender, receiver);
        receiver.Session.Send(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildMessage(ChatType.Whisper, Language.Universal, sender.Guid, message, sender.ChatTag));
        session.Send(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildMessage(ChatType.WhisperInform, Language.Universal, receiver.Guid, message, receiver.ChatTag));

        if (receiver.IsDnd)
        {
            session.Send(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Dnd, Language.Universal, receiver.Guid, receiver.DndMessage, ChatTag.None));
        }
        else if (receiver.IsAfk)
        {
            session.Send(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Afk, Language.Universal, receiver.Guid, receiver.AfkMessage, ChatTag.None));
        }
    }

    /// <summary>
    /// vmangos WorldSession::IsLanguageAllowedForChatType: addon messages only on group, guild
    /// and channel chat; Universal only for AFK/DND replies; every other language anywhere.
    /// </summary>
    private static bool IsLanguageAllowedForChatType(Language language, ChatType type) => language switch
    {
        Language.Addon => type is ChatType.Party or ChatType.Guild or ChatType.Officer or ChatType.Raid
            or ChatType.RaidLeader or ChatType.RaidWarning or ChatType.Battleground or ChatType.BattlegroundLeader
            or ChatType.Channel,
        Language.Universal => type is ChatType.Afk or ChatType.Dnd,
        _ => true,
    };

    /// <summary>
    /// vmangos HandleEmoteOpcode / HandleTextEmoteOpcode: a muted player cannot emote; the
    /// "You must wait ... before speaking again." notification is sent instead. True when muted.
    /// </summary>
    private static bool RejectMuted(WorldSession session, Player player)
    {
        if (session.Services.GetRequiredService<ChatFeature>().MuteNotice(player) is not { } notice)
        {
            return false;
        }

        session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(notice));
        return true;
    }

    /// <summary>
    /// CMSG_TEXT_EMOTE: u32 text emote, u32 emote number, u64 target (vmangos Misc::TextEmote), handled as vmangos
    /// HandleTextEmoteOpcode (ChatHandler.cpp:711-753): alive and not animation-locked, may speak; with the client's
    /// EmotesText.dbc and Emotes.dbc (<see cref="ChatFeature.Emotes"/>) an unknown text emote is dropped and a known one
    /// plays its emote (except sit, sleep, kneel and none), first cancelling what an animation cancels
    /// (AURA_INTERRUPT_ANIM_CANCELS channels and auras); then everyone within the text-emote range, the emoter included,
    /// gets SMSG_TEXT_EMOTE with the target's name (a player's or a creature's, EmoteChatBuilder), and a creature target is
    /// told (CreatureAI::ReceiveEmote, <see cref="ITextEmoteReceiver"/>). Without the files no animation plays.
    /// </summary>
    private static void HandleTextEmote(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint textEmote = reader.ReadUInt32();
        uint emoteNumber = reader.ReadUInt32();
        var targetGuid = new ObjectGuid(reader.ReadUInt64());
        if (!player.IsAlive || (player.UnitFlags & UnitFlags.PreventAnim) != 0 || player.Map is not { } map)
        {
            return;
        }

        if (RejectMuted(session, player))
        {
            return;
        }

        if (session.Services.GetRequiredService<ChatFeature>().Emotes is { } emotes)
        {
            if (!emotes.TryGetTextEmote(textEmote, out uint emoteId))
            {
                return; // vmangos: "if (!em) return;"
            }

            if (EmoteCatalog.Animates(emoteId))
            {
                CancelAnimationAuras(session, player);
                PlayEmote(player, emoteId, emotes);
            }
        }

        Unit? target = targetGuid.IsEmpty ? null : map.FindObject(targetGuid) as Unit;
        string? targetName = target switch
        {
            Player p => p.Name,
            Game.Creatures.Creature c => c.Template.Name,
            _ => null,
        };
        map.BroadcastInRange(player, session.World.Options.ListenRangeTextEmote, WorldOpcode.SmsgTextEmote,
            ChatPackets.BuildTextEmote(player.Guid, textEmote, emoteNumber, targetName), includeSelf: true);

        if (target is Game.Creatures.Creature creature)
        {
            // vmangos HandleTextEmoteOpcode (Handlers/ChatHandler.cpp:751-752): the targeted creature's AI hears it (EventAI RECEIVE_EMOTE).
            creature.ReceiveEmote(player, textEmote);
            foreach (ITextEmoteReceiver receiver in session.Services.GetServices<IWorldFeature>().OfType<ITextEmoteReceiver>())
            {
                receiver.ReceiveEmote(creature, player, textEmote);
            }
        }
    }

    /// <summary>
    /// vmangos HandleEmoteOpcode / HandleTextEmoteOpcode (ChatHandler.cpp:674-675, 736-737): an animation ends what
    /// AURA_INTERRUPT_ANIM_CANCELS ends, channels first (Feign Death, for one).
    /// </summary>
    private static void CancelAnimationAuras(WorldSession session, Player player)
    {
        if (session.Services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.InterruptChannelsWithFlags(player, SpellAuraInterruptFlags.AnimCancels);
            spells.System.RemoveAurasWithInterruptFlags(player, SpellAuraInterruptFlags.AnimCancels);
        }
    }

    /// <summary>
    /// vmangos Unit::HandleEmote (Unit.cpp:1861-1872): an Emotes.dbc type other than 0 is a state (UNIT_NPC_EMOTESTATE),
    /// 0 a one-shot command (SMSG_EMOTE to the unit and everyone who sees it, HandleEmoteCommand); an emote the file does not
    /// list does nothing.
    /// </summary>
    private static void PlayEmote(Player player, uint emoteId, EmoteCatalog emotes)
    {
        if (emotes.Emote(emoteId) is not { } emote)
        {
            return;
        }

        if (emote.EmoteType != 0)
        {
            player.SetUInt32(Game.UpdateFields.UnitNpcEmotestate, emoteId);
            return;
        }

        byte[] packet = ChatPackets.BuildEmote(emoteId, player.Guid);
        player.Session.Send(WorldOpcode.SmsgEmote, packet);
        player.Map?.BroadcastToObservers(player, WorldOpcode.SmsgEmote, packet);
    }

    /// <summary>
    /// CMSG_EMOTE: u32 emote. Only the two animations the client sends on its own are accepted —
    /// EMOTE_ONESHOT_NONE (0) and EMOTE_ONESHOT_WAVE (3) — and, after the AURA_INTERRUPT_ANIM_CANCELS
    /// channels and auras end, played for the player and everyone who sees it (vmangos HandleEmoteOpcode →
    /// Unit::HandleEmoteCommand).
    /// </summary>
    private static void HandleEmote(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint emote = reader.ReadUInt32();
        if (!player.IsAlive || (player.UnitFlags & UnitFlags.PreventAnim) != 0 || RejectMuted(session, player))
        {
            return; // vmangos HandleEmoteOpcode: alive and not animation-locked, CanSpeak, then the emote filter
        }

        if (emote is not (0 or 3))
        {
            return;
        }

        CancelAnimationAuras(session, player);
        byte[] packet = ChatPackets.BuildEmote(emote, player.Guid);
        session.Send(WorldOpcode.SmsgEmote, packet);
        player.Map?.BroadcastToObservers(player, WorldOpcode.SmsgEmote, packet);
    }

    /// <summary>
    /// CMSG_WHO: u32 min level, u32 max level, CString name, CString guild, u32 race mask,
    /// u32 class mask, u32 zone count (≤ 10) + zones, u32 string count (≤ 4) + strings
    /// (vmangos Misc::Who, gtker cmsg_who). Filtering follows vmangos WhoListClientQueryTask: the zone
    /// filter limits one's own battleground zone to one's own instance, and the search strings match the
    /// name, the guild and the name of the zone (the area table, <see cref="WorldMaps.Areas"/>), see
    /// <see cref="WhoRules"/>.
    /// </summary>
    private static void HandleWho(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint levelMin = reader.ReadUInt32();
        uint levelMax = reader.ReadUInt32();
        string playerName = reader.ReadCString().ToLowerInvariant();
        string guildName = reader.ReadCString().ToLowerInvariant();
        uint raceMask = reader.ReadUInt32();
        uint classMask = reader.ReadUInt32();
        uint zoneCount = reader.ReadUInt32();
        if (zoneCount > 10)
        {
            return; // client limit; vmangos drops the request
        }

        var zones = new uint[zoneCount];
        for (int i = 0; i < zones.Length; i++)
        {
            zones[i] = reader.ReadUInt32();
        }

        uint stringCount = reader.ReadUInt32();
        if (stringCount > 4)
        {
            return;
        }

        var strings = new string[stringCount];
        for (int i = 0; i < strings.Length; i++)
        {
            strings[i] = reader.ReadCString().ToLowerInvariant();
        }

        if (levelMax >= WhoAnyLevelThreshold)
        {
            levelMax = byte.MaxValue;
        }

        WorldRuntimeOptions options = session.World.Options;
        Game.Maps.Terrain.AreaTable areas = WorldMaps.Of(session.World).Areas;
        uint askerMap = player.MapId;
        uint askerInstance = player.Map?.InstanceId ?? 0;
        var entries = new List<WhoEntry>();
        foreach (Player other in session.World.OnlinePlayers)
        {
            if (session.Security == AccountSecurity.Player
                && ((other.Team != player.Team && !options.AllowTwoSideWhoList) || other.Security > options.GmLevelInWhoList))
            {
                continue;
            }

            uint level = other.Level;
            uint race = (uint)other.Race;
            uint cls = (uint)other.Class;
            if (level < levelMin || level > levelMax
                || (classMask & (1u << (int)cls)) == 0
                || (raceMask & (1u << (int)race)) == 0)
            {
                continue;
            }

            string name = other.Name.ToLowerInvariant();
            // The member's guild name (vmangos sGuildMgr.GetGuildNameById, MiscHandler.cpp:147-158,
            // :201-203); empty without a guild or when the social feature is not installed.
            string guildDisplay = session.Services.GetService<SocialFeature>()?.Context.Guilds.GetGuildOf(other)?.Name ?? string.Empty;
            string guild = guildDisplay.ToLowerInvariant();
            // The zone's name for the search strings (vmangos AreaEntry::GetById(pzoneId)->Name, MiscHandler.cpp:178-183).
            string area = areas.GetById(other.ZoneId)?.Name.ToLowerInvariant() ?? string.Empty;
            if ((playerName.Length > 0 && !name.Contains(playerName, StringComparison.Ordinal))
                || (guildName.Length > 0 && !guild.Contains(guildName, StringComparison.Ordinal))
                || !WhoRules.ZoneFilterShows(zones, player.ZoneId, askerMap, askerInstance, other.ZoneId, other.MapId, other.Map?.InstanceId ?? 0)
                || !WhoRules.MatchesSearchStrings(strings, name, guild, area))
            {
                continue;
            }

            entries.Add(new WhoEntry(other.Name, guildDisplay, level, cls, race, other.ZoneId));
            if (entries.Count == WhoMaxEntries)
            {
                break;
            }
        }

        // The online total is reported only when the list was truncated (vmangos).
        int online = session.World.OnlinePlayerCount;
        uint onlineCount = (uint)(online > WhoMaxEntries ? online : entries.Count);
        session.Send(WorldOpcode.SmsgWho, MiscPackets.BuildWho(entries, onlineCount));
    }
}
