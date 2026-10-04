using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Death.Travel;

/// <summary>What the ghost rules decided for an area trigger a dead player stepped on.</summary>
/// <param name="Trigger">The teleport to use (the trigger's own, or the entrance of the dungeon the corpse is in); null when refused.</param>
/// <param name="RefusedMapName">The name of the dungeon the ghost may not enter, when refused.</param>
public readonly record struct GhostEntry(AreaTriggerTeleport? Trigger, string? RefusedMapName)
{
    public bool Refused => Trigger is null;

    /// <summary>The text of the refusal (vmangos <c>"You cannot enter %s while in ghost form."</c>).</summary>
    public string? Message => RefusedMapName is null ? null : $"You cannot enter {RefusedMapName} while in ghost form.";
}

/// <summary>
/// Ghosts at dungeon entrances (vmangos <c>WorldSession::HandleAreaTriggerOpcode</c>, MiscHandler.cpp:712-756): a dead player may only
/// enter a dungeon that its corpse lies in, or one the corpse's dungeon is nested in (a linked parent chain, such as Blackrock Depths
/// and Blackrock Spire's dungeons); anything else is refused with a message. When the corpse is in another dungeon than the trigger's,
/// the ghost is sent to the entrance of the corpse's dungeon instead ("need find areatrigger to inner dungeon for landing point"). The
/// Molten Core special case of patches up to 1.2 is not modelled: this core's patch is 1.12.
/// </summary>
public static class GhostEntryRules
{
    /// <summary>Apply the rules to <paramref name="trigger"/>. Living players, GMs aside, and non-dungeon targets pass unchanged.</summary>
    public static GhostEntry Resolve(Player player, AreaTriggerTeleport trigger, MapRegistry registry, IEnumerable<AreaTriggerTeleport> allTeleports)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(allTeleports);
        if (player.IsAlive || registry.Find(trigger.TargetMap) is not { IsDungeon: true } target)
        {
            return new GhostEntry(trigger, null);
        }

        uint corpseMap = player.Combat.Corpse?.MapId ?? 0;

        // "check back way from corpse to entrance": the corpse's map, then each parent while the map is a dungeon.
        uint instanceMap = corpseMap;
        var seen = new HashSet<uint>();
        do
        {
            if (instanceMap == target.Entry)
            {
                break;
            }

            MapTemplate? instance = registry.Find(instanceMap);
            instanceMap = instance is { IsDungeon: true } && seen.Add(instanceMap) ? instance.Parent : 0;
        }
        while (instanceMap != 0);

        if (instanceMap == 0)
        {
            return new GhostEntry(null, target.Name);
        }

        if (trigger.TargetMap != corpseMap)
        {
            // GetMapEntranceTrigger(corpseMapId): the trigger that leads into the corpse's dungeon (the lowest id, as the instance lookups pick).
            AreaTriggerTeleport? corpseAt = allTeleports.Where(t => t.TargetMap == corpseMap).OrderBy(t => t.Id).FirstOrDefault();
            if (corpseAt is not null)
            {
                return new GhostEntry(corpseAt, null);
            }
        }

        return new GhostEntry(trigger, null);
    }
}
