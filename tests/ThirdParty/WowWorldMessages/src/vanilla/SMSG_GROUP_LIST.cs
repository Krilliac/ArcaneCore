using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GROUP_LIST: VanillaServerMessage, IWorldMessage {
    public required Vanilla.GroupType GroupType { get; set; }
    /// <summary>
    /// mangoszero/cmangos/vmangos: own flags (groupid | (assistant?0x80:0))
    /// </summary>
    public required byte Flags { get; set; }
    public required List<GroupListMember> Members { get; set; }
    public required ulong Leader { get; set; }
    public struct OptionalGroupNotEmpty {
        public required Vanilla.GroupLootSetting LootSetting { get; set; }
        /// <summary>
        /// Zero if loot_setting is not MASTER_LOOT
        /// </summary>
        public required ulong MasterLoot { get; set; }
        public required Vanilla.ItemQuality LootThreshold { get; set; }
    }
    public required OptionalGroupNotEmpty? GroupNotEmpty { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)GroupType, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Flags, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Members.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Members) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteULong(Leader, cancellationToken).ConfigureAwait(false);

        if (GroupNotEmpty is { } groupNotEmpty) {
            await w.WriteByte((byte)groupNotEmpty.LootSetting, cancellationToken).ConfigureAwait(false);

            await w.WriteULong(groupNotEmpty.MasterLoot, cancellationToken).ConfigureAwait(false);

            await w.WriteByte((byte)groupNotEmpty.LootThreshold, cancellationToken).ConfigureAwait(false);

        }
    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 125, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 125, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GROUP_LIST> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var groupType = (Vanilla.GroupType)await r.ReadByte(cancellationToken).ConfigureAwait(false);
        __size += 1;

        var flags = await r.ReadByte(cancellationToken).ConfigureAwait(false);
        __size += 1;

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfMembers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        __size += 4;

        var members = new List<GroupListMember>();
        for (var i = 0; i < amountOfMembers; ++i) {
            members.Add(await Vanilla.GroupListMember.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
            __size += members[^1].Size();
        }

        var leader = await r.ReadULong(cancellationToken).ConfigureAwait(false);
        __size += 8;

        OptionalGroupNotEmpty? optionalGroupNotEmpty = null;
        if (__size < bodySize) {
            var lootSetting = (Vanilla.GroupLootSetting)await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            var masterLoot = await r.ReadULong(cancellationToken).ConfigureAwait(false);
            __size += 8;

            var lootThreshold = (Vanilla.ItemQuality)await r.ReadByte(cancellationToken).ConfigureAwait(false);
            __size += 1;

            optionalGroupNotEmpty = new OptionalGroupNotEmpty {
                LootSetting = lootSetting,
                MasterLoot = masterLoot,
                LootThreshold = lootThreshold,
            };
        }

        return new SMSG_GROUP_LIST {
            GroupType = groupType,
            Flags = flags,
            Members = members,
            Leader = leader,
            GroupNotEmpty = optionalGroupNotEmpty,
        };
    }

    internal int Size() {
        var size = 0;

        // group_type: Generator.Generated.DataTypeEnum
        size += 1;

        // flags: Generator.Generated.DataTypeInteger
        size += 1;

        // amount_of_members: Generator.Generated.DataTypeInteger
        size += 4;

        // members: Generator.Generated.DataTypeArray
        size += Members.Sum(e => e.Size());

        // leader: Generator.Generated.DataTypeGuid
        size += 8;

        if (GroupNotEmpty is { } groupNotEmpty) {
            // loot_setting: Generator.Generated.DataTypeEnum
            size += 1;

            // master_loot: Generator.Generated.DataTypeGuid
            size += 8;

            // loot_threshold: Generator.Generated.DataTypeEnum
            size += 1;

        }
        return size;
    }

}

