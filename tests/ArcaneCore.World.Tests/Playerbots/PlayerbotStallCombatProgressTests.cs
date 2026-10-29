using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The stall watch (<see cref="PlayerbotStallWatch"/>) and fighting in place. A level-60 bot gains no XP, so a fight without loot
/// changed nothing the fingerprint looked at. Time moves only on the manual world clock.
/// </summary>
public sealed class PlayerbotStallCombatProgressTests
{
    private const uint ThinkMs = 500;
    private const int Thinks = 260; // 130 s of world time, past the 120 s stall bound
    private const float CreatureOffset = 3f;
    private const uint TankHealth = 1_000_000;
    private const uint FriendlyFaction = 35; // friendly to all: the creature does not attack the bot back

    /// <summary>
    /// Live (2026-10-27): 1134 Combat stalls in one day, every bot level 60. A level-60 bot gets no XP, so a fight in place that
    /// hit and killed creatures without loot counted as no progress and the bot was reported stalled after 120 s and gave its goal up.
    /// Dealing damage and killing are progress. The bot is put back where it stood each think, so it never moves 10 yards.
    /// </summary>
    [Fact]
    public async Task AStationaryLevel60BotThatDealsDamageAndKills_IsNotReportedStalled()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 120 });
        try
        {
            (float X, float Y, float Z, float O, uint Xp, uint Money) start = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 60;
                return (player.X, player.Y, player.Z, player.Orientation, player.GetUInt32(UpdateFields.PlayerXp), player.Money);
            });
            uint next = 995_100;
            Creature target = await host.OnWorldAsync(() => Spawn(host, session, start.X, start.Y, start.Z, next++));

            for (int think = 1; think <= Thinks; think++)
            {
                await host.World.AdvanceClockAsync(ThinkMs);
                bool kill = think % 120 == 0; // every 60 s
                bool hit = !kill && think % 10 == 0; // every 5 s: 1 damage each, a creature has 20 health
                Creature current = target;
                target = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Relocate(start.X, start.Y, start.Z, start.O, host.World.NowMs);
                    Creature result = current;
                    if (hit) player.Map!.Combat.DealDamage(player, current, 1);
                    if (kill)
                    {
                        player.Map!.Combat.DealDamage(player, current, 1_000);
                        result = Spawn(host, session, start.X, start.Y, start.Z, next++);
                    }
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(ThinkMs);
                    return result;
                });
            }

            Assert.Equal(60u, await host.OnWorldAsync(() => (uint)session.Player!.Level));
            Assert.Equal(start.Money, await host.OnWorldAsync(() => session.Player!.Money));
            Assert.Equal(start.Xp, await host.OnWorldAsync(() => session.Player!.GetUInt32(UpdateFields.PlayerXp)));
            Assert.Null(await host.OnWorldAsync(() => brain.StallReport));
            Assert.Equal(0, await host.OnWorldAsync(() => brain.StallCount));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The control: the same level-60 bot with a creature beside it but no damage and no kills is genuinely idle, and is still reported
    /// stalled after 120 s (the live Combat stalls must not hide real ones).
    /// </summary>
    [Fact]
    public async Task AnIdleLevel60Bot_IsStillReportedStalled()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 120 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 60;
                Spawn(host, session, player.X, player.Y, player.Z, 995_200);
                return true;
            });

            for (int think = 0; think < Thinks; think++)
            {
                await host.World.AdvanceClockAsync(ThinkMs);
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(ThinkMs); return true; });
            }

            string? report = await host.OnWorldAsync(() => brain.StallReport);
            Assert.NotNull(report);
            Assert.StartsWith("stalled", report);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// Guards the masking case: a hit counts as combat progress only when it takes the victim's health to a new low. Here the creature
    /// is restored to full health before every 1-damage hit and is never killed, so each hit lands on a target that healed back up. Raw
    /// damage would count as progress and hide a real stall; the bot must still be reported stalled after 120 s.
    /// </summary>
    [Fact]
    public async Task AStationaryLevel60BotHittingATargetThatHealsBack_IsStillReportedStalled()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 120 });
        try
        {
            (float X, float Y, float Z, float O) start = await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 60;
                return (player.X, player.Y, player.Z, player.Orientation);
            });
            Creature target = await host.OnWorldAsync(() =>
            {
                Creature spawned = Spawn(host, session, start.X, start.Y, start.Z, 995_300);
                // Not selectable: the brain does not take it on with its own melee, whose varying hits (crits) against the heal would
                // set new lows now and then; only the test's fixed 1-damage hits land.
                spawned.UnitFlags |= UnitFlags.NotSelectable;
                return spawned;
            });

            for (int think = 1; think <= Thinks; think++)
            {
                await host.World.AdvanceClockAsync(ThinkMs);
                bool hit = think % 10 == 0; // every 5 s
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Relocate(start.X, start.Y, start.Z, start.O, host.World.NowMs);
                    // Healed back to full every think, so nothing the bot does can take it to a new low after the first hit; the hit
                    // does not start combat, so the brain does not join in with its own melee.
                    target.MaxHealth = TankHealth;
                    target.Health = TankHealth;
                    if (hit) player.Map!.Combat.DealDamage(player, target, 1, startsCombat: false);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(ThinkMs);
                    return true;
                });
            }

            Assert.True(await host.OnWorldAsync(() => target.IsAlive));
            // The hits landed: the first one took the target to a new low (counted once); every later one only matched it.
            Assert.Equal(1UL, await host.OnWorldAsync(() => PlayerbotKillObserver.For(session.Player!.Map!.Combat).ProgressHitsBy(session.Player!)));
            string? report = await host.OnWorldAsync(() => brain.StallReport);
            Assert.NotNull(report);
            Assert.StartsWith("stalled", report);
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    private static Creature Spawn(WorldTestHost host, WorldSession session, float x, float y, float z, uint low)
        => DungeonBotHost.AddCreature(session.Player!.Map!, low, x + CreatureOffset, y, z, 0, host.World.NowMs, FriendlyFaction);

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
