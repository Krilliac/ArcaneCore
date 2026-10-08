using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_INIT_WORLD_STATES: VanillaServerMessage, IWorldMessage {
    public required Vanilla.Map Map { get; set; }
    public required Vanilla.Area Area { get; set; }
    public required List<WorldState> States { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort((ushort)States.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in States) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 706, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 706, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_INIT_WORLD_STATES> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfStates = await r.ReadUShort(cancellationToken).ConfigureAwait(false);

        var states = new List<WorldState>();
        for (var i = 0; i < amountOfStates; ++i) {
            states.Add(await Vanilla.WorldState.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_INIT_WORLD_STATES {
            Map = map,
            Area = area,
            States = states,
        };
    }

    internal int Size() {
        var size = 0;

        // map: Generator.Generated.DataTypeEnum
        size += 4;

        // area: Generator.Generated.DataTypeEnum
        size += 4;

        // amount_of_states: Generator.Generated.DataTypeInteger
        size += 2;

        // states: Generator.Generated.DataTypeArray
        size += States.Sum(e => 8);

        return size;
    }

}

