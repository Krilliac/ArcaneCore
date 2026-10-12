using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Whether a retreat can succeed, on the real world (<see cref="RiskTestWorld"/>, manual clock). w29 live: 66 of 85 death loops ended
/// with the bot retreating, 34 of them from creatures at most two levels above it, and 111 of 121 attackers ran faster than an
/// unbuffed bot (speed_run 1.14 or 1.43): the retreat turned a lost fight into a death with no blow dealt on the way out.
/// </summary>
public sealed class PlayerbotRetreatFeasibilityWorldTests
{
    private const uint FastEntry = 992001;

    /// <summary>A hurt bot (30%) against one engaged creature of the given run rate, hitting hard enough to be losing to.</summary>
    private static async Task<(RiskTestWorld World, Creature Mob)> HurtBotVsAsync(float runSpeed, uint entry, Action<RiskTestWorld>? prepare = null)
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(entry, health: 400, minDamage: 5, maxDamage: 8, runSpeed: runSpeed), 2, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            prepare?.Invoke(world);
            world.Brain.Risk.RetreatStats = new PlayerbotRetreatStats();
            world.Engage(mob);
            world.Player.Health = world.Player.MaxHealth * 30 / 100;
            return true;
        });
        return (world, mob);
    }

    /// <summary>
    /// (a) The bot is losing at 30% to one creature that outruns it (speed_run 1.14286: 8.0 yards a second against 7.0), with no
    /// escape ready. Running 55 yards or more would take it about 8 seconds under the creature's blows, which it does not have: it
    /// keeps fighting (and its emergency potion is the rotation's, below 25%). Before, it ran and died on the way.
    /// </summary>
    [Fact]
    public async Task ALosingBot_DoesNotRunFromAFasterCreature_WithNoEscape()
    {
        (RiskTestWorld world, Creature mob) = await HurtBotVsAsync(1.14286f, FastEntry);
        await using (world)
        {
            bool judged = await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Active || world.Brain.Risk.Tracker.LastVerdict is not null || !world.Player.IsAlive);
            Assert.True(judged, "the bot never judged the fight");
            Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active),
                "the bot ran from a faster creature with no escape: " + await world.OnWorldAsync(() => $"{world.Brain.Risk.Retreat.Reason} {world.Brain.Risk.Tracker.LastVerdict}"));
            Assert.Equal("cornered-no-escape", await world.OnWorldAsync(() => world.Brain.Risk.Tracker.LastVerdict?.Reason));
            Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Tracker.LastVerdict!.Value.Retreat));
            Assert.True(mob.IsAlive);
            // The refusal is counted once for the fight, however many times the bot judges it.
            await world.ThinkUntilAsync(1_000, () => false);
            Assert.Equal((1L, 0L), await world.OnWorldAsync(() => (world.Brain.Risk.RetreatStats.Refused, world.Brain.Risk.RetreatStats.Ended)));
        }
    }

    /// <summary>(c) The same fight against a creature slower than the bot (4.2 yards a second): the retreat gets away, as before.</summary>
    [Fact]
    public async Task ALosingBot_StillRunsFromASlowerCreature()
    {
        (RiskTestWorld world, _) = await HurtBotVsAsync(0.6f, FastEntry + 1);
        await using (world)
        {
            Assert.True(await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Active || !world.Player.IsAlive),
                "the bot did not retreat from a slower creature: " + await world.OnWorldAsync(() => $"{world.Brain.Risk.Tracker.LastVerdict}"));
            Assert.True(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active));
        }
    }

    /// <summary>(b) A mage with Frost Nova ready roots a creature that outruns it (8 seconds, the retreat's length): it retreats.</summary>
    [Fact]
    public async Task ALosingMage_WithFrostNovaReady_RunsFromAFasterCreature()
    {
        const uint novaId = 992_400;
        (RiskTestWorld world, _) = await HurtBotVsAsync(1.14286f, FastEntry + 2, w =>
        {
            w.Player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
            SpellFeature feature = w.Session.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = novaId, Name = PlayerbotEscapes.FrostNova, Duration = new SpellDuration(8_000, 0, 8_000),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            Assert.True(feature.Spellbook.LearnSpell(w.Player, novaId));
        });
        await using (world)
        {
            Assert.True(await world.ThinkUntilAsync(5_000, () => world.Brain.Risk.Retreat.Active || !world.Player.IsAlive),
                "the mage did not retreat with Frost Nova ready: " + await world.OnWorldAsync(() => $"{world.Brain.Risk.Tracker.LastVerdict}"));
            Assert.True(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active));
        }
    }

    /// <summary>
    /// (d) A retreat that ends with the bot dead still leaves the place where the fight turned in its danger memory and hazards
    /// (before, only a retreat that ended alive did, and the death recorded only the spot of the death).
    /// </summary>
    [Fact]
    public async Task ARetreatThatEndsInDeath_StillMarksThePullSite()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(FastEntry + 3, health: 400, minDamage: 5, maxDamage: 8), 2, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() => world.Brain.Risk.RetreatStats = new PlayerbotRetreatStats());
        Vector3 pullSite = await world.OnWorldAsync(() =>
        {
            world.Engage(mob);
            Vector3 site = new(world.Player.X, world.Player.Y, world.Player.Z);
            world.Brain.Risk.StartRetreat(world.Player, [mob], "test");
            // The bot ran 60 yards before the creature caught it: the death is more than a remembered spot (30 yards) from the pull.
            world.Player.Relocate(site.X, site.Y - 60f, site.Z, world.Player.Orientation, world.Host.World.NowMs);
            world.Player.Map!.Combat.DealDamage(mob, world.Player, world.Player.Health, direct: false);
            Assert.False(world.Player.IsAlive);
            return site;
        });

        await world.ThinkAsync();

        await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            uint now = world.Host.World.NowMs;
            Assert.Equal("died", risk.Retreat.Outcome);
            Assert.Contains(risk.Hazards.Active(world.Player.MapId, now), h => h.Kind == "retreat" && Vector3.Distance(h.At, pullSite) < 1f);
            Assert.True(risk.Memory.Hits(world.Player.MapId, pullSite, 0, now) > 0, "the pull site is not in the danger memory");
            Assert.Equal((1L, 1L), (risk.RetreatStats.Died, risk.RetreatStats.Ended));
            return true;
        });
    }
}
