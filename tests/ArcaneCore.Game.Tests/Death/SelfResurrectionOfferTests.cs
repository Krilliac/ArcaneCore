using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

public sealed class SelfResurrectionOfferTests
{
    // vmangos Player.cpp:19866-19879 supplies the five vanilla rank pairs.
    [Theory]
    [InlineData(20707u, 3026u)]
    [InlineData(20762u, 20758u)]
    [InlineData(20763u, 20759u)]
    [InlineData(20764u, 20760u)]
    [InlineData(20765u, 20761u)]
    public void SoulstoneOfferIsCapturedBeforeDeathRemovesItsAura(uint auraId, uint resurrectionId)
    {
        using var kit = new SpellTestKit(Soulstone(auraId), Resurrection(resurrectionId));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, auraId, SpellCastTargets.ForSelf(), triggered: true));

        Die(kit, player);

        Assert.Empty(kit.System.GetAuras(player));
        Assert.Equal(resurrectionId, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Theory]
    [InlineData(20707u, 98u, 92u)]
    [InlineData(20707u, 99u, 91u)]
    [InlineData(990001u, 99u, 92u)]
    public void UnrelatedDummyAurasDoNotOfferSoulstone(uint id, uint visual, uint icon)
    {
        using var kit = new SpellTestKit(Soulstone(id) with { SpellVisual = visual, SpellIconId = icon });
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, id, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void RemovedSoulstoneDoesNotOfferResurrection()
    {
        using var kit = new SpellTestKit(Soulstone(20707));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 20707, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.RemoveAuras(player, 20707);
        Die(kit, player);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void DeathPreservesAnAlreadySelectedServerOffer()
    {
        using var kit = new SpellTestKit(Soulstone(20707));
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 20707, SpellCastTargets.ForSelf(), triggered: true);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 20761);
        Die(kit, player);
        Assert.Equal(20761u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void AnotherResurrectionClearsTheOldDeathOffer()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        player.Map!.Combat.KillPlayer(player);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);

        player.Map!.Combat.ResurrectPlayer(player, 0.5f, applySickness: false);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfferCastsInternalEffectWithoutSpellbookKnowledge_AndCannotReplay(bool ghost)
    {
        using var kit = new SpellTestKit(Soulstone(20707), Resurrection(3026));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        kit.System.CastSpell(player, 20707, SpellCastTargets.ForSelf(), triggered: true);
        Die(kit, player);
        if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));
        Assert.False(kit.Spellbook.HasSpell(player, 3026));

        Assert.True(kit.System.TrySelfResurrect(player));

        Assert.Equal(250u, player.Health);
        Assert.Equal(DeathState.Alive, player.Combat.DeathState);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        player.Health = 123;
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(123u, player.Health);
    }

    [Fact]
    public void MissingContentConsumesTheOfferWithoutReviving()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        Die(kit, player);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.False(player.IsAlive);
    }

    [Fact]
    public void NormalCooldownRejectionConsumesTheOffer()
    {
        using var kit = new SpellTestKit(Resurrection(3026) with { RecoveryTime = 60_000 });
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 3026, SpellCastTargets.ForSelf(), triggered: false));
        Die(kit, player);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.False(player.IsAlive);
    }

    [Fact]
    public void SettlementHoldPreservesTheOfferUntilThePlayerCanAct()
    {
        using var kit = new SpellTestKit(Resurrection(3026));
        (Player player, _) = kit.AddPlayer(1);
        Die(kit, player);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);
        Guid hold = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(hold));
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(3026u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        player.EndQuestSettlement(hold);
        Assert.True(kit.System.TrySelfResurrect(player));
    }

    [Fact]
    public void TransitPreservesTheOfferUntilMapSimulationResumes()
    {
        using var kit = new SpellTestKit(Resurrection(3026));
        (Player player, _) = kit.AddPlayer(1);
        Die(kit, player);
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);
        kit.System.IsInTransit = _ => true;
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(3026u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        kit.System.IsInTransit = _ => false;
        Assert.True(kit.System.TrySelfResurrect(player));
    }

    [Fact]
    public void AliveStateAtZeroHealthCannotUseADeathOffer()
    {
        using var kit = new SpellTestKit(Resurrection(3026));
        (Player player, _) = kit.AddPlayer(1);
        player.Health = 0;
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 3026);
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.Equal(0u, player.Health);
        Assert.Equal(DeathState.Alive, player.Combat.DeathState);
    }

    internal static SpellInfo Soulstone(uint id) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        SpellVisual = 99, SpellIconId = 92, Duration = new SpellDuration(30_000, 0, 30_000),
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    internal static SpellInfo Resurrection(uint id) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.SelfResurrect, 25)) with
    {
        Attributes = SpellAttributes.AllowCastWhileDead, AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static void Die(SpellTestKit kit, Player player)
    {
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
    }
}
