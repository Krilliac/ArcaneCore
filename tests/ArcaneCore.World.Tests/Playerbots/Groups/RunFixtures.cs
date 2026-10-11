using System.Numerics;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Real world data for the dungeon and raid run tests. Every row below is copied from the sources the server imports, not made up:
/// <list type="bullet">
/// <item><description>encounters: cmangos classic-db z2815 <c>instance_dungeon_encounters</c> (Difficulty 0) joined to <c>instance_encounters</c>
/// (entry = Id): the DungeonEncounter id, order index, credit type and entry and the <c>lastEncounterDungeon</c> marker;</description></item>
/// <item><description>creatures: classic-db <c>creature</c> (the first static spawn) and <c>creature_template</c> (levels);</description></item>
/// <item><description>teleports: classic-db <c>areatrigger_teleport</c> (level, key item, attunement quest, target);</description></item>
/// <item><description>trigger volumes: the 1.12.1 client's AreaTrigger.dbc (map and centre).</description></item>
/// </list>
/// Maps and their player limits are classic-db <c>instance_template</c> (<c>maxPlayers</c>); the map type is the instance kind of Map.dbc.
/// Only the rows the tests use are carried (the real-data tests read the whole dump).
/// </summary>
internal static class RunFixtures
{
    public const uint Deadmines = 36, RazorfenKraul = 47, ScarletMonastery = 189, BlackrockSpire = 229, OnyxiasLair = 249, RagefireChasm = 389,
        MoltenCore = 409, BlackwingLair = 469, Naxxramas = 533;

    /// <summary>The four Scarlet Monastery wing triggers, classic-db areatrigger_teleport 45 (Graveyard), 610 (Cathedral), 612 (Armory), 614 (Library).</summary>
    public static readonly uint[] ScarletTriggers = [45, 610, 612, 614];

    /// <summary>The Four Horsemen's credit: a spell id (credit type 1), not a creature.</summary>
    public const uint FourHorsemenSpell = 59450;

    public static MapTemplate[] Maps() =>
    [
        .. MapRegistry.DefaultContinents,
        new(Deadmines, 0, MapType.Instance, 0, 10, 0, 0, -11207.8f, 1681.15f, "The Deadmines", "instance_deadmines"),
        new(RazorfenKraul, 0, MapType.Instance, 0, 10, 0, 1, -4459.45f, -1660.21f, "Razorfen Kraul", "instance_razorfen_kraul"),
        new(ScarletMonastery, 0, MapType.Instance, 0, 10, 0, 0, 2892.24f, -811.264f, "Scarlet Monastery", "instance_scarlet_monastery"),
        new(BlackrockSpire, 0, MapType.Instance, 0, 10, 0, 0, 0, 0, "Blackrock Spire", ""),
        new(OnyxiasLair, 0, MapType.Raid, 0, 40, 5, 1, -4753.31f, -3752.42f, "Onyxia's Lair", "instance_onyxias_lair"),
        new(RagefireChasm, 0, MapType.Instance, 0, 10, 0, 1, 1816.76f, -4423.37f, "Ragefire Chasm", ""),
        new(MoltenCore, 0, MapType.Raid, 0, 40, 7, 0, -7510.56f, -1036.7f, "Molten Core", "instance_molten_core"),
        new(BlackwingLair, 0, MapType.Raid, 0, 40, 7, 0, -7663.41f, -1218.67f, "Blackwing Lair", "instance_blackwing_lair"),
        new(Naxxramas, 0, MapType.Raid, 0, 40, 7, 0, 0, 0, "Naxxramas", "instance_naxxramas"),
        new(230, 0, MapType.Instance, 0, 5, 0, 0, 0, 0, "Blackrock Depths", ""),
    ];

    public static MapRegistry Registry() => new(Maps());

