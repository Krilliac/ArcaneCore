namespace ArcaneCore.Data.World.GameObjects;

/// <summary>
/// <c>gameobject_template</c>: column meanings from cmangos-classic mangos.sql and vmangos
/// GameObjectInfo (GameObject.h); see <see cref="Kernel.WorldData.GameObjects.GameObjectTemplate"/>.
/// </summary>
public sealed class GameObjectTemplateRow
{
    public uint Entry { get; set; }

    public uint Type { get; set; }

    public uint DisplayId { get; set; }

    public string Name { get; set; } = string.Empty;

    public uint Faction { get; set; }

    public uint Flags { get; set; }

    public float Size { get; set; } = 1.0f;

    public uint Data0 { get; set; }

    public uint Data1 { get; set; }

    public uint Data2 { get; set; }

    public uint Data3 { get; set; }

    public uint Data4 { get; set; }

    public uint Data5 { get; set; }

    public uint Data6 { get; set; }

    public uint Data7 { get; set; }

    public uint Data8 { get; set; }

    public uint Data9 { get; set; }

    public uint Data10 { get; set; }

    public uint Data11 { get; set; }

    public uint Data12 { get; set; }

    public uint Data13 { get; set; }

    public uint Data14 { get; set; }

    public uint Data15 { get; set; }

    public uint Data16 { get; set; }

    public uint Data17 { get; set; }

    public uint Data18 { get; set; }

    public uint Data19 { get; set; }

    public uint Data20 { get; set; }

    public uint Data21 { get; set; }

    public uint Data22 { get; set; }

    public uint Data23 { get; set; }

    public uint[] GetData() =>
    [
        Data0, Data1, Data2, Data3, Data4, Data5, Data6, Data7, Data8, Data9, Data10, Data11,
        Data12, Data13, Data14, Data15, Data16, Data17, Data18, Data19, Data20, Data21, Data22, Data23,
    ];

    public void SetData(IReadOnlyList<uint> data)
    {
        uint D(int i) => i < data.Count ? data[i] : 0;
        (Data0, Data1, Data2, Data3, Data4, Data5, Data6, Data7) = (D(0), D(1), D(2), D(3), D(4), D(5), D(6), D(7));
        (Data8, Data9, Data10, Data11, Data12, Data13, Data14, Data15) = (D(8), D(9), D(10), D(11), D(12), D(13), D(14), D(15));
        (Data16, Data17, Data18, Data19, Data20, Data21, Data22, Data23) = (D(16), D(17), D(18), D(19), D(20), D(21), D(22), D(23));
    }
}

/// <summary><c>gameobject_spawn</c>: one placed object (cmangos/vmangos <c>gameobject</c>).</summary>
public sealed class GameObjectSpawnRow
{
    public uint Guid { get; set; }

    public uint Entry { get; set; }

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float Orientation { get; set; }

    public float Rotation0 { get; set; }

    public float Rotation1 { get; set; }

    public float Rotation2 { get; set; }

    public float Rotation3 { get; set; }

    public int SpawnTimeSeconds { get; set; } = 300;

    /// <summary><c>spawntimesecsmax</c>: the upper bound of the respawn delay; null means the same as <see cref="SpawnTimeSeconds"/> (world schema 18, <see cref="GameObjectSpawnDataModule"/>).</summary>
    public int? SpawnTimeMaxSeconds { get; set; }

    /// <summary>vmangos <c>spawn_flags</c> (world schema 18); 0 when the dump has none.</summary>
    public uint SpawnFlags { get; set; }

    public uint AnimProgress { get; set; } = 100;

    public byte State { get; set; } = 1;
}

/// <summary><c>gameobject_questrelation</c>: the object entry <see cref="Id"/> starts <see cref="Quest"/>.</summary>
public sealed class GameObjectQuestStarterRow
{
    public uint Id { get; set; }

    public uint Quest { get; set; }
}

/// <summary><c>gameobject_involvedrelation</c>: the object entry <see cref="Id"/> ends <see cref="Quest"/>.</summary>
public sealed class GameObjectQuestEnderRow
{
    public uint Id { get; set; }

