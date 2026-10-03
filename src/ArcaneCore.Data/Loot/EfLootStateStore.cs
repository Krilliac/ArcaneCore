using System.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Loot;

/// <summary>
/// EF Core implementation of <see cref="ILootStateStore"/>. A commit is one serializable local
/// transaction in a dedicated context, in the style of <see cref="EfEconomyStore"/>: the ledger row
/// makes a retry observe AlreadyCommitted; the logical instance row must exist; the stored chest
/// must equal Expected; Updated must be the exact legal successor of Expected for the awards
/// (<see cref="LootStateRules"/>); and each participant's durable money and complete inventory must
/// equal its Before snapshot while its After differs by exactly the awards. Any mismatch rolls back
/// with a refusal result; any exception rolls back and propagates.
/// </summary>
public sealed class EfLootStateStore(CharacterDbContext db) : ILootStateStore
{
    public async Task<LootCommitResult> CommitAsync(LootCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (db.ChangeTracker.Entries().Any()
            || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Loot commits require a dedicated context without tracked caller state or a caller transaction.");
        }

        // Detach caller-owned lists before the first await.
        request = Freeze(request);
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            string key = request.OperationId.ToString("D");
            if (await db.Set<LootOperationRow>().AsNoTracking().AnyAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false))
            {
                return LootCommitResult.AlreadyCommitted;
            }

            int instanceId = checked((int)request.Key.InstanceId);
            if (!await db.Set<InstanceRow>().AsNoTracking().AnyAsync(i => i.Id == instanceId, cancellationToken).ConfigureAwait(false))
            {
                return LootCommitResult.ScopeMissing;
            }

            if (!LootStateRules.IsLegalSuccessor(request.Expected, request.Updated, request.Awards)
                || (request.Participants.Count > 0 || request.Awards.Count > 0)
                && !LootStateRules.AwardsMatchParticipants(request.Awards, request.Participants))
            {
                return LootCommitResult.InvalidTransition;
            }

            LootStateRecord? stored = await ReadAsync(request.Key, cancellationToken).ConfigureAwait(false);
            if (!Equals(stored, request.Expected))
            {
                return LootCommitResult.Conflict;
            }

            foreach (EconomyParticipant participant in request.Participants)
            {
                int id = participant.Before.Id;
                CharacterRecord? character = await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                    .ConfigureAwait(false);
                if (character is null)
                {
                    return LootCommitResult.CharacterMissing;
                }

                if (character.Money != participant.Before.Money)
                {
                    return LootCommitResult.Conflict;
                }

                IReadOnlyList<InventoryItemData> current = await new EfItemStore(db).GetInventoryAsync(id, cancellationToken).ConfigureAwait(false);
                if (!EconomyRequestValidation.SameInventory(current, participant.Before.Inventory!.Items))
                {
                    return LootCommitResult.Conflict;
                }
            }

            if (request.Participants.Count > 0)
            {
                List<uint> created = [.. EconomyRequestValidation.NewItemGuids(new EconomyCommitRequest(request.OperationId, request.Participants, []))];
                if (created.Count > 0
                    && await db.Set<ItemInstanceRow>().AsNoTracking().AnyAsync(r => created.Contains(r.Guid), cancellationToken).ConfigureAwait(false))
                {
                    return LootCommitResult.Conflict;
                }
            }

            await DeleteStateAsync(request.Key, cancellationToken).ConfigureAwait(false);
            StageState(request.Updated);
            var characters = new EfCharacterStore(db);
            foreach (EconomyParticipant participant in request.Participants)
            {
                if (!await characters.StageStateAsync(participant.After, cancellationToken).ConfigureAwait(false))
                {
                    return LootCommitResult.CharacterMissing;
                }
            }