    /// <summary>Trigger volumes (AreaTrigger.dbc): id, map, centre, radius.</summary>
    public static AreaTriggerTemplate[] Triggers() =>
    [
        new(45, 0, 2924.38f, -798.429f, 161.611f, 8f, 0, 0, 0, 0, "Scarlet Monastery Graveyard"),
        new(78, 0, -11208.5f, 1685.34f, 25.7612f, 7f, 0, 0, 0, 0, "Deadmines entrance"),
        new(244, 1, -4456.7f, -1655.99f, 86.1094f, 8f, 0, 0, 0, 0, "Razorfen Kraul entrance"),
        new(610, 0, 2925.18f, -820.545f, 161.634f, 8f, 0, 0, 0, 0, "Scarlet Monastery Cathedral"),
        new(612, 0, 2877.98f, -839.267f, 163.049f, 6f, 0, 0, 0, 0, "Scarlet Monastery Armory"),
        new(614, 0, 2859.73f, -824.91f, 162.083f, 8f, 0, 0, 0, 0, "Scarlet Monastery Library"),
        new(2230, 1, 1818.4f, -4427.26f, -10.4478f, 0f, 21.69f, 11.83f, 21.22f, 0.576f, "Ragefire Chasm entrance"),
        new(2848, 1, -4768.61f, -3752.11f, 53.4317f, 16f, 0, 0, 0, 0, "Onyxia's Lair entrance"),
        new(2886, 230, 1097.99f, -466.494f, -96.0718f, 20f, 0, 0, 0, 0, "The Molten Bridge"),
        new(3528, 0, -7510.87f, -1035.87f, 182.952f, 0f, 3.833f, 0.3611f, 2.056f, 0.7156f, "Molten Core Window"),
        new(3529, 0, -7512.16f, -1034.78f, 177.208f, 4f, 0, 0, 0, 0, "Molten Core Window (Lava)"),
        new(3726, 229, 178.729f, -474.412f, 120.565f, 0f, 3.806f, 7.917f, 7.889f, 0f, "Blackwing Lair entrance"),
        new(4156, 533, 3006.09f, -3434.17f, 306.195f, 6f, 0.2778f, 0.2778f, 0.2778f, 0f, "Naxxramas entrance"),
    ];

    /// <summary>Teleports (classic-db areatrigger_teleport): level, key items, quest, target.</summary>
    public static AreaTriggerTeleport[] Teleports() =>
    [
        new(45, "Scarlet Monestary Graveyard - Entering", "", 20, ScarletMonastery, 1687.27f, 1050.09f, 18.6773f, 0f),
        new(78, "Deadmines - Entering", "", 10, Deadmines, -14.5732f, -385.475f, 62.4561f, 0f),
        new(244, "Razorfen Kraul - Entering", "", 15, RazorfenKraul, 1942.27f, 1544.23f, 83.3055f, 0f),
        new(610, "Scarlet Monestary Cathedral - Entering", "", 20, ScarletMonastery, 853.179f, 1319.18f, 18.6714f, 0f),
        new(612, "Scarlet Monestary Armory - Entering", "", 20, ScarletMonastery, 1608.1f, -318.919f, 18.6714f, 0f),
        new(614, "Scarlet Monestary Library - Entering", "", 20, ScarletMonastery, 253.672f, -206.624f, 18.6773f, 0f),
        new(2230, "Ragefire Chasm - Entering", "", 8, RagefireChasm, 0.797643f, -8.23429f, -15.5288f, 0f),
        new(2848, "Onyxia's Lair - Entering", "", 50, OnyxiasLair, 30.8916f, -54.079f, -5.02784f, 0f, RequiredItem: 16309),
        new(2886, "The Molten Bridge", "", 50, MoltenCore, 1091.89f, -466.985f, -105.084f, 0f),
        new(3528, "The Molten Core Window Entrance", "", 50, MoltenCore, 1091.89f, -466.985f, -105.084f, 0f, RequiredQuestDone: 7848),
        new(3529, "The Molten Core Window(Lava) Entrance", "", 50, MoltenCore, 1091.89f, -466.985f, -105.084f, 0f, RequiredQuestDone: 7848),
        new(3726, "Blackwing Lair - Entering", "", 50, BlackwingLair, -7672.32f, -1107.05f, 396.651f, 0f, RequiredQuestDone: 7761),
        new(4156, "Naxxramas (Entrance)", "", 51, Naxxramas, 3498.28f, -5349.9f, 144.968f, 0f, RequiredQuestDone: 9378),
    ];

