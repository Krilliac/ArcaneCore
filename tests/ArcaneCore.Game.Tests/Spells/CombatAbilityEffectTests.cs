using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The Parry, Block and Dual Wield spell effects (vmangos SpellEffects.cpp: EffectParry :5280, EffectBlock :5286,
/// EffectDualWield :2620) set the player's ability flags. They are passive spells the player is given when the
/// ability is learned (Player::AddSpell casts learned passives), so the casts here are the triggered self casts.
/// </summary>
public sealed class CombatAbilityEffectTests
{
    // Synthetic ids: the real spells are 82, 107 and 674, and 107 is taken by the test kit.
    private const uint ParrySpell = 90082;
    private const uint BlockSpell = 90107;
    private const uint DualWieldSpell = 90674;

    private static SpellTestKit Kit() => new(
        Spell(ParrySpell, Effect(SpellEffectName.Parry, 0)),
        Spell(BlockSpell, Effect(SpellEffectName.Block, 0)),
        Spell(DualWieldSpell, Effect(SpellEffectName.DualWield, 0)));

    [Fact]
    public void TheThreeEffectsHaveHandlers()
    {
        using SpellTestKit kit = Kit();
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Parry));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Block));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.DualWield));
    }

    [Fact]
    public void ParryBlockAndDualWieldSetTheFlagsOfAPlayerTarget()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        PlayerStatState state = player.StatState;
        Assert.False(state.CanParry || state.CanBlock || state.CanDualWield);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, ParrySpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(state.CanParry);
        Assert.False(state.CanBlock || state.CanDualWield);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, BlockSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, DualWieldSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(state.CanParry && state.CanBlock && state.CanDualWield);
    }

    [Fact]
    public void AnAttachedStatSystemRecomputesThePercentagesWhenAnAbilityIsLearned()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        new PlayerStatSystem().Attach(player);
        Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerParryPercentage));
        Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerBlockPercentage));

        kit.System.CastSpell(player, ParrySpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, BlockSpell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(5f, player.GetFloat(UpdateFields.PlayerParryPercentage), 0.001f);
        Assert.Equal(5f, player.GetFloat(UpdateFields.PlayerBlockPercentage), 0.001f);
    }
}
