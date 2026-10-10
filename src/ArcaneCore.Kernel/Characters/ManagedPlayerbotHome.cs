namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// A managed playerbot's home area: the brain prefers goals inside the circle (map, centre, radius) and walks back to it when it
/// strays. An ArcaneCore feature; no reference server has a bot home. One row per bot (<see cref="BotId"/> is the key).
/// </summary>
public sealed record PlayerbotHome(
    Guid BotId, int CharacterId, uint MapId, float X, float Y, float Z, float Radius, string? Place, long SetUnix);

/// <summary>Radius limits of a <see cref="PlayerbotHome"/>. These are ArcaneCore choices, not retail values.</summary>
public static class PlayerbotHomeBounds
{
    public const float MinRadius = 50f;
    public const float MaxRadius = 5000f;
    public const float DefaultRadius = 600f;
    public const int MaxPlaceLength = 64;
}

/// <summary>Durable storage of the managed playerbots' home areas (characters schema 56), separate from the bot revision CAS.</summary>
public interface IManagedPlayerbotHomeStore
{
    Task<IReadOnlyList<PlayerbotHome>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the bot's home. Throws <see cref="ArgumentException"/> for a non-finite centre, a radius outside
    /// <see cref="PlayerbotHomeBounds"/>, an empty bot id, a non-positive character id or an over-long place.</summary>
    Task SetAsync(PlayerbotHome home, CancellationToken cancellationToken = default);

    /// <summary>Remove the bot's home. False when it had none.</summary>
    Task<bool> ClearAsync(Guid botId, CancellationToken cancellationToken = default);
}
