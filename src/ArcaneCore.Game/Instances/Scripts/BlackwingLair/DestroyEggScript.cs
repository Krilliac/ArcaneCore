using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>mangos-classic boss_razorgore.cpp DestroyEgg::OnEffectExecute (spell 19873).</summary>
[SpellScript(19873)]
public sealed class DestroyEggScript : ISpellScript
{
    public void OnCast(SpellCast cast)
    {
        if (cast.Caster is not Creature { Entry: 12435 }
            || cast.Caster.Map?.FindUpdater<BlackwingLairInstance>() is not { } raid
            || cast.Caster.Map.FindUpdater<GameObjectMapSystem>()?.Find(cast.Targets.GameObject) is not { } egg)
            return;
        raid.DestroyEgg(egg);
    }
}
