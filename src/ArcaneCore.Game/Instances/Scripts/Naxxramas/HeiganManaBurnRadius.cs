using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// vmangos boss_heigan.cpp HeiganManaBurnScript::OnSetTargetMap widens spell 29310's
/// target map from the imported SpellRadius.dbc value of 25 yards to 28 yards.
/// </summary>
public sealed class HeiganManaBurnRadius : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterSpellAreaRadiusOverride(29310, 0, 28f);
}
