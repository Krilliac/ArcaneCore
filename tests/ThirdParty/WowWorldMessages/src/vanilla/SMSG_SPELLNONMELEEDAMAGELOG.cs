using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPELLNONMELEEDAMAGELOG: VanillaServerMessage, IWorldMessage {
    public required ulong Target { get; set; }
    public required ulong Attacker { get; set; }
    public required uint Spell { get; set; }
    public required uint Damage { get; set; }
    public required Vanilla.SpellSchool School { get; set; }
    public required uint AbsorbedDamage { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: sent as int32
    /// </summary>
    public required uint Resisted { get; set; }
    /// <summary>
    /// cmangos/mangoszero/vmangos: if 1, then client show spell name (example: %s's ranged shot hit %s for %u school or %s suffers %u school damage from %s's spell_name
    /// </summary>
    public required bool PeriodicLog { get; set; }
    public required byte Unused { get; set; }
    public required uint Blocked { get; set; }
    public required Vanilla.HitInfo HitInfo { get; set; }
    /// <summary>
    /// cmangos has some that might be correct `https://github.com/cmangos/mangos-classic/blob/524a39412dae7946d06e4b8f319f45b615075815/src/game/Entities/Unit.cpp#L5497`.
    /// </summary>
    public required byte ExtendFlag { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(Target, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Attacker, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Damage, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)School, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(AbsorbedDamage, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Resisted, cancellationToken).ConfigureAwait(false);

        await w.WriteBool8(PeriodicLog, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unused, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Blocked, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)HitInfo, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(ExtendFlag, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 592, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 592, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPELLNONMELEEDAMAGELOG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var target = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var attacker = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var damage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var school = (Vanilla.SpellSchool)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var absorbedDamage = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var resisted = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var periodicLog = await r.ReadBool8(cancellationToken).ConfigureAwait(false);

        var unused = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var blocked = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var hitInfo = (Vanilla.HitInfo)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var extendFlag = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_SPELLNONMELEEDAMAGELOG {
            Target = target,
            Attacker = attacker,
            Spell = spell,
            Damage = damage,
            School = school,
            AbsorbedDamage = absorbedDamage,
            Resisted = resisted,
            PeriodicLog = periodicLog,
            Unused = unused,
            Blocked = blocked,
            HitInfo = hitInfo,
            ExtendFlag = extendFlag,
        };
    }

    internal int Size() {
        var size = 0;

        // target: Generator.Generated.DataTypePackedGuid
        size += Target.PackedGuidLength();

        // attacker: Generator.Generated.DataTypePackedGuid
        size += Attacker.PackedGuidLength();

        // spell: Generator.Generated.DataTypeSpell
        size += 4;

        // damage: Generator.Generated.DataTypeInteger
        size += 4;

        // school: Generator.Generated.DataTypeEnum
        size += 1;

        // absorbed_damage: Generator.Generated.DataTypeInteger
        size += 4;

        // resisted: Generator.Generated.DataTypeInteger
        size += 4;

        // periodic_log: Generator.Generated.DataTypeBool
        size += 1;

        // unused: Generator.Generated.DataTypeInteger
        size += 1;

        // blocked: Generator.Generated.DataTypeInteger
        size += 4;

        // hit_info: Generator.Generated.DataTypeEnum
        size += 4;

        // extend_flag: Generator.Generated.DataTypeInteger
        size += 1;

        return size;
    }

}

