using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Gives the Transform aura its creature data: the display id of the creature a transform spell names (mangoszero
/// <c>Aura::HandleAuraTransform</c>, SpellAuraShapeshift.cpp:548-562). The template's display is chosen by its probabilities
/// (<see cref="Creature.ChooseDisplayId"/>); an entry that is not in the data is logged and the aura uses the pig model. The creature
/// content is read per call from the creature feature (an immutable snapshot, replaced whole on reload).
/// </summary>
public sealed class TransformDisplayFeature(CreatureWorldFeature creatures, ILogger<TransformDisplayFeature> logger) : IWorldFeature, ITransformDisplaySource
{
    private readonly Random _random = new();

    public void Attach(WorldRuntime world) => TransformDisplays.Register(world, this);

    public uint? FindDisplay(uint creatureEntry)
    {
        if (creatures.Content.FindTemplate(creatureEntry) is not { } template)
        {
            logger.LogError("Auras: unknown creature id = {Entry} (only its model id is needed) from a Transform aura", creatureEntry);
            return null;
        }

        return Creature.ChooseDisplayId(template, _random);
    }

    public void ReportNoModel(uint spellId)
        => logger.LogError("Transform aura of spell {Spell} does not have a creature entry defined and has no custom model", spellId);
}
