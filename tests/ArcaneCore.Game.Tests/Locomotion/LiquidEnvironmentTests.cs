using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Player::UpdateTerainEnvironmentFlags (vmangos Player.cpp:20353-20420) over real terrain: ground 20, liquid surface 100.</summary>
public sealed class LiquidEnvironmentTests
{
    private static EnvironmentFlags FlagsAt(LiquidTypeFlags? liquid, float z)
    {
        using var kit = new HazardKit(liquid);
        Player player = kit.AddPlayer(1, z, out _);
        return player.Locomotion.Environment;
    }

    [Theory]
    // above the surface: only "inside an area with liquid"
    [InlineData(LiquidTypeFlags.Water, 101f, EnvironmentFlags.Liquid)]
    // within 0.1 yard of the surface (the water walk band): not in water yet
    [InlineData(LiquidTypeFlags.Water, 99.95f, EnvironmentFlags.Liquid)]
    // up to two yards below the surface: in water, but too shallow to swim (100 > 99.5 + 1.5 is false)
    [InlineData(LiquidTypeFlags.Water, 99.5f, EnvironmentFlags.Liquid | EnvironmentFlags.InWater)]
    // two yards down: swimming, still not fully submerged (the head, 2 yards above z, is at the surface)
    [InlineData(LiquidTypeFlags.Water, 98f, EnvironmentFlags.Liquid | EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid)]
    // under water, the surface above the head: submerged
    [InlineData(LiquidTypeFlags.Water, 97f, EnvironmentFlags.Liquid | EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid | EnvironmentFlags.Underwater)]
    [InlineData(LiquidTypeFlags.Ocean, 97f, EnvironmentFlags.Liquid | EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid | EnvironmentFlags.Underwater)]
    // magma counts in the water walk band too, and is never "in water"
    [InlineData(LiquidTypeFlags.Magma, 99.95f, EnvironmentFlags.Liquid | EnvironmentFlags.InMagma)]
    [InlineData(LiquidTypeFlags.Magma, 98f, EnvironmentFlags.Liquid | EnvironmentFlags.InMagma | EnvironmentFlags.HighLiquid)]
    [InlineData(LiquidTypeFlags.Magma, 97f, EnvironmentFlags.Liquid | EnvironmentFlags.InMagma | EnvironmentFlags.HighLiquid | EnvironmentFlags.Underwater)]
    [InlineData(LiquidTypeFlags.Magma, 101f, EnvironmentFlags.Liquid)]
    [InlineData(LiquidTypeFlags.Slime, 99.95f, EnvironmentFlags.Liquid | EnvironmentFlags.InSlime)]
    [InlineData(LiquidTypeFlags.Slime, 97f, EnvironmentFlags.Liquid | EnvironmentFlags.InSlime | EnvironmentFlags.HighLiquid | EnvironmentFlags.Underwater)]
    public void TheFlags_FollowTheDepthBandsAndTheLiquidKind(LiquidTypeFlags liquid, float z, EnvironmentFlags expected)
        => Assert.Equal(expected, FlagsAt(liquid, z));

    [Theory]
    [InlineData(101f)]
    [InlineData(97f)]
    public void DeepWater_IsHighSeaAtAnyDepth(float z)
    {
        EnvironmentFlags flags = FlagsAt(LiquidTypeFlags.Water | LiquidTypeFlags.DeepWater, z);
        Assert.True(flags.HasFlag(EnvironmentFlags.HighSea));
        Assert.True(flags.HasFlag(EnvironmentFlags.Liquid));
    }

    [Fact]
    public void WithoutLiquid_EveryLiquidFlagIsClear()
        => Assert.Equal(EnvironmentFlags.None, FlagsAt(null, 50f));

