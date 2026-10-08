using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_DISPEL_FAILED: VanillaServerMessage, IWorldMessage {
    public required ulong Caster { get; set; }
    public required ulong Target { get; set; }
    public required List<uint> Spells { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteULong(Target, cancellationToken).ConfigureAwait(false);

        foreach (var v in Spells) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 610, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 610, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_DISPEL_FAILED> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var caster = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        var target = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        var spells = new List<uint>();
        while (__size < bodySize) {
            spells.Add(await r.ReadUInt(cancellationToken).ConfigureAwait(false));
            __size += 4;
        }

        return new SMSG_DISPEL_FAILED {
            Caster = caster,
            Target = target,
            Spells = spells,
        };
    }

    internal int Size() {
        var size = 0;

        // caster: Generator.Generated.DataTypeGuid
        size += 8;

        // target: Generator.Generated.DataTypeGuid
        size += 8;

        // spells: Generator.Generated.DataTypeArray
        size += Spells.Sum(e => 4);

        return size;
    }

}

