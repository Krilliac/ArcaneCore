using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SEND_MAIL: VanillaClientMessage, IWorldMessage {
    public required ulong Mailbox { get; set; }
    public required string Receiver { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    /// <summary>
    /// cmangos: stationery?
    /// </summary>
    public required uint Unknown1 { get; set; }
    /// <summary>
    /// cmangos: 0x00000000
    /// </summary>
    public required uint Unknown2 { get; set; }
    public required ulong Item { get; set; }
    public required uint Money { get; set; }
    public required uint CashOnDeliveryAmount { get; set; }
    /// <summary>
    /// cmangos: const 0
    /// </summary>
    public required uint Unknown3 { get; set; }
    /// <summary>
    /// cmangos: const 0
    /// </summary>
    public required uint Unknown4 { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Mailbox, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Receiver, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Subject, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Body, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown2, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Item, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Money, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(CashOnDeliveryAmount, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown3, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown4, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 568, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 568, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SEND_MAIL> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var mailbox = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var receiver = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var subject = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var body = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown2 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var item = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var money = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var cashOnDeliveryAmount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown3 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown4 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new CMSG_SEND_MAIL {
            Mailbox = mailbox,
            Receiver = receiver,
            Subject = subject,
            Body = body,
            Unknown1 = unknown1,
            Unknown2 = unknown2,
            Item = item,
            Money = money,
            CashOnDeliveryAmount = cashOnDeliveryAmount,
            Unknown3 = unknown3,
            Unknown4 = unknown4,
        };
    }

    internal int Size() {
        var size = 0;

        // mailbox: Generator.Generated.DataTypeGuid
        size += 8;

        // receiver: Generator.Generated.DataTypeCstring
        size += Receiver.Length + 1;

        // subject: Generator.Generated.DataTypeCstring
        size += Subject.Length + 1;

        // body: Generator.Generated.DataTypeCstring
        size += Body.Length + 1;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown2: Generator.Generated.DataTypeInteger
        size += 4;

        // item: Generator.Generated.DataTypeGuid
        size += 8;

        // money: Generator.Generated.DataTypeGold
        size += 4;

        // cash_on_delivery_amount: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown3: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown4: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

