using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters;

/// <summary>
/// Character deletion hooks for features that keep live per-character state (caches, write
/// queues, friend lists, groups, guilds …). Implement on an <see cref="Features.IWorldFeature"/>,
/// which is then also registered as an <see cref="ICharacterDeleteHook"/>. Every method has a
/// default that does nothing. All run on the session task; reach world state through
/// <see cref="Game.Maps.WorldRuntime.InvokeAsync{T}"/>.
/// <para>
/// The stored rows are removed by the characters-database modules
/// (<see cref="Data.Characters.ICharacterDataCleanup"/>) in one transaction. These hooks cover the
/// rest, in vmangos HandleCharDeleteOpcode → Player::DeleteFromDB order: refuse
/// (<see cref="CanDeleteCharacterAsync"/>), drain this character's queued writes so none lands
/// after the rows are gone (<see cref="OnCharacterDeletingAsync"/>), then drop the live state once
/// the rows are committed (<see cref="OnCharacterDeletedAsync"/>). See
/// docs/integration/character-delete.md.
/// </para>
/// </summary>
public interface ICharacterDeleteHook
{
    /// <summary>Before anything changes. Return false to refuse (CHAR_DELETE_FAILED); an exception refuses too.</summary>
    Task<bool> CanDeleteCharacterAsync(WorldSession session, CharacterRecord character) => Task.FromResult(true);

    /// <summary>
    /// Every hook allowed the deletion and the rows are about to be removed: wait for this
    /// character's queued writes. An exception refuses the deletion; nothing is removed.
    /// </summary>
    Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => Task.CompletedTask;

    /// <summary>
    /// The rows are gone: drop caches, list entries and memberships that point at it. The
    /// character directory still knows the character (for names in notifications); it forgets it
    /// after the last hook, and a hook may remove it earlier. An exception is logged; the others still run.
    /// <para>
    /// Must be idempotent. When a deletion's outcome was lost, or any hook throws, the deletion
    /// stays pending in the ledger and the finalizers run again (at most once more per session, on
    /// the next character list), including for a character whose rows are already gone.
    /// A hook that queues removals awaits them (bounded by <see cref="CharacterDeletion.DrainTimeout"/>)
    /// so that a pending deletion completes only after they were attempted.
    /// </para>
    /// </summary>
    Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character) => Task.CompletedTask;
}

/// <summary>CMSG_CHAR_DELETE (vmangos WorldSession::HandleCharDeleteOpcode) with the <see cref="ICharacterDeleteHook"/>s.</summary>
public static class CharacterDeletion
{
    /// <summary>Upper bound a hook waits for a queue it just fed to be attempted (post-delete drains).</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    // Only reached by a host that registered a ledger but not the reconciler (none ships): one shared guard.
    private static readonly CharacterDeletionReconciler Fallback = new();

    /// <summary>
    /// Delete <paramref name="rawGuid"/> for the session's account. True means the stored rows are
    /// durably gone and the runtime finalizers ran (or, for a deletion whose acknowledgement was
    /// lost, were re-run now). False (CHAR_DELETE_FAILED) means the character is unknown, owned
    /// by another account, still in the world, refused by a hook, or its deletion was not observed
    /// as committed. When the outcome could not be read back, a deletion that did commit is
    /// finalized by the next <see cref="ReconcilePendingAsync"/> sweep (every character-list
    /// request) or by a retry of this call, so the failure is never final for the live state.
    /// </summary>
    public static async Task<bool> TryDeleteAsync(WorldSession session, ulong rawGuid)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (rawGuid == 0 || rawGuid > int.MaxValue)
        {
            return false;
        }

