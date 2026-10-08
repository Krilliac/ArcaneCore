using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_TRAINER_LIST: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required uint TrainerType { get; set; }
    public required List<TrainerSpell> Spells { get; set; }
    public required string Greeting { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TrainerType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Spells.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Spells) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteCString(Greeting, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 433, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 433, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_TRAINER_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var trainerType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfSpells = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spells = new List<TrainerSpell>();
        for (var i = 0; i < amountOfSpells; ++i) {
            spells.Add(await Vanilla.TrainerSpell.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var greeting = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new SMSG_TRAINER_LIST {
            Guid = guid,
            TrainerType = trainerType,
            Spells = spells,
            Greeting = greeting,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // trainer_type: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_spells: Generator.Generated.DataTypeInteger
        size += 4;

        // spells: Generator.Generated.DataTypeArray
        size += Spells.Sum(e => 38);

        // greeting: Generator.Generated.DataTypeCstring
        size += Greeting.Length + 1;

        return size;
    }

}