    private static InstanceEncounter E(uint map, uint index, uint id, uint creditEntry, string name, bool last = false, byte type = 0)
        => new(map, index, id, type, creditEntry, last, name);

    /// <summary>Encounters (classic-db instance_dungeon_encounters + instance_encounters), in table order by map and index.</summary>
    public static InstanceEncounter[] Encounters() =>
    [
        E(Deadmines, 0, 161, 644, "Rhahk'zor"),
        E(Deadmines, 1, 162, 643, "Sneed"),
        E(Deadmines, 2, 163, 1763, "Gilnid"),
        E(Deadmines, 3, 164, 646, "Mr. Smite"),
        E(Deadmines, 4, 165, 645, "Cookie"),
        E(Deadmines, 5, 166, 647, "Captain Greenskin"),
        E(Deadmines, 6, 167, 639, "Edwin VanCleef", last: true),
        E(RazorfenKraul, 0, 438, 6168, "Roogug"),
        E(RazorfenKraul, 1, 439, 4424, "Aggem Thorncurse"),
        E(RazorfenKraul, 2, 440, 4428, "Death Speaker Jargba"),
        E(RazorfenKraul, 3, 441, 4420, "Overlord Ramtusk"),
        E(RazorfenKraul, 4, 443, 4421, "Charlga Razorflank", last: true),
        E(RazorfenKraul, 5, 883, 4422, "Agathelos the Raging"),
        E(ScarletMonastery, 0, 444, 3983, "Interrogator Vishas"),
        E(ScarletMonastery, 1, 445, 4543, "Bloodmage Thalnos", last: true),
        E(ScarletMonastery, 2, 446, 3974, "Houndmaster Loksey"),
        E(ScarletMonastery, 3, 447, 6487, "Arcanist Doan", last: true),
        E(ScarletMonastery, 4, 448, 3975, "Herod", last: true),
        E(ScarletMonastery, 5, 449, 4542, "High Inquisitor Fairbanks"),
        E(ScarletMonastery, 6, 450, 3977, "High Inquisitor Whitemane", last: true),
        E(RagefireChasm, 0, 430, 11517, "Oggleflint"),
        E(RagefireChasm, 1, 431, 11520, "Taragaman the Hungerer", last: true),
        E(RagefireChasm, 2, 432, 11518, "Jergosh the Invoker"),
        E(RagefireChasm, 3, 433, 11519, "Bazzalan"),
        E(OnyxiasLair, 0, 707, 10184, "Onyxia", last: true),
        E(MoltenCore, 0, 663, 12118, "Lucifron"),
        E(MoltenCore, 1, 664, 11982, "Magmadar"),
        E(MoltenCore, 2, 665, 12259, "Gehennas"),
        E(MoltenCore, 3, 666, 12057, "Garr"),
        E(MoltenCore, 4, 667, 12264, "Shazzrah"),
        E(MoltenCore, 5, 668, 12056, "Baron Geddon"),
        E(MoltenCore, 6, 669, 12098, "Sulfuron Harbinger"),
        E(MoltenCore, 7, 670, 11988, "Golemagg the Incinerator"),
        E(MoltenCore, 8, 671, 12018, "Majordomo Executus"),
        E(MoltenCore, 9, 672, 11502, "Ragnaros", last: true),
        E(BlackwingLair, 0, 610, 12435, "Razorgore the Untamed"),
        E(BlackwingLair, 1, 611, 13020, "Vaelastrasz the Corrupt"),
        E(BlackwingLair, 2, 612, 12017, "Broodlord Lashlayer"),
        E(BlackwingLair, 3, 613, 11983, "Firemaw"),
        E(BlackwingLair, 4, 614, 14601, "Ebonroc"),
        E(BlackwingLair, 5, 615, 11981, "Flamegor"),
        E(BlackwingLair, 6, 616, 14020, "Chromaggus"),
        E(BlackwingLair, 7, 617, 11583, "Nefarian", last: true),
        E(Naxxramas, 0, 673, 15956, "Anub'Rekhan"),
        E(Naxxramas, 8, 692, FourHorsemenSpell, "The Four Horsemen", type: 1),
    ];

