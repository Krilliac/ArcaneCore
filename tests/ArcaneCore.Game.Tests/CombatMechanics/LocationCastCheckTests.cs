using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>vmangos SpellMgr::GetSpellAllowedInLocationError's hard-coded cases (SpellMgr.cpp:2650-2694).</summary>
public sealed class LocationCastCheckTests
{
    [Theory]
    [InlineData(22564u, 30u, true, true, SpellCastResult.CastOk)]
    [InlineData(22564u, 0u, false, true, SpellCastResult.RequiresArea)]
    [InlineData(23538u, 30u, true, false, SpellCastResult.RequiresArea)] // no player
    [InlineData(23333u, 489u, true, true, SpellCastResult.CastOk)]
    [InlineData(23335u, 30u, true, true, SpellCastResult.RequiresArea)]
    [InlineData(2584u, 529u, true, true, SpellCastResult.CastOk)]
    [InlineData(2584u, 0u, false, true, SpellCastResult.OnlyBattlegrounds)]
    [InlineData(22012u, 489u, true, false, SpellCastResult.CastOk)] // the spirit guide is a creature
    [InlineData(22012u, 1u, false, false, SpellCastResult.OnlyBattlegrounds)]
    [InlineData(133u, 0u, false, true, SpellCastResult.CastOk)]
    public void Evaluate_FollowsVmangos(uint spell, uint map, bool battleground, bool player, SpellCastResult expected)
        => Assert.Equal(expected, LocationCastCheck.Evaluate(spell, map, battleground, player));

    [Fact]
    public void TheWarsongFlag_CannotBeCastOutsideWarsongGulch()
    {
        using var kit = new SpellTestKit(Spell(23333, Effect(SpellEffectName.Dummy, 0)) with { SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        GeneralCastChecks.Install(kit.System);
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.RequiresArea, kit.System.CastSpell(player, 23333, SpellCastTargets.ForSelf(), triggered: false));
    }
}