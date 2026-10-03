using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature : ICharacterDeleteHook
{
    /// <summary>
    /// The characters-database cleanup (Data EconomyCharacterCleanup) returned, deleted or
    /// neutralized the deleted character's letters and auctions in the deletion transaction.
    /// Re-read what it touched so the caches match the rows: auctions the character sold or
    /// bid on, and the mailboxes of online characters that got a letter back or hold a letter
    /// from it (its cash on delivery was cleared).
    /// </summary>
    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (!Enabled || _stopping || _world is not { } world)
        {
            return;
        }

        int id = character.Id;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_readStop.Token);
        budget.CancelAfter(EconomySettlements.Budget);
        IReadOnlyList<AuctionRecord> auctions;
        IReadOnlyDictionary<uint, ItemInstanceData> items;
        IReadOnlyList<MailRecord> letters;
        await using (AsyncServiceScope scope = _scopes.CreateAsyncScope())
        {
            IEconomyStore store = scope.ServiceProvider.GetRequiredService<IEconomyStore>();
            auctions = [.. (await store.GetAuctionsAsync(budget.Token).ConfigureAwait(false)).Where(a => a.SellerId == id)];
            items = await store.GetEscrowItemsAsync([.. auctions.Select(a => a.ItemGuid)], budget.Token).ConfigureAwait(false);
            letters = await store.GetMailsInvolvingAsync(id, budget.Token).ConfigureAwait(false);
        }

        await world.InvokeAsync(() =>
        {
            ResyncDeletedCharacter(id, auctions, items, letters);
            return true;
        }).WaitAsync(budget.Token).ConfigureAwait(false);
    }

    /// <summary>World thread: apply the deletion cleanup to the caches.</summary>
    private void ResyncDeletedCharacter(int id, IReadOnlyList<AuctionRecord> sold, IReadOnlyDictionary<uint, ItemInstanceData> items,
        IReadOnlyList<MailRecord> letters)
    {
        _mailboxes.Remove(id);
        Dictionary<uint, AuctionRecord> stillSold = sold.ToDictionary(a => a.Id);
        foreach (AuctionView view in _auctions.Values.Where(v => v.Auction.SellerId == id || v.Auction.BidderId == id).ToList())
        {
            AuctionRecord auction = view.Auction;
            if (auction.SellerId == id)
            {
                if (stillSold.TryGetValue(auction.Id, out AuctionRecord? kept) && items.TryGetValue(kept.ItemGuid, out ItemInstanceData? item))
                {
                    _auctions[auction.Id] = new AuctionView(kept, item);
                }
                else
                {
                    _auctions.Remove(auction.Id);
                }
            }
            else
            {
                _auctions[auction.Id] = view with { Auction = auction with { BidderId = 0, Bid = 0 } };
            }
        }

        long now = Now;
        foreach (IGrouping<int, MailRecord> byReceiver in letters.Where(m => m.ReceiverId != id).GroupBy(m => m.ReceiverId))
        {
            if (OnlinePlayer(byReceiver.Key) is not { } receiver)
            {
                continue;
            }

            if (_mailboxes.TryGetValue(byReceiver.Key, out Mailbox? box))
            {
                // A read in flight started before the cleanup; the bump makes it read again.
                box.Revision++;
                if (box.Loaded)
                {
                    LoadMailbox(receiver, sendList: false);
                }
            }

            if (byReceiver.Any(m => (m.Checked & MailCheckMask.Returned) != 0 && m.DeliverTime >= now - 60))
            {
                receiver.Session.Send(WorldOpcode.SmsgReceivedMail, EconomyPackets.ReceivedMail());
            }
        }
    }
}
