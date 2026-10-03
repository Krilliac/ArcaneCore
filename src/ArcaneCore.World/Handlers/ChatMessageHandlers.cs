using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// One parsed CMSG_MESSAGECHAT (vmangos Chat::ChatMessage): the chat type, the language after
/// the GM / two-side-chat Universal conversion, the whisper target or channel name (empty for
/// other types) and the text.
/// </summary>
public readonly record struct ClientChatMessage(ChatType Type, Language Language, string Target, string Text);

/// <summary>
/// A world feature that serves chat types the core does not (channel, party, raid, guild) or
/// vets messages before the core delivers them (ignore lists). Implementations must also be
/// <see cref="Features.IWorldFeature"/>s to be discovered.
/// <para>
/// <see cref="ChatHandlers"/> offers every message to the handlers, in feature-name order,
/// after the language checks and command parsing and before its own say/yell/emote/whisper/
/// AFK/DND handling; the first handler that returns true consumes it. Addon messages
/// (<see cref="Language.Addon"/>) are offered without language checks or command parsing and
/// are dropped when no handler takes them, as vmangos does (HandleChatMessageOpcode skips
/// both for LANG_ADDON; IsLanguageAllowedForChatType limits addon to group, guild,
/// battleground and channel chat). World thread.
/// </para>
/// </summary>
public interface IChatMessageHandler
{
    /// <summary>Handle <paramref name="message"/> from <paramref name="player"/>; true when it was consumed.</summary>
    bool TryHandle(WorldSession session, Player player, ClientChatMessage message);
}