    public uint Quest { get; set; }
}

/// <summary><c>lock_template</c>: one Lock.dbc record (imported by <see cref="GameObjectLootDumpImporter.ReadLocks"/>).</summary>
public sealed class LockTemplateRow
{
    public uint Id { get; set; }

    public uint Type1 { get; set; }

    public uint Type2 { get; set; }

    public uint Type3 { get; set; }

    public uint Type4 { get; set; }

    public uint Type5 { get; set; }

    public uint Type6 { get; set; }

    public uint Type7 { get; set; }

    public uint Type8 { get; set; }

    public uint Index1 { get; set; }

    public uint Index2 { get; set; }

    public uint Index3 { get; set; }

    public uint Index4 { get; set; }

    public uint Index5 { get; set; }

    public uint Index6 { get; set; }

    public uint Index7 { get; set; }

    public uint Index8 { get; set; }

    public uint Skill1 { get; set; }

    public uint Skill2 { get; set; }

    public uint Skill3 { get; set; }

    public uint Skill4 { get; set; }

    public uint Skill5 { get; set; }

    public uint Skill6 { get; set; }

    public uint Skill7 { get; set; }

    public uint Skill8 { get; set; }

    public (uint[] Types, uint[] Indexes, uint[] Skills) Get() => (
        [Type1, Type2, Type3, Type4, Type5, Type6, Type7, Type8],
        [Index1, Index2, Index3, Index4, Index5, Index6, Index7, Index8],
        [Skill1, Skill2, Skill3, Skill4, Skill5, Skill6, Skill7, Skill8]);

    public void Set(IReadOnlyList<uint> types, IReadOnlyList<uint> indexes, IReadOnlyList<uint> skills)
    {
        static uint V(IReadOnlyList<uint> a, int i) => i < a.Count ? a[i] : 0;
        (Type1, Type2, Type3, Type4, Type5, Type6, Type7, Type8) = (V(types, 0), V(types, 1), V(types, 2), V(types, 3), V(types, 4), V(types, 5), V(types, 6), V(types, 7));
        (Index1, Index2, Index3, Index4, Index5, Index6, Index7, Index8) = (V(indexes, 0), V(indexes, 1), V(indexes, 2), V(indexes, 3), V(indexes, 4), V(indexes, 5), V(indexes, 6), V(indexes, 7));
        (Skill1, Skill2, Skill3, Skill4, Skill5, Skill6, Skill7, Skill8) = (V(skills, 0), V(skills, 1), V(skills, 2), V(skills, 3), V(skills, 4), V(skills, 5), V(skills, 6), V(skills, 7));
    }
}

/// <summary>Columns shared by every <c>*_loot_template</c> table (cmangos/vmangos layout).</summary>
public abstract class LootTemplateRowBase
{
    public uint Entry { get; set; }

    public uint Item { get; set; }

    public float ChanceOrQuestChance { get; set; } = 100f;

    public byte GroupId { get; set; }

    public int MinCountOrRef { get; set; } = 1;

    public uint MaxCount { get; set; } = 1;

    public uint ConditionId { get; set; }
}

/// <summary><c>creature_loot_template</c>.</summary>
public sealed class CreatureLootTemplateRow : LootTemplateRowBase;

/// <summary><c>gameobject_loot_template</c>.</summary>
public sealed class GameObjectLootTemplateRow : LootTemplateRowBase;

/// <summary><c>item_loot_template</c>.</summary>
public sealed class ItemLootTemplateRow : LootTemplateRowBase;

/// <summary><c>skinning_loot_template</c>.</summary>
public sealed class SkinningLootTemplateRow : LootTemplateRowBase;

/// <summary><c>reference_loot_template</c>.</summary>
public sealed class ReferenceLootTemplateRow : LootTemplateRowBase;

/// <summary><c>creature_loot_info</c>: the loot columns of creature_template, owned by the loot module.</summary>
public sealed class CreatureLootInfoRow
{
    public uint Entry { get; set; }

    public uint LootId { get; set; }

    public uint SkinningLootId { get; set; }

    public uint MinGold { get; set; }

    public uint MaxGold { get; set; }
}
