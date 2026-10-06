using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotInspectionTests
{
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
    public async Task InspectionRefusesOffWorldThreadCapture()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try { Assert.Throws<InvalidOperationException>(() => PlayerbotInspector.Capture(session, brain)); }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static Creature Create(uint entry, uint flags, uint low)
        => new(low, new CreatureTemplate { Entry = entry, Name = "inspection fixture", CreatureType = 7,
            NpcFlags = flags, MinLevelHealth = 20, MaxLevelHealth = 20 }, null, CreatureContent.Empty, new Random(1));
}
