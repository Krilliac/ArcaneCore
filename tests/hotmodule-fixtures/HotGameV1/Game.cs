using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Scripting;
using ArcaneCore.World.HotCode.Modules;

namespace HotGame;

/// <summary>A script hook object: only OnLogin is overridden, so only that hook array gets it.</summary>
public sealed class LoginHook : IPlayerHooks
{
    public void OnLogin(Player player)
    {
    }
}

/// <summary>Creature AI registrations. The factories are never called by the host tests.</summary>
public sealed class GameAi : ICreatureAiGroup
{
    public void Register(ICreatureAiRegistry registry)
    {
        registry.ForEntry(990001, _ => throw new NotSupportedException());
        registry.Named("HotGameAI", _ => throw new NotSupportedException());
#if V2
        registry.ForEntry(990002, _ => throw new NotSupportedException());
#endif
    }
}
