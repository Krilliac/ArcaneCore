using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_SPELL_GO: VanillaServerMessage, IWorldMessage {
    public class CastFlagsType {
        public required CastFlags Inner;
        public CastFlagsAmmo? Ammo;
    }
    public class CastFlagsAmmo {
        public required uint AmmoDisplayId { get; set; }
        public required uint AmmoInventoryType { get; set; }
    }
    /// <summary>
    /// cmangos/vmangos/mangoszero: if cast item is used, set this to guid of cast item, otherwise set it to same as caster.
    /// </summary>
    public required ulong CastItem { get; set; }
    public required ulong Caster { get; set; }
    public required uint Spell { get; set; }
    public required CastFlagsType Flags { get; set; }
    public required List<ulong> Hits { get; set; }
    public required List<SpellMiss> Misses { get; set; }
    public required SpellCastTargets Targets { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WritePackedGuid(CastItem, cancellationToken).ConfigureAwait(false);

        await w.WritePackedGuid(Caster, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteUShort((ushort)Flags.Inner, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)Hits.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Hits) {
            await w.WriteULong(v, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteByte((byte)Misses.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Misses) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await Targets.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);

        if (Flags.Ammo is {} castFlagsAmmo) {
            await w.WriteUInt(castFlagsAmmo.AmmoDisplayId, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(castFlagsAmmo.AmmoInventoryType, cancellationToken).ConfigureAwait(false);

        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 306, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 306, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_SPELL_GO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var castItem = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var caster = await r.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var flags = new CastFlagsType {
            Inner = (CastFlags)await r.ReadUShort(cancellationToken).ConfigureAwait(false),
        };

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfHits = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var hits = new List<ulong>();
        for (var i = 0; i < amountOfHits; ++i) {
            hits.Add(await r.ReadULong(cancellationToken).ConfigureAwait(false));
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfMisses = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var misses = new List<SpellMiss>();
        for (var i = 0; i < amountOfMisses; ++i) {
            misses.Add(await Vanilla.SpellMiss.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        var targets = await SpellCastTargets.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false);

        if (flags.Inner.HasFlag(Vanilla.CastFlags.Ammo)) {
            var ammoDisplayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var ammoInventoryType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            flags.Ammo = new CastFlagsAmmo {
                AmmoDisplayId = ammoDisplayId,
                AmmoInventoryType = ammoInventoryType,
            };
        }

        return new SMSG_SPELL_GO {
            CastItem = castItem,
            Caster = caster,
            Spell = spell,
            Flags = flags,
            Hits = hits,
            Misses = misses,
            Targets = targets,
        };
    }

    internal int Size() {
        var size = 0;

        // cast_item: Generator.Generated.DataTypePackedGuid
        size += CastItem.PackedGuidLength();

        // caster: Generator.Generated.DataTypePackedGuid
        size += Caster.PackedGuidLength();

        // spell: Generator.Generated.DataTypeSpell
        size += 4;

        // flags: Generator.Generated.DataTypeFlag
        size += 2;

        // amount_of_hits: Generator.Generated.DataTypeInteger
        size += 1;

        // hits: Generator.Generated.DataTypeArray
        size += Hits.Sum(e => 8);

        // amount_of_misses: Generator.Generated.DataTypeInteger
        size += 1;

        // misses: Generator.Generated.DataTypeArray
        size += Misses.Sum(e => 9);

        // targets: Generator.Generated.DataTypeStruct
        size += Targets.Size();

        if (Flags.Ammo is {} castFlagsAmmo) {
            // ammo_display_id: Generator.Generated.DataTypeInteger
            size += 4;

            // ammo_inventory_type: Generator.Generated.DataTypeInteger
            size += 4;

        }

        return size;
    }

}

