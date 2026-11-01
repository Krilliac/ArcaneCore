using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>
/// The arrival of a committed transfer whose client is gone (design _wf/handoff-control-DESIGN.md section 6: activation with no client must return without throwing).
/// After the commit the transfer must complete, but without the client's session the target cannot build the player. The character arrives offline instead: its
/// stored position becomes the transfer's target, written under the target's ownership token, so its next login enters the world where the transfer took it,
/// on the worker that now owns it. May run on any thread.
/// </summary>
public interface ITransferOfflineArrival
{
    /// <summary>Writes the arrival of <paramref name="bundle"/>'s character under <paramref name="ownershipToken"/> (the token the target writes it with). Throws when the write is refused or fails; the caller retries.</summary>
    Task CompleteAsync(PlayerTransferBundle bundle, CharacterOwnershipToken ownershipToken, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="ITransferOfflineArrival"/>: rewrites the character row's map and position through <see cref="ICharacterStore.SaveStateAsync"/>, every other stored
/// field written back unchanged (the source's settlement save, durable before the bundle was captured, is the character's last state). The transport seat follows the
/// target's own rule (<see cref="WorkerTransferTarget.ApplyTransportIntent"/>): kept only when the teleport stayed aboard. Life, inventory, action bar and bind point are
/// left as stored (null in the state).
/// </summary>
public sealed class CharacterStoreOfflineArrival(IServiceScopeFactory scopes) : ITransferOfflineArrival
{
    public async Task CompleteAsync(PlayerTransferBundle bundle, CharacterOwnershipToken ownershipToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(ownershipToken);
        int characterId = checked((int)new ObjectGuid(bundle.CharacterGuid).Low);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ICharacterStore characters = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
        CharacterRecord character = await characters.GetByIdAsync(characterId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"character {characterId} does not exist");
        if (character.AccountId != bundle.AccountId)
        {
            throw new InvalidOperationException($"character {characterId} is not owned by account {bundle.AccountId}");
        }

        TransferPosition at = bundle.TargetPosition;
        TransportSeat? seat = bundle.NotLeaveTransport && character.TransportGuid != 0
            ? new TransportSeat(character.TransportGuid, character.TransportX, character.TransportY, character.TransportZ, character.TransportOrientation)
            : null;
        await characters.SaveStateAsync(new CharacterState(
            character.Id,
            bundle.TargetMapId,
            character.ZoneId,
            at.X,
            at.Y,
            at.Z,
            at.Orientation,
            character.Level,
            character.PlayedTime,
            character.LevelPlayedTime,
            character.Money,
            character.ActionBarToggles,
            ActionButtons: null,
            Home: null,
            Inventory: null,
            Life: null,
            character.BankBagSlotCount,
            seat,
            character.Drunk,
            ownershipToken,
            character.CharacterFlags), cancellationToken).ConfigureAwait(false);
    }
}
