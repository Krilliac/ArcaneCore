using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Social;

/// <summary>
/// The chat mute and anti-flood gate (vmangos HandleChatMessageOpcode, ChatHandler.cpp:221-247 and :417-430;
/// MasterPlayer::UpdateSpeakTime). It is an <see cref="IChatMessageHandler"/> that consumes a refused message
/// after telling the speaker how long to wait (SMSG_NOTIFICATION, mangos_string 705) and otherwise lets every
/// later handler and the core deliver it. Named to sort before <see cref="SocialFeature"/>: handlers are offered
/// messages in feature-name order, so guild, party and channel chat are gated too.
/// <para>
/// Limits (docs/areas/social.md): <see cref="ChatHandlers"/> parses '.' commands before offering a message, so a
/// muted speaker can still run commands and commands are not counted (vmangos checks the mute, counts the
/// message and only then parses commands); the mute lives with the session and nothing is stored; emotes are
/// not gated.
/// </para>
/// </summary>
public sealed class ChatRestrictionFeature(IConfiguration? configuration = null, TimeProvider? clock = null)
    : IWorldFeature, IChatMessageHandler
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Anti-flood settings (World:Chat, restart-only).</summary>
    public ChatRestrictionOptions Options { get; } = new();

    /// <summary>The rules and the mute table (world thread).</summary>
    public ChatRestrictionService Service { get; private set; } = null!;

    public void Attach(WorldRuntime world)
    {
        configuration?.GetSection(ChatRestrictionOptions.SectionName).Bind(Options);
        Service = new ChatRestrictionService(Options, () => _clock.GetUtcNow().ToUnixTimeSeconds());
        world.PlayerLoggingOut += player => Service.Forget(player.Session.AccountId, player.Guid.Low);
    }

    public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
    {
        bool? whisperToPlainPlayer = null;
        if (message.Type == ChatType.Whisper && message.Language != Language.Addon)
        {
            string name = CharacterNames.Normalize(message.Target);
            if (name.Length > 0 && session.World.FindOnlinePlayer(name) is { } receiver)
            {
                whisperToPlainPlayer = receiver.Security == AccountSecurity.Player;
            }
        }

        ChatDecision decision = Service.Evaluate(
            session.AccountId, player.Guid.Low, session.Security > AccountSecurity.Player, message.Type, message.Language, whisperToPlainPlayer);
        if (decision.Allowed)
        {
            return false;
        }

        session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(ChatRestrictionService.WaitMessage(decision.MuteRemainingSeconds)));
        return true;
    }
}
