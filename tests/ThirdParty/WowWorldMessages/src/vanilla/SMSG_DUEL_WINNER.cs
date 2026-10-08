using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_DUEL_WINNER: VanillaServerMessage, IWorldMessage {
    public required Vanilla.DuelWinnerReason Reason { get; set; }
    public required string OpponentName { get; set; }
    public required string InitiatorName { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Reason, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(OpponentName, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(InitiatorName, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 363, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 363, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_DUEL_WINNER> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var reason = (Vanilla.DuelWinnerReason)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var opponentName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var initiatorName = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_DUEL_WINNER {
            Reason = reason,
            OpponentName = opponentName,
            InitiatorName = initiatorName,
        };
    }

    internal int Size() {
        var size = 0;

        // reason: Generator.Generated.DataTypeEnum
        size += 1;

        // opponent_name: Generator.Generated.DataTypeCstring
        size += OpponentName.Length + 1;

        // initiator_name: Generator.Generated.DataTypeCstring
        size += InitiatorName.Length + 1;

        return size;
    }

}

