using System.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Economy;

/// <summary>
/// EF Core implementation of <see cref="IEconomyStore"/>. A commit is one serializable local
/// transaction in a dedicated context, in the style of <see cref="Quests.EfCharacterQuestRewardStore"/>:
/// the ledger row makes a retry observe AlreadyCommitted; every participant's durable money and
/// complete inventory must equal its Before snapshot; every row change checks its expected
/// value; and a final integrity pass proves that each touched item exists exactly once
/// (in one inventory, or escrowed under exactly one letter or auction). Any mismatch rolls
/// back with Conflict; any exception rolls back and propagates.
/// </summary>
public sealed class EfEconomyStore(CharacterDbContext db) : IEconomyStore
{
    public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (db.ChangeTracker.Entries().Any()
            || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Economy commits require a dedicated context without tracked caller state or a caller transaction.");
        }

        // Detach caller-owned lists before the first await.
        request = EconomyRequestValidation.Freeze(request);
        EconomyRequestValidation.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            string key = request.OperationId.ToString("D");
            if (await db.Set<EconomyOperationRow>().AsNoTracking().AnyAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false))
            {
                return EconomyCommitResult.AlreadyCommitted;
            }

            foreach (EconomyParticipant participant in request.Participants)
            {
                int id = participant.Before.Id;
                CharacterRecord? character = await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                    .ConfigureAwait(false);
                if (character is null)
                {
                    return EconomyCommitResult.CharacterMissing;
                }

                if (character.Money != participant.Before.Money)
                {
                    return EconomyCommitResult.Conflict;
                }

                if (participant.Before.Life is { } beforeLife
                    && !await LifeMatchesAsync(id, beforeLife, cancellationToken).ConfigureAwait(false))
                {
                    return EconomyCommitResult.Conflict;
                }

                IReadOnlyList<InventoryItemData> stored = await new EfItemStore(db).GetInventoryAsync(id, cancellationToken).ConfigureAwait(false);
                if (!EconomyRequestValidation.SameInventory(stored, participant.Before.Inventory!.Items))
                {
                    return EconomyCommitResult.Conflict;
                }
            }

            List<uint> created = [.. EconomyRequestValidation.NewItemGuids(request)];
            if (created.Count > 0
                && await db.Set<ItemInstanceRow>().AsNoTracking().AnyAsync(r => created.Contains(r.Guid), cancellationToken).ConfigureAwait(false))
            {
                return EconomyCommitResult.Conflict;
            }

            var touched = new HashSet<uint>();
            var escrowed = new List<ItemInstanceData>();
            foreach (EconomyChange change in request.Changes)
            {
                if (!await StageChangeAsync(change, request, touched, escrowed, cancellationToken).ConfigureAwait(false))
                {
                    return EconomyCommitResult.Conflict;
                }
            }

            var store = new EfCharacterStore(db);
            foreach (EconomyParticipant participant in request.Participants)
            {
                if (!await store.StageStateAsync(participant.After, cancellationToken).ConfigureAwait(false))
                {
                    return EconomyCommitResult.CharacterMissing;
                }

                touched.UnionWith(participant.Before.Inventory!.Items.Select(i => i.Item.Guid)
                    .Except(participant.After.Inventory!.Items.Select(i => i.Item.Guid)));
                touched.UnionWith(participant.After.Inventory!.Items.Select(i => i.Item.Guid)
                    .Except(participant.Before.Inventory!.Items.Select(i => i.Item.Guid)));
            }

            // Each participant's diff removes rows it no longer owns. In a swap, a row the other
            // participant now holds may have been staged for deletion by its previous owner's
            // diff; any row kept by some After inventory survives (its values are the new owner's).
            HashSet<uint> kept = [.. request.Participants.SelectMany(p => p.After.Inventory!.Items.Select(i => i.Item.Guid))];
            foreach (var entry in db.ChangeTracker.Entries<ItemInstanceRow>()
                .Where(e => e.State == EntityState.Deleted && kept.Contains(e.Entity.Guid)).ToList())
            {
                entry.State = EntityState.Modified;
            }

            foreach (var entry in db.ChangeTracker.Entries<CharacterInventoryRow>()
                .Where(e => e.State == EntityState.Deleted && kept.Contains(e.Entity.ItemGuid)).ToList())
            {
                entry.State = EntityState.Modified;
            }

            // The inventory diff removed an escrowed item's row with its slot; it stays as an ownerless item.
            foreach (ItemInstanceData item in escrowed)
            {
                ItemInstanceRow row = db.ChangeTracker.Entries<ItemInstanceRow>().Select(e => e.Entity).SingleOrDefault(r => r.Guid == item.Guid)
                    ?? throw new InvalidOperationException($"escrowed item {item.Guid} was not staged");
                if (db.Entry(row).State == EntityState.Deleted)
                {
                    db.Entry(row).State = EntityState.Modified;
                }

                row.CopyFrom(0, item);
            }

            db.Add(new EconomyOperationRow { Id = key, CommittedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // The integrity pass reads this transaction's own writes.
            if (!await ItemsAreUniqueAsync(touched, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return EconomyCommitResult.Conflict;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return EconomyCommitResult.Committed;
        }
        catch (Exception commitError)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Economy commit and rollback failed; discard the context.", commitError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<bool> LifeMatchesAsync(int characterId, CharacterLife expected, CancellationToken cancellationToken)
    {
        CharacterVitalsRow? vitals = await db.Set<CharacterVitalsRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (vitals is null || vitals.Health != expected.Health || vitals.Xp != expected.Xp
            || vitals.DeathExpireTime != expected.DeathExpireUnix || vitals.IsGhost != expected.IsGhost
            || expected.Powers.Count != 5
            || vitals.Power1 != expected.Powers[0] || vitals.Power2 != expected.Powers[1]
            || vitals.Power3 != expected.Powers[2] || vitals.Power4 != expected.Powers[3]
            || vitals.Power5 != expected.Powers[4])
        {
            return false;
        }

        CharacterCorpseRow? corpse = await db.Set<CharacterCorpseRow>().AsNoTracking()
            .FirstOrDefaultAsync(row => row.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (expected.Corpse is not { } wanted)
        {
            return corpse is null;
        }

        return corpse is not null && corpse.MapId == wanted.MapId
            && corpse.X == wanted.X && corpse.Y == wanted.Y && corpse.Z == wanted.Z
            && corpse.Orientation == wanted.Orientation && corpse.GhostTime == wanted.GhostTimeUnix
            && corpse.Type == wanted.Type;
    }

    public async Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        string key = operationId.ToString("D");
        return await db.Set<EconomyOperationRow>().AsNoTracking().AnyAsync(r => r.Id == key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default)
        => (await db.Set<MailRow>().AsNoTracking().Where(r => r.ReceiverId == receiverId).OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToRecord()).ToList();

    public async Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default)
        => (await db.Set<MailRow>().AsNoTracking().Where(r => r.ExpireTime <= now).OrderBy(r => r.ExpireTime).ThenBy(r => r.Id)
            .Take(max).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToRecord()).ToList();

    public async Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default)
    {
        uint sender = checked((uint)characterId);
        const byte normal = (byte)MailMessageType.Normal;
        return (await db.Set<MailRow>().AsNoTracking()
                .Where(r => r.ReceiverId == characterId || (r.MessageType == normal && r.SenderId == sender))
                .OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => r.ToRecord()).ToList();
    }

    public async Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default)
        => (await db.Set<ItemTextRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == itemTextId, cancellationToken).ConfigureAwait(false))?.Text;

    public async Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
        => (await db.Set<AuctionRow>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => r.ToRecord()).ToList();

    public async Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(
        IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default)
    {
        List<uint> guids = [.. itemGuids];
        return await db.Set<ItemInstanceRow>().AsNoTracking().Where(r => r.OwnerGuid == 0 && guids.Contains(r.Guid))
            .ToDictionaryAsync(r => r.Guid, r => r.ToData(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
    {
        if (db.ChangeTracker.Entries().Any()
            || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Auction snapshots require a dedicated context without tracked caller state or a caller transaction.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool sqlite = db.Database.IsSqlite();
        IDbContextTransaction transaction = await BeginSnapshotAsync(sqlite, cancellationToken).ConfigureAwait(false);
        try
        {
            IQueryable<AuctionRow> query = db.Set<AuctionRow>().AsNoTracking();
            if (filter.AuctionId is { } auctionId)
            {
                query = query.Where(r => r.Id == auctionId);
            }

            if (filter.SellerId is { } sellerId)
            {
                query = query.Where(r => r.SellerId == sellerId);
            }

            AuctionRecord[] auctions = [.. (await query.OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToRecord())];
            IReadOnlyDictionary<uint, ItemInstanceData> escrow = await GetEscrowItemsAsync([.. auctions.Select(a => a.ItemGuid)], cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AuctionSnapshot(auctions, escrow);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            {
                // The original failure propagates; the dedicated context is discarded by its scope.
            }

            throw;
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            if (sqlite)
            {
                // Opened by BeginSnapshotAsync: EF does not close a connection it was asked to keep open.
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }

            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// A read-only transaction that sees one snapshot. MariaDB/MySQL/PostgreSQL: repeatable read
    /// (an MVCC snapshot from the first statement, no locking reads). SQLite: Microsoft.Data.Sqlite
    /// promotes repeatable read to serializable (BEGIN IMMEDIATE, the write lock), which would make
    /// a pure read contend with every commit, so a deferred transaction is opened instead: its
    /// SHARED lock already holds the file steady across the two statements.
    /// </summary>
    private async Task<IDbContextTransaction> BeginSnapshotAsync(bool sqlite, CancellationToken cancellationToken)
    {
        if (!sqlite)
        {
            return await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        }

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            return await db.Database.UseTransactionAsync(connection.BeginTransaction(deferred: true), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the SQLite snapshot transaction was not attached");
        }
        catch
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => new(
        await db.Set<MailRow>().MaxAsync(r => (uint?)r.Id, cancellationToken).ConfigureAwait(false) ?? 0,
        await db.Set<AuctionRow>().MaxAsync(r => (uint?)r.Id, cancellationToken).ConfigureAwait(false) ?? 0,
        await db.Set<ItemTextRow>().MaxAsync(r => (uint?)r.Id, cancellationToken).ConfigureAwait(false) ?? 0);

    private async Task<bool> StageChangeAsync(EconomyChange change, EconomyCommitRequest request, HashSet<uint> touched,
        List<ItemInstanceData> escrowed, CancellationToken cancellationToken)
    {
        switch (change)
        {
            case EscrowFromInventory escrow:
            {
                EconomyParticipant? owner = request.Participants.SingleOrDefault(p => p.Before.Id == escrow.CharacterId);
                uint guid = escrow.Item.Guid;
                if (owner is null
                    || !owner.Before.Inventory!.Items.Any(i => i.Item.Guid == guid)
                    || owner.After.Inventory!.Items.Any(i => i.Item.Guid == guid))
                {
                    return false;
                }

                // Load and track the row so the inventory diff resolves the same instance.
                ItemInstanceRow? row = await db.Set<ItemInstanceRow>().FirstOrDefaultAsync(r => r.Guid == guid, cancellationToken).ConfigureAwait(false);
                if (row is null || row.OwnerGuid != escrow.CharacterId)
                {
                    return false;
                }

                escrowed.Add(escrow.Item);
                touched.Add(guid);
                return true;
            }

            case ReleaseFromEscrow release:
            {
                EconomyParticipant? owner = request.Participants.SingleOrDefault(p => p.Before.Id == release.CharacterId);
                if (owner is null
                    || owner.Before.Inventory!.Items.Any(i => i.Item.Guid == release.ItemGuid)
                    || !owner.After.Inventory!.Items.Any(i => i.Item.Guid == release.ItemGuid))
                {
                    return false;
                }

                ItemInstanceRow? row = await db.Set<ItemInstanceRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Guid == release.ItemGuid, cancellationToken)
                    .ConfigureAwait(false);
                touched.Add(release.ItemGuid);
                return row is { OwnerGuid: 0 };
            }

            case DeleteEscrowItem delete:
            {
                ItemInstanceRow? row = await db.Set<ItemInstanceRow>().FirstOrDefaultAsync(r => r.Guid == delete.ItemGuid, cancellationToken)
                    .ConfigureAwait(false);
                if (row is not { OwnerGuid: 0 })
                {
                    return false;
                }

                await ItemLootPersistence.StageReplaceAsync(db, [], [row.Guid], cancellationToken).ConfigureAwait(false);
                db.Remove(row);
                touched.Add(delete.ItemGuid);
                return true;
            }

            case InsertMail insert:
            {
                MailRecord mail = insert.Mail;
                if (mail.Id == 0
                    || await db.Set<MailRow>().AsNoTracking().AnyAsync(r => r.Id == mail.Id, cancellationToken).ConfigureAwait(false)
                    || !await db.Characters.AsNoTracking().AnyAsync(c => c.Id == mail.ReceiverId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (insert.Body is { } body)
                {
                    if (mail.ItemTextId == 0
                        || await db.Set<ItemTextRow>().AsNoTracking().AnyAsync(r => r.Id == mail.ItemTextId, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }

                    db.Add(new ItemTextRow { Id = mail.ItemTextId, Text = body });
                }

                var row = new MailRow();
                row.CopyFrom(mail);
                db.Add(row);
                if (mail.HasItem)
                {
                    touched.Add(mail.ItemGuid);
                }

                return true;
            }

            case UpdateMail update:
            {
                if (update.Expected.Id != update.Updated.Id)
                {
                    return false;
                }

                MailRow? row = await db.Set<MailRow>().FirstOrDefaultAsync(r => r.Id == update.Expected.Id, cancellationToken).ConfigureAwait(false);
                if (row is null || row.ToRecord() != update.Expected)
                {
                    return false;
                }

                row.CopyFrom(update.Updated);
                if (update.Expected.HasItem)
                {
                    touched.Add(update.Expected.ItemGuid);
                }

                if (update.Updated.HasItem)
                {
                    touched.Add(update.Updated.ItemGuid);
                }

                return true;
            }

            case DeleteMail delete:
            {
                MailRow? row = await db.Set<MailRow>().FirstOrDefaultAsync(r => r.Id == delete.Expected.Id, cancellationToken).ConfigureAwait(false);
                if (row is null || row.ToRecord() != delete.Expected)
                {
                    return false;
                }

                db.Remove(row);
                // The text dies with its last holder: kept when copied into a letter item or
                // carried over to another letter of this operation (a return to sender).
                uint textId = delete.Expected.ItemTextId;
                if (textId != 0
                    && !request.Changes.OfType<InsertMail>().Any(i => i.Mail.ItemTextId == textId)
                    && !request.Changes.OfType<UpdateMail>().Any(u => u.Updated.ItemTextId == textId)
                    && !await db.Set<MailRow>().AsNoTracking().AnyAsync(r => r.ItemTextId == textId && r.Id != delete.Expected.Id, cancellationToken).ConfigureAwait(false)
                    && !await db.Set<ItemInstanceRow>().AsNoTracking().AnyAsync(r => r.Text == textId, cancellationToken).ConfigureAwait(false)
                    && await db.Set<ItemTextRow>().FirstOrDefaultAsync(r => r.Id == textId, cancellationToken).ConfigureAwait(false) is { } text)
                {
                    db.Remove(text);
                }

                if (delete.Expected.HasItem)
                {
                    touched.Add(delete.Expected.ItemGuid);
                }

                return true;
            }

            case InsertAuction insert:
            {
                AuctionRecord auction = insert.Auction;
                if (auction.Id == 0 || auction.ItemGuid == 0
                    || await db.Set<AuctionRow>().AsNoTracking().AnyAsync(r => r.Id == auction.Id, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                var row = new AuctionRow();
                row.CopyFrom(auction);
                db.Add(row);
                touched.Add(auction.ItemGuid);
                return true;
            }

            case UpdateAuction update:
            {
                if (update.Expected.Id != update.Updated.Id)
                {
                    return false;
                }

                AuctionRow? row = await db.Set<AuctionRow>().FirstOrDefaultAsync(r => r.Id == update.Expected.Id, cancellationToken).ConfigureAwait(false);
                if (row is null || row.ToRecord() != update.Expected)
                {
                    return false;
                }

                row.CopyFrom(update.Updated);
                touched.Add(update.Expected.ItemGuid);
                touched.Add(update.Updated.ItemGuid);
                return true;
            }

            case DeleteAuction delete:
            {
                AuctionRow? row = await db.Set<AuctionRow>().FirstOrDefaultAsync(r => r.Id == delete.Expected.Id, cancellationToken).ConfigureAwait(false);
                if (row is null || row.ToRecord() != delete.Expected)
                {
                    return false;
                }

                db.Remove(row);
                touched.Add(delete.Expected.ItemGuid);
                return true;
            }

            default:
                throw new ArgumentException($"unsupported economy change {change.GetType().Name}", nameof(request));
        }
    }

    /// <summary>
    /// Each touched item exists exactly once: absent with no reference; owned by a character
    /// with no letter/auction reference; or ownerless with exactly one reference and no slot.
    /// </summary>
    private async Task<bool> ItemsAreUniqueAsync(HashSet<uint> touched, CancellationToken cancellationToken)
    {
        if (touched.Count == 0)
        {
            return true;
        }

        List<uint> guids = [.. touched];
        Dictionary<uint, int> owners = await db.Set<ItemInstanceRow>().AsNoTracking().Where(r => guids.Contains(r.Guid))
            .ToDictionaryAsync(r => r.Guid, r => r.OwnerGuid, cancellationToken).ConfigureAwait(false);
        List<uint> mailRefs = await db.Set<MailRow>().AsNoTracking().Where(r => guids.Contains(r.ItemGuid))
            .Select(r => r.ItemGuid).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<uint> auctionRefs = await db.Set<AuctionRow>().AsNoTracking().Where(r => guids.Contains(r.ItemGuid))
            .Select(r => r.ItemGuid).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<uint> slotted = [.. await db.Set<CharacterInventoryRow>().AsNoTracking().Where(r => guids.Contains(r.ItemGuid))
            .Select(r => r.ItemGuid).ToListAsync(cancellationToken).ConfigureAwait(false)];
        foreach (uint guid in guids)
        {
            int references = mailRefs.Count(g => g == guid) + auctionRefs.Count(g => g == guid);
            bool valid = owners.TryGetValue(guid, out int owner)
                ? owner == 0 ? references == 1 && !slotted.Contains(guid) : references == 0 && slotted.Contains(guid)
                : references == 0 && !slotted.Contains(guid);
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Request copying and shape checks shared by economy stores.</summary>
public static class EconomyRequestValidation
{
    public static EconomyCommitRequest Freeze(EconomyCommitRequest request) => request with
    {
        Participants = request.Participants.Select(p => new EconomyParticipant(Copy(p.Before), Copy(p.After),
            p.ConsumedItemGuids is null ? null : p.ConsumedItemGuids.ToArray())).ToArray(),
        Changes = request.Changes.Select(c => c switch
        {
            EscrowFromInventory e => e with { Item = CopyItem(e.Item) },
            _ => c,
        }).ToArray(),
    };

    public static void Validate(EconomyCommitRequest request)
    {
        if (request.OperationId == Guid.Empty)
        {
            throw new ArgumentException("An economy operation requires an identity.", nameof(request));
        }

        var ids = new HashSet<int>();
        foreach (EconomyParticipant participant in request.Participants)
        {
            if (participant.Before.Id <= 0 || participant.After.Id != participant.Before.Id || !ids.Add(participant.Before.Id))
            {
                throw new ArgumentException("Participants must be distinct characters with matching Before/After identities.", nameof(request));
            }

            ValidateInventory(participant.Before.Inventory);
            ValidateInventory(participant.After.Inventory);
            if (participant.ConsumedItemGuids is { } consumed)
            {
                if (consumed.Any(guid => guid == 0) || consumed.Distinct().Count() != consumed.Count
                    || consumed.Any(guid => !participant.Before.Inventory!.Items.Any(item => item.Item.Guid == guid)
                        || participant.After.Inventory!.Items.Any(item => item.Item.Guid == guid)
                        || request.Participants.Any(other => other.Before.Id != participant.Before.Id
                            && other.After.Inventory!.Items.Any(item => item.Item.Guid == guid))
                        || request.Changes.OfType<EscrowFromInventory>().Any(change => change.Item.Guid == guid)))
                {
                    throw new ArgumentException("Consumed item GUIDs must be distinct, owned in Before, absent in After, and not handed to another participant or escrow.", nameof(request));
                }
            }
        }

        if (request.Changes.Count == 0 && request.Participants.Count == 0)
        {
            throw new ArgumentException("An economy operation must change something.", nameof(request));
        }

        List<uint> held = [.. request.Participants.SelectMany(p => p.After.Inventory!.Items.Select(i => i.Item.Guid))];
        if (held.Distinct().Count() != held.Count)
        {
            throw new ArgumentException("An item cannot end in two inventories.", nameof(request));
        }

        // Conservation: an item leaves an inventory only to another participant or into escrow,
        // and enters one only from another participant, from escrow, or as a brand-new instance
        // (checked against storage by NewItemGuids).
        foreach (EconomyParticipant participant in request.Participants)
        {
            int id = participant.Before.Id;
            foreach (uint guid in Lost(participant))
            {
                bool handedOver = request.Participants.Any(o => o.Before.Id != id && Gained(o).Contains(guid));
                bool escrowed = request.Changes.OfType<EscrowFromInventory>().Any(e => e.CharacterId == id && e.Item.Guid == guid);
                bool consumed = participant.ConsumedItemGuids?.Contains(guid) == true;
                if ((handedOver ? 1 : 0) + (escrowed ? 1 : 0) + (consumed ? 1 : 0) != 1)
                {
                    throw new ArgumentException($"item {guid} leaves character {id} without exactly one destination.", nameof(request));
                }
            }
        }
    }

    /// <summary>Item GUIDs that enter a participant's inventory from neither another participant nor escrow: they must not exist yet.</summary>
    public static IReadOnlyList<uint> NewItemGuids(EconomyCommitRequest request)
    {
        var result = new List<uint>();
        foreach (EconomyParticipant participant in request.Participants)
        {
            int id = participant.Before.Id;
            result.AddRange(Gained(participant).Where(guid =>
                !request.Participants.Any(o => o.Before.Id != id && Lost(o).Contains(guid))
                && !request.Changes.OfType<ReleaseFromEscrow>().Any(r => r.CharacterId == id && r.ItemGuid == guid)));
        }

        return result;
    }

    private static HashSet<uint> Lost(EconomyParticipant p)
        => [.. p.Before.Inventory!.Items.Select(i => i.Item.Guid).Except(p.After.Inventory!.Items.Select(i => i.Item.Guid))];

    private static HashSet<uint> Gained(EconomyParticipant p)
        => [.. p.After.Inventory!.Items.Select(i => i.Item.Guid).Except(p.Before.Inventory!.Items.Select(i => i.Item.Guid))];

    public static bool SameInventory(IReadOnlyList<InventoryItemData> first, IReadOnlyList<InventoryItemData> second)
    {
        InventoryItemData[] left = first.OrderBy(i => i.Item.Guid).ToArray();
        InventoryItemData[] right = second.OrderBy(i => i.Item.Guid).ToArray();
        return left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.ContainerGuid == pair.Second.ContainerGuid && pair.First.Slot == pair.Second.Slot
            && SameItem(pair.First.Item, pair.Second.Item));
    }

    public static bool SameItem(ItemInstanceData a, ItemInstanceData b)
        => (a with { Charges = b.Charges, Enchantments = b.Enchantments }) == b
            && a.Charges.SequenceEqual(b.Charges) && a.Enchantments.SequenceEqual(b.Enchantments);

    private static void ValidateInventory(InventorySnapshot? inventory)
    {
        if (inventory is null || inventory.Items.Any(i => i.Item.Guid == 0 || i.Item.Entry == 0 || i.Item.Count == 0)
            || inventory.Items.Select(i => i.Item.Guid).Distinct().Count() != inventory.Items.Count
            || inventory.Items.Select(i => (i.ContainerGuid, i.Slot)).Distinct().Count() != inventory.Items.Count)
        {
            throw new ArgumentException("Participants require complete inventories with unique item GUIDs and positions and positive counts.");
        }
    }

    private static CharacterState Copy(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with
        {
            ActionButtons = state.ActionButtons?.ToArray(),
            Inventory = state.Inventory is { } inventory
                ? new InventorySnapshot(inventory.Items.Select(i => i with { Item = CopyItem(i.Item) }).ToArray(), inventory.AmmoId)
                : null,
            Life = state.Life is { } life ? life with { Powers = life.Powers.ToArray() } : null,
        };
    }

    private static ItemInstanceData CopyItem(ItemInstanceData item)
        => item with { Charges = item.Charges.ToArray(), Enchantments = item.Enchantments.ToArray() };
}
