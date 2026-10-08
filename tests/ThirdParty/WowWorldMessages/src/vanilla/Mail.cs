using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using MailTypeType = OneOf.OneOf<Mail.MailTypeAuction, Mail.MailTypeCreature, Mail.MailTypeGameobject, Mail.MailTypeNormal, MailType>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class Mail {
    public class MailTypeAuction {
        public required uint AuctionId { get; set; }
    }
    public class MailTypeCreature {
        public required uint SenderId { get; set; }
    }
    public class MailTypeGameobject {
        public required uint SenderId { get; set; }
    }
    public class MailTypeNormal {
        public required ulong Sender { get; set; }
    }
    public required uint MessageId { get; set; }
    public required MailTypeType MessageType { get; set; }
    internal MailType MessageTypeValue => MessageType.Match(
        _ => Vanilla.MailType.Auction,
        _ => Vanilla.MailType.Creature,
        _ => Vanilla.MailType.Gameobject,
        _ => Vanilla.MailType.Normal,
        v => v
    );
    public required string Subject { get; set; }
    public required uint ItemTextId { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: set to 0
    /// </summary>
    public required uint Unknown1 { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: stationery (Stationery.dbc)
    /// </summary>
    public required uint Stationery { get; set; }
    public required uint Item { get; set; }
    public required uint ItemEnchantId { get; set; }
    public required uint ItemRandomPropertyId { get; set; }
    public required uint ItemSuffixFactor { get; set; }
    public required byte ItemStackSize { get; set; }
    public required uint ItemSpellCharges { get; set; }
    public required uint MaxDurability { get; set; }
    public required uint Durability { get; set; }
    public required uint Money { get; set; }
    public required uint CashOnDeliveryAmount { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: All have a comment with 'flags' but send the timestamp from the item.
    /// </summary>
    public required uint CheckedTimestamp { get; set; }
    public required float ExpirationTime { get; set; }
    /// <summary>
    /// cmangos/vmangos/mangoszero: mail template (MailTemplate.dbc)
    /// </summary>
    public required uint MailTemplateId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(MessageId, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)MessageTypeValue, cancellationToken).ConfigureAwait(false);

        if (MessageType.Value is Mail.MailTypeNormal mailTypeNormal) {
            await w.WriteULong(mailTypeNormal.Sender, cancellationToken).ConfigureAwait(false);

        }
        else if (MessageType.Value is Mail.MailTypeCreature mailTypeCreature) {
            await w.WriteUInt(mailTypeCreature.SenderId, cancellationToken).ConfigureAwait(false);

        }
        else if (MessageType.Value is Mail.MailTypeGameobject mailTypeGameobject) {
            await w.WriteUInt(mailTypeGameobject.SenderId, cancellationToken).ConfigureAwait(false);

        }
        else if (MessageType.Value is Mail.MailTypeAuction mailTypeAuction) {
            await w.WriteUInt(mailTypeAuction.AuctionId, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteCString(Subject, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemTextId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Stationery, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemEnchantId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSuffixFactor, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ItemStackSize, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSpellCharges, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MaxDurability, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Durability, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Money, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CashOnDeliveryAmount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CheckedTimestamp, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(ExpirationTime, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(MailTemplateId, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<Mail> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var messageId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        MailTypeType messageType = (Vanilla.MailType)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (messageType.Value is Vanilla.MailType.Normal) {
            var sender = await r.ReadULong(cancellationToken).ConfigureAwait(false);

            messageType = new MailTypeNormal {
                Sender = sender,
            };
        }
        else if (messageType.Value is Vanilla.MailType.Creature) {
            var senderId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            messageType = new MailTypeCreature {
                SenderId = senderId,
            };
        }
        else if (messageType.Value is Vanilla.MailType.Gameobject) {
            var senderId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            messageType = new MailTypeGameobject {
                SenderId = senderId,
            };
        }
        else if (messageType.Value is Vanilla.MailType.Auction) {
            var auctionId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            messageType = new MailTypeAuction {
                AuctionId = auctionId,
            };
        }

        var subject = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var itemTextId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var stationery = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemEnchantId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemRandomPropertyId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemSuffixFactor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var itemStackSize = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var itemSpellCharges = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var maxDurability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var durability = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var money = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var cashOnDeliveryAmount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var checkedTimestamp = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var expirationTime = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var mailTemplateId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new Mail {
            MessageId = messageId,
            MessageType = messageType,
            Subject = subject,
            ItemTextId = itemTextId,
            Unknown1 = unknown1,
            Stationery = stationery,
            Item = item,
            ItemEnchantId = itemEnchantId,
            ItemRandomPropertyId = itemRandomPropertyId,
            ItemSuffixFactor = itemSuffixFactor,
            ItemStackSize = itemStackSize,
            ItemSpellCharges = itemSpellCharges,
            MaxDurability = maxDurability,
            Durability = durability,
            Money = money,
            CashOnDeliveryAmount = cashOnDeliveryAmount,
            CheckedTimestamp = checkedTimestamp,
            ExpirationTime = expirationTime,
            MailTemplateId = mailTemplateId,
        };
    }

    internal int Size() {
        var size = 0;

        // message_id: Generator.Generated.DataTypeInteger
        size += 4;

        // message_type: Generator.Generated.DataTypeEnum
        size += 1;

        if (MessageType.Value is Mail.MailTypeNormal mailTypeNormal) {
            // sender: Generator.Generated.DataTypeGuid
            size += 8;

        }
        else if (MessageType.Value is Mail.MailTypeCreature mailTypeCreature) {
            // sender_id: Generator.Generated.DataTypeInteger
            size += 4;

        }
        else if (MessageType.Value is Mail.MailTypeGameobject mailTypeGameobject) {
            // sender_id: Generator.Generated.DataTypeInteger
            size += 4;

        }
        else if (MessageType.Value is Mail.MailTypeAuction mailTypeAuction) {
            // auction_id: Generator.Generated.DataTypeInteger
            size += 4;

        }

        // subject: Generator.Generated.DataTypeCstring
        size += Subject.Length + 1;

        // item_text_id: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        // stationery: Generator.Generated.DataTypeInteger
        size += 4;

        // item: Generator.Generated.DataTypeItem
        size += 4;

        // item_enchant_id: Generator.Generated.DataTypeInteger
        size += 4;

        // item_random_property_id: Generator.Generated.DataTypeInteger
        size += 4;

        // item_suffix_factor: Generator.Generated.DataTypeInteger
        size += 4;

        // item_stack_size: Generator.Generated.DataTypeInteger
        size += 1;

        // item_spell_charges: Generator.Generated.DataTypeInteger
        size += 4;

        // max_durability: Generator.Generated.DataTypeInteger
        size += 4;

        // durability: Generator.Generated.DataTypeInteger
        size += 4;

        // money: Generator.Generated.DataTypeGold
        size += 4;

        // cash_on_delivery_amount: Generator.Generated.DataTypeInteger
        size += 4;

        // checked_timestamp: Generator.Generated.DataTypeInteger
        size += 4;

        // expiration_time: Generator.Generated.DataTypeFloatingPoint
        size += 4;

        // mail_template_id: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

