using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Economy;

/// <summary>CMSG_SEND_MAIL fields (wow_messages 1.12).</summary>
public sealed record SendMailRequest(ObjectGuid Mailbox, string Receiver, string Subject, string Body, ObjectGuid Item, uint Money, uint Cod);

public sealed partial class EconomyFeature
{
    /// <summary>Longest letter body stored (item_text.text).</summary>
    public const int MaxBodyLength = 4000;

    /// <summary>Longest subject accepted (the client limits it to 64 characters).</summary>
    public const int MaxSubjectLength = 128;

    private readonly Dictionary<int, Mailbox> _mailboxes = [];
    private readonly HashSet<uint> _busyMails = [];

    /// <summary>The cached letters of an online character, newest first (tests and GM tools).</summary>
    public IReadOnlyList<MailView>? CachedMail(int characterId)
        => _mailboxes.TryGetValue(characterId, out Mailbox? box) && box.Loaded ? [.. box.Mails] : null;

    /// <summary>MSG_QUERY_NEXT_MAIL_TIME: 0 when an unread delivered letter waits.</summary>
    public void QueryNextMailTime(WorldSession session, Player player)
    {
        long now = Now;
        bool unread = _mailboxes.TryGetValue(IdOf(player), out Mailbox? box) && box.Loaded
            && box.Mails.Any(m => (m.Mail.Checked & MailCheckMask.Read) == 0 && m.Mail.DeliverTime <= now && m.Mail.ExpireTime > now);
        session.Send(WorldOpcode.MsgQueryNextMailTime, EconomyPackets.NextMailTime(unread));
    }

    /// <summary>CMSG_GET_MAIL_LIST: refresh the mailbox from storage, then send it.</summary>
    public void GetMailList(WorldSession session, Player player, ObjectGuid mailbox)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (!Enabled)
        {
            session.Send(WorldOpcode.SmsgMailListResult, EconomyPackets.MailList([], Now, Templates.Find));
            return;
        }

