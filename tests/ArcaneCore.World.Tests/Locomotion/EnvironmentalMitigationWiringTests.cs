using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>
/// The daemon's environmental and fall damage go through the spell combat rules (vmangos Player::EnvironmentalDamage,
/// Player.cpp:713-758; Player::HandleFall, Player.cpp:20852-20856; docs/areas/locomotion.md).
/// </summary>
public sealed class EnvironmentalMitigationWiringTests
{
    private const uint FireImmunity = 990_601;
    private const uint PhysicalImmunity = 990_602;
    private const uint FireWard = 990_603;
    private const uint PhysicalTakenMinus50 = 990_604;
    private const uint FireTakenMinus50 = 990_605;

    private static SpellInfo SelfAura(uint id, AuraType type, int amount, uint misc) => new()
    {
        Id = id, Name = $"Spell {id}", RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0), SpellVisual = 1,
        Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = type, BasePoints = amount - 1, BaseDice = 1, DieSides = 1, MiscValue = (int)misc,
            TargetA = SpellImplicitTarget.UnitCaster,
        }],
    };

    /// <summary>The online player and the daemon's spell system, whose store now also holds the test auras.</summary>
    private static async Task<(Player Player, SpellSystem Spells)> PrepareAsync(WorldTestHost host)
    {
        Player player = await host.PlayerAsync("Lavawalker");
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        uint fire = SpellSchoolMasks.Of(SpellSchool.Fire);
        uint physical = SpellSchoolMasks.Of(SpellSchool.Normal);
        await host.OnWorldAsync(() => spells.Store = new SpellStore(
            [
                .. spells.Store.All,
                SelfAura(FireImmunity, AuraType.SchoolImmunity, 0, fire),
                SelfAura(PhysicalImmunity, AuraType.SchoolImmunity, 0, physical),
                SelfAura(FireWard, AuraType.SchoolAbsorb, 30, fire),
                SelfAura(PhysicalTakenMinus50, AuraType.ModDamagePercentTaken, -50, physical),
                SelfAura(FireTakenMinus50, AuraType.ModDamagePercentTaken, -50, fire),
            ], [], []));
        return (player, spells);
    }

    /// <summary>Cast the self aura, then give the player 1000 health (world thread).</summary>
    private static void Grant(SpellSystem spells, Player player, params uint[] auras)
    {
        foreach (uint aura in auras)
        {
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, aura, SpellCastTargets.ForSelf(), triggered: true));
        }

        player.MaxHealth = 1000;
        player.Health = 1000;
    }

    [Fact]
    public async Task FireImmunity_StopsLava_ButNotDrowning()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LAVA", "Lavawalker");
        (Player player, SpellSystem spells) = await PrepareAsync(host);

        (uint Lava, uint Fire, uint Drowning, uint Health) dealt = await host.OnWorldAsync(() =>
        {
            Grant(spells, player, FireImmunity);
            uint lava = EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Lava, 100);
            uint fire = EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Fire, 100);
            uint drowning = EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Drowning, 100);
            return (lava, fire, drowning, player.Health);
        });

        Assert.Equal((0u, 0u, 100u, 900u), dealt); // Player.cpp:723-726: fire immunity ends lava and fire damage
    }

    [Fact]
    public async Task PhysicalImmunity_StopsTheFallDrowningAndFatigue_ButNotLava()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LAVA", "Lavawalker");
        (Player player, SpellSystem spells) = await PrepareAsync(host);

        (uint Fall, uint Drowning, uint Exhausted, uint Lava) dealt = await host.OnWorldAsync(() =>
        {
            Grant(spells, player, PhysicalImmunity);
            return (EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Fall, 100),
                EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Drowning, 100),
                EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Exhausted, 100),
                EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Lava, 100));
        });

        Assert.Equal((0u, 0u, 0u, 100u), dealt); // Player.cpp:737-741
    }

    [Fact]
    public async Task FireResistance_ResistsPartOfTheLava()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LAVA", "Lavawalker");
        (Player player, SpellSystem spells) = await PrepareAsync(host);

        uint dealt = await host.OnWorldAsync(() =>
        {
            Grant(spells, player);
            // 400 fire resistance at level 60: 400 x 0.15 / 60 = 1.0, capped at a 0.75 resist chance (SpellCaster.cpp:917-922),
            // which always resists 75% (the quarter-step outcome of a 0.75 chance).
            player.Level = 60;
            player.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 400);
            return EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Lava, 100);
        });

        Assert.Equal(25u, dealt);
    }

    [Fact]
    public async Task AFireWard_AbsorbsLava_AndBreaksWhenItIsUsedUp()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LAVA", "Lavawalker");
        (Player player, SpellSystem spells) = await PrepareAsync(host);

        (uint Dealt, uint Health, bool WardLeft) result = await host.OnWorldAsync(() =>
        {
            Grant(spells, player, FireWard);
            uint lava = EnvironmentalDamage.Apply(host.World, player, EnvironmentalDamageType.Lava, 100);
            return (lava, player.Health, spells.HasAura(player, FireWard));
        });

        Assert.Equal((70u, 930u, false), result); // 30 absorbed (Unit.cpp:1957-2000), the shield removed at 0
    }

    [Fact]
    public async Task ThePhysicalDamageTakenAuras_ScaleTheFall_AndTheFireOnesDoNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LAVA", "Lavawalker");
        (Player player, SpellSystem spells) = await PrepareAsync(host);

        (float Before, float After) multiplier = await host.OnWorldAsync(() =>
        {
            float before = LocomotionEnvironment.FallModifiersFor(host.World).TakenDamageMultiplier(player);
            Grant(spells, player, PhysicalTakenMinus50, FireTakenMinus50);
            return (before, LocomotionEnvironment.FallModifiersFor(host.World).TakenDamageMultiplier(player));
        });

        Assert.Equal((1.0f, 0.5f), multiplier); // GetTotalAuraMultiplierByMiscMask(MOD_DAMAGE_PERCENT_TAKEN, NORMAL), Player.cpp:20854
    }

    [Fact]
    public async Task TheSeams_AreNotThePassThroughDefaults()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Assert.IsNotType<NoEnvironmentalMitigation>(LocomotionEnvironment.MitigationFor(host.World));
        Assert.IsNotType<NoFallDamageModifiers>(LocomotionEnvironment.FallModifiersFor(host.World));
    }
}
