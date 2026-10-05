using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Death;

public sealed class GhostAuraTests
{
    private const uint Ghost = 992095;
    private const uint OtherGhost = 992096;

    [Fact]
    public void GhostHandlerIsDiscovered()
    {
        using var kit = Kit();
        Assert.True(kit.System.HasAuraHandler(AuraType.Ghost));
    }

    [Fact]
    public void ApplyAndRemove_SetOnlyGhostVisibilityBitAndPlayerFlag()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes1, 3, 0x12);
        player.Flags |= PlayerFlags.Afk;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Ghost, SpellCastTargets.ForSelf(), true));
        Assert.Equal(0x13, player.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.True(player.Flags.HasFlag(PlayerFlags.Afk));
        kit.System.RemoveAuras(player, Ghost);
        Assert.Equal(0x12, player.GetByte(UpdateFields.UnitFieldBytes1, 3));
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.True(player.Flags.HasFlag(PlayerFlags.Afk));
    }

    [Fact]
    public void RemovingOneGhostAura_ClearsFlagsEvenWhenAnotherRemains_AsSourceHandlerDoes()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Ghost, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, OtherGhost, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(player, Ghost);
        Assert.True(kit.System.HasAura(player, OtherGhost));
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.Equal(0, player.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);
    }

    [Fact]
    public void QuestSettlement_BlocksApplicationAndRemovalUntilRetried()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation));
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(player, Ghost, SpellCastTargets.ForSelf(), true));
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.True(player.EndQuestSettlement(operation));
        kit.System.CastSpell(player, Ghost, SpellCastTargets.ForSelf(), true);
        Assert.True(player.BeginQuestSettlement(operation));
        kit.System.RemoveAuras(player, Ghost);
        Assert.True(kit.System.HasAura(player, Ghost));
        Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.True(player.EndQuestSettlement(operation));
        kit.System.RemoveAuras(player, Ghost);
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
    }

    [Fact]
    public void NonPlayerGhost_ChangesOnlyUnitVisibility()
    {
        using var kit = Kit();
        var creature = new CombatTestUnit();
        creature.Spawn(kit.World.GetMap(0), 0, 0);
        creature.SetByte(UpdateFields.UnitFieldBytes1, 3, 0x22);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(creature, Ghost, SpellCastTargets.ForSelf(), true));
        Assert.Equal(0x23, creature.GetByte(UpdateFields.UnitFieldBytes1, 3));
        kit.System.RemoveAuras(creature, Ghost);
        Assert.Equal(0x22, creature.GetByte(UpdateFields.UnitFieldBytes1, 3));
    }

    [Fact]
    public void ReplacementPlayerWithSameGuid_DoesNotInheritOrLoseOldGhostFlags()
    {
        using var kit = Kit();
        (Player old, _) = kit.AddPlayer(1);
        kit.System.CastSpell(old, Ghost, SpellCastTargets.ForSelf(), true);
        kit.World.RemovePlayer(old);
        kit.System.RemoveUnit(old);
        (Player replacement, _) = kit.AddPlayer(1);
        Assert.False(replacement.Flags.HasFlag(PlayerFlags.Ghost));
        kit.System.CastSpell(replacement, Ghost, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(old, Ghost);
        kit.System.RemoveAurasByCaster(old, Ghost, replacement.Guid);
        Assert.Empty(kit.System.GetAuras(old));
        kit.System.RemoveUnit(old);
        Assert.True(kit.System.HasAura(replacement, Ghost));
        Assert.True(replacement.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.Equal(1, replacement.GetByte(UpdateFields.UnitFieldBytes1, 3) & 1);
    }

    private static SpellTestKit Kit() => new(Definition(Ghost), Definition(OtherGhost));

    private static SpellInfo Definition(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Ghost)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
    };
}
