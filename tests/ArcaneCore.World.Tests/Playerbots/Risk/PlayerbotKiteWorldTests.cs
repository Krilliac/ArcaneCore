using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A hurt ranged bot kites on the real world (<see cref="RiskTestWorld"/>, manual clock): a mage at 45% health with a creature on it
/// slows it with an instant Frostbolt, walks back out to casting range while the snare lasts, and goes on casting from there. The
/// creature is the server's own (aggro, chase, leash); the bolt is a synthetic instant spell whose second effect is a 50% speed snare
/// for 8 seconds (the real rows are pinned by the DBC fact in <see cref="PlayerbotEscapeSpellDataTests"/>).
/// </summary>
public sealed class PlayerbotKiteWorldTests
{
    private const uint FrostboltId = 992_500;
    private const uint FireballId = 992_501;
    private const uint MobEntry = 992_600;
    private const uint IdleEntry = 992_601;

    /// <summary>An instant bolt with a 30-yard range; with <paramref name="snare"/> a 50% movement-speed snare for 8 seconds on the target.</summary>
    private static SpellInfo Bolt(uint id, string name, bool snare) => new()
    {
        Id = id, Name = name, Rank = "Rank 1", CastTime = new SpellCastTime(0, 0, 0), RangeIndex = 5, Range = new SpellRange(0f, 30f),
        StartRecoveryCategory = 133, StartRecoveryTime = 1500, DamageClass = SpellDamageClass.Magic,
        Duration = snare ? new SpellDuration(8_000, 0, 8_000) : default,
        Effects =
        [
            new SpellEffectInfo { Effect = SpellEffectName.SchoolDamage, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = 0, BaseDice = 1, DieSides = 1 },
            snare
                ? new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDecreaseSpeed, TargetA = SpellImplicitTarget.UnitEnemy, BasePoints = -51, BaseDice = 1, DieSides = 1 }
                : new SpellEffectInfo(),
            new SpellEffectInfo(),
        ],
    };

    /// <summary>A mage at 45% health with one creature of its own speed on it (400 health, 1 to 2 damage), two yards away.</summary>
    private static async Task<(RiskTestWorld World, Creature Mob)> HurtMageVsAsync(bool snare, Action<PlayerbotOptions>? configure = null,
        bool aggroOnSight = false, Action<RiskTestWorld>? prepare = null)
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync(configure, aggroOnSight: aggroOnSight);
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(MobEntry, health: 400, minDamage: 1, maxDamage: 2, runSpeed: 1.0f), 2, 0));
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            prepare?.Invoke(world);
            Player player = world.Player;
            player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
            SpellFeature feature = world.Session.Services.GetRequiredService<SpellFeature>();
            SpellInfo bolt = snare ? Bolt(FrostboltId, "Frostbolt", snare: true) : Bolt(FireballId, "Fireball", snare: false);
            feature.System.Store = new SpellStore([.. feature.System.Store.All, bolt], [], []);
            Assert.True(feature.Spellbook.LearnSpell(player, bolt.Id));
            world.Engage(mob);
            player.Health = player.MaxHealth * 45 / 100;
            return true;
        });
        return (world, mob);
    }

    private static float Gap(RiskTestWorld world, Creature mob)
        => Vector3.Distance(new Vector3(world.Player.X, world.Player.Y, world.Player.Z), new Vector3(mob.X, mob.Y, mob.Z));

    private static string Describe(RiskTestWorld world, Creature mob)
        => $"gap {Gap(world, mob):F1} mob speed {mob.RunSpeed:F1} bot speed {world.Player.RunSpeed:F1} steps {world.Brain.Kite.Steps} over {world.Brain.Kite.Over} "
            + $"retreat {world.Brain.Risk.Retreat.Active} verdict {world.Brain.Risk.Tracker.LastVerdict}";

    /// <summary>K1: the snare lands, the mage is at least 8 yards from the creature within 6 seconds, and it casts again from there.</summary>
    [Fact]
    public async Task AHurtMage_SnaresThePursuer_BacksAway_AndCastsAgain()
    {
        (RiskTestWorld world, Creature mob) = await HurtMageVsAsync(snare: true);
        await using (world)
        {
            bool far = await world.ThinkUntilAsync(6_000, () => Gap(world, mob) >= 8f);
            Assert.True(far, "the mage never got 8 yards away: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.True(await world.OnWorldAsync(() => mob.RunSpeed < world.Player.RunSpeed * PlayerbotKiteModel.SlowSpeedRatio),
                "the creature was never snared: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.True(await world.OnWorldAsync(() => world.Brain.Kite.Steps >= 1));

            uint health = await world.OnWorldAsync(() => mob.Health);
            Assert.True(await world.ThinkUntilAsync(6_000, () => mob.Health < health), "the mage never cast again from where it stopped");

            Assert.False(await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Active), "retreating: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.Risk.Retreat.Count));
            Assert.False(await world.OnWorldAsync(() => mob.IsInEvadeMode), "the creature evaded");
        }
    }

    /// <summary>K2 (guard): no slow in the book, so no kite: the mob stays on the mage (the gap stays under 6 yards) while the mage fights.</summary>
    [Fact]
    public async Task AHurtMage_WithNoSlow_DoesNotKite()
    {
        (RiskTestWorld world, Creature mob) = await HurtMageVsAsync(snare: false);
        await using (world)
        {
            bool away = await world.ThinkUntilAsync(4_000, () => Gap(world, mob) >= 6f);
            Assert.False(away, "the mage walked away with nothing to slow the creature: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.Kite.Steps));
            Assert.True(await world.OnWorldAsync(() => mob.Health) < 399, "the mage never cast: the guard proves nothing");
        }
    }

    /// <summary>K3: an idle hostile creature lies on the away line: the mage side-steps round its aggro and that creature never enters combat.</summary>
    [Fact]
    public async Task AKitingMage_SideStepsAnIdleCreature_AndNeverWakesIt()
    {
        (RiskTestWorld world, Creature mob) = await HurtMageVsAsync(snare: true, aggroOnSight: true);
        await using (world)
        {
            Creature idle = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(IdleEntry, health: 60), -26, 0));
            await world.SeeAsync(idle);
            bool far = await world.ThinkUntilAsync(6_000, () => Gap(world, mob) >= 8f);
            Assert.True(far, "the mage never got 8 yards away: " + await world.OnWorldAsync(() => Describe(world, mob)));
            await world.ThinkUntilAsync(2_000, () => false);
            Assert.False(await world.OnWorldAsync(() => idle.Combat.IsInCombat), "the idle creature was woken");
            Assert.Null(await world.OnWorldAsync(() => idle.Combat.Victim));
            Assert.True(await world.OnWorldAsync(() => Math.Abs(world.Player.Y - mob.Y) > 3f), "the mage went straight back, not round the sleeper");
        }
    }

    /// <summary>K4: a pit lies behind the mage: no step ends on, or walks into, the floor ten yards down.</summary>
    [Fact]
    public async Task AKitingMage_NeverStepsIntoAPitBehindIt()
    {
        PitGround? ground = null;
        float startZ = 0;
        (RiskTestWorld world, Creature mob) = await HurtMageVsAsync(snare: true, prepare: w =>
        {
            ground = new PitGround(w.Player.Z) { Pit = (new Vector3(w.Player.X - 8f, w.Player.Y, w.Player.Z), 6f) };
            startZ = w.Player.Z;
            WorldCollision.Of(w.Host.World).Install(lineOfSight: ground);
        });
        await using (world)
        {
            float lowest = startZ;
            bool far = await world.ThinkUntilAsync(6_000, () => Gap(world, mob) >= 8f, each: () => lowest = MathF.Min(lowest, world.Player.Z));
            Assert.True(far, "the mage never got 8 yards away: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.True(lowest >= startZ - 2f, $"the mage fell {startZ - lowest:F1} yards");
            Assert.NotNull(ground);
        }
    }

    /// <summary>K5: with <c>World:Playerbots:Combat:Kite:Enabled</c> off the mage casts the same snare and stays where it stands: K1's "before".</summary>
    [Fact]
    public async Task WithKitingOff_TheMageSnaresAndStaysPut()
    {
        (RiskTestWorld world, Creature mob) = await HurtMageVsAsync(snare: true, configure: options => options.Combat.Kite.Enabled = false);
        await using (world)
        {
            bool away = await world.ThinkUntilAsync(4_000, () => Gap(world, mob) >= 6f);
            Assert.False(away, "the mage walked away with kiting off: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.True(await world.OnWorldAsync(() => mob.RunSpeed < world.Player.RunSpeed * PlayerbotKiteModel.SlowSpeedRatio),
                "the snare never landed, so the guard proves nothing: " + await world.OnWorldAsync(() => Describe(world, mob)));
            Assert.Equal(0, await world.OnWorldAsync(() => world.Brain.Kite.Steps));
        }
    }

    /// <summary>Flat ground at <c>z</c> with an optional pit (a floor 10 yards lower).</summary>
    private sealed class PitGround(float z) : ILineOfSight
    {
        public (Vector3 At, float Radius)? Pit { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z2, float maxSearchDistance)
            => Pit is { } pit && Vector2.Distance(new Vector2(x, y), new Vector2(pit.At.X, pit.At.Y)) < pit.Radius ? z - 10f : z;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z2, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
