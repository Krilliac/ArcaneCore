using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SET_PROFICIENCY: VanillaServerMessage, IWorldMessage {
    public required Vanilla.ItemClass ClassType { get; set; }
    public required uint ItemSubClassMask { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)ClassType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ItemSubClassMask, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 7, 295, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 7, 295, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SET_PROFICIENCY> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var classType = (Vanilla.ItemClass)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var itemSubClassMask = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_SET_PROFICIENCY {
            ClassType = classType,
            ItemSubClassMask = itemSubClassMask,
        };
    }

}

