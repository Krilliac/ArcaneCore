using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

/// <summary>
/// The cast results of the Skinning spell after vmangos Spell::CheckCast (Spell.cpp:5940-5969): TARGET_UNSKINNABLE without the flag, TARGET_NOT_LOOTED
/// while the corpse still holds loot (a critter is exempt) or after it was skinned, and inside the tapper's 5 s head start for a stranger.
/// </summary>
public sealed class SkinningFidelityWorldTests
{
    private const uint SkinCast = 9310;
    private const uint BoarEntry = 4201;
    private const uint CritterEntry = 4202;
    private const uint BoarLoot = 4201;
    private const uint SkinLoot = 4301;
    private const uint Hide = 783;

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    private static Creature Corpse(WorldTestHost host, Player player, uint entry, uint guid, uint creatureType)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Test Beast", MinLevel = 20, MaxLevel = 20, MinLevelHealth = 60, MaxLevelHealth = 60, Faction = 32, CreatureType = creatureType };
        var spawn = new CreatureSpawn { Guid = guid, Entry = entry, MapId = 0, X = player.X + 1, Y = player.Y, Z = player.Z };
        var creature = new Creature(guid, template, spawn, new CreatureContent([template], [spawn], [], [], []), new Random(1));
        host.OnWorldAsync(() =>
        {
            host.World.GetMap(0).AddObject(creature);
            creature.Health = 0;
        }).GetAwaiter().GetResult();
        return creature;
    }

    [Fact]
    public async Task TheSkinningCast_FollowsTheVmangosCheckOrder()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("SKINFID", "Skinfid");
        Player player = await host.PlayerAsync("Skinfid");
        SpellSystem spells = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System;
        spells.Random = new FixedRandom(int.MaxValue);
        await host.WaitForWorldAsync(() => player.Skills is not null, "skills attached");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Skinning, 120, 150, 2));

        Creature boar = Corpse(host, player, BoarEntry, 4201, creatureType: 1);
        Creature critter = Corpse(host, player, CritterEntry, 4202, creatureType: 8);
        SpellCastResult Cast(Creature target) => host.OnWorldAsync(() => spells.CastSpell(player, SkinCast, SpellCastTargets.ForUnit(target.Guid), triggered: false)).GetAwaiter().GetResult();

        // Both corpses hold money (the loot service generated it for the player, who therefore taps them) and are skinnable.
        await host.OnWorldAsync(() =>
        {
            Game.Loot.LootService loot = context.Feature!.FindSystem(0)!.Loot!;
            loot.OnCreatureKilled(player, boar);
            loot.OnCreatureKilled(player, critter);
        });
        Assert.True(boar.UnitFlags.HasFlag(UnitFlags.Skinnable));            // set at death by the loot service, not when it was looted out
        Assert.True(critter.UnitFlags.HasFlag(UnitFlags.Skinnable));

        Assert.Equal(SpellCastResult.TargetNotLooted, Cast(boar));            // loot first: money still on the corpse
        Assert.Equal(SpellCastResult.CastOk, Cast(critter));                  // a critter needs no looting

        // Unskinnable beats everything.
        boar.UnitFlags &= ~UnitFlags.Skinnable;
        Assert.Equal(SpellCastResult.TargetUnskinnable, Cast(boar));
    }

    [Fact]
    public async Task AStrangerInsideTheTappersHeadStart_AnswersTargetNotLooted_UntilItRunsOut()
    {
        await using WorldTestHost host = Start(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("SKINSTR", "Skinstr");
        Player player = await host.PlayerAsync("Skinstr");
        SpellSystem spells = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System;
        spells.Random = new FixedRandom(int.MaxValue);
        await host.WaitForWorldAsync(() => player.Skills is not null, "skills attached");
        await host.OnWorldAsync(() => player.Skills!.Set(SkillIds.Skinning, 120, 150, 2));
        Creature critter = Corpse(host, player, CritterEntry, 4202, creatureType: 8);
        critter.UnitFlags |= UnitFlags.Skinnable;
        SpellCastResult Cast() => host.OnWorldAsync(() => spells.CastSpell(player, SkinCast, SpellCastTargets.ForUnit(critter.Guid), triggered: false)).GetAwaiter().GetResult();

        Assert.Equal(SpellCastResult.TargetNotLooted, Cast()); // nobody tapped it: only the 5 s head start protects it, and it has not run out
        critter.SkinningForOthersMs = 0;
        Assert.Equal(SpellCastResult.CastOk, Cast());
    }
    private static WorldTestHost Start(out GameObjectTestContext context)
    {
        var goContent = new GameObjectContent([], [], [], [], []);
        var lootContent = new LootContent(
            [(LootTableKind.Skinning, new LootStoreRow(SkinLoot, Hide, 100f, 0, 1, 1))],
            [new CreatureLootInfo(BoarEntry, BoarLoot, SkinLoot, 10, 10), new CreatureLootInfo(CritterEntry, 0, SkinLoot, 10, 10)]);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = Hide, Class = 7, Name = "Test Hide", DisplayId = 4, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start(configureServices: Configure);
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [new SkillLineRecord(SkillIds.Skinning, SkillCategories.Profession, "Skinning", 0)],
            [new SkillRaceClassInfoRecord(SkillIds.Skinning, 0, 0, 0, 0, 21)],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            []));
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [
                new SpellTemplateRow { Id = SkinCast, SpellName = "Test Skinning", RangeIndex = 12, Effect1 = 95, EffectImplicitTargetA1 = 25, Targets = 0x402, EffectBaseDice1 = 1, EffectDieSides1 = 1 },
            ],
            [], [], [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 12, MaxRange = 5 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = SkinCast }],
            [])));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
    }
}