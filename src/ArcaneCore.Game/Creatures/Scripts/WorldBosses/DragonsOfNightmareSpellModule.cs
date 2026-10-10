using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts.WorldBosses;

/// <summary>
/// vmangos EmeraldDragonsDreamFogScript (scripts/world/dragons_of_nightmare/boss_dragon_of_nightmare.cpp, spell_emerald_dragons_dream_fog):
/// Dream Fog 24781, the sleep pulse of the Dream Fog cloud, hits a single target (OnSetTargetMap unMaxTargets = 1).
/// </summary>
public sealed class DragonsOfNightmareSpellModule : ISpellHandlerModule
{
    public const uint SpellDreamFogSleep = 24781;

    public void Register(SpellSystem system) => system.RegisterSpellMaxTargetsOverride(SpellDreamFogSleep, 1);
}