    private static (CreatureTemplate Template, CreatureSpawn Spawn) C(uint map, uint entry, string name, byte level, float x, float y, float z, uint spawnGuid)
        => (new CreatureTemplate { Entry = entry, Name = name, MinLevel = level, MaxLevel = level },
            new CreatureSpawn { Guid = spawnGuid, Entry = entry, MapId = map, X = x, Y = y, Z = z });

    /// <summary>The bosses with a static spawn (classic-db creature, first spawn) and their template levels. Sneed, Majordomo, Ragnaros and Nefarian have none.</summary>
    private static (CreatureTemplate Template, CreatureSpawn Spawn)[] Bosses() =>
    [
        C(Deadmines, 639, "Edwin VanCleef", 21, -87.369f, -819.895f, 39.3004f, 3600059),
        C(Deadmines, 644, "Rhahk'Zor", 19, -191.671f, -453.176f, 54.5211f, 3600074),
        C(Deadmines, 645, "Cookie", 20, -69.3213f, -850.149f, 16.9312f, 3600075),
        C(Deadmines, 646, "Mr. Smite", 20, -22.8471f, -797.283f, 20.3745f, 3600076),
        C(Deadmines, 647, "Captain Greenskin", 20, -64.1282f, -832.946f, 41.2341f, 3600077),
        C(Deadmines, 1763, "Gilnid", 20, -177.969f, -576.657f, 19.3107f, 3600095),
        C(RazorfenKraul, 4420, "Overlord Ramtusk", 32, 2203.13f, 1640.05f, 85.9022f, 4700001),
        C(RazorfenKraul, 4421, "Charlga Razorflank", 33, 2190.4f, 1864.29f, 79.1389f, 4700002),
        C(RazorfenKraul, 4422, "Agathelos the Raging", 33, 1994.4f, 1976.44f, 63.292f, 4700003),
        C(RazorfenKraul, 4424, "Aggem Thorncurse", 30, 2082.31f, 1463.54f, 73.1789f, 4700004),
        C(RazorfenKraul, 4428, "Death Speaker Jargba", 30, 2146.43f, 1411.24f, 73.9538f, 4700008),
        C(RazorfenKraul, 6168, "Roogug", 28, 2142.99f, 1581.57f, 80.3842f, 4700191),
        C(RagefireChasm, 11517, "Oggleflint", 16, -147.548f, 38.7127f, -38.8039f, 3890129),
        C(RagefireChasm, 11518, "Jergosh the Invoker", 16, -376.811f, 209.224f, -21.801f, 3890130),
        C(RagefireChasm, 11519, "Bazzalan", 16, -384.867f, 146.045f, 7.82184f, 3890131),
        C(RagefireChasm, 11520, "Taragaman the Hungerer", 16, -244.743f, 150.085f, -18.7494f, 3890132),
        C(ScarletMonastery, 3974, "Houndmaster Loksey", 34, 119.938f, -260.869f, 18.6299f, 1890005),
        C(ScarletMonastery, 3975, "Herod", 40, 1965.09f, -431.607f, 6.26076f, 1890006),
        C(ScarletMonastery, 3977, "High Inquisitor Whitemane", 42, 1202.13f, 1399.07f, 29.0931f, 1890008),
        C(ScarletMonastery, 3983, "Interrogator Vishas", 32, 1786.55f, 1124.44f, 7.57362f, 1890010),
        C(ScarletMonastery, 4542, "High Inquisitor Fairbanks", 40, 1158.86f, 1354.27f, 30.389f, 1890314),
        C(ScarletMonastery, 4543, "Bloodmage Thalnos", 34, 1820.34f, 1416.71f, -7.91731f, 1890315),
        C(ScarletMonastery, 6487, "Arcanist Doan", 37, 148.32f, -428.692f, 18.4864f, 1890335),
        C(OnyxiasLair, 10184, "Onyxia", 63, -4.8689f, -217.171f, -86.7104f, 2490001),
        C(MoltenCore, 11982, "Magmadar", 63, 1144.63f, -1020.48f, -185.663f, 4090221),
        C(MoltenCore, 11988, "Golemagg the Incinerator", 63, 793.248f, -998.265f, -206.762f, 4090222),
        C(MoltenCore, 12056, "Baron Geddon", 63, 747.547f, -981.676f, -178.401f, 4090223),
        C(MoltenCore, 12057, "Garr", 63, 679.838f, -497.988f, -213.733f, 4090224),
        C(MoltenCore, 12098, "Sulfuron Harbinger", 63, 601.087f, -1179.11f, -195.882f, 4090239),
        C(MoltenCore, 12118, "Lucifron", 63, 1000.2f, -955.407f, -179.583f, 4090266),
        C(MoltenCore, 12259, "Gehennas", 63, 898.074f, -545.87f, -203.405f, 4090269),
        C(MoltenCore, 12264, "Shazzrah", 63, 583.49f, -799.755f, -205.356f, 4090270),
        C(BlackwingLair, 11981, "Flamegor", 63, -7407.94f, -1031.04f, 477.35f, 4690002),
        C(BlackwingLair, 11983, "Firemaw", 63, -7433.38f, -923.829f, 465.354f, 4690003),
        C(BlackwingLair, 12017, "Broodlord Lashlayer", 63, -7573.89f, -1035.25f, 449.248f, 4690004),
        C(BlackwingLair, 12435, "Razorgore the Untamed", 63, -7571.7f, -1088.25f, 413.465f, 4690013),
        C(BlackwingLair, 13020, "Vaelastrasz the Corrupt", 63, -7483.79f, -1015.99f, 408.652f, 4690089),
        C(BlackwingLair, 14020, "Chromaggus", 63, -7515.34f, -1029.62f, 476.73f, 4690161),
        C(BlackwingLair, 14601, "Ebonroc", 63, -7358.19f, -994.32f, 477.35f, 4690327),
        C(Naxxramas, 15956, "Anub'Rekhan", 63, 3316.47f, -3476.23f, 287.26f, 5330151),
    ];