        int id = (int)rawGuid;
        var guid = ObjectGuid.Player((uint)id);
        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        ICharacterDeletionStore? ledger = session.Services.GetService<ICharacterDeletionStore>();
        ICharacterDeleteHook[] hooks = [.. session.Services.GetServices<ICharacterDeleteHook>()];
        CharacterRecord? character;
        Guid operation = Guid.Empty;
        try
        {
            // A character still in the world (e.g. a lingering previous session) is not deletable.
            if (session.World.IsOnline(guid))
            {
                return false;
            }

            character = await characters.GetByIdAsync(id).ConfigureAwait(false);
            if (character is null)
            {
                // A retry for a deletion that committed but was never finalized (its acknowledgement
                // was lost). The rows are already gone, so the CanDelete hooks and the settlement
                // barriers do not run; ownership is kept by listing only this account's pending rows.
                PendingCharacterDeletion? pending = ledger is null
                    ? null
                    : (await ledger.GetPendingAsync(session.AccountId).ConfigureAwait(false)).FirstOrDefault(p => p.CharacterId == id);
                if (pending is null)
                {
                    return false;
                }

                await FinalizeAsync(session, hooks, Recorded(pending), pending.OperationId).ConfigureAwait(false);
                return true;
            }

            if (character.AccountId != session.AccountId)
            {
                return false;
            }

            foreach (ICharacterDeleteHook hook in hooks)
            {
                if (!await hook.CanDeleteCharacterAsync(session, character).ConfigureAwait(false))
                {
                    session.Logger.LogInformation("[{Endpoint}] {Hook} refused to delete character {Id}",
                        session.RemoteEndpoint, hook.GetType().Name, id);
                    return false;
                }
            }

            // A successful detached settlement can still publish shared caches. Drain its
            // storage and publication before deletion removes rows and invalidates those caches.
            foreach (ICharacterSettlementBarrier barrier in session.Services.GetServices<ICharacterSettlementBarrier>())
            {
                await barrier.WaitForSettlementAsync(id).ConfigureAwait(false);
            }

            // Queued snapshots of this character must not land after its rows are removed.
            if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            {
                await saves.FlushCharacterAsync(id).ConfigureAwait(false);
            }

            foreach (ICharacterDeleteHook hook in hooks)
            {
                await hook.OnCharacterDeletingAsync(session, character).ConfigureAwait(false);
            }

            if (session.World.IsOnline(guid))
            {
                return false;
            }

            if (ledger is null)
            {
                // No durable ledger (an in-memory host): the store's own answer is all there is.
                if (!await characters.DeleteAsync(id, session.AccountId).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else
            {
                operation = Guid.NewGuid();
                if (!await DeleteWithLedgerAsync(session, ledger, operation, id).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] could not delete character {Id}", session.RemoteEndpoint, id);
            return false;
        }

        await FinalizeAsync(session, hooks, character, operation).ConfigureAwait(false);
        session.Logger.LogInformation("[{Endpoint}] '{Account}' deleted character '{Name}'",
            session.RemoteEndpoint, session.AccountName, character.Name);
        return true;
    }

    /// <summary>
    /// Finalize every committed-but-unfinalized deletion of the session's account (the ledger rows
    /// a lost acknowledgement or a failed finalizer left behind). Called before a character list is
    /// answered, so recovery does not depend on the client retrying a delete for a character it no
    /// longer sees. Each operation is retried at most once per session; a character whose id has a
    /// live row again is left alone. Never throws: the character list must not fail because of it.
    /// </summary>
    public static async Task ReconcilePendingAsync(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Services.GetService<ICharacterDeletionStore>() is not { } ledger)
        {
            return;
        }

        try
        {
            IReadOnlyList<PendingCharacterDeletion> pending = await ledger.GetPendingAsync(session.AccountId).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                return;
            }

            ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
            ICharacterDeleteHook[] hooks = [.. session.Services.GetServices<ICharacterDeleteHook>()];
            CharacterDeletionReconciler reconciler = Reconciler(session);
            foreach (PendingCharacterDeletion operation in pending)
            {
                if (session.World.IsOnline(ObjectGuid.Player((uint)operation.CharacterId))
                    || !reconciler.TrySweep(session, operation.OperationId))
                {
                    continue;
                }

                if (await characters.GetByIdAsync(operation.CharacterId).ConfigureAwait(false) is not null)
                {
                    session.Logger.LogWarning(
                        "[{Endpoint}] deletion {Operation} of character {Id} stays pending: the id has a character again",
                        session.RemoteEndpoint, operation.OperationId, operation.CharacterId);
                    continue;
                }

                session.Logger.LogInformation("[{Endpoint}] finalizing the pending deletion of character {Id}",
                    session.RemoteEndpoint, operation.CharacterId);
                await FinalizeAsync(session, hooks, Recorded(operation), operation.OperationId).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] could not reconcile pending character deletions", session.RemoteEndpoint);
        }
    }

