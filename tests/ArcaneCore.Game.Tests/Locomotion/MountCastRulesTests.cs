using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

public sealed class MountCastRulesTests
{
    private const uint Mount = 961001;
    private const uint Other = 961002;

    private static SpellInfo MountSpell => Spell(Mount, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 2402)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        AuraInterruptFlags = SpellAuraInterruptFlags.UnderWaterCancels | SpellAuraInterruptFlags.Damage,
        Attributes = SpellAttributes.OnlyOutdoors,
        CastTime = new SpellCastTime(1500, 0, 1500),
        InterruptFlags = SpellInterruptFlags.DamageCancels,
    };

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(MountSpell, Spell(Other, Effect(SpellEffectName.Dummy, 0)));
        LocomotionEnvironment.RegisterMountDisplays(kit.World, new Displays());
        return kit;
    }

    private sealed class Displays : IMountDisplaySource
    {
        public uint? FindMountDisplay(uint creatureEntry) => creatureEntry == 2402 ? 14337u : null;
    }

    private static SpellCastResult Check(SpellTestKit kit, Player player, bool triggered = false)
        => new MountCastCheck().Check(new SpellCastCheckContext(kit.System, player, MountSpell,
            SpellCastTargets.ForSelf(), player, triggered, Strict: true));

    [Fact]
    public void SwimmingOrDeepWater_PreventsTheMountCast()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.AddMovementFlags(MovementFlags.Swimming);
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14337);
        Assert.Equal(SpellCastResult.OnlyAbovewater, Check(kit, player));
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        player.RemoveMovementFlags(MovementFlags.Swimming);
        player.Locomotion.Environment = EnvironmentFlags.InWater | EnvironmentFlags.HighLiquid;
        Assert.Equal(SpellCastResult.OnlyAbovewater, Check(kit, player));
        player.Locomotion.Environment = EnvironmentFlags.InWater;
        Assert.Equal(SpellCastResult.CastOk, Check(kit, player));
    }

    [Fact]
    public void DisallowedFormAndTaxi_PreventMounting()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.Cat);
        Assert.Equal(SpellCastResult.NotShapeshift, Check(kit, player));
        player.SetByte(UpdateFields.UnitFieldBytes1, 2, (byte)ShapeshiftForm.None);
        player.UnitFlags |= UnitFlags.TaxiFlight;
        Assert.Equal(SpellCastResult.NotOnTaxi, Check(kit, player));
    }

    [Fact]
    public void InstanceMapExceptions_FollowTheVanillaMapTable()
    {
        static MapTemplate Map(uint id, MapType type) => new(id, 0, type, 0, 0, 0, 0, 0, 0, "test", "");
        Assert.True(MountCastCheck.IsMountAllowed(Map(0, MapType.Common)));
        Assert.False(MountCastCheck.IsMountAllowed(Map(33, MapType.Instance)));
        foreach (uint id in new uint[] { 209, 269, 309, 509 })
        {
            Assert.True(MountCastCheck.IsMountAllowed(Map(id, MapType.Instance)));
        }
    }

    [Fact]
    public void AnotherOrdinarySpell_DismountsAndRemovesTheMountAura()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Mount, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, Mount));
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Other, SpellCastTargets.ForSelf(), triggered: false));
        Assert.False(kit.System.HasAura(player, Mount));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void ADirectMountDisplay_AlsoDismountsForAnOrdinarySpell()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 14337);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Other, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void MountCastTime_UsesSpellData_AndCompletesAtFifteenHundredMilliseconds()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Mount, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(1499);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        kit.Advance(1);
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void DamageBreaksAnInterruptibleMountAura_AndCancelsAMountCast()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Mount, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.OnDamageTaken(player, attacker: null, damage: 1, periodic: false);
        Assert.False(kit.System.HasAura(player, Mount));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Mount, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(1);
        kit.System.OnDamageTaken(player, attacker: null, damage: 1, periodic: false);
        kit.Advance(1499);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }
}