        LoadMailbox(player, sendList: true);
    }

    /// <summary>CMSG_SEND_MAIL (vmangos WorldSession::HandleSendMail).</summary>
    public void SendMail(WorldSession session, Player player, SendMailRequest request)
    {
        if (!MailboxAccess.CanUseMailbox(player, request.Mailbox))
        {
            return;
        }

        if (ValidateSendMail(player, request, out CharacterIdentity? receiver, out InventoryResult equipError) is { } error)
        {
            SendMailResult(session, 0, MailAction.Send, error, equipError);
            return;
        }

        // The receiver's box size is durable state; count it before freezing the sender.
        int receiverId = receiver!.Id;
        Read((store, ct) => store.GetMailsAsync(receiverId, ct), mails =>
        {
            if (mails.Count >= Options.MaxMailboxSize)
            {
                SendMailResult(session, 0, MailAction.Send, MailResult.RecipientCapReached);
                return;
            }

            ContinueSendMail(session, player, request);
        }, () => SendMailResult(session, 0, MailAction.Send, MailResult.InternalError));
    }

    private void ContinueSendMail(WorldSession session, Player player, SendMailRequest request)
    {
        if (!Settlements.CanAct(session, player))
        {
            SendMailResult(session, 0, MailAction.Send, MailResult.InternalError);
            return;
        }

        if (ValidateSendMail(player, request, out CharacterIdentity? receiver, out InventoryResult equipError) is { } error)
        {
            SendMailResult(session, 0, MailAction.Send, error, equipError);
            return;
        }

        Item? item = request.Item.IsEmpty ? null : player.Inventory.GetItemByGuid(request.Item);
        InventoryResult staged = player.Inventory.TryStageEconomyTransfer(item is null ? [] : [item.Guid], [], out EconomyInventoryStage? stage);
        if (staged != InventoryResult.Ok)
        {
            SendMailResult(session, 0, MailAction.Send, MailResult.EquipError, staged);
            return;
        }

        long now = Now;
        bool hasBody = request.Body.Length > 0;
        ItemInstanceData? itemData = stage!.RemovedData.SingleOrDefault();
        var mail = new MailRecord
        {
            Id = NextMailId(),
            MessageType = MailMessageType.Normal,
            Stationery = player.IsGameMaster ? MailStationery.Gm : MailStationery.Default,
            SenderId = player.Guid.Low,
            ReceiverId = receiver!.Id,
            Subject = request.Subject,
            ItemTextId = hasBody ? NextTextId() : 0,
            ItemGuid = itemData?.Guid ?? 0,
            ItemEntry = itemData?.Entry ?? 0,
            Money = request.Money,
            Cod = request.Cod,
            Checked = hasBody ? MailCheckMask.HasBody : MailCheckMask.None,
            DeliverTime = now,
            ExpireTime = now + ((request.Cod > 0 ? Options.CodExpireDays : Options.MailExpireDays) * MailRules.SecondsPerDay),
        };
        uint cost = request.Money + Options.MailPostage;
        EconomyActor? actor = Settlements.CreateActor(session, player, stage, player.Money - cost);
        var changes = new List<EconomyChange>();
        if (itemData is not null)
        {
            changes.Add(new EscrowFromInventory(IdOf(player), itemData));
        }

        changes.Add(new InsertMail(mail, hasBody ? request.Body : null));
        if (actor is null || !Start([actor], changes, outcome =>
            {
                if (outcome == EconomyOutcome.After)
                {
                    SendMailResult(session, mail.Id, MailAction.Send, MailResult.Ok);
                    Deliver(new MailView(mail, itemData));
                }
                else if (outcome != EconomyOutcome.Unknown)
                {
                    SendMailResult(session, 0, MailAction.Send, MailResult.InternalError);
                }
            }))
        {
            SendMailResult(session, 0, MailAction.Send, MailResult.InternalError);
        }
    }

    private MailResult? ValidateSendMail(Player player, SendMailRequest request, out CharacterIdentity? receiver, out InventoryResult equipError)
    {
        equipError = InventoryResult.Ok;
        receiver = null;
        if (!Enabled || request.Subject.Length > MaxSubjectLength || request.Body.Length > MaxBodyLength)
        {
            return MailResult.InternalError;
        }

        receiver = request.Receiver.Length == 0 ? null : _directory?.FindByName(CharacterNames.Normalize(request.Receiver));
        if (receiver is null)
        {
            return MailResult.RecipientNotFound;
        }

        if (receiver.Id == IdOf(player))
        {
            return MailResult.CannotSendToSelf;
        }

        if (!Options.AllowCrossTeamMail && !player.IsGameMaster && IsAlliance(receiver.Race) != (player.Team == Team.Alliance))
        {
            return MailResult.NotYourTeam;
        }

        if ((ulong)request.Money + Options.MailPostage > player.Money)
        {
            return MailResult.NotEnoughMoney;
        }

        if (request.Item.IsEmpty)
        {
            // vmangos refuses cash on delivery without an item.
            return request.Cod > 0 ? MailResult.InternalError : null;
        }

        if (player.Inventory.GetItemByGuid(request.Item) is not { } item)
        {
            equipError = InventoryResult.ItemNotFound;
            return MailResult.EquipError;
        }

        equipError = player.Inventory.CanTransferOut(item);
        return equipError == InventoryResult.Ok ? null : MailResult.EquipError;
    }

    /// <summary>CMSG_MAIL_TAKE_MONEY.</summary>
    public void TakeMoney(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (FindOwnMail(player, mailId) is not { Mail.Money: > 0 } view || !Settlements.CanAct(session, player)
            || (ulong)player.Money + view.Mail.Money > EconomyOptions.MaxMoney
            || player.Inventory.TryStageEconomyTransfer([], [], out EconomyInventoryStage? stage) != InventoryResult.Ok
            || Settlements.CreateActor(session, player, stage!, player.Money + view.Mail.Money) is not { } actor)
        {
            SendMailResult(session, mailId, MailAction.MoneyTaken, MailResult.InternalError);
            return;
        }

        MailRecord updated = view.Mail with { Money = 0 };
        RunMailOperation([actor], [new UpdateMail(view.Mail, updated)], [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                ReplaceMail(IdOf(player), view with { Mail = updated });
                SendMailResult(session, mailId, MailAction.MoneyTaken, MailResult.Ok);
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                SendMailResult(session, mailId, MailAction.MoneyTaken, MailResult.InternalError);
            }
        }, () => SendMailResult(session, mailId, MailAction.MoneyTaken, MailResult.InternalError));
    }

    /// <summary>CMSG_MAIL_TAKE_ITEM; a cash-on-delivery letter is paid and its sender receives the payment.</summary>
    public void TakeItem(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (FindOwnMail(player, mailId) is not { Item: { } item } view || !view.Mail.HasItem || !Settlements.CanAct(session, player))
        {
            SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.InternalError);
            return;
        }

        MailRecord mail = view.Mail;
        if (mail.Cod > player.Money)
        {
            SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.NotEnoughMoney);
            return;
        }

        InventoryResult staged = player.Inventory.TryStageEconomyTransfer([], [item], out EconomyInventoryStage? stage);
        if (staged != InventoryResult.Ok)
        {
            SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.EquipError, staged);
            return;
        }

        if (Settlements.CreateActor(session, player, stage!, player.Money - mail.Cod) is not { } actor)
        {
            SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.InternalError);
            return;
        }

        long now = Now;
        MailRecord updated = mail with { ItemGuid = 0, ItemEntry = 0, Cod = 0 };
        var changes = new List<EconomyChange> { new UpdateMail(mail, updated), new ReleaseFromEscrow(IdOf(player), item.Guid) };
        MailView? payment = null;
        if (mail.Cod > 0 && mail.MessageType == MailMessageType.Normal && CharacterExists((int)mail.SenderId))
        {
            // vmangos HandleMailTakeItem: the COD amount goes back to the sender as a letter.
            payment = new MailView(new MailRecord
            {
                Id = NextMailId(),
                MessageType = MailMessageType.Normal,
                SenderId = player.Guid.Low,
                ReceiverId = (int)mail.SenderId,
                Subject = mail.Subject,
                Money = mail.Cod,
                Checked = MailCheckMask.CodPayment,
                DeliverTime = now,
                ExpireTime = now + (Options.MailExpireDays * MailRules.SecondsPerDay),
            }, null);
            changes.Add(new InsertMail(payment.Mail, null));
        }

        RunMailOperation([actor], changes, [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                ReplaceMail(IdOf(player), new MailView(updated, null));
                SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.Ok, itemGuid: item.Guid, itemCount: item.Count);
                if (payment is not null)
                {
                    Deliver(payment);
                }
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.InternalError);
            }
        }, () => SendMailResult(session, mailId, MailAction.ItemTaken, MailResult.InternalError));
    }

    /// <summary>CMSG_MAIL_MARK_AS_READ (no reply).</summary>
    public void MarkAsRead(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox) || FindOwnMail(player, mailId) is not { } view
            || (view.Mail.Checked & MailCheckMask.Read) != 0)
        {
            return;
        }

        // vmangos: reading a letter shortens nothing in 1.12 except unread COD (3 days stays).
        MailRecord updated = view.Mail with { Checked = view.Mail.Checked | MailCheckMask.Read };
        RunMailOperation([], [new UpdateMail(view.Mail, updated)], [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                ReplaceMail(IdOf(player), view with { Mail = updated });
            }
        }, null);
    }

    /// <summary>CMSG_MAIL_RETURN_TO_SENDER: a player's letter goes back with its item and money, COD cleared.</summary>
    public void ReturnToSender(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (FindOwnMail(player, mailId) is not { } view || !MailRules.CanReturn(view.Mail) || !CharacterExists((int)view.Mail.SenderId))
        {
            SendMailResult(session, mailId, MailAction.ReturnedToSender, MailResult.InternalError);
            return;
        }

        var returned = new MailView(MailRules.Returned(view.Mail, NextMailId(), Now, Options), view.Item);
        RunMailOperation([], [new DeleteMail(view.Mail), new InsertMail(returned.Mail, null)], [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                RemoveMail(IdOf(player), mailId);
                SendMailResult(session, mailId, MailAction.ReturnedToSender, MailResult.Ok);
                Deliver(returned);
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                SendMailResult(session, mailId, MailAction.ReturnedToSender, MailResult.InternalError);
            }
        }, () => SendMailResult(session, mailId, MailAction.ReturnedToSender, MailResult.InternalError));
    }

    /// <summary>CMSG_MAIL_DELETE: only an emptied letter (no item, no money) may be deleted.</summary>
    public void DeleteMail(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (FindOwnMail(player, mailId) is not { } view || view.Mail.HasItem || view.Mail.Money > 0)
        {
            SendMailResult(session, mailId, MailAction.Deleted, MailResult.InternalError);
            return;
        }

        RunMailOperation([], [new DeleteMail(view.Mail)], [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                RemoveMail(IdOf(player), mailId);
                SendMailResult(session, mailId, MailAction.Deleted, MailResult.Ok);
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                SendMailResult(session, mailId, MailAction.Deleted, MailResult.InternalError);
            }
        }, () => SendMailResult(session, mailId, MailAction.Deleted, MailResult.InternalError));
    }

    /// <summary>CMSG_MAIL_CREATE_TEXT_ITEM: a Plain Letter carrying the body enters the inventory; the letter is marked copied.</summary>
    public void CreateTextItem(WorldSession session, Player player, ObjectGuid mailbox, uint mailId)
    {
        if (!MailboxAccess.CanUseMailbox(player, mailbox))
        {
            return;
        }

        if (FindOwnMail(player, mailId) is not { } view || view.Mail.ItemTextId == 0
            || (view.Mail.Checked & MailCheckMask.Copied) != 0 || _items is null
            || Templates.Find(Options.LetterItemEntry) is null || !Settlements.CanAct(session, player))
        {
            SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.InternalError);
            return;
        }

        var letter = new ItemInstanceData
        {
            Guid = _items.GuidAllocator.Next(),
            Entry = Options.LetterItemEntry,
            Count = 1,
            Creator = view.Mail.MessageType == MailMessageType.Normal ? ObjectGuid.Player(view.Mail.SenderId).Value : 0,
            TextId = view.Mail.ItemTextId,
            Flags = (uint)ItemDynFlags.Readable,
            Charges = [0, 0, 0, 0, 0],
            Enchantments = new uint[21],
        };
        InventoryResult staged = player.Inventory.TryStageEconomyTransfer([], [letter], out EconomyInventoryStage? stage);
        if (staged != InventoryResult.Ok)
        {
            SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.EquipError, staged);
            return;
        }

        if (Settlements.CreateActor(session, player, stage!, player.Money) is not { } actor)
        {
            SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.InternalError);
            return;
        }

        // The new instance is part of the After inventory; the store inserts it with the participant.
        MailRecord updated = view.Mail with { Checked = view.Mail.Checked | MailCheckMask.Copied };
        RunMailOperation([actor], [new UpdateMail(view.Mail, updated)], [mailId], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                ReplaceMail(IdOf(player), view with { Mail = updated });
                SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.Ok);
            }
            else if (outcome != EconomyOutcome.Unknown)
            {
                SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.InternalError);
            }
        }, () => SendMailResult(session, mailId, MailAction.MadePermanent, MailResult.InternalError));
    }

    /// <summary>CMSG_ITEM_TEXT_QUERY: the text of a cached letter or a carried letter item.</summary>
    public void QueryItemText(WorldSession session, Player player, uint textId)
    {
        bool owned = textId != 0 && ((_mailboxes.TryGetValue(IdOf(player), out Mailbox? box) && box.Mails.Any(m => m.Mail.ItemTextId == textId))
            || player.Inventory.AllItems.Any(i => i.ToData().TextId == textId));
        if (!owned)
        {
            return;
        }

        Read((store, ct) => store.GetItemTextAsync(textId, ct),
            text => session.Send(WorldOpcode.SmsgItemTextQueryResponse, EconomyPackets.ItemTextResponse(textId, text ?? string.Empty)));
    }

    private void RunMailOperation(IReadOnlyList<EconomyActor> actors, IReadOnlyList<EconomyChange> changes, IReadOnlyList<uint> mailIds,
        Action<EconomyOutcome> finished, Action? refused)
    {
        if (mailIds.Any(_busyMails.Contains))
        {
            refused?.Invoke();
            return;
        }

        foreach (uint id in mailIds)
        {
            _busyMails.Add(id);
        }

        bool started = Start(actors, changes, outcome =>
        {
            foreach (uint id in mailIds)
            {
                _busyMails.Remove(id);
            }

            finished(outcome);
        });
        if (!started)
        {
            foreach (uint id in mailIds)
            {
                _busyMails.Remove(id);
            }

            refused?.Invoke();
        }
    }

    private MailView? FindOwnMail(Player player, uint mailId)
    {
        long now = Now;
        return Enabled && _mailboxes.TryGetValue(IdOf(player), out Mailbox? box) && box.Loaded
            ? box.Mails.FirstOrDefault(m => m.Mail.Id == mailId && m.Mail.ReceiverId == IdOf(player)
                && m.Mail.DeliverTime <= now && m.Mail.ExpireTime > now)
            : null;
    }

    /// <summary>Show a committed letter to its receiver when online.</summary>
    private void Deliver(MailView view)
    {
        if (OnlinePlayer(view.Mail.ReceiverId) is not { } receiver)
        {
            return;
        }

        if (_mailboxes.TryGetValue(view.Mail.ReceiverId, out Mailbox? box))
        {
            box.Revision++;
            if (box.Loaded)
            {
                box.Mails.Insert(0, view);
            }
        }

        receiver.Session.Send(WorldOpcode.SmsgReceivedMail, EconomyPackets.ReceivedMail());
    }

    private void ReplaceMail(int characterId, MailView view)
    {
        if (_mailboxes.TryGetValue(characterId, out Mailbox? box))
        {
            box.Revision++;
            int index = box.Mails.FindIndex(m => m.Mail.Id == view.Mail.Id);
            if (index >= 0)
            {
                box.Mails[index] = view;
            }
        }
    }

    private void RemoveMail(int characterId, uint mailId)
    {
        if (_mailboxes.TryGetValue(characterId, out Mailbox? box))
        {
            box.Revision++;
            box.Mails.RemoveAll(m => m.Mail.Id == mailId);
        }
    }

    private void LoadMailbox(Player player, bool sendList)
    {
        int id = IdOf(player);
        if (!_mailboxes.TryGetValue(id, out Mailbox? box))
        {
            box = new Mailbox();
            _mailboxes[id] = box;
        }

        long revision = box.Revision;
        Read(async (store, ct) =>
        {
            IReadOnlyList<MailRecord> mails = await store.GetMailsAsync(id, ct).ConfigureAwait(false);
            IReadOnlyDictionary<uint, ItemInstanceData> items = await store
                .GetEscrowItemsAsync(mails.Where(m => m.HasItem).Select(m => m.ItemGuid).ToArray(), ct).ConfigureAwait(false);
            return mails.OrderByDescending(m => m.Id)
                .Select(m => new MailView(m, m.HasItem ? items.GetValueOrDefault(m.ItemGuid) : null)).ToList();
        }, mails =>
        {
            if (!ReferenceEquals(_world?.FindOnlinePlayer(player.Guid), player) || !_mailboxes.TryGetValue(id, out Mailbox? current))
            {
                return;
            }

            if (current.Revision != revision)
            {
                // A commit changed this box while the read ran; read again.
                LoadMailbox(player, sendList);
                return;
            }

            current.Mails = mails;
            current.Loaded = true;
            if (sendList)
            {
                long now = Now;
                List<MailView> visible = [.. mails.Where(m => m.Mail.DeliverTime <= now && m.Mail.ExpireTime > now)];
                player.Session.Send(WorldOpcode.SmsgMailListResult, EconomyPackets.MailList(visible, now, Templates.Find));
            }
        });
    }

    private void ExpireMails(IReadOnlyList<MailRecord> expired)
    {
        IReadOnlyCollection<uint> guids = [.. expired.Where(m => m.HasItem).Select(m => m.ItemGuid)];
        Read((store, ct) => store.GetEscrowItemsAsync(guids, ct), items =>
        {
            long now = Now;
            foreach (MailRecord mail in expired)
            {
                ItemInstanceData? item = mail.HasItem ? items.GetValueOrDefault(mail.ItemGuid) : null;
                ExpireMail(new MailView(mail, item), now);
            }
        });
    }

    /// <summary>vmangos Player::UpdateMail expiry: return a player's unreturned letter with contents, else delete it and its item.</summary>
    private void ExpireMail(MailView view, long now)
    {
        MailRecord mail = view.Mail;
        bool hasContents = mail.HasItem || mail.Money > 0;
        if (hasContents && MailRules.CanReturn(mail) && CharacterExists((int)mail.SenderId))
        {
            var returned = new MailView(MailRules.Returned(mail, NextMailId(), now, Options), view.Item);
            RunMailOperation([], [new DeleteMail(mail), new InsertMail(returned.Mail, null)], [mail.Id], outcome =>
            {
                if (outcome == EconomyOutcome.After)
                {
                    RemoveMail(mail.ReceiverId, mail.Id);
                    Deliver(returned);
                }
            }, null);
            return;
        }

        var changes = new List<EconomyChange> { new DeleteMail(mail) };
        if (mail.HasItem)
        {
            changes.Add(new DeleteEscrowItem(mail.ItemGuid));
        }

        RunMailOperation([], changes, [mail.Id], outcome =>
        {
            if (outcome == EconomyOutcome.After)
            {
                RemoveMail(mail.ReceiverId, mail.Id);
            }
        }, null);
    }

    private static void SendMailResult(WorldSession session, uint mailId, MailAction action, MailResult result,
        InventoryResult equipError = InventoryResult.Ok, uint itemGuid = 0, uint itemCount = 0)
        => session.Send(WorldOpcode.SmsgSendMailResult, EconomyPackets.SendMailResult(mailId, action, result, equipError, itemGuid, itemCount));

    private sealed class Mailbox
    {
        public List<MailView> Mails { get; set; } = [];
        public bool Loaded { get; set; }
        public long Revision { get; set; }
    }
}
