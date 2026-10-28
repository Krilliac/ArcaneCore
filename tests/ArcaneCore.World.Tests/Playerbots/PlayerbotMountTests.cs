using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Riding reserves, the riding trainer and mount vendor, and riding on long walks, over a synthetic Mounts trainer and mount vendor.</summary>
public sealed class PlayerbotMountTests
{
    private static WorldTestHost Start(MountFixture fixture, TrainerReactions? reactions = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ISpellContentStore>(fixture);
            if (reactions is not null) services.AddSingleton<IPlayerReputation>(reactions);
        });

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        ItemSpell mount = new(MountFixture.MountSpell, 0, -1, 0, 0, 0, 0);
        items.Templates.Templates.AddRange(
        [
            // Skill-based mount of the first tier (rank 75): usable once the bot has the riding skill.
            new ItemTemplate { Entry = 98001, Name = "Skill mount", Class = 15, Stackable = 1, BuyPrice = 300, RequiredSkill = 762, RequiredSkillRank = 75, Spells = [mount] },
            // Cheaper, but only for orcs.
            new ItemTemplate { Entry = 98002, Name = "Orc mount", Class = 15, Stackable = 1, BuyPrice = 100, RequiredSkill = 762, RequiredSkillRank = 75, AllowableRace = 1u << 1, Spells = [mount] },
            // The cheapest the human can use: a mount named by its on-use spell, no skill.
            new ItemTemplate { Entry = 98003, Name = "Spell mount", Class = 15, Stackable = 1, BuyPrice = 250, Spells = [mount] },
            // Cheaper still, but of the second tier (rank 150).
            new ItemTemplate { Entry = 98004, Name = "Epic mount", Class = 15, Stackable = 1, BuyPrice = 120, RequiredSkill = 762, RequiredSkillRank = 150, Spells = [mount] },
            // A first-tier mount no vendor stocks until a test adds it to the NPC data.
            new ItemTemplate { Entry = 98005, Name = "Late mount", Class = 15, Stackable = 1, BuyPrice = 90, RequiredSkill = 762, RequiredSkillRank = 75, Spells = [mount] },
        ]);
        return items;
    }

    private static void InstallMountSpell(WorldTestHost host, SpellAttributes attributes = 0)
    {
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id != MountFixture.MountSpell), new SpellInfo
        {
            Id = MountFixture.MountSpell, Attributes = attributes,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature }, new(), new()],
        }], [], []);
    }

    /// <summary>
    /// With spell batching on (ARCANECORE_TEST_SPELL_BATCH_MS), a mount-up that went off waits in the batch for its go: the world runs on
    /// to the batch boundary. Without batching nothing is held and this returns at once.
    /// </summary>
    private static Task SpellBatchGoneAsync(WorldTestHost host, WorldSession session)
        => host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System.HasPendingGo(session.Player!),
            "the batched mount-up went off");

    [Fact]
    public async Task Reserve_IsTheRidingTrainingPlusTheCheapestUsableMount_FromTheDataAndDiscounted()
    {
        var fixture = new MountFixture();
        var reactions = new TrainerReactions();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture, reactions);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    player.Money = 10_000;
                    var options = new PlayerbotOptions { Enabled = true };
                    var budget = new PlayerbotBudget(session, options);
                    budget.Refresh(player);
                    PlayerbotRidingStatus riding = budget.Riding;
                    Assert.Equal(PlayerbotRidingStep.Train, riding.Step);
                    Assert.Equal(500u, riding.TrainingCost);
                    Assert.Equal(250u, riding.MountCost); // not the 100 orc mount, not the 120 second-tier mount, not the 300 skill mount
                    Assert.Equal(98003u, riding.MountItem);
                    Assert.Equal(MountFixture.Vendor, riding.MountVendor);
                    Assert.Contains(new PlayerbotBudgetLine(PlayerbotSpendKind.Riding, 750u), budget.Lines.ToArray());
                    Assert.Equal(9_250u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));

                    // Both prices are the discounted ones.
                    reactions.Discount = 0.8f;
                    budget.Invalidate();
                    budget.Refresh(player);
                    Assert.Equal(400u, riding.TrainingCost);
                    Assert.Equal(200u, riding.MountCost);
                    Assert.Equal(9_400u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task Reserve_StartsOnlyWithinSaveLevelsAheadOfTheTiersRequiredLevel()
    {
        var fixture = new MountFixture { TierLevel = 6 };
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!; // level 1
                    player.Money = 10_000;
                    var options = new PlayerbotOptions { Enabled = true };
                    options.Mounts.SaveLevelsAhead = 4;
                    var budget = new PlayerbotBudget(session, options);
                    budget.Refresh(player);
                    Assert.Equal(PlayerbotRidingStep.Distant, budget.Riding.Step);
                    Assert.Equal(10_000u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    options.Mounts.SaveLevelsAhead = 5;
                    budget.Invalidate();
                    budget.Refresh(player);
                    Assert.Equal(PlayerbotRidingStep.Saving, budget.Riding.Step);
                    Assert.Equal(9_250u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    Assert.False(budget.TrainingDue(player)); // saving, but the level is not there
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task RidingTrainer_TeachesTheBotOfItsRace_RefusesAnotherRace_ThenTheBotBuysTheCheapestUsableMount()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(MountFixture.TrainerGuid)
                    && session.Player.VisibleObjects.Contains(MountFixture.OtherTrainerGuid) && session.Player.VisibleObjects.Contains(MountFixture.VendorGuid), "npcs visible");
                var options = new PlayerbotOptions { Enabled = true };
                var budget = new PlayerbotBudget(session, options);
                var goals = new PlayerbotTownGoals(session, options) { Budget = budget };
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    player.Money = 10_000;
                    QuestNpcServices services = session.Services.GetRequiredService<QuestNpcFeature>().Services;
                    NpcInfo own = services.Deps.Creatures!.Find(player, MountFixture.TrainerGuid)!;
                    NpcInfo other = services.Deps.Creatures!.Find(player, MountFixture.OtherTrainerGuid)!;
                    Assert.Equal(500u, services.GetRidingTrainerQuote(player, own)!.Cost);
                    Assert.Null(services.GetRidingTrainerQuote(player, other)); // a trainer of another race
                    Assert.Null(services.GetClassTrainerQuote(player, own));    // and a class quote still refuses mounts
                    return true;
                });
                await host.OnWorldAsync(() => Assert.True(goals.Update(session.Player!, 1000)));
                await host.OnWorldAsync(() => Assert.True(goals.Update(session.Player!, 1500)));
                await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.HasSpell(session.Player!, MountFixture.Learned),
                    "riding learned");
                Assert.Equal(9_500u, await host.PlayerStateAsync("Controlone", p => p.Money));

                // Now it can ride and owns no mount: the cheapest it can use is bought from the vendor.
                await host.OnWorldAsync(() =>
                {
                    budget.Invalidate();
                    budget.Refresh(session.Player!);
                    Assert.Equal(PlayerbotRidingStep.BuyMount, budget.Riding.Step);
                    Assert.Equal(250u, budget.Riding.MountCost);
                    return true;
                });
                for (int i = 0; i < 6 && await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(98003)) == 0; i++)
                {
                    uint now = 2000u + (uint)i * 1500u;
                    await host.OnWorldAsync(() => { goals.Update(session.Player!, now); return true; });
                }

                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(98003)));
                Assert.Equal(0u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(98001) + p.Inventory.GetItemCount(98002) + p.Inventory.GetItemCount(98004)));
                Assert.Equal(9_250u, await host.PlayerStateAsync("Controlone", p => p.Money));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task RidingTraining_WaitsUntilTheMoneyCoversTheTrainingAndTheMount()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
                    player.Money = 700; // the training alone (500) but not the mount (250)
                    Assert.False(budget.TrainingDue(player));
                    player.Money = 750;
                    Assert.True(budget.TrainingDue(player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    private static PlayerbotRoute LongRoute(Player player, float yards = 200)
        => new([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + yards, player.Y, player.Z)], yards);

    /// <summary>What a mount-up that did not happen saw: the bot's state that gates it and the mount driver's own fields.</summary>
    internal static string MountState(WorldTestHost host, WorldSession session, Player p)
    {
        object driver = PlayerbotMount.For(session);
        string fields = string.Join(", ", driver.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.Name + "=" + f.GetValue(driver)));
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        return $"now={session.World.NowMs} stand={p.StandState} unit={p.UnitFlags} move={p.Movement.Flags} map={(p.Map is null ? "none" : p.MapId)} "
            + $"outdoors={p.Map?.Collision.IsOutdoors(p.X, p.Y, p.Z)} pendingGo={spells.HasPendingGo(p)} alive={p.IsAlive} at={p.X:F1},{p.Y:F1},{p.Z:F1} "
            + $"mountItems={p.Inventory.GetItemCount(98003)} bagsLoaded={p.Inventory.IsLoaded} settling={p.IsQuestSettlementPending} combat={p.Combat.IsInCombat}; {fields}";
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldSession, string> MountStates = new();

    /// <summary>The <see cref="MountState"/> after the last <see cref="TryMountAsync"/> of <paramref name="session"/> (for a failure message).</summary>
    private static string? LastMountState(WorldSession session) => MountStates.TryGetValue(session, out string? state) ? state : null;

    private static async Task<(bool Took, bool Mounted, int Failures)> TryMountAsync(WorldTestHost host, WorldSession session, Action<Player>? arrange = null,
        SpellAttributes attributes = 0, ILineOfSight? los = null)
    {
        var options = new PlayerbotOptions { Enabled = true };
        bool took = await host.OnWorldAsync(() =>
        {
            InstallMountSpell(host, attributes);
            Player player = session.Player!;
            if (los is not null) WorldCollision.Of(host.World).Install(lineOfSight: los, pathfinder: new OpenPathfinder());
            player.Inventory.AddItem(98003, 1, out _);
            arrange?.Invoke(player);
            session.ManagedBudget = new ManagedActionBudget(5);
            return PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
        });

        // Spell batching on: the mount-up is underway until its go; the walking step after it finds the bot mounted.
        if (await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasPendingGo(session.Player!)))
        {
            await SpellBatchGoneAsync(host, session);
            took = await host.OnWorldAsync(() => PlayerbotMount.BeforeStep(session, session.Player!, LongRoute(session.Player!), options));
        }

        (bool, bool, int) result = await host.OnWorldAsync(() => (took, MountService.IsMounted(session.Player!), PlayerbotMount.For(session).Failures));
        MountStates.AddOrUpdate(session, await host.OnWorldAsync(() => MountState(host, session, session.Player!)));
        return result;
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task LongLegOutdoors_MountsThroughTheRealItemUsePath()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                (bool took, bool mounted, int failures) = await TryMountAsync(host, session);
                Assert.True(mounted, LastMountState(session));
                Assert.False(took); // mounted at once: the step walks on
                Assert.Equal(0, failures);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task ShortLeg_InCombat_Swimming_Shapeshifted_AndInstanceBotsDoNotMount()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    player.Inventory.AddItem(98003, 1, out _);
                    session.ManagedBudget = new ManagedActionBudget(5);
                    var options = new PlayerbotOptions { Enabled = true };
                    // Under the minimum leg.
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player, 40), options);
                    Assert.False(MountService.IsMounted(player));
                    // In combat.
                    player.Map!.Combat.SetInCombatState(player, 10_000);
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    player.Map!.Combat.CombatStop(player);
                    // Shapeshifted.
                    player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0);
                    // Swimming.
                    player.AddMovementFlags(MovementFlags.Swimming);
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    player.RemoveMovementFlags(MovementFlags.Swimming);
                    // Mounts disabled.
                    options.Mounts.Enabled = false;
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    // Nothing above counted as a failed attempt: no backoff was set.
                    Assert.Equal(0, PlayerbotMount.For(session).Failures);
                    options.Mounts.Enabled = true;
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    return true;
                });
                await SpellBatchGoneAsync(host, session);
                Assert.True(await host.OnWorldAsync(() => MountService.IsMounted(session.Player!)));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task Indoors_BacksOffInsteadOfRetryingEveryThink()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new IndoorFloor(), pathfinder: new OpenPathfinder());
                    player.Inventory.AddItem(98003, 1, out _);
                    var options = new PlayerbotOptions { Enabled = true };
                    session.ManagedBudget = new ManagedActionBudget(5);
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    int used = 5 - session.ManagedBudget.Remaining;
                    Assert.Equal(0, used); // refused before any packet: indoors
                    Assert.NotEqual(0u, PlayerbotMount.For(session).BackoffUntil);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task RefusedMountCast_BacksOffAndDoesNotRetryOnTheNextThinks()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    // A mount spell that may be cast only indoors: the server's own check (MountCastCheck) refuses it outdoors.
                    InstallMountSpell(host, (SpellAttributes)0x4000);
                    Player player = session.Player!;
                    player.Inventory.AddItem(98003, 1, out _);
                    var options = new PlayerbotOptions { Enabled = true };
                    session.ManagedBudget = new ManagedActionBudget(50);
                    PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.False(MountService.IsMounted(player));
                    Assert.Equal(1, PlayerbotMount.For(session).Failures);
                    int afterFirst = session.ManagedBudget.Remaining;
                    Assert.True(afterFirst < 50, "the first attempt went through the real packet path");
                    for (int i = 0; i < 5; i++) PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                    Assert.Equal(afterFirst, session.ManagedBudget.Remaining); // backed off: no further packets
                    Assert.Equal(1, PlayerbotMount.For(session).Failures);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task MountedBot_DismountsBeforeTalkingToAnNpc()
    {
        var fixture = new MountFixture();
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(MountFixture.TrainerGuid), "trainer visible");
                string afterStep = string.Empty;
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    player.Inventory.AddItem(98003, 1, out _);
                    session.ManagedBudget = new ManagedActionBudget(5);
                    bool took = PlayerbotMount.BeforeStep(session, player, LongRoute(player), new PlayerbotOptions { Enabled = true });
                    afterStep = $"took={took}; " + MountState(host, session, player);
                    return true;
                });
                await SpellBatchGoneAsync(host, session);
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    Assert.True(MountService.IsMounted(player), afterStep + " || later: " + MountState(host, session, player));
                    session.ManagedBudget = new ManagedActionBudget(5);
                    var writer = new PacketWriter(8);
                    writer.WriteUInt64(MountFixture.TrainerGuid.Value);
                    Assert.True(session.TryManagedAction(WorldOpcode.CmsgTrainerList, writer.ToArray()));
                    Assert.False(MountService.IsMounted(player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    private static async Task RunAsync(MountFixture fixture, Func<WorldTestHost, WorldSession, Task> body)
    {
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try { await body(host, session); }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task Reserve_IsDropped_WhenNoRidingTrainerStandsOnTheBotsMap()
    {
        await RunAsync(new MountFixture { TrainerMap = 1 }, (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 10_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal(PlayerbotRidingStep.None, budget.Riding.Step);
            Assert.DoesNotContain(budget.Lines.ToArray(), line => line.Kind is PlayerbotSpendKind.Riding or PlayerbotSpendKind.Mount);
            Assert.Equal(10_000u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
            Assert.False(budget.TrainingDue(player));
            Assert.Equal("none", budget.NextStep(player));
            return true;
        }));
    }

    [Fact]
    public async Task Reserve_IsDropped_WhenNoMountVendorStandsOnTheBotsMap()
    {
        await RunAsync(new MountFixture { VendorMap = 1 }, (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 10_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal(PlayerbotRidingStep.None, budget.Riding.Step);
            Assert.DoesNotContain(budget.Lines.ToArray(), line => line.Kind is PlayerbotSpendKind.Riding or PlayerbotSpendKind.Mount);
            Assert.Equal(10_000u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
            return true;
        }));
    }

    [Fact]
    public async Task Reserve_ForTheSecondTier_IsPlannedEvenThoughTheBotOwnsAnOldTiersMount()
    {
        // Both tiers open at level 1. The bot has learned Apprentice riding and carries the Apprentice (rank 75) mount: the Journeyman
        // training and the epic mount (rank 150) are still what it saves for.
        await RunAsync(new MountFixture { TierLevel = 1, EpicLevel = 1 }, (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 100_000;
            Assert.True(host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(player, MountFixture.Learned));
            Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(98001, 1, out _));
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            PlayerbotRidingStatus riding = budget.Riding;
            Assert.Equal(PlayerbotRidingStep.Train, riding.Step);
            Assert.Equal(1, riding.TierIndex);
            Assert.Equal(5000u, riding.TrainingCost);
            Assert.Equal(98004u, riding.MountItem);
            Assert.Equal(120u, riding.MountCost);
            Assert.Contains(new PlayerbotBudgetLine(PlayerbotSpendKind.Riding, 5120u), budget.Lines.ToArray());
            return true;
        }));
    }

    [Fact]
    public async Task ServiceIndex_IsRebuilt_WhenTheNpcDataIsReloaded()
    {
        var fixture = new MountFixture();
        await RunAsync(fixture, (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 10_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal(98003u, budget.Riding.MountItem);
            Assert.Equal(250u, budget.Riding.MountCost);

            // A reload (.reload npc) swaps the NPC store: the vendor now also stocks a cheaper mount.
            QuestNpcServices services = session.Services.GetRequiredService<QuestNpcFeature>().Services;
            NpcContent content = ((INpcContentStore)fixture).LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            services.ReplaceNpcs(new NpcStore(content with
            {
                VendorItems = [.. content.VendorItems, new VendorItem { Entry = MountFixture.Vendor, Item = 98005, Slot = 4 }],
            }));
            budget.Invalidate();
            budget.Refresh(player);
            Assert.Equal(98005u, budget.Riding.MountItem);
            Assert.Equal(90u, budget.Riding.MountCost);
            return true;
        }));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task BattleStance_DoesNotStopTheBotFromMountingWithAKnownMountSpell()
    {
        await RunAsync(new MountFixture(), async (host, session) =>
        {
            (bool took, bool battle, int fails) = await TryMountAsync(host, session, p => { host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(p, MountFixture.MountSpell); p.SetByte(UpdateFields.UnitFieldBytes1, 2, 17); }); // battle stance
            Assert.Equal((false, 0), (took, fails));
            Assert.True(battle, LastMountState(session));
        });
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task BatchedMountUp_IsUnderwayUntilItsGo_NotAFailedSummon()
    {
        // With a spell batch window the instant mount-up (here a mount item's use) leaves the cast slot and waits for the boundary: the
        // bot must wait for it, not judge it refused, back off and report bot-mount/summon-failed.
        await RunAsync(new MountFixture(), async (host, session) =>
        {
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            Player player = session.Player!;
            var options = new PlayerbotOptions { Enabled = true };
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                spells.SpellBatchWindowMs = 400;
                InstallMountSpell(host);
                player.Inventory.AddItem(98003, 1, out _);
                session.ManagedBudget = new ManagedActionBudget(5);
                Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                Assert.True(spells.HasPendingGo(player));
                Assert.False(MountService.IsMounted(player));
                Assert.True(PlayerbotMount.IsBusy(session));
                Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options)); // still underway: waits, sends nothing
                Assert.Equal(0, PlayerbotMount.For(session).Failures);
                return true;
            });

            await SpellBatchGoneAsync(host, session);
            await host.OnWorldAsync(() =>
            {
                Assert.True(MountService.IsMounted(player));
                Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                Assert.False(PlayerbotMount.IsBusy(session));
                Assert.Equal(0, PlayerbotMount.For(session).Failures);
                Assert.Equal(0u, PlayerbotMount.For(session).BackoffUntil);
                Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
                return true;
            });
        });
    }

    [Fact]
    public void OnlyTimingRefusals_AreNotFailedMountAttempts()
    {
        Assert.True(PlayerbotMount.IsTransientRefusal(SpellCastResult.NotReady));
        Assert.True(PlayerbotMount.IsTransientRefusal(SpellCastResult.SpellInProgress));
        Assert.True(PlayerbotMount.IsTransientRefusal(SpellCastResult.Moving));
        Assert.False(PlayerbotMount.IsTransientRefusal(SpellCastResult.NotShapeshift));
        Assert.False(PlayerbotMount.IsTransientRefusal(SpellCastResult.OnlyIndoors));
    }

    [Fact]
    public async Task ClassMountSpell_WithInsufficientMana_SkipsMountingWithoutReportingSummonFailed()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            const uint summonWarhorse = 13819;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id != summonWarhorse), new SpellInfo
            {
                Id = summonWarhorse,
                Name = "Summon Warhorse",
                PowerType = (int)PowerType.Mana,
                ManaCost = 100,
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            Player player = session.Player!;
            Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 200);
            player.SetUInt32(UpdateFields.UnitFieldPower1, 50); // < 100 mana

            using var capture = PlayerbotAnomalies.CaptureFor(player);
            var options = new PlayerbotOptions { Enabled = true };
            session.ManagedBudget = new ManagedActionBudget(5);

            Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.False(MountService.IsMounted(player));
            Assert.Equal(5, session.ManagedBudget.Remaining);
            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task StuckLootingFlag_IsClearedBeforeMount_AndSummonWarhorseCompletesWithMountAuraApplied()
    {
        await RunAsync(new MountFixture(), async (host, session) =>
        {
            Player player = session.Player!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            var options = new PlayerbotOptions { Enabled = true };
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                const uint summonWarhorse = 13819;
                var warhorseSpell = new SpellInfo
                {
                    Id = summonWarhorse,
                    Name = "Summon Warhorse",
                    PowerType = (int)PowerType.Mana,
                    ManaCost = 100,
                    CastTime = new SpellCastTime(3000, 0, 3000),
                    Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id != summonWarhorse), warhorseSpell], [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 200);
                player.SetUInt32(UpdateFields.UnitFieldPower1, 200);

                player.UnitFlags |= UnitFlags.Looting;
                Assert.False(PlayerbotMount.CanMountHere(player));

                session.ManagedBudget = new ManagedActionBudget(5);

                // Walking step clears the stuck Looting flag and begins the 3000 ms cast.
                Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                Assert.Equal(0u, (uint)(player.UnitFlags & UnitFlags.Looting));
                Assert.True(PlayerbotMount.IsBusy(session));

                feature.System.Update(3000);
                return true;
            });
            await SpellBatchGoneAsync(host, session);
            await host.OnWorldAsync(() =>
            {
                Assert.True(MountService.IsMounted(player));
                Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                Assert.False(PlayerbotMount.IsBusy(session));
                Assert.Equal(0, PlayerbotMount.For(session).Failures);
                Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
                return true;
            });
        });
    }

    [Fact]
    public async Task DeepWaterEnvironment_WithoutSwimmingFlag_DoesNotAttemptToMount()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Inventory.AddItem(98003, 1, out _);
            player.Locomotion.Environment = EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid;
            Assert.False(player.Movement.HasFlag(MovementFlags.Swimming));
            Assert.False(PlayerbotMount.CanMountHere(player));

            var options = new PlayerbotOptions { Enabled = true };
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.False(MountService.IsMounted(player));
            Assert.Equal(5, session.ManagedBudget.Remaining);
            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            return true;
        }));
    }

    [Fact]
    public async Task MountCastInterruptedByCombatSitOrDismount_ClearsCastingWithoutReportingSummonFailed()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            const uint summonWarhorse = 13819;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id != summonWarhorse), new SpellInfo
            {
                Id = summonWarhorse,
                Name = "Summon Warhorse",
                CastTime = new SpellCastTime(3000, 0, 3000),
                Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
            }], [], []);
            Player player = session.Player!;
            Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));
            using var capture = PlayerbotAnomalies.CaptureFor(player);
            var options = new PlayerbotOptions { Enabled = true };

            // 1. Interrupted by entering combat before the next Step.
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.True(PlayerbotMount.IsBusy(session));
            feature.System.InterruptCurrentCast(player);
            player.Map!.Combat.SetInCombatState(player, 10_000);
            Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.False(PlayerbotMount.IsBusy(session));
            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
            player.Map!.Combat.CombatStop(player);

            // 2. Interrupted by sitting before the next Step.
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.True(PlayerbotMount.IsBusy(session));
            feature.System.InterruptCurrentCast(player);
            player.SetStandState(StandState.Sit);
            Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.False(PlayerbotMount.IsBusy(session));
            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
            player.SetStandState(StandState.Stand);

            // 3. Interrupted by DismountForAction mid-cast.
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.True(PlayerbotMount.IsBusy(session));
            feature.System.InterruptCurrentCast(player);
            PlayerbotMount.DismountForAction(session, player, WorldOpcode.CmsgAttackswing);
            Assert.False(PlayerbotMount.IsBusy(session));
            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
            return true;
        }));
    }

    private static PlayerbotRidingTrips FarTrips(WorldTestHost host, WorldSession session, PlayerbotOptions options, out PlayerbotBudget budget)
    {
        WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
        InstallMountSpell(host);
        budget = new PlayerbotBudget(session, options);
        return new PlayerbotRidingTrips(session, options) { Budget = budget };
    }

    [Fact]
    public async Task RidingTrip_StartsWhenDue_AndWalksTowardTheTrainer()
    {
        await RunAsync(new MountFixture { SpawnOffset = 130 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var options = new PlayerbotOptions { Enabled = true };
            PlayerbotRidingTrips trips = FarTrips(host, session, options, out _);
            player.Money = 700; // the training (500) but not the mount (250): saving, nothing to walk to yet
            Assert.False(trips.HasCandidate(player));
            Assert.False(trips.Update(player, 500));
            Assert.Equal(0u, trips.TargetEntry);

            player.Money = 10_000;
            float start = player.X;
            Assert.True(trips.HasCandidate(player));
            session.ManagedBudget = new ManagedActionBudget(1);
            trips.Update(player, 500);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(trips.Update(player, 500));
            Assert.Equal(MountFixture.TrainerEntry, trips.TargetEntry);
            Assert.Equal(PlayerbotRidingTripKind.Trainer, trips.Kind);
            return true;
        }));
    }

    [Fact]
    public async Task RidingTrip_DoesNotStart_WhenNoSiteStandsOnTheBotsMap()
    {
        await RunAsync(new MountFixture { SpawnOffset = 130, SpawnMap = 1 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerbotRidingTrips trips = FarTrips(host, session, new PlayerbotOptions { Enabled = true }, out _);
            player.Money = 10_000;
            float start = player.X;
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(trips.HasCandidate(player));
            Assert.False(trips.Update(player, 500));
            Assert.Equal(0u, trips.TargetEntry);
            Assert.Equal(start, player.X);
            return true;
        }));
    }

    [Fact]
    public async Task RidingTrip_SetsAStuckSiteAside_ThenRestsBeforeTryingAgain()
    {
        await RunAsync(new MountFixture { SpawnOffset = 130 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerbotRidingTrips trips = FarTrips(host, session, new PlayerbotOptions { Enabled = true }, out _);
            player.Money = 10_000;
            session.ManagedBudget = new ManagedActionBudget(1);
            trips.Update(player, 500);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(trips.Update(player, 500));
            Assert.Equal(MountFixture.TrainerEntry, trips.TargetEntry);

            // No step can be taken (no packet budget): the bot closes no distance, and after the stuck time the only site is set aside.
            session.ManagedBudget = new ManagedActionBudget(0);
            trips.Update(player, 500);
            Assert.False(trips.Update(player, PlayerbotRidingTrips.StuckMs));
            Assert.Equal(0u, trips.TargetEntry);

            // Every site is blocked: the trip rests instead of planning again at the next think.
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(trips.HasCandidate(player));
            Assert.False(trips.Update(player, 500));
            Assert.Equal(0u, trips.TargetEntry);
            return true;
        }));
    }

    [Fact]
    public async Task ServiceIndex_IsRebuilt_WhenTheItemTemplatesAreReloaded()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 10_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal(98003u, budget.Riding.MountItem);

            // .reload item_template: a new mount the vendor already lists is now an item, and the cheap one needs a level the bot lacks.
            var items = host.WorldServices.GetRequiredService<ArcaneCore.World.Items.ItemsFeature>();
            ItemTemplate old = items.LoadedStore.Find(98003)!;
            ItemSpell mount = new(MountFixture.MountSpell, 0, -1, 0, 0, 0, 0);
            items.ReplaceTemplates(new ArcaneCore.Game.Items.ItemTemplateStore(
            [
                .. Items().Templates.Templates.Where(t => t.Entry != 98003),
                old with { RequiredLevel = 50 },
                new ItemTemplate { Entry = 98006, Name = "Reloaded mount", Class = 15, Stackable = 1, BuyPrice = 80, RequiredSkill = 762, RequiredSkillRank = 75, Spells = [mount] },
            ]));
            budget.Invalidate();
            budget.Refresh(player);
            Assert.Equal(98006u, budget.Riding.MountItem);
            Assert.Equal(80u, budget.Riding.MountCost);
            return true;
        }));
    }

    [Fact]
    public async Task RidingTraining_IsNotDue_WhenTheNextOfferIsRedForAReasonNoLevelMends()
    {
        await RunAsync(new MountFixture { TierSkillLacking = true }, (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 100_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal(PlayerbotRidingStep.None, budget.Riding.Step);
            Assert.False(budget.TrainingDue(player));
            Assert.DoesNotContain(budget.Lines.ToArray(), line => line.Kind is PlayerbotSpendKind.Riding or PlayerbotSpendKind.Mount);
            return true;
        }));
    }

    [Theory]
    [InlineData(56u)]
    [InlineData(60u)]
    public async Task Mount_OfTheLearnedTierIsBought_EvenWhenTheNextTierIsWithinSaveRange(uint level)
    {
        // Apprentice learned, no mount, the Journeyman training 4 levels away (or open at 60): the Apprentice mount is still what is bought.
        var fixture = new MountFixture { TierLevel = 56, EpicLevel = 60 };
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = Start(fixture);
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(MountFixture.VendorGuid), "vendor visible");
                var options = new PlayerbotOptions { Enabled = true };
                var budget = new PlayerbotBudget(session, options);
                var goals = new PlayerbotTownGoals(session, options) { Budget = budget };
                await host.OnWorldAsync(() =>
                {
                    InstallMountSpell(host);
                    Player player = session.Player!;
                    player.Level = (byte)level;
                    player.Money = 100_000;
                    Assert.True(host.WorldServices.GetRequiredService<SpellFeature>().Spellbook.LearnSpell(player, MountFixture.Learned));
                    budget.Refresh(player);
                    Assert.Equal(PlayerbotRidingStep.BuyMount, budget.Riding.Step);
                    Assert.Equal(0, budget.Riding.MountTierIndex);
                    Assert.Equal(1, budget.Riding.TierIndex);
                    Assert.Equal(98003u, budget.Riding.MountItem);
                    Assert.Contains("mount-tier:0", budget.Report(player));
                    return true;
                });
                for (int i = 0; i < 8 && await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(98003)) == 0; i++)
                {
                    uint now = 1000u + (uint)i * 1500u;
                    await host.OnWorldAsync(() => { goals.Update(session.Player!, now); return true; });
                }

                Assert.Equal(1u, await host.PlayerStateAsync("Controlone", p => p.Inventory.GetItemCount(98003)));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    [Fact]
    public async Task MountVendorThatKeepsFailing_IsLeftOutAndTheNextCheapestIsPlanned()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            player.Money = 10_000;
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
            budget.Refresh(player);
            Assert.Equal((MountFixture.Vendor, 98003u), (budget.Riding.MountVendor, budget.Riding.MountItem));

            for (int i = 1; i < PlayerbotBudget.VendorFailureLimit; i++) budget.NoteVendorFailure(MountFixture.Vendor);
            budget.Invalidate();
            budget.Refresh(player);
            Assert.Equal(MountFixture.Vendor, budget.Riding.MountVendor); // not yet

            budget.NoteVendorFailure(MountFixture.Vendor);
            budget.Refresh(player);
            Assert.Equal((MountFixture.SecondVendor, 98001u, 300u), (budget.Riding.MountVendor, budget.Riding.MountItem, budget.Riding.MountCost));

            for (int i = 0; i < PlayerbotBudget.VendorFailureLimit; i++) budget.NoteVendorFailure(MountFixture.SecondVendor);
            budget.Refresh(player);
            Assert.Equal(PlayerbotRidingStep.None, budget.Riding.Step); // no vendor left: the mount line is dropped until the exclusion ends
            Assert.DoesNotContain(budget.Lines.ToArray(), line => line.Kind is PlayerbotSpendKind.Riding or PlayerbotSpendKind.Mount);
            return true;
        }));
    }

    [Fact]
    public async Task OutOfCombatUpkeep_WhenMountedWithoutPullTarget_DoesNotDismountOrReportSummonFailed()
    {
        await RunAsync(new MountFixture(), async (host, session) =>
        {
            Player player = session.Player!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            var options = new PlayerbotOptions { Enabled = true };
            var combatSpells = new PlayerbotCombatSpells(session);
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                const uint summonWarhorse = 13819;
                const uint blessingOfMight = 19740;
                var warhorseSpell = new SpellInfo
                {
                    Id = summonWarhorse,
                    Name = "Summon Warhorse",
                    PowerType = (int)PowerType.Mana,
                    ManaCost = 10,
                    CastTime = new SpellCastTime(3000, 0, 3000),
                    Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                var battleStance = new SpellInfo
                {
                    Id = blessingOfMight,
                    Name = "Battle Stance",
                    RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModShapeshift, MiscValue = 17, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                };
                feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => s.Id is not summonWarhorse and not blessingOfMight), warhorseSpell, battleStance], [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, summonWarhorse));
                Assert.True(feature.Spellbook.LearnSpell(player, blessingOfMight));
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 500);
                player.SetUInt32(UpdateFields.UnitFieldPower1, 500);

                session.ManagedBudget = new ManagedActionBudget(5);

                Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                feature.System.Update(3000);
                return true;
            });
            await SpellBatchGoneAsync(host, session);
            await host.OnWorldAsync(() =>
            {
                Assert.True(MountService.IsMounted(player));

                session.ManagedBudget = new ManagedActionBudget(5);
                combatSpells.PullTarget = null;
                Assert.False(combatSpells.UpdateOutOfCombat(player, 200));
                Assert.True(MountService.IsMounted(player));

                Assert.False(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
                Assert.Equal(0, PlayerbotMount.For(session).Failures);
                Assert.False(capture.Any(PlayerbotAnomalies.Key("mount", "summon-failed")), capture.Describe());
                return true;
            });
        });
    }
}

