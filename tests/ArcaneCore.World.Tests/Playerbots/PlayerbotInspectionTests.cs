using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotInspectionTests
{
    [Fact]
    public async Task InspectionReportsIncomingAttackersAndLiveEquipmentWithoutMutation()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 41001, Name = "inspection blade",
            Class = (uint)ItemClass.Weapon, InventoryType = (uint)InventoryType.WeaponMainHand,
            MaxDurability = 40, Damages = [new ItemDamage(2, 3, 0)] });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 41002, Name = "inspection boots",
            Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 7 });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            var attackers = new List<Creature>();
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(41001, 1, out Item? blade));
                _ = player.Inventory.AutoEquipItem(blade!.BagSlot, blade.Slot);
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(41002, 1, out Item? boots));
                _ = player.Inventory.AutoEquipItem(boots!.BagSlot, boots.Slot);
                for (uint index = 0; index < 5; index++)
                {
                    var attacker = new Creature(41003 + index, new CreatureTemplate { Entry = 41003 + index, Name = "incoming", CreatureType = 7,
                        MinLevelHealth = 20, MaxLevelHealth = 20 }, null, CreatureContent.Empty, new Random(1));
                    attacker.SetPosition(player.X + 2 + index, player.Y, player.Z, 0);
                    player.Map!.AddObject(attacker);
                    attackers.Add(attacker);
                }
                return true;
            });
            await host.WaitForWorldAsync(() => attackers.All(attacker => session.Player!.VisibleObjects.Contains(attacker.Guid)), "inspection attacker visibility");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                foreach (Creature attacker in attackers)
                {
                    Assert.True(player.Map!.Combat.Attack(attacker, player));
                    player.Map!.Combat.DealDamage(attacker, player, 1);
                }
                Assert.Null(player.Combat.Victim);
                Assert.Equal(5, player.Combat.Attackers.Count);
                uint health = player.Health;
                PlayerbotInspection report = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Null(report.Victim);
                Assert.Equal(5, report.AttackerCount);
                Assert.Equal(4, report.Attackers.Count);
                Assert.Equal(attackers.OrderBy(attacker => attacker.Guid.Value).Take(4).Select(attacker => attacker.Guid.Value),
                    report.Attackers.Select(attacker => attacker.Guid));
                Assert.Equal(41001u, report.Equipment.MainHand!.Entry);
                Assert.Equal(40u, report.Equipment.MainHand.MaxDurability);
                Assert.Equal(41002u, report.Equipment.Feet!.Entry);
                Assert.Equal(health, player.Health);
                Assert.Equal(5, player.Combat.Attackers.Count);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task InspectionSeparatesBrainTargetFromActualVictimWithoutChangingGameplay()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            Creature? target = null, service = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                target = Create(6, 0, 1);
                service = Create(1213, 0x4004, 2);
                target.SetPosition(player.X + 2, player.Y, player.Z, 0);
                service.SetPosition(player.X + 1, player.Y, player.Z, 0);
                player.Map!.AddObject(target); player.Map.AddObject(service);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(target!.Guid)
                && session.Player.VisibleObjects.Contains(service!.Guid), "inspection candidates");
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(service!.Guid.Value)));
                uint health = player.Health, money = player.Money;
                var victim = player.Combat.Victim;
                int budget = session.ManagedBudget.Remaining;
                PlayerbotInspection report = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Equal(6u, report.Target!.Entry);
                Assert.Equal(0u, report.Target.NpcFlags);
                Assert.Equal(1213u, report.Victim!.Entry);
                Assert.Equal(0x4004u, report.Victim.NpcFlags);
                Assert.Equal(service.Guid.Value, report.Victim.Guid);
                Assert.Equal(health, player.Health);
                Assert.Equal(money, player.Money);
                Assert.Same(victim, player.Combat.Victim);
                Assert.Equal(budget, session.ManagedBudget.Remaining);
                Assert.NotEmpty(report.KnownSpells);
                string json = JsonSerializer.Serialize(report);
                Assert.DoesNotContain("SessionKey", json);
                Assert.DoesNotContain("Verifier", json);
                Assert.DoesNotContain("AccountName", json);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task CombatDecisionRefreshesReportedTargetAfterAnotherGoal()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            Creature? target = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                target = Create(6, 0, 3);
                target.SetPosition(player.X + 2, player.Y, player.Z, 0);
                player.Map!.AddObject(target);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(target!.Guid), "combat candidate");
            await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(8);
                brain.Update(500);
                Assert.Same(target, brain.InspectionTarget);
                // Another goal can replace the public report while the combat target is retained.
                typeof(PlayerbotBrain).GetProperty("TargetEntry", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.SetValue(brain, 78u);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(target!.Guid.Value)));
                brain.Update(500);
                PlayerbotInspection report = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Equal(6u, report.ReportedTarget);
                Assert.Equal(6u, report.Target!.Entry);
                Assert.Equal(6u, report.Victim!.Entry);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task InspectionRefusesOffWorldThreadCapture()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try { Assert.Throws<InvalidOperationException>(() => PlayerbotInspector.Capture(session, brain)); }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task InspectionReportsReleasedCorpseWithoutChangingDeathStateOrPosition()
    {
        var clock = new InspectionClock(1_000);
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                session.ManagedBudget = new ManagedActionBudget(3);
                return true;
            });
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                float x = player.X, y = player.Y, z = player.Z;
                DeathState death = player.Combat.DeathState;
                uint health = player.Health;
                PlayerFlags flags = player.Flags;
                int budget = session.ManagedBudget!.Remaining;
                PlayerbotInspection report = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Equal(DeathState.Dead, report.DeathState);
                Assert.True(report.Ghost);
                Assert.NotNull(report.Corpse);
                Assert.Equal(player.MapId, report.Corpse!.MapId);
                Assert.Equal(player.Combat.Corpse!.Guid.Value, report.Corpse.Guid);
                Assert.Equal(0f, report.Corpse.Distance!.Value);
                // At the exact death second, the existing expiry-window rule is on
                // its 60-second boundary; inspection follows that ordinary rule.
                Assert.Equal(60L, report.Corpse.ReclaimDelayRemainingSeconds);
                Assert.Equal(x, report.PlayerX);
                Assert.Equal(y, report.PlayerY);
                Assert.Equal(z, report.PlayerZ);
                Assert.Equal(death, player.Combat.DeathState);
                Assert.Equal(x, player.X);
                Assert.Equal(y, player.Y);
                Assert.Equal(z, player.Z);
                Assert.Equal(health, player.Health);
                Assert.Equal(flags, player.Flags);
                Assert.Equal(budget, session.ManagedBudget.Remaining);
                clock.Seconds += 31;
                PlayerbotInspection expired = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Equal(0L, expired.Corpse!.ReclaimDelayRemainingSeconds);
                Assert.False(player.IsAlive); // An elapsed delay is not an inspection-triggered reclaim.
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task InspectionReportsLivingPlayerWithoutCorpse()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                PlayerbotInspection report = Assert.IsType<PlayerbotInspection>(PlayerbotInspector.Capture(session, brain));
                Assert.Equal(DeathState.Alive, report.DeathState);
                Assert.False(report.Ghost);
                Assert.Null(report.Corpse);
                return true;
            });
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static Creature Create(uint entry, uint flags, uint low)
        => new(low, new CreatureTemplate { Entry = entry, Name = "inspection fixture", CreatureType = 7,
            NpcFlags = flags, MinLevelHealth = 20, MaxLevelHealth = 20 }, null, CreatureContent.Empty, new Random(1));

    private sealed class InspectionClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
