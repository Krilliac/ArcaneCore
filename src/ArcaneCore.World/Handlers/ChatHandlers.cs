using ArcaneCore.Game;
using ArcaneCore.Game.Chat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
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
            // language or flood gates, skip command parsing (vmangos HandleChatMessageOpcode /
            // SanitizeChatMessage) and only travel the group, guild, battleground and channel chat
            // that features serve; unserved, they are dropped.
            if (chat.Options.AddonChannel)
            {
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

            if (TryRunCommand(session, player, message))
            {
                return;
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

        CommandTable commands = session.Services.GetRequiredService<CommandTable>();
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
        if (receiver is null)
        {
            session.Send(WorldOpcode.SmsgChatPlayerNotFound, ChatPackets.BuildPlayerNotFound(name));
            return;
        }

        // "Can only whisper GMs while muted" (vmangos ChatHandler.cpp:420).
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
    /// CMSG_TEXT_EMOTE: u32 text emote, u32 emote number, u64 target (vmangos Misc::TextEmote).
    /// Sent to everyone within the text-emote range, the emoter included, with the target's
    /// name (vmangos HandleTextEmoteOpcode / EmoteChatBuilder). The accompanying animation
    /// needs EmotesText.dbc and arrives with the content platform (M8).
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

        string? targetName = targetGuid.IsEmpty ? null : map.FindPlayer(targetGuid)?.Name;
        map.BroadcastInRange(player, session.World.Options.ListenRangeTextEmote, WorldOpcode.SmsgTextEmote,
            ChatPackets.BuildTextEmote(player.Guid, textEmote, emoteNumber, targetName), includeSelf: true);
    }

    /// <summary>
    /// CMSG_EMOTE: u32 emote. Only the two animations the client sends on its own are accepted —
    /// EMOTE_ONESHOT_NONE (0) and EMOTE_ONESHOT_WAVE (3) — and played for the player and
    /// everyone who sees it (vmangos HandleEmoteOpcode → Unit::HandleEmoteCommand).
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

        byte[] packet = ChatPackets.BuildEmote(emote, player.Guid);
        session.Send(WorldOpcode.SmsgEmote, packet);
        player.Map?.BroadcastToObservers(player, WorldOpcode.SmsgEmote, packet);
    }

    /// <summary>
    /// CMSG_WHO: u32 min level, u32 max level, CString name, CString guild, u32 race mask,
    /// u32 class mask, u32 zone count (≤ 10) + zones, u32 string count (≤ 4) + strings
    /// (vmangos Misc::Who, gtker cmsg_who). Filtering follows vmangos WhoListClientQueryTask;
    /// search strings match names and guilds (area names need AreaTable.dbc, M8).
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
            const string guild = ""; // guilds arrive in M14
            if ((playerName.Length > 0 && !name.Contains(playerName, StringComparison.Ordinal))
                || (guildName.Length > 0 && !guild.Contains(guildName, StringComparison.Ordinal))
                || (zones.Length > 0 && Array.IndexOf(zones, other.ZoneId) < 0)
                || !MatchesSearchStrings(strings, name, guild))
            {
                continue;
            }

            entries.Add(new WhoEntry(other.Name, guild, level, cls, race, other.ZoneId));
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

    /// <summary>vmangos: any non-empty search string matching the name or guild shows the player; all empty shows everyone.</summary>
    private static bool MatchesSearchStrings(string[] strings, string name, string guild)
    {
        bool show = true;
        foreach (string term in strings)
        {
            if (term.Length == 0)
            {
                continue;
            }

            if (name.Contains(term, StringComparison.Ordinal) || guild.Contains(term, StringComparison.Ordinal))
            {
                return true;
            }

            show = false;
        }

        return show;
    }
}
