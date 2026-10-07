using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotDeathTransitionTests
{
    private static readonly ObjectGuid OldTargetGuid = ObjectGuid.WithEntry(HighGuid.Unit, 992820, 1);
    [Fact]
    public Task FatalDamageReleaseAndReclaim_RetiresLootAndKeepsCombatAvailable()
        => RunDeathTransition(false);

    [Fact]
    public Task CorpseLootPrecedesCarriedEquipmentUpgradeOutsideCombat()
        => RunDeathTransition(true);

    private static async Task RunDeathTransition(bool carriedUpgrade)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 992899, Name = "carried boots",
            Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 70, Stackable = 1 });
        using IDisposable? itemScope = carriedUpgrade ? items.Use() : null;
        await using WorldTestHost host = StartWithLoot();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var clock = new TestDeathClock(1_000);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        Creature oldTarget = null!;
        try
        {
            await host.OnWorldAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(OldTargetGuid), "old target visibility");

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                oldTarget = Assert.IsType<Creature>(player.Map!.FindObject(OldTargetGuid));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Same(oldTarget, brain.InspectionTarget);
                Assert.Equal(PlayerbotGoalKind.Combat, brain.Goal);
                // Spell-first combat need not establish a melee victim. Use the ordinary
                // attack handler to establish the melee state this death scenario needs.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(oldTarget.Guid.Value)));
                Assert.Same(oldTarget, player.Combat.Victim);

                player.Map!.Combat.DealDamage(player, oldTarget, oldTarget.Health);
                Assert.False(oldTarget.IsAlive);
                if (carriedUpgrade)
                {
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(992899, 1, out _));
                    player.Map.Combat.CombatStop(player);
                    Assert.False(player.Combat.IsInCombat);
                }
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Loot, brain.Goal);
                Assert.Same(oldTarget, brain.InspectionTarget);
                if (carriedUpgrade)
                    Assert.NotEqual(992899u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)?.Entry);
                LootService loot = Assert.IsType<LootService>(host.WorldServices.GetRequiredService<GameObjectLootFeature>()
                    .FindSystem(player.Map!)?.Loot);
                Assert.NotNull(loot.OpenLootOf(player));
                uint moneyBeforeDeath = player.Money;
                session.ManagedBudget = new ManagedActionBudget(0);
                brain.Update(500); // Read the actual loot response and queue ordinary collection actions.
                Assert.Equal(moneyBeforeDeath, player.Money);

                Creature fatalAttacker = AddHostile(host, player, 992821, "fatal attacker");
                player.Map!.Combat.DealDamage(fatalAttacker, player, player.Health);
                Assert.False(player.IsAlive);
                brain.Update(500);

                Assert.Equal(PlayerbotGoalKind.Recover, brain.Goal);
                Assert.Null(brain.InspectionTarget);
                Assert.Equal(0u, brain.TargetEntry);

                Assert.True(player.Map!.Combat.RepopPlayer(player));
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                MovementControl.Request(player, MovementChangeType.WaterWalk, true);
                int pending = player.Locomotion.Pending.Changes.Count;
                session.ManagedBudget = new ManagedActionBudget(0);
                brain.Update(500);
                Assert.True(player.Locomotion.Pending.HasPending);
                Assert.Equal(pending, player.Locomotion.Pending.Changes.Count);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(0, session.ManagedBudget.Remaining);
                for (int i = 0; i < 16 && player.Locomotion.Pending.HasPending; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }
                Assert.False(player.Locomotion.Pending.HasPending);
                Assert.True(player.Movement.HasFlag(MovementFlags.WaterWalking));

                player.Map!.RemoveObject(oldTarget);
                player.Map!.RemoveObject(fatalAttacker);
                clock.Seconds += 31;
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(500);
                Assert.True(player.IsAlive);
                Assert.Null(player.Combat.Corpse);
                Assert.NotEqual(PlayerbotGoalKind.Loot, brain.Goal);
                Assert.Null(brain.InspectionTarget);
                for (int i = 0; i < 8; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500);
                }
                Assert.Equal(moneyBeforeDeath, player.Money);
                return true;
            });

            Creature freshAttacker = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                freshAttacker = AddHostile(host, player, 992822, "fresh defensive attacker");
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(freshAttacker.Guid),
                "fresh attacker visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(freshAttacker.Guid.Value)));
                player.Map!.Combat.DealDamage(freshAttacker, player, 1);
                Assert.Same(freshAttacker, player.Combat.Victim);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Same(freshAttacker, brain.InspectionTarget);
                Assert.Same(freshAttacker, player.Combat.Victim);
                Assert.True(player.Combat.IsInCombat);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static WorldTestHost StartWithLoot()
    {
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([], [], [], [], []),
            new LootContent([], [new CreatureLootInfo(992820, 0, 0, 1, 1)]));
        try { return WorldTestHost.Start(configureServices: services => services.AddSingleton<ICreatureDataStore>(new DeathCreatureStore())); }
        finally { GameObjectTestStore.Current.Value = null; }
    }

    private static Creature AddHostile(WorldTestHost host, Player player, uint low, string name)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = (uint)low, Name = name, CreatureType = 1,
            MinLevelHealth = 20, MaxLevelHealth = 20,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(player.X + 1, player.Y, player.Z, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    private sealed class DeathCreatureStore : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CreatureContent(
                [new CreatureTemplate { Entry = 992820, Name = "old target", CreatureType = 1,
                    MinLevelHealth = 20, MaxLevelHealth = 20, ExtraFlags = Creature.ExtraFlagNoAggro }],
                [new CreatureSpawn { Guid = 1, Entry = 992820, MapId = 0,
                    X = -8948.95f, Y = -132.493f, Z = 83.5312f }], [], [], []));
    }
}
