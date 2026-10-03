using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Chat;

/// <summary>
/// Where a speaker's mute comes from besides the flood gate (vmangos WorldSession::m_muteTime is
/// loaded from the account's mutetime and set by <c>.mute</c>). The chat gates take the latest end
/// time over <see cref="ChatFeature"/>'s own in-memory flood mute and every registered source, so
/// the lane that persists account mutes (live ban enforcement, GM commands) can plug in by
/// registering an implementation in DI without touching the chat handlers.
/// </summary>
public interface IChatMuteSource
{
    /// <summary>Unix time (seconds) until which <paramref name="player"/> is muted; 0 or a past time when not muted.</summary>
    long MutedUntilUnixSeconds(Player player);
}
