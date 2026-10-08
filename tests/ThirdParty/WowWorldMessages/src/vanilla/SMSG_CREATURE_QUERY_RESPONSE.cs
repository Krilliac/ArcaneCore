using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CREATURE_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// When the `found` optional is not present all emulators bitwise OR the entry with `0x80000000`.``
    /// </summary>
    public required uint CreatureEntry { get; set; }
    public struct OptionalFound {
        public required string Name1 { get; set; }
        public required string Name2 { get; set; }
        public required string Name3 { get; set; }
        public required string Name4 { get; set; }
        public required string SubName { get; set; }
        public required uint TypeFlags { get; set; }
        /// <summary>
        /// cmangos: CreatureType.dbc   wdbFeild8
        /// </summary>
        public required uint CreatureType { get; set; }
        public required Vanilla.CreatureFamily CreatureFamily { get; set; }
        /// <summary>
        /// cmangos: Creature Rank (elite, boss, etc)
        /// </summary>
        public required uint CreatureRank { get; set; }
        /// <summary>
        /// cmangos: wdbFeild11
        /// </summary>
        public required uint Unknown0 { get; set; }
        /// <summary>
        /// cmangos: Id from CreatureSpellData.dbc wdbField12
        /// </summary>
        public required uint SpellDataId { get; set; }
        /// <summary>
        /// cmangos: DisplayID      wdbFeild13 and workaround, way to manage models must be fixed
        /// </summary>
        public required uint DisplayId { get; set; }
        /// <summary>
        /// cmangos: wdbFeild14
        /// </summary>
        public required byte Civilian { get; set; }
        public required byte RacialLeader { get; set; }
    }
    public required OptionalFound? Found { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(CreatureEntry, cancellationToken).ConfigureAwait(false);

        if (Found is { } found) {
            await w.WriteCString(found.Name1, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name2, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name3, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.Name4, cancellationToken).ConfigureAwait(false);

            await w.WriteCString(found.SubName, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.TypeFlags, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.CreatureType, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)found.CreatureFamily, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.CreatureRank, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.Unknown0, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.SpellDataId, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(found.DisplayId, cancellationToken).ConfigureAwait(false);

            await w.WriteByte(found.Civilian, cancellationToken).ConfigureAwait(false);

            await w.WriteByte(found.RacialLeader, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 97, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 97, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CREATURE_QUERY_RESPONSE> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var creatureEntry = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        OptionalFound? optionalFound = null;
        if (__size < bodySize) {
            var name1 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name1.Length + 1;

            var name2 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name2.Length + 1;

            var name3 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name3.Length + 1;

            var name4 = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += name4.Length + 1;

            var subName = await r.ReadCString(cancellationToken).ConfigureAwait(false);
            __size += subName.Length + 1;

            var typeFlags = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var creatureType = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var creatureFamily = (Vanilla.CreatureFamily)await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var creatureRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var unknown0 = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var spellDataId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var displayId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
            __size += 4;

            var civilian = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var racialLeader = await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            optionalFound = new OptionalFound {
                Name1 = name1,
                Name2 = name2,
                Name3 = name3,
                Name4 = name4,
                SubName = subName,
                TypeFlags = typeFlags,
                CreatureType = creatureType,
                CreatureFamily = creatureFamily,
                CreatureRank = creatureRank,
                Unknown0 = unknown0,
                SpellDataId = spellDataId,
                DisplayId = displayId,
                Civilian = civilian,
                RacialLeader = racialLeader,
            };
        }

        return new SMSG_CREATURE_QUERY_RESPONSE {
            CreatureEntry = creatureEntry,
            Found = optionalFound,
        };
    }

    internal int Size() {
        var size = 0;

        // creature_entry: Generator.Generated.DataTypeInteger
        size += 4;

        if (Found is { } found) {
            // name1: Generator.Generated.DataTypeCstring
            size += found.Name1.Length + 1;

            // name2: Generator.Generated.DataTypeCstring
            size += found.Name2.Length + 1;

            // name3: Generator.Generated.DataTypeCstring
            size += found.Name3.Length + 1;

            // name4: Generator.Generated.DataTypeCstring
            size += found.Name4.Length + 1;

            // sub_name: Generator.Generated.DataTypeCstring
            size += found.SubName.Length + 1;

            // type_flags: Generator.Generated.DataTypeInteger
            size += 4;

            // creature_type: Generator.Generated.DataTypeInteger
            size += 4;

            // creature_family: Generator.Generated.DataTypeEnum
            size += 4;

            // creature_rank: Generator.Generated.DataTypeInteger
            size += 4;

            // unknown0: Generator.Generated.DataTypeInteger
            size += 4;

            // spell_data_id: Generator.Generated.DataTypeInteger
            size += 4;

            // display_id: Generator.Generated.DataTypeInteger
            size += 4;

            // civilian: Generator.Generated.DataTypeInteger
            size += 1;

            // racial_leader: Generator.Generated.DataTypeInteger
            size += 1;

        }
        return size;
    }

}

