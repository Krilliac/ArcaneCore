using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Social;

/// <summary>
/// The explicit chat mutes of the social lane's <see cref="ChatRestrictionService"/> (the seam for <c>.mute</c> and a
/// stored mute), offered to <see cref="ChatFeature"/> as an <see cref="IChatMuteSource"/>. Integration decision
/// (docs/integration/wave3-integration.md): the chat lane's <see cref="ChatFeature"/> owns the gates (mute check,
/// anti-flood counter, whisper-to-staff rule, vmangos HandleChatMessageOpcode ChatHandler.cpp:221-247 and :417-430),
/// so this feature no longer gates or counts anything itself; two counters would mute twice and ignore each other's
/// options. The service keeps its pure flood logic and tests, which the runtime no longer calls.
/// </summary>
public sealed class ChatRestrictionFeature(IConfiguration? configuration = null, TimeProvider? clock = null)
    : IWorldFeature, IChatMuteSource
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Anti-flood settings (World:Chat, restart-only); used by the service's own logic only.</summary>
    public ChatRestrictionOptions Options { get; } = new();

    /// <summary>The rules and the mute table (world thread).</summary>
    public ChatRestrictionService Service { get; private set; } = null!;

    public void Attach(WorldRuntime world)
    {
        configuration?.GetSection(ChatRestrictionOptions.SectionName).Bind(Options);
        Service = new ChatRestrictionService(Options, () => _clock.GetUtcNow().ToUnixTimeSeconds());
    }

    public long MutedUntilUnixSeconds(Player player)
        => Service is { } service && service.MuteRemaining(player.AccountId) is var remaining and > 0
            ? _clock.GetUtcNow().ToUnixTimeSeconds() + remaining
            : 0;
}
