using ArcaneCore.Game.Maps;
using ArcaneCore.World.Persistence;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Asks whether a bot's character is live on another world worker: in a split cluster every worker reads the one playerbot registry, but a character is
/// held by one worker at a time (its per-character claim). A start that finds it held elsewhere must change nothing: it writes no registry row (the holder's
/// own checkpoint would see the changed revision and drop its healthy bot) and records no fault.
/// </summary>
internal interface IPlayerbotLiveOwnerProbe
{
    /// <summary>The ownership scope of the other worker that holds the character live, or null when none does (this worker's own claim, no claim, a dead holder, or no worker scope).</summary>
    Task<string?> FindAsync(int characterId, CancellationToken cancellationToken);
}

/// <summary>The production probe: the character claim of <see cref="CharacterClaimService"/>, read with this world's ownership token. Null everywhere outside Worker fencing scope.</summary>
internal sealed class ClaimLiveOwnerProbe(IServiceProvider services, WorldRuntime world) : IPlayerbotLiveOwnerProbe
{
    public Task<string?> FindAsync(int characterId, CancellationToken cancellationToken)
        => services.GetService(typeof(CharacterClaimService)) is CharacterClaimService claims
            ? claims.FindLiveOtherOwnerAsync(characterId, world.CharacterOwnership, cancellationToken)
            : Task.FromResult<string?>(null);
}
