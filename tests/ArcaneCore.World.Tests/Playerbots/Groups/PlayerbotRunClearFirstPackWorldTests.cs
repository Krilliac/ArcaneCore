using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Five Horde bots entering one bound instance and clearing the first pack; a mid-pull wipe recovering. Split from the former <c>PlayerbotRunClearWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunClearWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunClearFirstPackWorldTests : PlayerbotRunClearWorldTestBase
{
    [Fact]
    public async Task FiveHordeBots_EnterOneBoundInstance_ClearTheFirstPackBeforeAnyBoss()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;

        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Creature[] initialPack = Pack(world, bots[0].Id);
        Assert.Equal(3, initialPack.Length);
        Assert.Equal(2, initialPack.Count(c => c.Entry == GroupRfcContent.Trogg));
        Assert.Single(initialPack, c => c.Entry == GroupRfcContent.Shaman);
        Assert.All(initialPack, c =>
        {
            Assert.InRange(c.Level, (byte)13, (byte)15);
            Assert.InRange(Vector3.Distance(new Vector3(c.X, c.Y, c.Z), new Vector3(0.797643f, -8.23429f, -15.5288f)), 15f, 25f);
        });
        Assert.All(initialPack, a => Assert.All(initialPack, b => Assert.True(Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)) <= 10f)));
        await world.OnWorldAsync(() =>
        {
            uint instance = world.Player(bots[0].Id).Map!.InstanceId;
            Assert.NotEqual(0u, instance);
            Assert.All(bots, b => Assert.Equal(instance, world.Player(b.Id).Map!.InstanceId));
            var server = world.GroupManager.GetGroup(world.Player(bots[0].Id).Guid)!;
            Assert.Equal(instance, world.World.Services.GetRequiredService<InstanceFeature>().Instances.GetGroupBind(server, GroupRfcContent.Map)?.Save.InstanceId);
            Assert.Equal(5, server.MemberCount);
            return true;
        });

        bool sawPack = false, bossEarly = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            sawPack |= run.PullsLeft > 0;
            bossEarly |= trash.Any(c => c.IsAlive) && Bosses(world, bots[0].Id).Any(c => c.Combat.IsInCombat);
            return sawPack && trash.Length == 3 && trash.All(c => !c.IsAlive) && run.PullsLeft == 0;
        }), await world.TraceAsync());
        Assert.False(bossEarly, await world.TraceAsync());
        Assert.Equal(0, run.Killed);
        Assert.Equal(11517u, run.CurrentBoss!.Entry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MidPullWipe_RecoversAndReenters_WithoutRespawningKilledTrash(bool resurrection)
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());

        // Wait for one real trash kill with the rest of its pack still alive, then force a majority wipe.
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        ulong[] killed = await world.OnWorldAsync(() =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            ulong[] dead = [.. trash.Where(c => !c.IsAlive).Select(c => c.Guid.Value)];
            Creature attacker = trash.First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }
            return dead;
        });
        Assert.NotEmpty(killed);
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
        bool sawGhostEntrance = false, sawGhostOutside = false, sawRevivedInside = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            sawGhostEntrance |= bots.Take(3).Any(b => world.Coordinator.FindAI(b.Id)?.Recovery.LastStep == PlayerbotRecoveryStep.WalkToEntrance);
            sawGhostOutside |= bots.Take(3).Any(b => world.Player(b.Id).MapId == 1 && (world.Player(b.Id).Flags & PlayerFlags.Ghost) != 0);
            sawRevivedInside |= bots.Take(3).Any(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive
                && MathF.Abs(world.Player(b.Id).X - 0.797643f) < 8f && MathF.Abs(world.Player(b.Id).Y + 8.23429f) < 8f);
            return run.Step == PlayerbotRunStep.Retry;
        }), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Enter), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Clear
            && bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive)), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
        Assert.Equal(11517u, run.CurrentBoss!.Entry);
        Assert.All(killed, guid => Assert.Contains(Pack(world, bots[0].Id), c => c.Guid.Value == guid && !c.IsAlive));
        if (!resurrection)
        {
            Assert.True(sawGhostEntrance, await world.TraceAsync());
            Assert.True(sawGhostOutside, await world.TraceAsync());
            Assert.True(sawRevivedInside, await world.TraceAsync());
            Assert.All(bots, b => Assert.Null(world.Coordinator.FindAI(b.Id)!.LastResurrection));
        }
    }
}
