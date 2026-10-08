using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_LIST_STABLED_PETS_Server: VanillaServerMessage, IWorldMessage {
    public required ulong Npc { get; set; }
    public required byte StableSlots { get; set; }
    public required List<StabledPet> Pets { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Npc, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Pets.Count, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(StableSlots, cancellationToken).ConfigureAwait(false);

        foreach (var v in Pets) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 623, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 623, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_LIST_STABLED_PETS_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var npc = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfPets = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var stableSlots = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var pets = new List<StabledPet>();
        for (var i = 0; i < amountOfPets; ++i) {
            pets.Add(await Vanilla.StabledPet.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new MSG_LIST_STABLED_PETS_Server {
            Npc = npc,
            StableSlots = stableSlots,
            Pets = pets,
        };
    }

    internal int Size() {
        var size = 0;

        // npc: Generator.Generated.DataTypeGuid
        size += 8;

        // amount_of_pets: Generator.Generated.DataTypeInteger
        size += 1;

        // stable_slots: Generator.Generated.DataTypeInteger
        size += 1;

        // pets: Generator.Generated.DataTypeArray
        size += Pets.Sum(e => e.Size());

        return size;
    }

}

