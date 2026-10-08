using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_SET_ACTION_BUTTON: VanillaClientMessage, IWorldMessage {
    public required byte Button { get; set; }
    public required ushort Action { get; set; }
    public required byte Misc { get; set; }
    public required byte ActionType { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte(Button, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort(Action, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Misc, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ActionType, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, 9, 296, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, 9, 296, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_SET_ACTION_BUTTON> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var button = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var action = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var misc = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var actionType = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new CMSG_SET_ACTION_BUTTON {
            Button = button,
            Action = action,
            Misc = misc,
            ActionType = actionType,
        };
    }

}

