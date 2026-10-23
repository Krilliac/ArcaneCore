using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Game.Social;

/// <summary>Accepts social writes made on the world thread for ordered, asynchronous persistence.</summary>
public interface ISocialPersistence
{
    void SetSocial(int characterId, int otherId, SocialFlags flags);

    /// <summary>
    /// Like <see cref="SetSocial"/>, but false when the write cannot be accepted (a bound on the
    /// writes waiting for storage was reached, or the queue stopped); nothing is queued then and the
    /// caller drops the session. Default: always accepted.
    /// </summary>
    bool TrySetSocial(int characterId, int otherId, SocialFlags flags)
    {
        SetSocial(characterId, otherId, flags);
        return true;
    }

    void SaveGuild(GuildData guild);

    void DeleteGuild(int guildId);

    /// <summary>
    /// A deleted character's friend/ignore rows (owned and pointing at it) and guild membership,
    /// after every earlier write (docs/integration/character-delete.md). Default: nothing to purge.
    /// </summary>
    void PurgeCharacter(int characterId)
    {
    }

    /// <summary>Completes once every write queued before the call was attempted. Default: nothing is queued.</summary>
    Task FlushAsync() => Task.CompletedTask;
}
