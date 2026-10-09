using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic boss_razorgore.cpp DestroyEgg::OnEffectExecute (spell 19873). The possessed Razorgore casts it without a target;
/// <see cref="BlackwingLairTargetModule"/> resolves the nearest egg in range (TARGET_GAMEOBJECT_SCRIPT_NEAR_CASTER) into the cast's
/// object target before the DUMMY effect runs. With no egg in range the cast is refused (Spell::CheckCast script targeting:
/// SPELL_FAILED_BAD_TARGETS).
/// </summary>
[SpellScript(BlackwingLairTargetModule.DestroyEgg)]
public sealed class DestroyEggScript : ISpellScript
{
    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
        => BlackwingLairTargetModule.FindEgg(context.Caster, context.Spell.Range.Max, context.Targets.GameObject) is null
            ? SpellCastResult.BadTargets : SpellCastResult.CastOk;

    public void OnEffectExecute(SpellEffectContext context)
    {
        SpellCast cast = context.Cast;
        if (context.Effect.Effect != SpellEffectName.Dummy
            || cast.Caster is not Creature { Entry: BlackwingLairTargetModule.Razorgore }
            || cast.Caster.Map?.FindUpdater<BlackwingLairInstance>() is not { } raid
            || cast.Caster.Map.FindUpdater<GameObjectMapSystem>()?.Find(cast.Targets.GameObject) is not { } egg)
            return;
        raid.DestroyEgg(egg);
    }
}
