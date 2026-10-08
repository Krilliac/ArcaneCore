using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_BATTLEGROUND_PLAYER_POSITIONS_Server: VanillaServerMessage, IWorldMessage {
    public required List<BattlegroundPlayerPosition> Teammates { get; set; }
    public required List<BattlegroundPlayerPosition> Carriers { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Teammates.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Teammates) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteByte((byte)Carriers.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Carriers) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 745, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 745, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_BATTLEGROUND_PLAYER_POSITIONS_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfTeammates = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var teammates = new List<BattlegroundPlayerPosition>();
        for (var i = 0; i < amountOfTeammates; ++i) {
            teammates.Add(await Vanilla.BattlegroundPlayerPosition.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfCarriers = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var carriers = new List<BattlegroundPlayerPosition>();
        for (var i = 0; i < amountOfCarriers; ++i) {
            carriers.Add(await Vanilla.BattlegroundPlayerPosition.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new MSG_BATTLEGROUND_PLAYER_POSITIONS_Server {
            Teammates = teammates,
            Carriers = carriers,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_teammates: Generator.Generated.DataTypeInteger
        size += 4;

        // teammates: Generator.Generated.DataTypeArray
        size += Teammates.Sum(e => 16);

        // amount_of_carriers: Generator.Generated.DataTypeInteger
        size += 1;

        // carriers: Generator.Generated.DataTypeArray
        size += Carriers.Sum(e => 16);

        return size;
    }

}

