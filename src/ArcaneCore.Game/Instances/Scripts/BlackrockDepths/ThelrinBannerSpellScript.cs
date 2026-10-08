using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>mangos-classic blackrock_depths/blackrock_depths.cpp SummonThelrinDnd::OnEffectExecute (27517 effect 1).</summary>
[SpellScript(27517)]
public sealed class ThelrinBannerSpellScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 1 || context.Target.Map?.FindUpdater<InstanceData>() is not BlackrockDepthsInstance depths)
            return;
        uint state = depths.GetData(BlackrockDepthsInstance.TypeRingOfLaw);
        if (state is EncounterState.Done or EncounterState.Special) return;
        depths.SetData(BlackrockDepthsInstance.TypeRingOfLaw,
            state == EncounterState.InProgress ? EncounterState.Special : 5u);
    }
}