/// <summary>A floor with a roof: <see cref="ILineOfSight.TryGetAreaInfo"/> answers an area that is not outdoors.</summary>
internal sealed class IndoorFloor : ILineOfSight
{
    public bool Enabled => true;
    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info) { info = default; return true; }
}

/// <summary>A human-race Mounts trainer, a trainer of another race and a mount vendor next to the start position, with known prices.</summary>
internal sealed class MountFixture : ICreatureDataStore, INpcContentStore, ISpellContentStore
{
    public const uint TrainerEntry = 971, OtherTrainerEntry = 972, Vendor = 973, SecondVendor = 974, MountCreature = 97199;
    public const uint Teaching = 971010, Learned = 971011, TeachingEpic = 971020, LearnedEpic = 971021, MountSpell = 97100;
    public static readonly ObjectGuid TrainerGuid = ObjectGuid.WithEntry(HighGuid.Unit, TrainerEntry, 97101);
    public static readonly ObjectGuid OtherTrainerGuid = ObjectGuid.WithEntry(HighGuid.Unit, OtherTrainerEntry, 97201);
    public static readonly ObjectGuid VendorGuid = ObjectGuid.WithEntry(HighGuid.Unit, Vendor, 97301);

    /// <summary>The first tier's required level (npc_trainer.reqlevel).</summary>
    public uint TierLevel { get; init; } = 1;

