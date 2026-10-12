using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Scripting;
using ArcaneCore.Game.Scripting.Modules;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Scripting;
using ArcaneCore.World.Scripting.Modules.DuelReset;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Scripting;

/// <summary>Module discovery, the Modules:&lt;Name&gt;:Enabled switch and .reload config (docs/integration/script-hooks.md).</summary>
public sealed class ScriptHooksFeatureTests
{
    private static (ServiceProvider Services, WorldRuntime World) NewWorld(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        ServiceProvider sp = services.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return (sp, new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance));
    }

    [Fact]
    public void FeatureAndDuelResetModuleAreDiscovered()
    {
        Assert.Contains(typeof(ScriptHooksFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(DuelResetModule), ScriptHooksFeature.ModuleTypes);
    }

    [Fact]
    public async Task ByDefault_NoModuleLoads_AndNoHookIsRegistered()
    {
        (ServiceProvider sp, WorldRuntime world) = NewWorld([]);
        await using (sp)
        using (world)
        {
            var feature = new ScriptHooksFeature(sp);
            feature.Attach(world);

            Assert.Empty(feature.Loaded);
            Assert.Empty(world.Scripts.Registered);
        }
    }

    [Fact]
    public async Task Enabled_LoadsDuelReset_WithItsSettings_AndReloadAppliesNewOnes()
    {
        (ServiceProvider sp, WorldRuntime world) = NewWorld(new()
        {
            ["Modules:DuelReset:Enabled"] = "true",
            ["Modules:DuelReset:CooldownAge"] = "45",
            ["Modules:DuelReset:Zones"] = "",
        });
        await using (sp)
        using (world)
        {
            var feature = new ScriptHooksFeature(sp);
            feature.Attach(world);

            DuelResetModule module = Assert.IsType<DuelResetModule>(Assert.Single(feature.Loaded));
            DuelResetScript script = Assert.IsType<DuelResetScript>(Assert.Single(world.Scripts.Registered));
            Assert.Same(module.Script, script);
            Assert.Equal(45u, script.Settings.CooldownAge);
            Assert.True(script.Settings.HealthMana);

            IConfiguration reloaded = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:DuelReset:Enabled"] = "true",
                ["Modules:DuelReset:HealthMana"] = "false",
            }).Build();
            feature.ReloadConfig(reloaded);

            Assert.False(script.Settings.HealthMana);
            Assert.Equal(30u, script.Settings.CooldownAge);
        }
    }

    // ---- Lazy bridges: a hook added by a hot swap must reach the Game events (ScriptHooksFeature.EnsureBridges on HooksChanged) ----

    private sealed class EquipCounter : IItemHooks
    {
        public int Equips;
        public int Unequips;

        public void OnEquip(Player player, Item item, byte slot) => Equips++;

        public void OnUnequip(Player player, Item item, byte slot) => Unequips++;
    }

    private sealed class GroupCounter : IGroupHooks
    {
        public readonly List<ObjectGuid> Added = [];
        public int Disbands;

        public void OnMemberAdded(Group group, ObjectGuid member) => Added.Add(member);

        public void OnDisband(Group group) => Disbands++;
    }

    private sealed class SpellOnly : ISpellHooks
    {
        public void OnCastFinished(ArcaneCore.Game.Spells.SpellCast cast, bool completed) { }
    }

    private sealed class GroupOnly : IGroupHooks
    {
        public void OnDisband(Group group) { }
    }

    private static WorldTestHost StartWithSword()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ArcaneCore.Kernel.Items.ItemDamage(1, 3, 0)] });
        content.Templates.StartingItems.Add(new ArcaneCore.Kernel.Items.StartingItem(1, 1, 25, 1));
        using (content.Use())
        {
            return WorldTestHost.Start();
        }
    }

    /// <summary>Take the sword off and put it back on (world thread): one OnUnequip and one OnEquip for a bound player.</summary>
    private static void TakeOffAndWearSword(Player player)
    {
        Item sword = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
        player.Inventory.SwapItem(sword.BagSlot, sword.Slot, InventorySlots.Bag0, InventorySlots.ItemStart + 10);
        Assert.Null(player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
        Item bagged = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart + 10)!;
        player.Inventory.AutoEquipItem(bagged.BagSlot, bagged.Slot);
        Assert.NotNull(player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
    }

    [Fact]
    public async Task AnEquipHook_AddedByHotSwapAfterLogin_FiresOncePerEquip_ThroughRepeatedSwaps_AndStopsWhenSwappedOut()
    {
        await using WorldTestHost host = StartWithSword();
        ScriptHookRegistry scripts = host.World.Scripts;
        await using WorldTestClient client = await host.EnterWorldAsync("HOOKEQ1", "Swapper");
        Assert.False(await host.OnWorldAsync(() => scripts.Item.HasEquipHooks), "no built-in module may have registered an equip hook");
        var first = new EquipCounter();
        var second = new EquipCounter();
        int changed = 0;
        await host.OnWorldAsync(() => scripts.HooksChanged += () => changed++);

        // No hook yet: wearing the sword reaches nothing.
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Swapper")!));
        Assert.Equal(0, first.Equips);

        // First swap adds the first hook for the kind: the bridge subscribes now and binds the player who is already online.
        await host.OnWorldAsync(() => scripts.CommitSwap([], [first]));
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Swapper")!));
        Assert.Equal(1, first.Equips);
        Assert.Equal(1, first.Unequips);

        // A second swap raises HooksChanged again: EnsureBridges must not bind the player a second time.
        await host.OnWorldAsync(() => scripts.CommitSwap([], [second]));
        Assert.Equal(2, changed);
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Swapper")!));
        Assert.Equal(2, first.Equips);
        Assert.Equal(1, second.Equips);

        // Swapping everything back out must not throw, and no removed hook fires again.
        await host.OnWorldAsync(() => scripts.CommitSwap([first, second], []));
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Swapper")!));
        Assert.Equal(2, first.Equips);
        Assert.Equal(1, second.Equips);
        Assert.False(await host.OnWorldAsync(() => scripts.Item.HasEquipHooks));

        // And a hook that comes back after the empty state fires once per equip, with the bridge not doubled by the earlier swaps.
        var third = new EquipCounter();
        await host.OnWorldAsync(() => scripts.CommitSwap([], [third]));
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Swapper")!));
        Assert.Equal(1, third.Equips);
        Assert.Equal(2, first.Equips);
    }

    [Fact]
    public async Task AnEquipHook_AddedByHotSwap_AlsoReachesAPlayerWhoLogsInAfterwards()
    {
        await using WorldTestHost host = StartWithSword();
        ScriptHookRegistry scripts = host.World.Scripts;
        var hook = new EquipCounter();
        await host.OnWorldAsync(() => scripts.CommitSwap([], [hook]));

        await using WorldTestClient client = await host.EnterWorldAsync("HOOKEQ2", "Latecomer");
        await host.OnWorldAsync(() => TakeOffAndWearSword(host.World.FindOnlinePlayer("Latecomer")!));

        Assert.Equal(1, hook.Equips);
        Assert.Equal(1, hook.Unequips);
    }

    [Fact]
    public async Task AGroupHook_AddedByHotSwap_FiresOncePerMember_ThroughASecondSwap_AndNotAfterSwapOut()
    {
        await using WorldTestHost host = StartWithSword();
        ScriptHookRegistry scripts = host.World.Scripts;
        await using WorldTestClient leaderClient = await host.EnterWorldAsync("HOOKGR1", "Leaderone");
        await using WorldTestClient memberClient = await host.EnterWorldAsync("HOOKGR2", "Membertwo");
        GroupManager groups = host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
        var first = new GroupCounter();
        var second = new GroupCounter();

        // Invite, accept, then the member leaves (a two-person group disbands). Returns the member's guid.
        async Task<ObjectGuid> FormAndDisband() => await host.OnWorldAsync(() =>
        {
            Player leader = host.World.FindOnlinePlayer("Leaderone")!;
            Player member = host.World.FindOnlinePlayer("Membertwo")!;
            groups.Invite(leader, member.Name);
            groups.Accept(member);
            Assert.True(groups.AreInSameGroup(leader.Guid, member.Guid));
            groups.Leave(member);
            Assert.Null(groups.GetGroup(member.Guid));
            return member.Guid;
        });

        // Before any hook exists nothing is subscribed, so the first swap is what turns the bridge on.
        await host.OnWorldAsync(() => scripts.CommitSwap([], [first]));
        ObjectGuid memberGuid = await FormAndDisband();
        Assert.Equal(1, first.Added.Count(g => g == memberGuid));
        Assert.Equal(1, first.Disbands);

        // A second swap raises HooksChanged again: still one call per event for each hook, not two.
        await host.OnWorldAsync(() => scripts.CommitSwap([], [second]));
        await FormAndDisband();
        Assert.Equal(2, first.Added.Count(g => g == memberGuid));
        Assert.Equal(2, first.Disbands);
        Assert.Equal(1, second.Added.Count(g => g == memberGuid));
        Assert.Equal(1, second.Disbands);

        // Swapped out: no throw, no further calls.
        await host.OnWorldAsync(() => scripts.CommitSwap([first, second], []));
        await FormAndDisband();
        Assert.Equal(2, first.Disbands);
        Assert.Equal(1, second.Disbands);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<string> Warnings = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose() { }

        private sealed class Capture(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    lock (owner.Warnings)
                    {
                        owner.Warnings.Add(formatter(state, exception));
                    }
                }
            }
        }
    }

    [Fact]
    public async Task WithoutTheSocialAndSpellFeatures_TheMissingBridgeIsWarnedAboutOnce_NotOnEveryHooksChange()
    {
        var provider = new CapturingLoggerProvider();
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.AddProvider(provider));
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ServiceProvider sp = collection.BuildServiceProvider();
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        await using (sp)
        {
            new ScriptHooksFeature(sp).Attach(world);
            var group = new GroupOnly();
            var spell = new SpellOnly();

            world.Scripts.CommitSwap([], [group]);
            world.Scripts.CommitSwap([], [spell]);
            world.Scripts.CommitSwap([group], []);
            world.Scripts.CommitSwap([], [new GroupOnly()]);

            string[] warnings;
            lock (provider.Warnings)
            {
                warnings = [.. provider.Warnings];
            }

            Assert.Single(warnings, w => w.Contains("No social feature", StringComparison.Ordinal));
            Assert.Single(warnings, w => w.Contains("No spell feature", StringComparison.Ordinal));
        }
    }
}
