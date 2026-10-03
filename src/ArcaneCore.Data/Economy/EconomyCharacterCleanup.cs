using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Kernel.Economy;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Economy;

/// <summary>
/// The economy rows of a character being deleted (<see cref="ICharacterDataCleanup"/>, run inside
/// the deletion transaction before the <c>characters</c> row goes). No new ids are allocated, so the
/// running economy feature's id counters stay valid; it re-reads the touched rows afterwards
/// (EconomyFeature.OnCharacterDeletedAsync).
/// <list type="bullet">
/// <item>A letter to the character that carries an item or money and came, unreturned, from another
/// living character goes back to that sender in place (same id, COD cleared, marked returned,
/// 30 more days), as vmangos Player::DeleteFromDB returns mail contents. Every other letter to the
/// character is deleted with its escrowed item and, when nothing else shows it, its text.</item>
/// <item>Cash on delivery is cleared on letters the character sent: nobody could receive the payment.</item>
/// <item>The character's auctions without a bid are removed with their escrowed item. An auction
/// with a bid stays and completes at expiry: the bidder still wins the item (the proceeds letter
/// is skipped because the seller no longer exists).</item>
/// <item>A bid the character holds is cleared (its money left with the character); the auction is open again.</item>
/// </list>
/// </summary>
public static class EconomyCharacterCleanup
{
    /// <summary>Days a returned letter stays (EconomyOptions.MailExpireDays default).</summary>
    public const int ReturnedLetterDays = 30;

    public static async Task StageAsync(CharacterDbContext db, int characterId, long now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (characterId <= 0)
        {
            return;
        }

        uint self = (uint)characterId;
        var removedItems = new HashSet<uint>();
        var textCandidates = new HashSet<uint>();
        var removedMails = new HashSet<uint>();

        List<MailRow> inbox = await db.Set<MailRow>().Where(r => r.ReceiverId == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> senders = [.. inbox.Where(r => r.MessageType == (byte)MailMessageType.Normal && r.SenderId != self && r.SenderId is > 0 and <= int.MaxValue)
            .Select(r => (int)r.SenderId).Distinct()];
        HashSet<int> living = [.. await db.Characters.AsNoTracking().Where(c => senders.Contains(c.Id)).Select(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)];
        foreach (MailRow row in inbox)
        {
            MailRecord mail = row.ToRecord();
            bool returnable = (mail.HasItem || mail.Money > 0) && mail.MessageType == MailMessageType.Normal
                && (mail.Checked & MailCheckMask.Returned) == 0 && mail.SenderId != self && living.Contains((int)mail.SenderId);
            if (returnable)
            {
                row.ReceiverId = (int)mail.SenderId;
                row.SenderId = self;
                row.Cod = 0;
                row.Checked = (uint)((mail.Checked & (MailCheckMask.HasBody | MailCheckMask.Copied)) | MailCheckMask.Returned);
                row.DeliverTime = now;
                row.ExpireTime = now + (ReturnedLetterDays * 86400L);
                continue;
            }

            db.Remove(row);
            removedMails.Add(mail.Id);
            if (mail.HasItem)
            {
                removedItems.Add(mail.ItemGuid);
            }

            if (mail.ItemTextId != 0)
            {
                textCandidates.Add(mail.ItemTextId);
            }
        }

        foreach (MailRow row in await db.Set<MailRow>()
            .Where(r => r.MessageType == (byte)MailMessageType.Normal && r.SenderId == self && r.ReceiverId != characterId && r.Cod > 0)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            row.Cod = 0;
        }

        foreach (AuctionRow row in await db.Set<AuctionRow>().AsNoTracking().Where(r => r.SellerId == characterId && r.BidderId == 0)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            // A bidder is a different settlement participant from the seller. Recheck the
            // complete auction atomically when removing it, even in a caller's read-committed
            // transaction. The successful delete keeps its write lock until that transaction
            // ends; a bid that won after our read instead refuses the entire character deletion.
            int removed = await db.Set<AuctionRow>().Where(r =>
                    r.Id == row.Id && r.HouseId == row.HouseId && r.ItemGuid == row.ItemGuid
                    && r.ItemEntry == row.ItemEntry && r.ItemCount == row.ItemCount && r.SellerId == row.SellerId
                    && r.StartBid == row.StartBid && r.Buyout == row.Buyout && r.ExpireTime == row.ExpireTime
                    && r.BidderId == row.BidderId && r.Bid == row.Bid && r.Deposit == row.Deposit)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            if (removed != 1)
            {
                throw new CharacterDeletionRefusedException("An auction changed during seller deletion.");
            }

            removedItems.Add(row.ItemGuid);
        }

        foreach (AuctionRow row in await db.Set<AuctionRow>().Where(r => r.BidderId == characterId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            row.BidderId = 0;
            row.Bid = 0;
        }

        if (removedItems.Count > 0)
        {
            db.RemoveRange(await db.Set<ItemInstanceRow>().Where(r => removedItems.Contains(r.Guid) && r.OwnerGuid == 0)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        foreach (uint textId in textCandidates)
        {
            // Kept while another letter or a surviving letter item still shows it.
            bool shown = await db.Set<MailRow>().AsNoTracking().AnyAsync(r => r.ItemTextId == textId && !removedMails.Contains(r.Id), cancellationToken).ConfigureAwait(false)
                || await db.Set<ItemInstanceRow>().AsNoTracking().AnyAsync(r => r.Text == textId && r.OwnerGuid != characterId && !removedItems.Contains(r.Guid), cancellationToken).ConfigureAwait(false);
            if (!shown && await db.Set<ItemTextRow>().FirstOrDefaultAsync(r => r.Id == textId, cancellationToken).ConfigureAwait(false) is { } text)
            {
                db.Remove(text);
            }
        }
    }
}