    /// <summary>The second tier's required level.</summary>
    public uint EpicLevel { get; init; } = 60;

    /// <summary>The first tier asks a skill the bot lacks: Red whatever its level.</summary>
    public bool TierSkillLacking { get; init; }

    /// <summary>Yards east of the start position the trainers and the vendor stand (130: out of sight, so a trip walks to them).</summary>
    public float SpawnOffset { get; init; }

    /// <summary>The map the trainers and the vendor stand on (the bot is on map 0).</summary>
    public uint SpawnMap { get; init; }

    /// <summary>The map the mount vendor stands on when that is not <see cref="SpawnMap"/>.</summary>
    public uint? VendorMap { get; init; }

    /// <summary>The map the riding trainers stand on when that is not <see cref="SpawnMap"/>.</summary>
    public uint? TrainerMap { get; init; }

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
    {
        TrainerSpells =
        [
            new TrainerSpell { Entry = TrainerEntry, Spell = Teaching, SpellCost = 500, ReqLevel = TierLevel, ReqSkill = TierSkillLacking ? 164u : 0u, ReqSkillValue = TierSkillLacking ? 300u : 0u },
            new TrainerSpell { Entry = TrainerEntry, Spell = TeachingEpic, SpellCost = 5000, ReqLevel = EpicLevel },
            new TrainerSpell { Entry = OtherTrainerEntry, Spell = Teaching, SpellCost = 500, ReqLevel = TierLevel },
        ],
        VendorItems =
        [
            new VendorItem { Entry = Vendor, Item = 98001, Slot = 0 }, new VendorItem { Entry = Vendor, Item = 98002, Slot = 1 },
            new VendorItem { Entry = Vendor, Item = 98003, Slot = 2 }, new VendorItem { Entry = Vendor, Item = 98004, Slot = 3 }, new VendorItem { Entry = Vendor, Item = 98006, Slot = 5 }, new VendorItem { Entry = SecondVendor, Item = 98001, Slot = 0 },
        ],
    });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
    [
        new CreatureTemplate { Entry = TrainerEntry, Name = "Riding trainer", Faction = 900011, NpcFlags = (uint)NpcFlags.Trainer, DisplayIds = [49], TrainerType = 1, TrainerRace = 1 },
        new CreatureTemplate { Entry = OtherTrainerEntry, Name = "Other riding trainer", Faction = 900011, NpcFlags = (uint)NpcFlags.Trainer, DisplayIds = [49], TrainerType = 1, TrainerRace = 2 },
        new CreatureTemplate { Entry = Vendor, Name = "Mount vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] },
        new CreatureTemplate { Entry = SecondVendor, Name = "Second mount vendor", Faction = 900011, NpcFlags = (uint)NpcFlags.Vendor, DisplayIds = [49] },
        new CreatureTemplate { Entry = MountCreature, Name = "Mount display", Faction = 900011, DisplayIds = [777] },
    ],
    [
        new CreatureSpawn { Guid = 97101, Entry = TrainerEntry, MapId = TrainerMap ?? SpawnMap, X = -8948.95f + SpawnOffset, Y = -132.49f, Z = 83.53f },
        new CreatureSpawn { Guid = 97201, Entry = OtherTrainerEntry, MapId = TrainerMap ?? SpawnMap, X = -8949.25f + SpawnOffset, Y = -132.49f, Z = 83.53f },
        new CreatureSpawn { Guid = 97401, Entry = SecondVendor, MapId = VendorMap ?? SpawnMap, X = -8949.65f + SpawnOffset, Y = -132.49f, Z = 83.53f },
        new CreatureSpawn { Guid = 97301, Entry = Vendor, MapId = VendorMap ?? SpawnMap, X = -8949.45f + SpawnOffset, Y = -132.49f, Z = 83.53f },
    ], [], [], []));

    Task<SpellContent> ISpellContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SpellContent(
    [
        new SpellTemplateRow { Id = Teaching, SpellName = "Apprentice", SpellLevel = 1, Effect1 = (uint)SpellEffectName.LearnSpell, EffectTriggerSpell1 = Learned, EffectImplicitTargetA1 = (uint)SpellImplicitTarget.UnitCaster },
        new SpellTemplateRow { Id = Learned, SpellName = "Apprentice riding", SpellLevel = 1 },
        new SpellTemplateRow { Id = TeachingEpic, SpellName = "Journeyman", SpellLevel = 1, Effect1 = (uint)SpellEffectName.LearnSpell, EffectTriggerSpell1 = LearnedEpic, EffectImplicitTargetA1 = (uint)SpellImplicitTarget.UnitCaster },
        new SpellTemplateRow { Id = LearnedEpic, SpellName = "Journeyman riding", SpellLevel = 1 },
    ], [], [], [], [], [], []));

    Task ISpellContentStore.ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class MountServices : IWorldTestServices
{
    public static readonly AsyncLocal<MountFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}
