using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

public sealed class TwistingNetherTests
{
    [Theory]
    [InlineData(0, 23700u)]
    [InlineData(9, 23700u)]
    [InlineData(10, 0u)]
    [InlineData(99, 0u)]
    public void PassiveDummyUsesTheExactTenPercentDeathRoll(int draw, uint expected)
    {
        using var kit = new SpellTestKit(Nether());
        (Player player, _) = kit.AddPlayer(1);
        var random = new ChanceRandom(draw);
        kit.System.Random = random;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true));
        Die(kit, player);
        Assert.Equal(expected, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.Equal(1, random.ChanceRolls);
        Assert.True(kit.System.HasAura(player, 23701));
    }

    [Theory]
    [InlineData(false, 0, 23700u)]
    [InlineData(true, 0, 23700u)]
    [InlineData(false, 99, 3026u)]
    [InlineData(true, 99, 3026u)]
    public void SourceSelectionAllowsSuccessfulNetherToOverrideSoulstoneInEitherAuraOrder(bool netherFirst, int draw, uint expected)
    {
        using var kit = new SpellTestKit(Nether(), SelfResurrectionOfferTests.Soulstone(20707));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.Random = new ChanceRandom(draw);
        kit.System.CastSpell(player, netherFirst ? 23701u : 20707u, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, netherFirst ? 20707u : 23701u, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        Assert.Equal(expected, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void ExistingDeathOfferDoesNotRerollNether()
    {
        using var kit = new SpellTestKit(Nether());
        (Player player, _) = kit.AddPlayer(1);
        var random = new ChanceRandom(0);
        kit.System.Random = random;
        kit.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 21169);
        Die(kit, player);
        Assert.Equal(21169u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.Equal(0, random.ChanceRolls);
    }

    [Theory]
    [InlineData(0, 23700u, 1u)]
    [InlineData(99, 21169u, 0u)]
    public void NetherResultControlsReincarnationFallbackAndAnkhConsumption(int draw, uint expected, uint ankhs)
    {
        SpellInfo reincarnation = SelfResurrectionOfferTests.Resurrection(21169) with { Reagents = [new(17030, 1)] };
        using var kit = new SpellTestKit(Nether(), SelfResurrectionOfferTests.Resurrection(23700), reincarnation);
        Player player = SpellReagentTests.AddPlayer(kit, 1);
        kit.Spellbook.Teach(player, 20608);
        kit.System.Random = new ChanceRandom(draw);
        kit.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        Assert.Equal(expected, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.True(kit.System.TrySelfResurrect(player));
        Assert.Equal(ankhs, player.Inventory.GetItemCount(17030));
    }

    [Fact]
    public void EveryAppliedDummyEffectGetsItsOwnReferenceRoll()
    {
        using var kit = new SpellTestKit(Nether() with { Effects = [Dummy(), Dummy()] });
        (Player player, _) = kit.AddPlayer(1);
        var random = new ChanceRandom(99);
        kit.System.Random = random;
        kit.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        Assert.Equal(2, random.ChanceRolls);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void ADifferentAuraTypeWithTheSameIdCannotProcNether()
    {
        using var kit = new SpellTestKit(Nether() with { Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModStat)] });
        (Player player, _) = kit.AddPlayer(1);
        var random = new ChanceRandom(0);
        kit.System.Random = random;
        kit.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.Equal(0, random.ChanceRolls);
    }

    [Fact]
    public void LifeReloadClearsASelfResOfferRatherThanRestoringIt()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        player.Map!.Combat.Kill(null, player);
        Assert.True(player.Map!.Combat.RepopPlayer(player));
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 23700);
        var stored = PlayerLife.Capture(player);
        PlayerLife.ApplyVitals(player, stored);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.True(stored.IsGhost);
        Assert.NotNull(stored.Corpse);
    }

    internal static SpellInfo Nether() => SpellTestKit.Spell(23701, Dummy()) with
    {
        Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellEffectInfo Dummy() => SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy);

    private static void Die(SpellTestKit kit, Player player)
    {
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
    }

    private sealed class ChanceRandom(int draw) : Random(1)
    {
        public int ChanceRolls { get; private set; }
        public override int Next(int maxValue)
        {
            if (maxValue != 100) return base.Next(maxValue);
            ChanceRolls++;
            return draw;
        }
    }
}