    /// <summary>
    /// The durable delete with its operation identity. When the call throws (the commit may have
    /// happened before the exception, as with a lost acknowledgement), the outcome is read back in
    /// a fresh scope: the session's own context and connection may be faulted. A deletion that is
    /// not observed as committed answers false; one that committed anyway stays in the ledger and
    /// is finalized by the sweep or a retry.
    /// </summary>
    private static async Task<bool> DeleteWithLedgerAsync(WorldSession session, ICharacterDeletionStore ledger, Guid operation, int id)
    {
        try
        {
            return await ledger.DeleteAsync(operation, id, session.AccountId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] deleting character {Id} failed; reading the outcome back",
                session.RemoteEndpoint, id);
        }

        try
        {
            await using AsyncServiceScope scope = session.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            ICharacterDeletionStore fresh = scope.ServiceProvider.GetRequiredService<ICharacterDeletionStore>();
            if (await fresh.IsCommittedAsync(operation).ConfigureAwait(false))
            {
                session.Logger.LogWarning("[{Endpoint}] deletion {Operation} of character {Id} committed; finalizing",
                    session.RemoteEndpoint, operation, id);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] could not read back the outcome of deleting character {Id}",
                session.RemoteEndpoint, id);
        }

        return false;
    }

    /// <summary>
    /// The runtime half of a committed deletion: the save queue forgets the character, every hook
    /// drops its live state (a failure is logged and the rest still run), the directory forgets it,
    /// and the ledger row is completed only when every hook succeeded, so the sweep re-runs the
    /// finalizers otherwise. Finalizers must therefore be idempotent. <paramref name="operation"/> is
    /// <see cref="Guid.Empty"/> for a host without a ledger. Never throws.
    /// </summary>
    private static async Task FinalizeAsync(
        WorldSession session, ICharacterDeleteHook[] hooks, CharacterRecord character, Guid operation)
    {
        CharacterDeletionReconciler reconciler = Reconciler(session);
        if (operation != Guid.Empty && !reconciler.TryBegin(operation))
        {
            return; // another session of the account is finalizing this very operation
        }

        try
        {
            int id = character.Id;
            session.Services.GetService<CharacterSaveQueue>()?.ForgetCharacter(id);
            bool clean = true;
            foreach (ICharacterDeleteHook hook in hooks)
            {
                try
                {
                    await hook.OnCharacterDeletedAsync(session, character).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    clean = false;
                    session.Logger.LogError(ex, "[{Endpoint}] {Hook} failed to clean up deleted character {Id}",
                        session.RemoteEndpoint, hook.GetType().Name, id);
                }
            }

            session.Services.GetRequiredService<CharacterDirectory>().Remove(id);
            if (operation == Guid.Empty)
            {
                return;
            }

            if (!clean)
            {
                session.Logger.LogWarning("[{Endpoint}] deletion {Operation} of character {Id} stays pending after a failed finalizer",
                    session.RemoteEndpoint, operation, id);
                return;
            }

            try
            {
                await using AsyncServiceScope scope = session.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ICharacterDeletionStore>().CompleteAsync(operation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                session.Logger.LogError(ex, "[{Endpoint}] could not complete deletion {Operation} of character {Id}; it stays pending",
                    session.RemoteEndpoint, operation, id);
            }
        }
        finally
        {
            if (operation != Guid.Empty)
            {
                reconciler.End(operation);
            }
        }
    }

    // Every hook uses only the character's id (and, for names in notifications, the directory entry).
    private static CharacterRecord Recorded(PendingCharacterDeletion pending)
        => new() { Id = pending.CharacterId, AccountId = pending.AccountId, Name = pending.Name };

    private static CharacterDeletionReconciler Reconciler(WorldSession session)
        => session.Services.GetService<CharacterDeletionReconciler>() ?? Fallback;
}
