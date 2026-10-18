using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>SPELL_EFFECT_PULL (70, a no-op as in vmangos) and SPELL_EFFECT_DESPAWN_OBJECT (130, the game-object target leaves the world).</summary>
public sealed class PullAndDespawnObjectEffectTests
{
    private const uint Chest = 990011;
    private const uint DespawnSpell = 990201;
    private const uint PullSpell = 990202;

    [Fact]
    public void DespawnObject_RemovesASummonedTargetObject_AndPullIsHandled()
    {
        using var kit = new SpellTestKit(
            Spell(DespawnSpell, Effect(SpellEffectName.DespawnObject, 0, SpellImplicitTarget.GameObject)) with
            { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(PullSpell, Effect(SpellEffectName.Pull, 0, SpellImplicitTarget.UnitCaster)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        var map = kit.World.GetMap(0);
        var objects = new GameObjectMapSystem(map, new GameObjectContent([GameObjectTestKit.GoTemplate(Chest, GameObjectType.Chest)], [], [], [], []));
        map.AddUpdater(objects);
        (Player caster, _) = kit.AddPlayer(1);
        GameObject go = objects.Summon(Chest, 2, 0, caster.Z, 0)!;
        Assert.NotNull(objects.Find(go.Guid));

        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = go.Guid };
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, DespawnSpell, targets, triggered: true));
        Assert.Null(objects.Find(go.Guid));

        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Pull));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, PullSpell, SpellCastTargets.ForSelf(), triggered: true));
    }
}