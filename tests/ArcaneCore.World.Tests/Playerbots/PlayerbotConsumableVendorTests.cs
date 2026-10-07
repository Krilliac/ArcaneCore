using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotConsumableVendorTests
{
    [Fact]
    public async Task StarterFoodVendorPurchaseSettlesThroughManagedBuyAndDoesNotDuplicate()
    {
        var fixture = new TownVendorFixture();
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 117, Name = "Starter food", Stackable = 20, BuyPrice = 10,
            Spells = [new ItemSpell(993000, 0, -1, 0, 0, 0, 0)] });
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store =
                        new SpellStore([Aura(993000, AuraType.ModRegen)], [], []);
                    player.Money = 100;
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(goals.Update(player, 1000));
                    return true;
                });
                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(117)));
                Assert.Equal(90u, await host.PlayerStateAsync("Controlone", p => p.Money));

                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, 993000, SpellCastTargets.ForSelf(), triggered: false));
                    Assert.Contains(spells.GetActiveCooldowns(player), row => row.SpellId == 993000);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                    Assert.False(goals.Update(player, 1500));
                    return true;
                });
                Assert.Equal(90u, await host.PlayerStateAsync("Controlone", p => p.Money));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task NonStarterFoodIsBoughtFromTheRealVendorPath()
    {
        await using WorldTestHost host = await StartCatalogVendor(
            [new VendorItem { Entry = TownVendorFixture.Entry, Item = 993100, Slot = 0 },
             new VendorItem { Entry = TownVendorFixture.Entry, Item = 993101, Slot = 1 }],
            new ItemTemplate { Entry = 993100, Stackable = 20, BuyPrice = 1, RequiredLevel = 60,
                Spells = [new ItemSpell(993101, 0, -1, 0, 0, 0, 0)] },
            new ItemTemplate { Entry = 993101, Stackable = 20, BuyPrice = 11, Spells = [new ItemSpell(993101, 0, -1, 0, 0, 0, 0)] },
            Aura(993101, AuraType.ModRegen));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
            await host.OnWorldAsync(() =>
            {
                session.Player!.Money = 100;
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(goals.Update(session.Player, 1000));
                return true;
            });
            Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(993101)));
            Assert.Equal(0u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(993100)));
            Assert.Equal(89u, await host.PlayerStateAsync("Controlone", p => p.Money));
        }
        finally { session.Kick(); await session.ManagedClosed; TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task ReversedVendorRowsStillPreferFoodForManaCapableBot()
    {
        await using WorldTestHost host = await StartCatalogVendor(
            [new VendorItem { Entry = TownVendorFixture.Entry, Item = 993102, Slot = 0 }, new VendorItem { Entry = TownVendorFixture.Entry, Item = 993103, Slot = 1 }],
            new ItemTemplate { Entry = 993102, Stackable = 20, BuyPrice = 7, SellPrice = 1, Quality = 0, Spells = [new ItemSpell(993102, 0, -1, 0, 0, 0, 0)] },
            Aura(993102, AuraType.ModPowerRegen, (int)PowerType.Mana),
            new ItemTemplate { Entry = 993103, Stackable = 20, BuyPrice = 10, Spells = [new ItemSpell(993103, 0, -1, 0, 0, 0, 0)] },
            Aura(993103, AuraType.ModRegen));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Money = 100;
                player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana, 100);
                SpellSystem.SetPower(player, PowerType.Mana, 10);
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(goals.Update(player, 1000));
                return true;
            });
            Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(993103)));
            Assert.Equal(0u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(993102)));
            Assert.Equal(90u, await host.PlayerStateAsync("Controlone", p => p.Money));
            await host.OnWorldAsync(() =>
            {
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(goals.Update(session.Player!, 1500));
                return true;
            });
            Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(993102)));
            Assert.Equal(83u, await host.PlayerStateAsync("Controlone", p => p.Money));
            await host.OnWorldAsync(() =>
            {
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.Update(session.Player!, 1500));
                Assert.Equal(1u, session.Player!.Inventory.GetItemCount(993102));
                Assert.Equal(83u, session.Player.Money);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; TownVendorServices.Current.Value = null; }
    }

    [Fact]
    public async Task VendorScanStopsAfterThirtyTwoRowsWithoutSpendingAnAction()
    {
        VendorItem[] rows = [.. Enumerable.Range(0, 32).Select(index => new VendorItem
            { Entry = TownVendorFixture.Entry, Item = (uint)(994000 + index), Slot = (uint)index }),
            new VendorItem { Entry = TownVendorFixture.Entry, Item = 993199, Slot = 32 }];
        await using WorldTestHost host = await StartCatalogVendor(rows,
            new ItemTemplate { Entry = 993199, Stackable = 20, BuyPrice = 5,
                Spells = [new ItemSpell(993199, 0, -1, 0, 0, 0, 0)] }, Aura(993199, AuraType.ModRegen));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid), "vendor visible");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Money = 100;
                var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.HasCandidate(player));
                Assert.False(goals.Update(player, 1500));
                Assert.Equal(1, session.ManagedBudget.Remaining);
                Assert.Equal(100u, player.Money);
                Assert.Equal(0u, player.Inventory.GetItemCount(993199));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static async Task<WorldTestHost> StartCatalogVendor(IReadOnlyList<VendorItem> rows, params object[] catalog)
    {
        var fixture = new TownVendorFixture { VendorItemsOverride = rows };
        var items = new ItemTestContent();
        foreach (object entry in catalog)
        {
            if (entry is ItemTemplate template) items.Templates.Templates.Add(template);
        }
        WorldTestHost host;
        using (items.Use())
        {
            TownVendorServices.Current.Value = fixture;
            try { host = WorldTestHost.Start(); }
            finally { TownVendorServices.Current.Value = null; }
        }
        await host.OnWorldAsync(() =>
        {
            host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore(
                [.. catalog.OfType<SpellInfo>()], [], []);
            return true;
        });
        return host;
    }

    [Fact]
    public void CatalogFoodAndStarterEntryUseTheSameSpellClassification()
    {
        var spells = new SpellSystem(new SpellStore([Aura(993001, AuraType.ModRegen)], [], []), () => 0);

        Assert.True(PlayerbotConsumables.TryClassify(Template(117, 993001), spells, out PlayerbotConsumableKind starter));
        Assert.Equal(PlayerbotConsumableKind.Food, starter);
        Assert.True(PlayerbotConsumables.TryClassify(Template(993101, 993001), spells, out PlayerbotConsumableKind catalog));
        Assert.Equal(PlayerbotConsumableKind.Food, catalog);
    }

    [Fact]
    public void ManaDrinkRequiresTheManaPowerType()
    {
        var spells = new SpellSystem(new SpellStore([
            Aura(993002, AuraType.ModPowerRegen, (int)PowerType.Mana),
            Aura(993003, AuraType.ModPowerRegen, (int)PowerType.Rage)], [], []), () => 0);

        Assert.True(PlayerbotConsumables.TryClassify(Template(993102, 993002), spells, out PlayerbotConsumableKind drink));
        Assert.Equal(PlayerbotConsumableKind.Drink, drink);
        Assert.False(PlayerbotConsumables.TryClassify(Template(993103, 993003), spells, out _));
    }

    [Fact]
    public void AmbiguousOrNegativeCatalogSpellsAreIgnored()
    {
        var spells = new SpellSystem(new SpellStore([
            Aura(993004, AuraType.ModRegen, basePoints: -1),
            new SpellInfo
            {
                Id = 993005, AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
                Effects = [
                    new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRegen, BasePoints = 1 },
                    new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModPowerRegen, BasePoints = 1, MiscValue = (int)PowerType.Mana },
                    new(),
                ],
            },
        ], [], []), () => 0);

        Assert.False(PlayerbotConsumables.TryClassify(Template(993104, 993004), spells, out _));
        Assert.False(PlayerbotConsumables.TryClassify(Template(993105, 993005), spells, out _));
    }

    private static ItemTemplate Template(uint entry, uint spell)
        => new() { Entry = entry, Stackable = 20, Spells = [new ItemSpell(spell, 0, -1, 0, 0, 0, 0)] };

    private static SpellInfo Aura(uint id, AuraType type, int misc = 0, int basePoints = 1)
        => new()
        {
            Id = id,
            RecoveryTime = 60_000,
            SpellFamilyName = 0,
            AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = type, BasePoints = basePoints,
                BaseDice = 1, DieSides = 1, MiscValue = misc, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
        };
}
