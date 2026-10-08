using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PERIODICAURALOG: VanillaServerMessage, IWorldMessage {
    public required ulong Target { get; set; }
    public required ulong Caster { get; set; }
    public required uint Spell { get; set; }
    public required List<AuraLog> Auras { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Target, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Auras.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Auras) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 590, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 590, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PERIODICAURALOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var target = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var caster = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfAuras = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auras = new List<AuraLog>();
        for (var i = 0; i < amountOfAuras; ++i) {
            auras.Add(await Vanilla.AuraLog.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_PERIODICAURALOG {
            Target = target,
            Caster = caster,
            Spell = spell,
            Auras = auras,
        };
    }

    internal int Size() {
        var size = 0;

        // target: Generator.Generated.DataTypePackedGuid
        size += Target.PackedGuidLength();

        // caster: Generator.Generated.DataTypePackedGuid
        size += Caster.PackedGuidLength();

        // spell: Generator.Generated.DataTypeSpell
        size += 4;

        // amount_of_auras: Generator.Generated.DataTypeInteger
        size += 4;

        // auras: Generator.Generated.DataTypeArray
        size += Auras.Sum(e => e.Size());

        return size;
    }

}

