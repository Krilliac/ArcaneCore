using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

public sealed class ReincarnationTests
{
    private static SpellInfo Effect() => SelfResurrectionOfferTests.Resurrection(21169) with
    {
        Reagents = [new(17030, 1)], RecoveryTime = 3_600_000,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LearnedPassiveWithAnkhOffersAndConsumesReincarnation(bool ghost)
    {
        using var kit = new SpellTestKit(Effect());
        Player player = SpellReagentTests.AddPlayer(kit, 2);
        kit.Spellbook.Teach(player, 20608);
        player.MaxHealth = 1000;
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(21169u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));

        Assert.True(kit.System.TrySelfResurrect(player));

        Assert.Equal(250u, player.Health);
        Assert.Equal(1u, player.Inventory.GetItemCount(17030));
        Assert.False(kit.System.IsSpellReady(player, Effect()));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Theory]
    [InlineData(false, 1u, false)]
    [InlineData(true, 0u, false)]
    [InlineData(true, 1u, true)]
    public void MissingPassiveMissingAnkhAndBankOnlyAnkhDoNotOffer(bool knows, uint ankhs, bool bank)
    {
        using var kit = new SpellTestKit(Effect());
        Player player = SpellReagentTests.AddPlayer(kit, ankhs, bank);
        if (knows) kit.Spellbook.Teach(player, 20608);
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void SoulstoneTakesPrecedenceAndLeavesTheAnkh()
    {
        using var kit = new SpellTestKit(Effect(), SelfResurrectionOfferTests.Soulstone(20707), SelfResurrectionOfferTests.Resurrection(3026));
        Player player = SpellReagentTests.AddPlayer(kit, 1);
        kit.Spellbook.Teach(player, 20608);
        kit.System.CastSpell(player, 20707, SpellCastTargets.ForSelf(), triggered: true);
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(3026u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.True(kit.System.TrySelfResurrect(player));
        Assert.Equal(1u, player.Inventory.GetItemCount(17030));
    }

    [Fact]
    public void CooldownPreventsANewOfferUntilItsExpiry()
    {
        using var kit = new SpellTestKit(Effect());
        Player player = SpellReagentTests.AddPlayer(kit, 2);
        kit.Spellbook.Teach(player, 20608);
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.True(kit.System.TrySelfResurrect(player));
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        player.Map!.Combat.ResurrectPlayer(player, 0.5f, applySickness: false);
        kit.Now += 3_600_001;
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(21169u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
    }

    [Fact]
    public void AnkhRemovedAfterDeathCausesNormalReagentRejection()
    {
        using var kit = new SpellTestKit(Effect());
        Player player = SpellReagentTests.AddPlayer(kit, 1);
        kit.Spellbook.Teach(player, 20608);
        player.Map!.Combat.Kill(null, player);
        kit.System.OnUnitDied(player);
        Assert.Equal(21169u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        player.Inventory.DestroyItemCount(17030, 1);
        Assert.False(kit.System.TrySelfResurrect(player));
        Assert.False(player.IsAlive);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
        Assert.True(kit.System.IsSpellReady(player, Effect()));
    }
}
