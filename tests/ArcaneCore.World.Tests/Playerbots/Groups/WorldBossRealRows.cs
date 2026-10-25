using System.Numerics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Real rows for the world-boss tests, copied from the world database (<c>creature_template</c> joined to <c>creature_spawn</c>: every rank-3
/// template with a spawn on map 0 or 1, plus the four Dragons of Nightmare templates, which have no database spawn at all) and from build 5875
/// <c>FactionTemplate.dbc</c> (the rows those templates and the two player teams' starting races name). Nothing here is invented: the derive rule
/// of the catalogue is pinned to these rows.
/// </summary>
internal static class WorldBossRealRows
{
    internal const uint Azuregos = 6109, Kazzak = 12397, Teremus = 7846;
    internal const uint Ysondre = 14887, Lethon = 14888, Emeriss = 14889, Taerar = 14890;

    /// <summary>creature_template.ExtraFlags bit 0 (CREATURE_FLAG_EXTRA_INSTANCE_BIND), the flag instance bosses carry.</summary>
    internal const uint ExtraInstanceBind = 1;

    /// <summary>One joined row: the template columns the catalogue reads and the spawn's guid, map and position.</summary>
    internal readonly record struct Row(uint Entry, string Name, byte MinLevel, byte MaxLevel, uint Faction, uint NpcFlags, uint UnitFlags, uint Rank,
        bool RacialLeader, uint ExtraFlags, uint SpawnGuid, uint MapId, float X, float Y, float Z)
    {
        internal CreatureTemplate Template => new()
        {
            Entry = Entry, Name = Name, MinLevel = MinLevel, MaxLevel = MaxLevel, Faction = Faction, NpcFlags = NpcFlags, UnitFlags = UnitFlags,
            Rank = Rank, RacialLeader = RacialLeader, ExtraFlags = ExtraFlags,
        };

        internal CreatureSpawn Spawn => new() { Guid = SpawnGuid, Entry = Entry, MapId = MapId, X = X, Y = Y, Z = Z };
    }

    /// <summary>Every rank-3 template with a spawn on map 0 or 1 in the world database (21 rows), ascending by entry.</summary>
    internal static readonly Row[] Rank3ContinentSpawns =
    [
        new(1284, "Archbishop Benedictus", 63, 63, 12, 2u, 4672u, 3, false, 65538u, 7566, 0, -8522.6103515625f, 848.7830200195312f, 106.7020034790039f),
        new(1748, "Highlord Bolvar Fordragon", 63, 63, 12, 2u, 4096u, 3, true, 0u, 10495, 0, -8439.98046875f, 329.3919982910156f, 122.5790023803711f),
        new(2425, "Varimathras", 62, 62, 68, 3u, 4096u, 3, false, 0u, 31901, 0, 1287.77001953125f, 333.50799560546875f, -59.99980163574219f),
        new(2784, "King Magni Bronzebeard", 63, 63, 55, 2u, 4160u, 3, true, 0u, 2088, 0, -4865.72998046875f, -1022.6699829101562f, 505.7980041503906f),
        new(3057, "Cairne Bloodhoof", 63, 63, 104, 3u, 4160u, 3, true, 0u, 26895, 1, -1209.6400146484375f, -112.697998046875f, 163.8820037841797f),
        new(3516, "Arch Druid Fandral Staghelm", 63, 63, 80, 3u, 4160u, 3, true, 0u, 46903, 1, 10173.0f, 2569.919921875f, 1366.9599609375f),
        new(4949, "Thrall", 63, 63, 125, 3u, 4160u, 3, true, 0u, 4770, 1, 1920.0699462890625f, -4125.68994140625f, 42.99760055541992f),
        new(4968, "Lady Jaina Proudmoore", 63, 63, 1096, 3u, 4160u, 3, false, 65538u, 7895, 1, -3749.68994140625f, -4454.14990234375f, 64.98789978027344f),
        new(6109, "Azuregos", 63, 63, 7, 1u, 0u, 3, false, 4096u, 52349, 1, 2622.219970703125f, -5977.93017578125f, 100.76000213623047f),
        new(7846, "Teremus the Devourer", 60, 60, 14, 0u, 0u, 3, false, 4096u, 134380, 0, -11685.900390625f, -2955.419921875f, 7.736000061035156f),
        new(7937, "High Tinker Mekkatorque", 63, 63, 64, 3u, 4160u, 3, false, 65538u, 34, 0, -4827.6298828125f, -1257.1600341796875f, 506.1189880371094f),
        new(7999, "Tyrande Whisperwind", 63, 63, 124, 2u, 4160u, 3, true, 65538u, 46242, 1, 9662.51953125f, 2524.570068359375f, 1360.0799560546875f),
        new(10181, "Lady Sylvanas Windrunner", 63, 63, 118, 268435459u, 36928u, 3, true, 0u, 32039, 0, 1290.4100341796875f, 316.24700927734375f, -57.23759841918945f),
        new(10540, "Vol'jin", 60, 60, 125, 3u, 4096u, 3, false, 0u, 3394, 1, 1934.06005859375f, -4162.259765625f, 40.994998931884766f),
        new(12397, "Lord Kazzak", 63, 63, 14, 0u, 0u, 3, false, 0u, 2723, 0, -12241.2998046875f, -2432.10009765625f, 2.9393599033355713f),
        new(15192, "Anachronos", 63, 63, 776, 3u, 512u, 3, false, 2u, 23098, 1, -8176.580078125f, -4722.56982421875f, 28.078399658203125f),
        new(15341, "General Rajaxx", 63, 63, 310, 0u, 64u, 3, false, 513u, 155940, 1, -8132.33984375f, 1525.1500244140625f, 37.97480010986328f),
        new(15481, "Spirit of Azuregos", 63, 63, 7, 1u, 768u, 3, false, 4096u, 35867, 1, 2578.1298828125f, -5963.58984375f, 97.83000183105469f),
        new(15740, "Colossus of Zora", 63, 63, 370, 0u, 64u, 3, false, 1052673u, 155261, 1, -7241.35986328125f, 1680.43994140625f, -66.16680145263672f),
        new(15741, "Colossus of Regal", 63, 63, 370, 0u, 64u, 3, false, 1052673u, 155262, 1, -8005.919921875f, 628.1799926757812f, -44.65999984741211f),
        new(15742, "Colossus of Ashi", 63, 63, 370, 0u, 64u, 3, false, 1052673u, 155263, 1, -6393.31982421875f, 1047.050048828125f, -50.88100051879883f),
    ];

    /// <summary>The four Dragons of Nightmare templates (no <c>creature_spawn</c> row: the portal feature places them).</summary>
    internal static readonly Row[] Dragons =
    [
        new(14887, "Ysondre", 63, 63, 50, 0u, 64u, 3, false, 0u, 0, 0, 0f, 0f, 0f),
        new(14888, "Lethon", 63, 63, 50, 0u, 64u, 3, false, 0u, 0, 0, 0f, 0f, 0f),
        new(14889, "Emeriss", 63, 63, 50, 0u, 64u, 3, false, 0u, 0, 0, 0f, 0f, 0f),
        new(14890, "Taerar", 63, 63, 50, 0u, 64u, 3, false, 0u, 0, 0, 0f, 0f, 0f),
    ];

    /// <summary>The faction templates the rows above name, with the human (1) and orc (2) templates the derive rule tests friendliness against.</summary>
    internal static FactionTemplateCatalog Factions() => new(
    [
        new(1, 1, 72, 3, 2, 12, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(2, 2, 72, 5, 4, 10, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(7, 7, 0, 0, 0, 0, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(12, 72, 0, 2, 2, 4, Enemy1: 0, Enemy2: 0, Friend1: 72, Friend2: 0),
        new(14, 14, 0, 8, 0, 1, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(55, 47, 0, 2, 2, 4, Enemy1: 0, Enemy2: 0, Friend1: 47, Friend2: 0),
        new(64, 54, 1, 2, 2, 4, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(68, 68, 0, 4, 4, 2, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(80, 69, 0, 2, 2, 4, Enemy1: 0, Enemy2: 0, Friend1: 69, Friend2: 0),
        new(104, 81, 0, 4, 4, 2, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(118, 68, 33, 4, 4, 2, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(124, 69, 33, 2, 2, 4, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(125, 76, 33, 4, 4, 2, Enemy1: 0, Enemy2: 0, Friend1: 0, Friend2: 0),
        new(310, 249, 1, 8, 0, 1, Enemy1: 0, Enemy2: 0, Friend1: 249, Friend2: 0),
        new(370, 915, 33, 8, 8, 1, Enemy1: 912, Enemy2: 0, Friend1: 915, Friend2: 0),
        new(776, 910, 0, 0, 0, 0, Enemy1: 249, Enemy2: 80, Friend1: 910, Friend2: 531),
        new(1096, 108, 2080, 3, 2, 12, Enemy1: 679, Enemy2: 0, Friend1: 0, Friend2: 0),
    ]);

    /// <summary>The content of the full set: every row's template and spawn, and the dragons' templates.</summary>
    internal static CreatureContent Content(Func<Row, Row>? edit = null, IEnumerable<CreatureTemplate>? extraTemplates = null, IEnumerable<CreatureSpawn>? extraSpawns = null)
    {
        Row[] spawned = [.. Rank3ContinentSpawns.Select(r => edit?.Invoke(r) ?? r)];
        return new CreatureContent(
            [.. spawned.Select(r => r.Template), .. Dragons.Select(r => r.Template), .. extraTemplates ?? []],
            [.. spawned.Select(r => r.Spawn), .. extraSpawns ?? []],
            [], [], []);
    }

    internal static Row Of(uint entry) => Rank3ContinentSpawns.Single(r => r.Entry == entry);

    internal static Vector3 PositionOf(uint entry) => new(Of(entry).X, Of(entry).Y, Of(entry).Z);
}