    /// <summary>The creature content: the bosses above, plus the unspawned templates (Sneed 20, Majordomo, Ragnaros, Nefarian 63).</summary>
    public static CreatureContent Creatures(params (CreatureTemplate Template, CreatureSpawn Spawn)[] extra)
    {
        (CreatureTemplate Template, CreatureSpawn Spawn)[] all = [.. Bosses(), .. extra];
        CreatureTemplate[] unspawned =
        [
            new() { Entry = 643, Name = "Sneed", MinLevel = 20, MaxLevel = 20 },
            new() { Entry = 12018, Name = "Majordomo Executus", MinLevel = 63, MaxLevel = 63 },
            new() { Entry = 11502, Name = "Ragnaros", MinLevel = 63, MaxLevel = 63 },
            new() { Entry = 11583, Name = "Nefarian", MinLevel = 63, MaxLevel = 63 },
        ];
        return new CreatureContent([.. all.Select(a => a.Template), .. unspawned], all.Select(a => a.Spawn), [], [], []);
    }

    public static (CreatureTemplate Template, CreatureSpawn Spawn) Creature(uint map, uint entry, string name, byte level, float x, float y, float z, uint spawnGuid)
        => C(map, entry, name, level, x, y, z, spawnGuid);

    public static PlayerbotRunCatalogue Build(IEnumerable<AreaTriggerTemplate>? triggers = null, IEnumerable<AreaTriggerTeleport>? teleports = null,
        CreatureContent? creatures = null, IReadOnlyList<InstanceEncounter>? encounters = null, IEnumerable<MapTemplate>? maps = null)
        => PlayerbotRunCatalogue.Build(new MapRegistry(maps ?? Maps()), triggers ?? Triggers(), teleports ?? Teleports(), creatures ?? Creatures(),
            encounters ?? Encounters());

    public static Vector3 At(float x, float y, float z) => new(x, y, z);
}
