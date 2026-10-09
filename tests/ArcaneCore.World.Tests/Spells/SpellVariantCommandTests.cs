using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

public sealed class SpellVariantCommandTests
{
    private const uint DefaultSpell = 990100;
    private const uint LanguageSpell = 668;
    private const uint RecipeSpell = 990102;
    private const uint ClassSpell = 990103;
    private const uint TalentSpell = 990104;
    private const uint TeachingSpell = 990105;
    private const uint TaughtSpell = 990106;

    [Theory]
    [InlineData("learn all", 6)]
    [InlineData("learn all_gm", 3)]
    [InlineData("learn all_crafts", 3)]
    [InlineData("learn all_default", 2)]
    [InlineData("learn all_lang", 1)]
    [InlineData("learn all_myclass", 5)]
    [InlineData("learn all_myspells", 5)]
    [InlineData("learn all_mytalents", 5)]
    [InlineData("learn all_mytaxis", 2)]
    [InlineData("learn all_recipes", 3)]
    [InlineData("learn all_trainer", 3)]
    [InlineData("learn all_items", 3)]
    [InlineData("unlearn all_gm", 3)]
    [InlineData("unlearn all_crafts", 3)]
    [InlineData("unlearn all_recipes", 3)]
    public void Variant_HasTheVmangosRank(string path, int rank)
    {
        CommandTable table = ChatCommands.CreateTable();
        ChatCommand command = Assert.IsType<ChatCommand>(table.Resolve(path, AccountSecurity.Administrator));
        Assert.Equal(rank, command.RequiredLevel(table.Gm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // vmangos-style hiding of unavailable commands must not hide a parent that leads to an available child
    public void LowerRankLanguageChild_IsReachableThroughTheHigherRankLearnRoot(bool hideUnavailable)
    {
        CommandTable table = ChatCommands.CreateTable(new GmOptions { HideUnavailable = hideUnavailable });
        Assert.Equal("learn all_lang", table.Lookup("learn all_lang", AccountSecurity.Moderator).Path);
        Assert.True(table.Lookup("learn all_lang", AccountSecurity.Moderator).Available);
        Assert.False(table.Lookup("learn 5", AccountSecurity.Moderator).Available);
    }

    private static SkillCatalog Catalog() => new(
        [new SkillLineRecord(164, SkillCategories.Profession, "Blacksmithing", 0),
            new SkillLineRecord(98, SkillCategories.Languages, "Common", 0),
            new SkillLineRecord(43, SkillCategories.Class, "Warrior", 0)],
        [], [],
        [new SkillLineAbilityRecord(1, 164, RecipeSpell, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(2, 98, LanguageSpell, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(3, 43, ClassSpell, 0, 1, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(4, 164, TaughtSpell, 0, 0, 0, 0, 0, 0, 0)]);

    private static TalentCatalog Talents() => new(
        [new TalentTabRecord(100, 1, 0)],
        [new TalentRecord(101, 100, 0, 0, [TalentSpell, 0, 0, 0, 0], 0, 0, 0)]);

    private static Task<string> Run(WorldTestClient client, string command) => RunCore(client, command);

    private static async Task<string> RunCore(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public async Task SpellAndCraftVariants_LearnAndForgetTheExpectedLoadedSpells()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(Catalog());
            services.AddSingleton(Talents());
        });
        await using WorldTestClient gm = await host.EnterWorldAsync("VARIANT", "Variant", AccountSecurity.Administrator);
        var player = await host.PlayerAsync("Variant");
        Assert.Equal(1, (int)player.Class); // class-family fixture is Warrior
        await host.OnWorldAsync(() =>
        {
            player.Skills?.LearnLanguage((uint)Language.Common);
            var spells = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>();
            spells.System.Store = new SpellStore([
                new SpellInfo { Id = 5 },
                new SpellInfo { Id = DefaultSpell },
                new SpellInfo { Id = LanguageSpell, Effects = [new SpellEffectInfo { Effect = SpellEffectName.Language, MiscValue = 7 }] },
                new SpellInfo { Id = RecipeSpell },
                new SpellInfo { Id = ClassSpell, SpellFamilyName = 4, SpellLevel = 1 },
                new SpellInfo { Id = TalentSpell },
                new SpellInfo { Id = TeachingSpell, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = TaughtSpell }] },
                new SpellInfo { Id = TaughtSpell, SpellFamilyName = 4, SpellLevel = 1 },
            ], [((byte)player.Race, (byte)player.Class, DefaultSpell)], []);
        });

        async Task<bool> Knows(uint id) => await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().Spellbook.HasSpell(player, id));

        Assert.Equal("GM spells learned.", await Run(gm, ".learn all_gm"));
        Assert.True(await Knows(5));
        Assert.Equal("You have forgotten all GM spells.", await Run(gm, ".unlearn all_gm"));
        Assert.False(await Knows(5));

        Assert.Contains("Default", await Run(gm, ".learn all_default"));
        Assert.True(await Knows(DefaultSpell));
        Assert.Contains("languages", await Run(gm, ".learn all_lang"));
        Assert.True(await Knows(LanguageSpell));

        Assert.Contains("Blacksmithing", await Run(gm, ".learn all_recipes blacksmith"));
        Assert.True(await Knows(RecipeSpell));
        Assert.Contains("forgotten", await Run(gm, ".unlearn all_recipes blacksmith"));
        Assert.False(await Knows(RecipeSpell));
        Assert.Contains("crafts learned", await Run(gm, ".learn all_crafts"));
        Assert.True(await Knows(RecipeSpell));
        Assert.Contains("forgotten all crafts", await Run(gm, ".unlearn all_crafts"));
        Assert.False(await Knows(RecipeSpell));

        Assert.Contains("Class spells learned", await Run(gm, ".learn all_myspells"));
        Assert.True(await Knows(ClassSpell));
        Assert.Contains("Class talents learned", await Run(gm, ".learn all_mytalents"));
        Assert.True(await Knows(TalentSpell));
        Assert.Contains("Class spells and talents", await Run(gm, ".learn all_myclass"));
        Assert.Contains("Eligible class spells", await Run(gm, ".learn all"));
        Assert.True(await Knows(TaughtSpell));
    }

    [Fact]
    public async Task TaxiTrainerAndItemVariants_UseLoadedContent()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(Catalog());
            services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
        });
        await using WorldTestClient gm = await host.EnterWorldAsync("VARCONTENT", "Varcontent", AccountSecurity.Administrator);
        var player = await host.PlayerAsync("Varcontent");
        await host.OnWorldAsync(() =>
        {
            player.Skills?.LearnLanguage((uint)Language.Common);
            host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store = new SpellStore([
                new SpellInfo { Id = TeachingSpell, Effects = [new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = TaughtSpell }] },
                new SpellInfo { Id = TaughtSpell, SpellLevel = 1 },
            ], [], []);
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.ReplaceNpcs(new NpcStore(NpcContent.Empty with
            {
                TrainerSpells = [new TrainerSpell { Entry = 8001, Spell = TeachingSpell }],
                TaxiNodes = [new TaxiNode { Id = 200, MapId = 0, X = -8947, Y = -132, Z = 83, MountAlliance = 1, MountHorde = 1 }],
            }));
            host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent([
                new CreatureTemplate { Entry = 8001, Name = "Trainer", NpcFlags = (uint)NpcFlags.Trainer, TrainerType = 0, TrainerClass = (byte)player.Class },
                new CreatureTemplate { Entry = 8002, Name = "Flightmaster", NpcFlags = (uint)NpcFlags.FlightMaster },
            ], [new CreatureSpawn { Guid = 8002, Entry = 8002, MapId = 0, X = -8948, Y = -132, Z = 83 }], [], [], []));
            host.WorldServices.GetRequiredService<ItemsFeature>().ReplaceTemplates(new ItemTemplateStore([
                new ItemTemplate { Entry = 8003, Name = "Teaching Item", Spells = [new ItemSpell(TeachingSpell, 0, 0, 0, 0, 0, 0)] },
            ]));
        });

        Assert.Contains("taxi nodes", await Run(gm, ".learn all_mytaxis"));
        Assert.True(await host.OnWorldAsync(() =>
            (host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!.TaxiMask[(200 - 1) / 32]
                & (1u << ((200 - 1) % 32))) != 0));
        Assert.Contains("trainers", await Run(gm, ".learn all_trainer"));
        Assert.True(await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().Spellbook.HasSpell(player, TaughtSpell)));
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.RemoveSpell(player, TaughtSpell));
        Assert.Contains("items", await Run(gm, ".learn all_items"));
        Assert.True(await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().Spellbook.HasSpell(player, TaughtSpell)));
    }
}
