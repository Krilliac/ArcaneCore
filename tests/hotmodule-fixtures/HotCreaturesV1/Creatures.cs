using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.HotCode.Modules;

namespace HotCreatures;

/// <summary>The AI the module gives to creature entry 990101.</summary>
public sealed class ModuleCreatureAI(Creature creature) : CreatureAI(creature)
{
}

public sealed class CreatureAiRegistrations : ICreatureAiGroup
{
    public void Register(ICreatureAiRegistry registry) => registry.ForEntry(990101, creature => new ModuleCreatureAI(creature));
}
