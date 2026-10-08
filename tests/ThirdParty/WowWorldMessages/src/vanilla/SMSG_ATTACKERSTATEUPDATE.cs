using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ATTACKERSTATEUPDATE: VanillaServerMessage, IWorldMessage {
    public required Vanilla.HitInfo HitInfo { get; set; }
    public required ulong Attacker { get; set; }
    public required ulong Target { get; set; }
    public required uint TotalDamage { get; set; }
    public required List<DamageInfo> Damages { get; set; }
    public required uint DamageState { get; set; }
    public required uint Unknown1 { get; set; }
    /// <summary>
    /// vmangos: spell id, seen with heroic strike and disarm as examples
    /// </summary>
    public required uint SpellId { get; set; }
    public required uint BlockedAmount { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)HitInfo, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Attacker, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Target, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TotalDamage, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Damages.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Damages) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(DamageState, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellId, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BlockedAmount, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 330, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 330, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ATTACKERSTATEUPDATE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var hitInfo = (Vanilla.HitInfo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var attacker = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var target = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var totalDamage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfDamages = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var damages = new List<DamageInfo>();
        for (var i = 0; i < amountOfDamages; ++i) {
            damages.Add(await Vanilla.DamageInfo.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var damageState = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var spellId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var blockedAmount = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_ATTACKERSTATEUPDATE {
            HitInfo = hitInfo,
            Attacker = attacker,
            Target = target,
            TotalDamage = totalDamage,
            Damages = damages,
            DamageState = damageState,
            Unknown1 = unknown1,
            SpellId = spellId,
            BlockedAmount = blockedAmount,
        };
    }

    internal int Size() {
        var size = 0;

        // hit_info: Generator.Generated.DataTypeEnum
        size += 4;

        // attacker: Generator.Generated.DataTypePackedGuid
        size += Attacker.PackedGuidLength();

        // target: Generator.Generated.DataTypePackedGuid
        size += Target.PackedGuidLength();

        // total_damage: Generator.Generated.DataTypeInteger
        size += 4;

        // amount_of_damages: Generator.Generated.DataTypeInteger
        size += 1;

        // damages: Generator.Generated.DataTypeArray
        size += Damages.Sum(e => 20);

        // damage_state: Generator.Generated.DataTypeInteger
        size += 4;

        // unknown1: Generator.Generated.DataTypeInteger
        size += 4;

        // spell_id: Generator.Generated.DataTypeInteger
        size += 4;

        // blocked_amount: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

