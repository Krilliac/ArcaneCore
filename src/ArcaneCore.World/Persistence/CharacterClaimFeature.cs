using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// Releases a character's per-character claim when the character leaves the world (discovered <see cref="IWorldFeature"/>; design _wf/writer-lock-DESIGN.md section 1b).
/// <see cref="WorldRuntime.PlayerLoggingOut"/> is raised before the logout snapshot is queued, so the release is posted to the world thread: it runs after <c>RemovePlayer</c>
/// finished, and <see cref="CharacterClaimService"/> then waits for the character's persistence barrier before deleting the claim. A player retired after a committed transfer
/// is skipped: its claim already moved to the target scope with the commit. Inert in Realm scope (the service ignores tokens that are not worker-shard tokens).
/// </summary>
public sealed class CharacterClaimFeature(IServiceProvider services) : IWorldFeature
{
    /// <inheritdoc />
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.PlayerLoggingOut += player =>
        {
            if (services.GetService<CharacterClaimService>() is not { } claims)
                return;
            int characterId = (int)player.Guid.Low;
            long ticket = claims.TicketOf(player); // the claim ticket of the login that put this player into the world
            if (world.IsRetiringTransferredPlayer)
            {
                claims.AbandonTicket(characterId, ticket); // the claim moved with the commit: nothing to release, and the ticket stops counting
                return;
            }

            CharacterOwnershipToken? token = player.CharacterOwnership ?? world.CharacterOwnership;
            // The release carries this login's ticket: a login that claims the character after this logout (a quick relogin that claimed before the posted release runs) invalidates it.
            world.Post(() => claims.ScheduleRelease(characterId, token, () => world.IsOnline(ObjectGuid.Player((uint)characterId)), ticket));
        };
    }
}
