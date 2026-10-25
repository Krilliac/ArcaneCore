using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The combat safety net for creatures a bot can never kill: a target held at its death-prevention threshold
/// (<c>InvincibilityHpThreshold</c>, the Blasted Lands servants of quest 2681 at 1 health) is set aside after
/// <c>Contention.InvincibleTargetAbandonSeconds</c>, reported once per entry, and not chosen again while it is set aside
/// (<see cref="PlayerbotInvincibleWatch"/>). The quest-side filter is in <see cref="PlayerbotQuestFilterAndFarReturnTests"/>.
/// </summary>
public sealed class PlayerbotInvincibleTargetTests
{
    private const uint AbandonSeconds = 8;
    private static readonly string AnomalyKey = PlayerbotAnomalies.Key("combat", PlayerbotInvincibleWatch.AnomalyCode);

    private static async Task<(RiskTestWorld World, Creature Mob, AnomalyCapture Capture)> StartFightAsync(uint entry)
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync(options =>
        {
            options.Risk.Enabled = false;
            options.Contention.InvincibleTargetAbandonSeconds = (int)AbandonSeconds;
        });
        Creature mob = await world.OnWorldAsync(() =>
        {
            world.Session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            return world.Spawn(RiskTestWorld.Template(entry, health: 500), 2f, 0f);
        });
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() => { world.Engage(mob); return true; });
        AnomalyCapture capture = await world.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(world.Player));
        return (world, mob, capture);
    }

    [Fact]
    public async Task ATargetHeldAtItsThreshold_IsAbandonedAfterTheLimit_ReportedOnce_AndNotPickedAgainWhileSetAside()
    {
        (RiskTestWorld world, Creature mob, AnomalyCapture capture) = await StartFightAsync(960201);
        await using RiskTestWorld _ = world;
        using AnomalyCapture __ = capture;
        // The EventAI death prevention: damage never takes the creature below 1 health.
        void Hold()
        {
            mob.InvincibilityHpThreshold = 1;
            if (mob.IsAlive) mob.Health = 1;
        }

        await world.OnWorldAsync(() => { Hold(); return true; });
        Assert.True(await world.ThinkUntilAsync(5_000, () => ReferenceEquals(world.Brain.InspectionTarget, mob), Hold),
            "the bot never fought the creature: the test proves nothing");

        // Abandoned within the limit plus the think granularity, well before the 20 s a fight that goes nowhere needs.
        uint heldFrom = await world.OnWorldAsync(() => world.Host.World.NowMs);
        bool released = await world.ThinkUntilAsync((AbandonSeconds + 4) * 1000,
            () => !ReferenceEquals(world.Brain.InspectionTarget, mob), Hold);
        uint heldMs = await world.OnWorldAsync(() => world.Host.World.NowMs) - heldFrom;
        Assert.True(await world.OnWorldAsync(() => mob.IsAlive), "the creature died: the test proves nothing");
        Assert.True(released, $"the bot held the invincible target for {heldMs} ms");
        Assert.True(heldMs >= (AbandonSeconds - 2) * 1000, $"the bot gave up after only {heldMs} ms");
        Assert.True(heldMs < PlayerbotBrain.FightNoProgressMs, $"{heldMs} ms is the no-progress watch, not this one");
        Assert.Equal(1, capture.Count(AnomalyKey));

        bool takenBack = await world.ThinkUntilAsync(60_000, () => ReferenceEquals(world.Brain.InspectionTarget, mob), Hold);
        Assert.False(takenBack, "the target set aside was taken back within a minute");
        Assert.Equal(1, capture.Count(AnomalyKey));
    }

    [Fact]
    public async Task ATargetWithAThresholdButHealthAboveIt_IsNotAbandoned()
    {
        (RiskTestWorld world, Creature mob, AnomalyCapture capture) = await StartFightAsync(960202);
        await using RiskTestWorld _ = world;
        using AnomalyCapture __ = capture;
        // A threshold is set (Spirit of Redemption style) but the creature is at full health: nothing holds it.
        void Armed()
        {
            mob.InvincibilityHpThreshold = 1;
            if (mob.IsAlive) mob.Health = mob.MaxHealth;
        }

        await world.OnWorldAsync(() => { Armed(); return true; });
        Assert.True(await world.ThinkUntilAsync(5_000, () => ReferenceEquals(world.Brain.InspectionTarget, mob), Armed),
            "the bot never fought the creature: the test proves nothing");

        // Past the limit with margin, still inside the 20 s of the no-progress watch (full health every think is no progress).
        bool released = await world.ThinkUntilAsync((AbandonSeconds + 4) * 1000,
            () => !ReferenceEquals(world.Brain.InspectionTarget, mob), Armed);
        Assert.False(released, "the bot dropped a target that is not held at its threshold");
        Assert.Equal(0, capture.Count(AnomalyKey));
    }

    [Fact]
    public async Task TheWatch_AbandonsOnlyAHeldTarget_NotALowHealthOneWithoutAThreshold_AndStartsOverWhenTheHoldBreaks()
    {
        (RiskTestWorld world, Creature mob, AnomalyCapture capture) = await StartFightAsync(960203);
        await using RiskTestWorld _ = world;
        using AnomalyCapture __ = capture;
        const uint limit = AbandonSeconds * 1000;
        await world.OnWorldAsync(() =>
        {
            var watch = new PlayerbotInvincibleWatch();
            uint now = 1_000;

            // A normal creature at 1 health, no threshold: never abandoned, however long.
            mob.InvincibilityHpThreshold = 0;
            mob.Health = 1;
            for (int i = 0; i < 300; i++, now += 100) Assert.False(watch.Observe(world.Player, mob, now, limit));

            // Held: abandoned once the limit is reached, not before.
            mob.InvincibilityHpThreshold = 1;
            Assert.False(watch.Observe(world.Player, mob, now, limit));
            uint since = now;
            for (; now - since < limit; now += 100) Assert.False(watch.Observe(world.Player, mob, now, limit));
            Assert.True(watch.Observe(world.Player, mob, now, limit));

            // A hold broken by health above the threshold starts the clock afresh.
            now += 100;
            Assert.False(watch.Observe(world.Player, mob, now, limit));
            for (int i = 0; i < 50; i++, now += 100) Assert.False(watch.Observe(world.Player, mob, now, limit));
            mob.Health = 100;
            now += 100;
            Assert.False(watch.Observe(world.Player, mob, now, limit));
            mob.Health = 1;
            since = now += 100;
            Assert.False(watch.Observe(world.Player, mob, now, limit));
            for (; now - since < limit - 500; now += 100) Assert.False(watch.Observe(world.Player, mob, now, limit));
            return true;
        });
        Assert.Equal(1, capture.Count(AnomalyKey));
    }

    [Fact]
    public async Task TheReport_IsOncePerEntry_AndRepeatsOnlyAfterTheBackoff()
    {
        (RiskTestWorld world, Creature mob, AnomalyCapture capture) = await StartFightAsync(960204);
        await using RiskTestWorld _ = world;
        using AnomalyCapture __ = capture;
        const uint limit = 1_000;
        await world.OnWorldAsync(() =>
        {
            var watch = new PlayerbotInvincibleWatch();
            mob.InvincibilityHpThreshold = 1;
            mob.Health = 1;
            uint now = 1_000;
            void Abandon()
            {
                watch.Observe(world.Player, mob, now, limit);
                now += limit;
                Assert.True(watch.Observe(world.Player, mob, now, limit));
                now += 100;
            }

            Abandon();
            Abandon();
            now += PlayerbotInvincibleWatch.ReportBackoffMs;
            Abandon();
            return true;
        });
        Assert.Equal(2, capture.Count(AnomalyKey));
    }
}
