using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// SMART_SCRIPT_TYPE_AREATRIGGER rows (<c>smart_scripts</c> source type 2, entryorguid = the trigger id) on CMSG_AREATRIGGER: the listener runs after the
/// packet's volume check, before any listener or teleport (<see cref="IAreaTriggerPreemptor"/>). As in AzerothCore, a trigger script that ran ends the
/// handler, so quest exploration, tavern and teleport do not follow. The catalog is read from the creature feature at use time.
/// </summary>
public sealed class SmartAreaTriggerFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerPreemptor
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
    }

    public bool Preempts(Player player, uint triggerId)
        => SmartAreaTrigger.Run(services.GetService<CreatureWorldFeature>()?.Content.Ai.SmartScripts ?? SmartScriptCatalog.Empty, player, triggerId);
}
