using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.World.Economy;

/// <summary>One item of a server letter.</summary>
public readonly record struct ServerMailItem(uint Entry, uint Count);

/// <summary>
/// A letter the server sends (AzerothCore ServerMailMgr::SendServerMail): from a creature when <see cref="SenderEntry"/> is set (MAIL_CREATURE,
/// default stationery), otherwise from the receiver with GM stationery, as AzerothCore does.
/// </summary>
public sealed record ServerMailRequest(int ReceiverId, uint SenderEntry, string Subject, string Body, uint Money, IReadOnlyList<ServerMailItem> Items);

public sealed partial class EconomyFeature
{
    /// <summary>
    /// World thread: send <paramref name="request"/> in one economy operation. The 1.12 client shows one item per letter, so every item
    /// goes in a letter of its own with the same subject and body; the money rides on the first. The items are created new, straight into
    /// escrow under their letter. <paramref name="finished"/> gets true once the letters are committed (and delivered to an online
    /// receiver); false when the economy store is off or the commit was refused.
    /// </summary>
    public void SendServerMail(ServerMailRequest request, Action<bool>? finished = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<MailView>? letters = BuildServerLetters(request);
        if (letters is null)
        {
            finished?.Invoke(false);
            return;
        }

        var changes = new List<EconomyChange>();
        foreach (MailView letter in letters)
        {
            if (letter.Item is { } item) changes.Add(new CreateEscrowItem(item));
            changes.Add(new InsertMail(letter.Mail, letter.Body));
        }

        if (!Start([], changes, outcome =>
            {
                bool ok = outcome == EconomyOutcome.After;
                if (ok) DeliverAll(letters);
                finished?.Invoke(ok);
            }))
        {
            finished?.Invoke(false);
        }
    }

    private List<MailView>? BuildServerLetters(ServerMailRequest request)
    {
        if (request.ReceiverId <= 0 || (request.Items.Count > 0 && _items is null)) return null;
        var items = new List<ItemInstanceData?>();
        foreach (ServerMailItem wanted in request.Items)
        {
            if (Templates.Find(wanted.Entry) is not { } template || wanted.Count == 0) return null;
            Item item = Item.Create(_items!.GuidAllocator.Next(), template, ObjectGuid.Empty);
            item.Count = Math.Min(wanted.Count, Math.Max(1u, template.Stackable));
            items.Add(item.ToData());
        }

        if (items.Count == 0) items.Add(null);
        long now = Now;
        bool creature = request.SenderEntry != 0;
        var letters = new List<MailView>();
        for (int i = 0; i < items.Count; i++)
        {
            ItemInstanceData? item = items[i];
            uint money = i == 0 ? request.Money : 0;
            bool hasBody = request.Body.Length > 0;
            (long deliver, long expire) = MailRules.SendTiming(now, item is not null || money > 0, 0, Options);
            var mail = new MailRecord
            {
                Id = NextMailId(),
                MessageType = creature ? MailMessageType.Creature : MailMessageType.Normal,
                Stationery = creature ? MailStationery.Default : MailStationery.Gm,
                SenderId = creature ? request.SenderEntry : (uint)request.ReceiverId,
                ReceiverId = request.ReceiverId,
                Subject = request.Subject,
                ItemTextId = hasBody ? NextTextId() : 0,
                ItemGuid = item?.Guid ?? 0,
                ItemEntry = item?.Entry ?? 0,
                Money = money,
                Checked = hasBody ? MailCheckMask.HasBody : MailCheckMask.Copied,
                DeliverTime = deliver,
                ExpireTime = expire,
            };
            letters.Add(new MailView(mail, item) { Body = hasBody ? request.Body : null });
        }

        return letters;
    }
}
