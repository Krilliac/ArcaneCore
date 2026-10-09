using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.Tests.SpawnGroups;

/// <summary>
/// cmangos spawn groups and alternative entries as classic-db 1.12.1 z2815 (D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz) has
/// them: the group, member, entry and flag values are the dump's rows; the members' positions are moved next to the test player (the
/// dump's are hundreds of yards apart, outside the test map's loaded grid), keeping their order. The dump is GPL data and is not
/// committed; these few values are fixtures, as the other real-row tests keep theirs.
/// <list type="bullet">
/// <item>Group 1 "Western Plaguelands (Ruins of Andorhal) - Musty Tome (176150,176151)": game objects, MaxCount 0, ten spawns 45459-45468
/// (all <c>id</c> 0), entries 176150 (MaxCount 1) and 176151 (MaxCount 9), both Chance 0: all ten spots, one real tome.</item>
/// <item>Group 21 "Western Plaguelands - Mithril Deposit | Gold Vein | Truesilver Deposit (1) Ore 000": game objects, MaxCount 1, five
/// spawns 78606-78618 (<c>id</c> 0), entries 1734 Gold Vein (Chance 5), 2040 Mithril Deposit (Chance 0), 2047 Truesilver Deposit (Chance 5).</item>
/// <item>Group 44 "Wetlands - Balgaras the Foul (1) Wandering 000": creatures, MaxCount 1, seven spawns 11000-11006 (<c>id</c> 0, 300 s),
/// each with <c>creature_spawn_entry</c> 1364 (Balgaras the Foul), no group entries.</item>
/// <item>Group 19008 "AQ War Effort (10 Hour War) - Qiraji Major He'al-ie 15816 &amp; Qiraji Major 15750 (4)": creatures, MaxCount 0,
/// WorldState condition 2099 ("Game Event 123 Active"), four spawns (<c>id</c> 0, 600 s, no <c>creature_spawn_entry</c>: four of the
/// 568), entries 15750 (Chance 0) and 15816 (MinCount 1, MaxCount 1).</item>
/// <item>Group 19995 "Barrens - 4 Random Kodos - Patrol - 1": creatures, Flags 1 (aggro together), four spawns (<c>id</c> 0, 275 s), each
/// with <c>creature_spawn_entry</c> 3235, 3236, 3237.</item>
/// <item>Group 2 "Kargath Expeditionary Force ...": creatures, Flags 3 (aggro and respawn together), five spawns with their own
/// <c>id</c> (9082-9086, 300 s), a formation (fanned out behind, spread 4, path 6883: FormationTests).</item>
/// <item><c>gameobject_spawn_entry</c> of spawn 11427 (<c>id</c> 0): 126049 and 128293, both "Magenta Cap Clusters".</item>
/// </list>
/// </summary>
internal static class ClassicDbSpawnGroupRows
{
    public const uint RealTome = 176150;
    public const uint FakeTome = 176151;
    public const uint GoldVein = 1734;
    public const uint MithrilDeposit = 2040;
    public const uint TruesilverDeposit = 2047;
    public const uint Balgaras = 1364;
    public const uint QirajiMajor = 15750;
    public const uint MajorHealie = 15816;
    public const uint MagentaCapA = 126049;
    public const uint MagentaCapB = 128293;

    public static readonly uint[] KodoEntries = [3235, 3236, 3237];

    public static readonly uint[] TomeSpawns = [45459, 45460, 45461, 45462, 45463, 45464, 45465, 45466, 45467, 45468];

    public static readonly uint[] OreSpawns = [78606, 78609, 78612, 78615, 78618];

    public static readonly uint[] BalgarasSpawns = [11000, 11001, 11002, 11003, 11004, 11005, 11006];

    public static readonly uint[] QirajiSpawns = [155391, 155404, 155417, 155703];

    public static readonly uint[] KodoSpawns = [15135, 15141, 15142, 15144];

    public static readonly (uint Guid, uint Entry)[] KargathSpawns = [(6877, 9085), (6880, 9083), (6883, 9086), (6885, 9082), (6886, 9084)];

    public static SpawnGroupDefinition MustyTome => new()
    {
        Id = 1,
        Name = "Western Plaguelands (Ruins of Andorhal) - Musty Tome (176150,176151)",
        Type = SpawnGroupType.GameObject,
        Members = [.. TomeSpawns.Select(g => new SpawnGroupMember(g, -1, 0))],
        RandomEntries = [new SpawnGroupRandomEntry(RealTome, 0, 1, 0), new SpawnGroupRandomEntry(FakeTome, 0, 9, 0)],
    };

    public static SpawnGroupDefinition Ore => new()
    {
        Id = 21,
        Name = "Western Plaguelands - Mithril Deposit | Gold Vein | Truesilver Deposit (1) Ore 000",
        Type = SpawnGroupType.GameObject,
        MaxCount = 1,
        Members = [.. OreSpawns.Select(g => new SpawnGroupMember(g, -1, 0))],
        RandomEntries =
        [
            new SpawnGroupRandomEntry(GoldVein, 0, 0, 5),
            new SpawnGroupRandomEntry(MithrilDeposit, 0, 0, 0),
            new SpawnGroupRandomEntry(TruesilverDeposit, 0, 0, 5),
        ],
    };

    public static SpawnGroupDefinition BalgarasTheFoul => new()
    {
        Id = 44,
        Name = "Wetlands - Balgaras the Foul (1) Wandering 000",
        Type = SpawnGroupType.Creature,
        MaxCount = 1,
        Members = [.. BalgarasSpawns.Select((g, i) => new SpawnGroupMember(g, i, 0))],
    };

    public static SpawnGroupDefinition QirajiMajors => new()
    {
        Id = 19008,
        Name = "AQ War Effort (10 Hour War) - Qiraji Major He'al-ie 15816 & Qiraji Major 15750 (4)",
        Type = SpawnGroupType.Creature,
        WorldStateCondition = 2099,
        Members = [.. QirajiSpawns.Select(g => new SpawnGroupMember(g, -1, 0))],
        RandomEntries = [new SpawnGroupRandomEntry(QirajiMajor, 0, 0, 0), new SpawnGroupRandomEntry(MajorHealie, 1, 1, 0)],
    };

    public static SpawnGroupDefinition RandomKodos => new()
    {
        Id = 19995,
        Name = "Barrens - 4 Random Kodos - Patrol - 1",
        Type = SpawnGroupType.Creature,
        Flags = SpawnGroupFlags.AggroTogether,
        Members = [.. KodoSpawns.Select((g, i) => new SpawnGroupMember(g, i, 0))],
    };

    public static SpawnGroupDefinition KargathExpeditionaryForce => new()
    {
        Id = 2,
        Name = "Kargath Expeditionary Force c.entry 9082,9083,9084,9085,9086 & Linked to 9077 for RP",
        Type = SpawnGroupType.Creature,
        Flags = SpawnGroupFlags.AggroTogether | SpawnGroupFlags.RespawnTogether,
        Members = [new(6877, 3, 0), new(6880, 4, 0), new(6883, 0, 0), new(6885, 2, 0), new(6886, 1, 0)],
        Formation = new SpawnGroupFormation(4, 4, 0, 6883, 2, "Kargath Expeditionary Force"),
    };
}
