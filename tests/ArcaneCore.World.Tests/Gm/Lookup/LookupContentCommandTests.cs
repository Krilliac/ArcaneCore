using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Lookup;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Skills;
using ArcaneCore.Game.Maps.Templates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Lookup;

/// <summary>
/// .lookup quest|skill|spell|area|map|taxinode, .guid, .list creature|object|auras and the .arcane info root (docs/integration/gm-lookup-lane.md).
/// Fixtures are synthetic rows, not dump data; ids are only chosen to look like classic ones.
/// </summary>
public sealed class LookupContentCommandTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private static async Task<List<string>> SayAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        List<string> lines = [(await gm.ReadChatAsync()).Text];
        lines.AddRange((await gm.CollectAsync(Quiet)).Where(m => m.Opcode == WorldOpcode.SmsgMessagechat).Select(m => ChatMessage.Parse(m.Payload).Text));
        return lines;
    }

    /// <summary>With the skill system on, a character only speaks Common once its spells say so; GM mode speaks plainly (ChatHandlers).</summary>
    private static Task SpeakFreelyAsync(WorldTestHost host, string name)
        => host.OnWorldAsync(() => host.World.FindOnlinePlayer(name)!.Flags |= PlayerFlags.Gm);

    [Fact]
    public void Levels_NewLookupsAreTicketMaster_ListIsGameMaster_ArcaneIsGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string leaf in new[] { "quest", "skill", "spell", "area", "map", "taxinode" })
        {
            Assert.Null(table.Resolve("lookup " + leaf, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve("lookup " + leaf, AccountSecurity.GameMaster));
        }

        Assert.Null(table.Resolve("guid", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("guid", AccountSecurity.GameMaster));
        foreach (string path in new[] { "list creature", "list object", "list auras", "arcane content", "arcane maps", "arcane reloads" })
        {
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task LookupQuest_ListsTitlesByIdWithLevelLinks()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LQGM", "Lqgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.ReplaceQuests(new QuestStore(new QuestContent(
            [
                new QuestTemplate { Entry = 783, Title = "A Threat Within", QuestLevel = 1 },
                new QuestTemplate { Entry = 33, Title = "Wolves Across the Border", QuestLevel = 4 },
                new QuestTemplate { Entry = 7, Title = "Kobold Camp Cleanup", QuestLevel = 2 },
            ], [], []))));

        Assert.Equal(["33 - |cffffffff|Hquest:33:4|h[Wolves Across the Border]|h|r"], await SayAsync(gm, ".lookup quest WOLVES"));
        Assert.Equal(["7 - |cffffffff|Hquest:7:2|h[Kobold Camp Cleanup]|h|r", "33 - |cffffffff|Hquest:33:4|h[Wolves Across the Border]|h|r"], await SayAsync(gm, ".lookup quest o"));
        Assert.Equal(["No quests found!"], await SayAsync(gm, ".lookup quest zzz"));
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".lookup quest"))[0]);
    }

    [Fact]
    public async Task LookupSkillAndSpell_ReadTheLoadedCatalogAndSpellStore()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await host.EnterWorldAsync("LSGM", "Lsgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await SpeakFreelyAsync(host, "Lsgm");

        Assert.Equal(["164 - |cffffffff|Hskill:164|h[Blacksmithing]|h|r"], await SayAsync(gm, ".lookup skill smith"));
        Assert.Equal(["No skills found!"], await SayAsync(gm, ".lookup skill zzz"));

        Assert.Equal(
            ["133 - |cffffffff|Hspell:133|h[Fireball Rank 1]|h|r", "143 - |cffffffff|Hspell:143|h[Fireball Rank 2]|h|r"],
            await SayAsync(gm, ".lookup spell fireball"));
        Assert.Equal(["168 - |cffffffff|Hspell:168|h[Frost Armor]|h|r"], await SayAsync(gm, ".lookup spell ARMOR"));
        Assert.Equal(["No spells found!"], await SayAsync(gm, ".lookup spell zzz"));
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".lookup spell"))[0]);
    }

    [Fact]
    public async Task LookupSkillAndSpell_AnswerNoneWhenNothingIsLoaded()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LNGM", "Lngm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal(["No skills found!"], await SayAsync(gm, ".lookup skill a"));
        Assert.Equal(["No quests found!"], await SayAsync(gm, ".lookup quest a"));
    }

    [Fact]
    public async Task LookupAreaMapAndTaxiNode_ListTheirTables()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LAGM", "Lagm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            WorldMaps.Of(host.World).Load(new MapContent(
                [
                    new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", string.Empty),
                    new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", string.Empty),
                    new MapTemplate(33, 0, MapType.Instance, 209, 10, 0, 0, 0, 0, "Shadowfang Keep", string.Empty),
                ],
                [
                    new AreaTemplate(12, 0, 0, 12, 0, 1, "Elwynn Forest", 0, 0),
                    new AreaTemplate(87, 0, 12, 87, 0, 3, "Goldshire", 0, 0),
                ],
                [], [], []));
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.ReplaceNpcs(new NpcStore(NpcContent.Empty with
            {
                TaxiNodes = [new TaxiNode { Id = 2, MapId = 0, Name = "Stormwind, Elwynn", MountAlliance = 1 }, new TaxiNode { Id = 6, MapId = 0, Name = "Ironforge, Dun Morogh", MountAlliance = 1 }],
            }));
        });

        Assert.Equal(["87 - |cffffffff|Harea:87|h[Goldshire]|h|r (map 0, in zone 12)"], await SayAsync(gm, ".lookup area gold"));
        Assert.Equal(["12 - |cffffffff|Harea:12|h[Elwynn Forest]|h|r (map 0, zone)"], await SayAsync(gm, ".lookup area elwynn"));
        Assert.Equal(["No area found!"], await SayAsync(gm, ".lookup area zzz"));

        Assert.Equal(["33 - |cffffffff|Hmap:33|h[Shadowfang Keep]|h|r (Instance)"], await SayAsync(gm, ".lookup map fang"));
        Assert.Equal(["0 - |cffffffff|Hmap:0|h[Eastern Kingdoms]|h|r (Common)"], await SayAsync(gm, ".lookup map eastern"));
        Assert.Equal(["No maps found!"], await SayAsync(gm, ".lookup map zzz"));

        Assert.Equal(["6 - |cffffffff|Htaxinode:6|h[Ironforge, Dun Morogh]|h|r"], await SayAsync(gm, ".lookup taxinode iron"));
        Assert.Equal(["No taxinodes found!"], await SayAsync(gm, ".lookup taxinode zzz"));
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".lookup area"))[0]);
    }

    [Fact]
    public async Task Guid_ShowsTheSelection_OrNoSelection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GDGM", "Gdgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal(["No selection."], await SayAsync(gm, ".guid"));

        ObjectGuid wolf = ObjectGuid.WithEntry(HighGuid.Unit, 299, 5);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Gdgm")!.Selection = wolf);
        Assert.Equal([$"Object GUID is: {wolf} (entry 299, counter 5)"], await SayAsync(gm, ".guid"));

        Player self = await host.PlayerAsync("Gdgm");
        await host.OnWorldAsync(() => self.Selection = self.Guid);
        Assert.Equal([$"Object GUID is: {self.Guid} (low {self.Guid.Low})"], await SayAsync(gm, ".guid"));
    }

    [Fact]
    public async Task ListCreatureAndObject_ShowSpawnsByGuid_WithTheDefaultAndExplicitCap()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("LCGM", "Lcgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal(
            [
                "101 - |cffffffff|Hcreature:101|h[Young Wolf X:1.50 Y:-2.25 Z:3.00 MapId:0]|h|r",
                "102 - |cffffffff|Hcreature:102|h[Young Wolf X:10.00 Y:20.00 Z:30.00 MapId:1]|h|r",
                "103 - |cffffffff|Hcreature:103|h[Young Wolf X:0.00 Y:0.00 Z:0.00 MapId:0]|h|r",
            ],
            await SayAsync(gm, ".list creature 299"));
        Assert.Equal(["101 - |cffffffff|Hcreature:101|h[Young Wolf X:1.50 Y:-2.25 Z:3.00 MapId:0]|h|r"], await SayAsync(gm, ".list creature 299 1"));
        Assert.Equal(["No creatures found!"], await SayAsync(gm, ".list creature 12345"));
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".list creature"))[0]);
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".list creature 299 0"))[0]);
        Assert.StartsWith("Syntax:", (await SayAsync(gm, ".list creature wolf"))[0]);

        Assert.Equal(["201 - |cffffffff|Hgameobject:201|h[Battered Chest X:5.00 Y:6.00 Z:7.00 MapId:0]|h|r"], await SayAsync(gm, ".list object 2061"));
        Assert.Equal(["No gameobjects found!"], await SayAsync(gm, ".list object 1"));
    }

    [Fact]
    public async Task ListAuras_ShowsTheSelectedUnitsAuras_OrYours()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LAUGM", "Laugm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal(["Target has 0 aura(s):"], await SayAsync(gm, ".list auras"));

        Player player = await host.PlayerAsync("Laugm");
        await host.OnWorldAsync(() =>
        {
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            var spell = new SpellInfo
            {
                Id = 168, Name = "Frost Armor", Rank = "Rank 1", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo
                {
                    Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModLanguage, MiscValue = (int)Language.Demonic,
                    TargetA = SpellImplicitTarget.UnitCaster,
                }, new(), new()],
            };
            spells.System.Store = new SpellStore([spell], [], []);
            Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, 168, SpellCastTargets.ForSelf(), triggered: true));
        });

        Assert.Equal(["Target has 1 aura(s):", "168 - |cffffffff|Hspell:168|h[Frost Armor Rank 1]|h|r x1"], await SayAsync(gm, ".list auras"));

        await host.OnWorldAsync(() => player.Selection = ObjectGuid.WithEntry(HighGuid.Unit, 299, 9));
        Assert.Equal(["You should select a character or a creature."], await SayAsync(gm, ".list auras"));
    }

    [Fact]
    public async Task ArcaneContent_CountsTheLoadedTables()
    {
        await using WorldTestHost host = StartWithSpawns(ConfigureSkills);
        await using WorldTestClient gm = await host.EnterWorldAsync("ACGM", "Acgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await SpeakFreelyAsync(host, "Acgm");
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.ReplaceQuests(new QuestStore(new QuestContent(
            [new QuestTemplate { Entry = 7, Title = "Kobold Camp Cleanup" }], [], []))));

        List<string> lines = await SayAsync(gm, ".arcane content");

        Assert.Contains("creatures: 1 templates, 3 spawns, definitions generation 0", lines);
        Assert.Contains("gameobjects: 1 templates, 1 spawns", lines);
        Assert.Contains("quests: 1 templates", lines);
        Assert.Contains("spells: 4 spells", lines);
        Assert.Contains("skills: 2 skill lines", lines);
        Assert.Contains(lines, l => l.StartsWith("maps: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("items: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("taxi nodes: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArcaneMaps_ListsEveryRunningMapWithItsCounts()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AMGM", "Amgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        List<string> lines = await SayAsync(gm, ".arcane maps");

        Assert.Equal("1 map(s) running, 1 player(s) online.", lines[0]);
        Assert.Matches(@"^map 0 instance 0 \[.*\]: 1 player\(s\), \d+ object\(s\), 0 in transit$", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    /// <summary>
    /// Regression: the per-instance lines honour World:GmCommands:LookupMaxResults like every other list (the summary line is not a
    /// result and is always sent). Two dungeon instances are created directly on the world thread next to the continent the GM stands on.
    /// </summary>
    [Fact]
    public async Task ArcaneMaps_CapsThePerInstanceLinesByLookupMaxResults()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("AMCGM", "Amcgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            host.World.GetMap(33, 101);
            host.World.GetMap(33, 102);
        });
        host.WorldServices.GetRequiredService<CommandTableSource>().Current.Gm.LookupMaxResults = 2;

        List<string> lines = await SayAsync(gm, ".arcane maps");

        Assert.Equal("3 map(s) running, 1 player(s) online.", lines[0]);
        Assert.Matches(@"^map 0 instance 0 \[.*\]: 1 player\(s\), \d+ object\(s\), 0 in transit$", lines[1]);
        Assert.Matches(@"^map 33 instance 101 \[.*\]: 0 player\(s\), \d+ object\(s\), 0 in transit$", lines[2]);
        Assert.Equal(["More results were omitted (World:GmCommands:LookupMaxResults)."], lines.Skip(3));

        host.WorldServices.GetRequiredService<CommandTableSource>().Current.Gm.LookupMaxResults = 0;
        lines = await SayAsync(gm, ".arcane maps");
        Assert.Equal(4, lines.Count);
        Assert.Matches(@"^map 33 instance 102 \[.*\]: 0 player\(s\), \d+ object\(s\), 0 in transit$", lines[3]);
    }

    [Fact]
    public async Task ArcaneReloads_ShowsTheCreatureGeneration_AndEachReloadable()
    {
        await using WorldTestHost host = StartWithSpawns();
        await using WorldTestClient gm = await host.EnterWorldAsync("ARGM", "Argm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        List<string> lines = await SayAsync(gm, ".arcane reloads");

        Assert.Equal("creature definitions generation: 0 (swaps since start)", lines[0]);
        Assert.True(lines.Count >= 2, "a reloadable list or the disabled notice follows");
    }

    [Fact]
    public async Task LookupCap_AppliesToTheNewLookups()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: ConfigureSkills);
        await using WorldTestClient gm = await host.EnterWorldAsync("LCPGM", "Lcpgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await SpeakFreelyAsync(host, "Lcpgm");
        host.WorldServices.GetRequiredService<CommandTableSource>().Current.Gm.LookupMaxResults = 1;

        Assert.Equal(
            ["133 - |cffffffff|Hspell:133|h[Fireball Rank 1]|h|r", "More results were omitted (World:GmCommands:LookupMaxResults)."],
            await SayAsync(gm, ".lookup spell fireball"));
    }

    private static WorldTestHost StartWithSpawns(Action<IServiceCollection>? configureServices = null)
    {
        var wolf = new CreatureTemplate { Entry = 299, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 32 };
        CreatureSpawn[] spawns =
        [
            new() { Guid = 103, Entry = 299, MapId = 0, X = 0, Y = 0, Z = 0 },
            new() { Guid = 101, Entry = 299, MapId = 0, X = 1.5f, Y = -2.25f, Z = 3 },
            new() { Guid = 102, Entry = 299, MapId = 1, X = 10, Y = 20, Z = 30 },
        ];
        var chest = new GameObjectTemplate { Entry = 2061, Type = 3, DisplayId = 10, Name = "Battered Chest", Data = new uint[GameObjectTemplate.DataCount] };
        var chestSpawn = new GameObjectSpawn { Guid = 201, Entry = 2061, MapId = 0, X = 5, Y = 6, Z = 7 };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([wolf], spawns, [], [], []));
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([chest], [chestSpawn], [], [], []), LootContent.Empty);
        try
        {
            return WorldTestHost.Start(configureServices: configureServices);
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static void ConfigureSkills(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [
                new SkillLineRecord(164, SkillCategories.Profession, "Blacksmithing", 0),
                new SkillLineRecord(171, SkillCategories.Profession, "Alchemy", 0),
            ],
            [], [], []));
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [Spell(133, "Fireball", "Rank 1"), Spell(143, "Fireball", "Rank 2"), Spell(168, "Frost Armor", ""), Spell(2575, "Mining", "Apprentice")],
            [], [], [new SpellRangeRow { Id = 1 }], [], [], [])));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
    }

    private static SpellTemplateRow Spell(uint id, string name, string rank)
        => new() { Id = id, SpellName = name, Rank = rank, RangeIndex = 1, EffectImplicitTargetA1 = 1, Effect1 = 3 };
}