    [Fact]
    public void TheCollisionHeight_MovesTheSwimAndSubmergedThresholds()
    {
        using var kit = new HazardKit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 97.5f, out _);
        // The default 2 yards: level 100 > 97.5 + 2.0 is true: submerged; the swim depth is 1.5.
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.Underwater));

        player.Locomotion.CollisionHeight = 2.6f; // a tall race: 97.5 + 2.6 = 100.1 is above the surface
        kit.MoveTo(player, 97.5f);
        player.Locomotion.LastEnvironmentSample = null;
        LiquidEnvironment.UpdateTerrainFlags(player);
        Assert.False(player.Locomotion.Environment.HasFlag(EnvironmentFlags.Underwater));
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.HighLiquid));
    }

    [Fact]
    public void AMovementPacket_ChangesTheFlagsAtOnce_AndALoginOrTeleportWithoutOneIsPickedUpByTheTick()
    {
        using var kit = new HazardKit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out _);
        Assert.Equal(EnvironmentFlags.Liquid, player.Locomotion.Environment);

        kit.MoveTo(player, 97f); // no tick in between
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.Underwater));

        player.Relocate(HazardKit.X, HazardKit.Y, 110f, 0, 5); // a near teleport: no movement packet
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.Underwater)); // not yet re-evaluated
        kit.Tick(50);
        Assert.Equal(EnvironmentFlags.Liquid, player.Locomotion.Environment);
    }

    [Fact]
    public void ANoOpTick_DoesNotAskTheTerrainAgain()
    {
        using var kit = new HazardKit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 97f, out _);
        player.Locomotion.Environment = EnvironmentFlags.None; // tamper: the unchanged position must not refresh it

        kit.Tick(50);

        Assert.Equal(EnvironmentFlags.None, player.Locomotion.Environment);
    }

    // --- water cancels ---------------------------------------------------------------------

    private sealed class Bridge : IEnvironmentSpellBridge
    {
        public List<string> Calls { get; } = [];

        public void RemoveAurasWithInterruptFlags(Unit unit, SpellAuraInterruptFlags flags) => Calls.Add($"auras {flags}");

        public void InterruptChannelsWithFlags(Unit unit, SpellAuraInterruptFlags flags) => Calls.Add($"channels {flags}");
    }

    [Fact]
    public void EnteringAndLeavingSwimmableWater_RemovesTheAurasThatNeedLandOrWater()
    {
        using var kit = new HazardKit(LiquidTypeFlags.Water);
        var bridge = new Bridge();
        LocomotionEnvironment.RegisterSpellBridge(kit.Kit.World, bridge);
        Player player = kit.AddPlayer(1, 110f, out _);
        bridge.Calls.Clear();

        kit.MoveTo(player, 99f);   // wading: 100 > 99 + 1.5 is false, no swim yet
        Assert.Empty(bridge.Calls);

        kit.MoveTo(player, 97f);   // swimming
        Assert.Equal(["channels UnderWaterCancels", "auras UnderWaterCancels"], bridge.Calls);

        bridge.Calls.Clear();
        kit.MoveTo(player, 110f);  // out
        Assert.Equal(["channels AboveWaterCancels", "auras AboveWaterCancels"], bridge.Calls);
    }

    private sealed class SystemBridge(SpellSystem system) : IEnvironmentSpellBridge
    {
        public void RemoveAurasWithInterruptFlags(Unit unit, SpellAuraInterruptFlags flags) => system.RemoveAurasWithInterruptFlags(unit, flags);

        public void InterruptChannelsWithFlags(Unit unit, SpellAuraInterruptFlags flags) => system.InterruptChannelsWithFlags(unit, flags);
    }

    [Fact]
    public void WithTheSpellSystem_AWaterBoundAuraEndsOnEnteringWater_AndALandBoundOneOnLeavingIt()
    {
        const uint landBound = 990001;
        const uint waterBound = 990002;
        SpellInfo Perm(uint id, SpellAuraInterruptFlags flags) => Tests.Spells.SpellTestKit.Spell(id, Tests.Spells.SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            AuraInterruptFlags = flags,
        };

        using var kit = new HazardKit(LiquidTypeFlags.Water, Perm(landBound, SpellAuraInterruptFlags.UnderWaterCancels), Perm(waterBound, SpellAuraInterruptFlags.AboveWaterCancels));
        LocomotionEnvironment.RegisterSpellBridge(kit.Kit.World, new SystemBridge(kit.Kit.System));
        Player player = kit.AddPlayer(1, 110f, out _);
        kit.Kit.System.CastSpell(player, landBound, SpellCastTargets.ForSelf(), triggered: true);
        kit.Kit.System.CastSpell(player, waterBound, SpellCastTargets.ForSelf(), triggered: true);

        kit.MoveTo(player, 97f);                       // swimming: the aura that does not work in water ends
        Assert.False(kit.Kit.System.HasAura(player, landBound));
        Assert.True(kit.Kit.System.HasAura(player, waterBound));

        kit.MoveTo(player, 110f);                      // out of the water: the one that needs water ends
        Assert.False(kit.Kit.System.HasAura(player, waterBound));
    }

    [Fact]
    public void TheInterruptFlags_HaveTheVmangosValues()
    {
        Assert.Equal(0x80u, (uint)SpellAuraInterruptFlags.UnderWaterCancels);
        Assert.Equal(0x100u, (uint)SpellAuraInterruptFlags.AboveWaterCancels);
    }

    [Fact]
    public void LeavingTheMap_ForgetsTheLiquid()
    {
        using var kit = new HazardKit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 97f, out _);

        kit.Kit.World.RemovePlayer(player);

        Assert.Equal(EnvironmentFlags.None, player.Locomotion.Environment);
    }
}
