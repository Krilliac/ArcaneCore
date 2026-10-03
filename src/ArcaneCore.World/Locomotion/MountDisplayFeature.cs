using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// Gives the mount aura its creature data: the display id of the creature a mount spell names (vmangos HandleAuraMounted,
/// SpellAuras.cpp:2257-2268). The creature template's display is chosen by its probabilities and, when its model has another
/// gender, replaced by it half of the time (Creature::ChooseDisplayId and GetCreatureDisplayInfoRandomGender, as a creature
/// does at spawn). A creature entry that is not in the data is logged and the rider stays on foot.
/// </summary>
public sealed class MountDisplayFeature(CreatureWorldFeature creatures, ILogger<MountDisplayFeature> logger) : IWorldFeature, IMountDisplaySource
{
    private readonly Random _random = new();

    public void Attach(WorldRuntime world) => LocomotionEnvironment.RegisterMountDisplays(world, this);

    public uint? FindMountDisplay(uint creatureEntry)
    {
        CreatureContent content = creatures.Content;
        if (content.FindTemplate(creatureEntry) is not { } template)
        {
            logger.LogError("AuraMounted: creature_template {Entry} not found in the database (only its display id is needed)", creatureEntry);
            return null;
        }

        uint displayId = Creature.ChooseDisplayId(template, _random);
        if (content.FindModel(displayId) is { DisplayIdOtherGender: not 0 } model && _random.Next(2) == 0 && content.FindModel(model.DisplayIdOtherGender) is { } other)
        {
            displayId = other.DisplayId;
        }

        return displayId;
    }
}