            db.Add(new LootOperationRow { Id = key, CommittedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return LootCommitResult.Committed;
        }
        catch (Exception commitError)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Loot commit and rollback failed; discard the context.", commitError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        string key = operationId.ToString("D");
        return await db.Set<LootOperationRow>().AsNoTracking().AnyAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LootStateRecord>> LoadInstanceStatesAsync(CancellationToken cancellationToken = default)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            // A reused instance id (after a restart the counter resumes above the highest stored
            // instance) must not inherit chests of a deleted instance whose rows survived a lost
            // delete or a delete that raced a commit.
            await db.Set<LootStatePlayerRow>().Where(r => !db.Set<InstanceRow>().Any(i => i.Id == r.InstanceId)
                    || !db.Set<LootStateRow>().Any(h => h.InstanceId == r.InstanceId && h.SpawnGuid == r.SpawnGuid))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<LootStateItemRow>().Where(r => !db.Set<InstanceRow>().Any(i => i.Id == r.InstanceId)
                    || !db.Set<LootStateRow>().Any(h => h.InstanceId == r.InstanceId && h.SpawnGuid == r.SpawnGuid))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Set<LootStateRow>().Where(r => !db.Set<InstanceRow>().Any(i => i.Id == r.InstanceId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        List<LootStateRow> headers = await db.Set<LootStateRow>().AsNoTracking().OrderBy(r => r.InstanceId).ThenBy(r => r.SpawnGuid)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<LootStateItemRow> items = await db.Set<LootStateItemRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<LootStatePlayerRow> players = await db.Set<LootStatePlayerRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. headers.Select(h => Assemble(h,
            items.Where(i => i.InstanceId == h.InstanceId && i.SpawnGuid == h.SpawnGuid),
            players.Where(p => p.InstanceId == h.InstanceId && p.SpawnGuid == h.SpawnGuid)))];
    }

    /// <summary>The stored chest, or null when no state exists.</summary>
    private async Task<LootStateRecord?> ReadAsync(LootStateKey key, CancellationToken cancellationToken)
    {
        int instanceId = checked((int)key.InstanceId);
        uint spawn = key.SpawnGuid;
        LootStateRow? header = await db.Set<LootStateRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.InstanceId == instanceId && r.SpawnGuid == spawn, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        List<LootStateItemRow> items = await db.Set<LootStateItemRow>().AsNoTracking()
            .Where(r => r.InstanceId == instanceId && r.SpawnGuid == spawn).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<LootStatePlayerRow> players = await db.Set<LootStatePlayerRow>().AsNoTracking()
            .Where(r => r.InstanceId == instanceId && r.SpawnGuid == spawn).ToListAsync(cancellationToken).ConfigureAwait(false);
        return Assemble(header, items, players);
    }

    private static LootStateRecord Assemble(LootStateRow header, IEnumerable<LootStateItemRow> items, IEnumerable<LootStatePlayerRow> players)
    {
        LootStatePlayerRow[] named = [.. players];
        return new LootStateRecord(
            new LootStateKey((uint)header.InstanceId, header.SpawnGuid), header.SourceEntry, header.LootOwner, header.Generation,
            header.Consumed, header.RespawnAt,
            [.. named.Where(p => p.Role == LootStatePlayerRow.RoleRecipient).Select(p => p.CharacterId).Order()],
            [.. items.OrderBy(i => i.Slot).Select(i => new LootStateItem(i.Slot, i.ItemId, i.Count, i.IsQuest, i.IsPerPlayer, i.IsLooted,
                [.. named.Where(p => p.Slot == i.Slot && p.Role == LootStatePlayerRow.RoleAllowed).Select(p => p.CharacterId).Order()],
                [.. named.Where(p => p.Slot == i.Slot && p.Role == LootStatePlayerRow.RoleLootedBy).Select(p => p.CharacterId).Order()]))]);
    }

    private async Task DeleteStateAsync(LootStateKey key, CancellationToken cancellationToken)
    {
        int instanceId = checked((int)key.InstanceId);
        uint spawn = key.SpawnGuid;
        await db.Set<LootStatePlayerRow>().Where(r => r.InstanceId == instanceId && r.SpawnGuid == spawn)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<LootStateItemRow>().Where(r => r.InstanceId == instanceId && r.SpawnGuid == spawn)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<LootStateRow>().Where(r => r.InstanceId == instanceId && r.SpawnGuid == spawn)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private void StageState(LootStateRecord record)
    {
        int instanceId = checked((int)record.Key.InstanceId);
        uint spawn = record.Key.SpawnGuid;
        db.Add(new LootStateRow
        {
            InstanceId = instanceId, SpawnGuid = spawn, SourceEntry = record.SourceEntry, LootOwner = record.LootOwnerCharacterId,
            Generation = record.Generation, Consumed = record.Consumed, RespawnAt = record.RespawnAtUnix,
        });
        foreach (int recipient in record.Recipients.Distinct())
        {
            db.Add(Player(instanceId, spawn, LootStatePlayerRow.RecipientSlot, recipient, LootStatePlayerRow.RoleRecipient));
        }

        foreach (LootStateItem item in record.Items)
        {
            db.Add(new LootStateItemRow
            {
                InstanceId = instanceId, SpawnGuid = spawn, Slot = item.Slot, ItemId = item.ItemId, Count = item.Count,
                IsQuest = item.IsQuest, IsPerPlayer = item.IsPerPlayer, IsLooted = item.IsLooted,
            });
            foreach (int character in item.AllowedLooters.Distinct())
            {
                db.Add(Player(instanceId, spawn, item.Slot, character, LootStatePlayerRow.RoleAllowed));
            }

            foreach (int character in item.LootedBy.Distinct())
            {
                db.Add(Player(instanceId, spawn, item.Slot, character, LootStatePlayerRow.RoleLootedBy));
            }
        }
    }

    private static LootStatePlayerRow Player(int instanceId, uint spawn, byte slot, int character, byte role)
        => new() { InstanceId = instanceId, SpawnGuid = spawn, Slot = slot, CharacterId = character, Role = role };

    private static LootCommitRequest Freeze(LootCommitRequest request)
    {
        EconomyCommitRequest participants = EconomyRequestValidation.Freeze(
            new EconomyCommitRequest(request.OperationId, request.Participants, []));
        return request with { Participants = participants.Participants, Awards = [.. request.Awards] };
    }

    private static void Validate(LootCommitRequest request)
    {
        if (request.OperationId == Guid.Empty || request.Key.InstanceId == 0 || request.Updated.Key != request.Key
            || (request.Expected is { } expected && expected.Key != request.Key))
        {
            throw new ArgumentException("A loot operation requires an identity and a state of the key it commits.", nameof(request));
        }

        if (request.Participants.Count > 0)
        {
            EconomyRequestValidation.Validate(new EconomyCommitRequest(request.OperationId, request.Participants, []));
        }
    }
}
