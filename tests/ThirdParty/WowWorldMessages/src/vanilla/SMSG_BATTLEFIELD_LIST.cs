using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_BATTLEFIELD_LIST: VanillaServerMessage, IWorldMessage {
    public required ulong Battlemaster { get; set; }
    public required Vanilla.Map Map { get; set; }
    public required Vanilla.BattlegroundBracket Bracket { get; set; }
    public required List<uint> Battlegrounds { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Battlemaster, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Bracket, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Battlegrounds.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Battlegrounds) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 573, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 573, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_BATTLEFIELD_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var battlemaster = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var bracket = (Vanilla.BattlegroundBracket)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var numberOfBattlegrounds = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var battlegrounds = new List<uint>();
        for (var i = 0; i < numberOfBattlegrounds; ++i) {
            battlegrounds.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_BATTLEFIELD_LIST {
            Battlemaster = battlemaster,
            Map = map,
            Bracket = bracket,
            Battlegrounds = battlegrounds,
        };
    }

    internal int Size() {
        var size = 0;

        // battlemaster: Generator.Generated.DataTypeGuid
        size += 8;

        // map: Generator.Generated.DataTypeEnum
        size += 4;

        // bracket: Generator.Generated.DataTypeEnum
        size += 1;

        // number_of_battlegrounds: Generator.Generated.DataTypeInteger
        size += 4;

        // battlegrounds: Generator.Generated.DataTypeArray
        size += Battlegrounds.Sum(e => 4);

        return size;
    }

}

