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
/// packet's volume check, before any teleport (<see cref="IAreaTriggerListener"/>). AzerothCore ends the handler when a trigger script ran; this seam
/// cannot, so quest exploration, tavern and teleport still follow (docs/integration/smartai-slice2-20261010.md). The catalog is read from the creature
/// feature at use time.
/// </summary>
public sealed class SmartAreaTriggerFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
    }

    public void OnAreaTrigger(Player player, uint triggerId)
        => SmartAreaTrigger.Run(services.GetService<CreatureWorldFeature>()?.Content.Ai.SmartScripts ?? SmartScriptCatalog.Empty, player, triggerId);
}
