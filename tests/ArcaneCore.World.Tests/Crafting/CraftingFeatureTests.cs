using System.Collections;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Crafting;

/// <summary>Crafting lane: the feature is discovered, installs the reagent pair once, and honours Crafting:Enabled.</summary>
public sealed class CraftingFeatureTests
{
    private static Action<IServiceCollection> Config(string? enabled) => services =>
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null ? [] : new Dictionary<string, string?> { [CraftingFeature.EnabledKey] = enabled })
            .Build());

    [Fact]
    public async Task TheFeatureIsDiscovered_AndInstallsTheReagentPairOnTheSpellSystem()
    {
        Assert.Contains(typeof(CraftingFeature), ArcaneCore.World.Features.WorldFeatures.FeatureTypes);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Config(null));

        (int checks, bool tradeFilter) = await host.OnWorldAsync(() =>
        {
            var system = host.WorldServices.GetRequiredService<SpellFeature>().System;
            return (system.CastChecks.OfType<ReagentCastCheck>().Count(), system.ReagentTradeFilter is not null);
        });

        // The reagents are taken by the cast itself (SpellSystem.StageCastReagents); the feature adds the tool check and the trade filter.
        Assert.Equal(1, checks);
        Assert.True(tradeFilter);
        Assert.Equal(1, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.Observers.OfType<FirstAidObserver>().Count()));
        Assert.True(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.CreateItem)));
        Assert.Contains(await host.OnWorldAsync(() =>
            ArcaneCore.World.Tests.Npc.GossipScriptLayers.Of(host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.GossipScript)),
            script => script is ProfessionSpecializationGossip);
    }

    [Fact]
    public async Task CraftingDisabled_InstallsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Config("false"));

        bool installed = await host.OnWorldAsync(() => ReagentRules.IsInstalled(host.WorldServices.GetRequiredService<SpellFeature>().System));

        Assert.False(installed);
        Assert.False(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasEffectHandler(ArcaneCore.Game.Spells.SpellEffectName.CreateItem)));
        Assert.IsNotType<ProfessionSpecializationGossip>(await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.GossipScript));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("garbage", true)]
    public void IsEnabled_DefaultsToRetail(string? value, bool expected)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { [CraftingFeature.EnabledKey] = value }).Build();

        Assert.Equal(expected, CraftingFeature.IsEnabled(configuration));
    }

    [Fact]
    public async Task NonTradedSlot_StillPaysForACraft_ATradedSlotDoesNot()
    {
        // vmangos setAcceptTradeMode marks only TRADE_SLOT_TRADED_COUNT (TradeHandler.cpp). Slot 6 is shown
        // (the enchant target) and never given away, so the only reagent there must still be consumed.
        const uint cloth = 2589;
        const uint bandage = 1251;
        const uint spellId = 99061;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = cloth, Class = 7, Name = "Linen Cloth", DisplayId = 1, Stackable = 20, Quality = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = bandage, Class = 0, Name = "Linen Bandage", DisplayId = 2, Stackable = 20, Quality = 1 });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, cloth, 1));
        WorldTestHost host;
        using (items.Use()) host = WorldTestHost.Start();
        await using (host)
        await using (WorldTestClient crafter = await host.EnterWorldAsync("CRAFTER", "Crafter"))
        await using (WorldTestClient partner = await host.EnterWorldAsync("PARTNER", "Partner"))
        {
            (SpellCastResult offered, SpellCastResult shown) = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Crafter")!;
                Player other = host.World.FindOnlinePlayer("Partner")!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.Store = new SpellStore([.. spells.System.Store.All.Where(s => s.Id != spellId), BandageCraft(spellId, cloth, bandage)], [], []);
                spells.Spellbook.LearnSpell(player, spellId);
                Item stack = Assert.Single(player.Inventory.AllItems, i => i.Entry == cloth);

                EconomyFeature economy = host.WorldServices.GetRequiredService<EconomyFeature>();
                var trades = (IDictionary)typeof(EconomyFeature).GetField("_trades", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(economy)!;
                var trade = new TradeSession(player, other) { Opened = true };
                trades[player] = trade;
                trades[other] = trade;

                trade.SideOf(player)[0] = stack.Guid;
                SpellCastResult inTrade = spells.System.HandleCastRequest(player, spellId, SpellCastTargets.ForSelf());

                trade.SideOf(player)[0] = ObjectGuid.Empty;
                trade.SideOf(player)[TradeRules.NonTradedSlot] = stack.Guid;
                Assert.False(spells.System.ReagentTradeFilter!(player, stack));
                SpellCastResult shownOnly = spells.System.HandleCastRequest(player, spellId, SpellCastTargets.ForSelf());
                return (inTrade, shownOnly);
            });

            // With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS) the craft goes off at the batch boundary: the bags are read after it.
            await host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System
                .HasPendingGo(host.World.FindOnlinePlayer("Crafter")!), "the batched craft went off");
            (uint clothLeft, uint made) = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Crafter")!;
                return (player.Inventory.GetItemCount(cloth), player.Inventory.GetItemCount(bandage));
            });

            Assert.Equal(SpellCastResult.ItemNotReady, offered);
            Assert.Equal(SpellCastResult.CastOk, shown);
            Assert.Equal(0u, clothLeft);
            Assert.Equal(1u, made);
        }
    }

    private static SpellInfo BandageCraft(uint spellId, uint reagent, uint output) => new()
    {
        Id = spellId,
        Name = "Synthetic Linen Bandage",
        Attributes = SpellAttributes.IsTradeskill,
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Reagents = [new SpellReagent(reagent, 1)],
        Effects =
        [
            new SpellEffectInfo
            {
                Effect = SpellEffectName.CreateItem, BasePoints = 0, BaseDice = 1, DieSides = 1,
                ItemType = output, TargetA = SpellImplicitTarget.UnitCaster,
            },
        ],
    };
}
