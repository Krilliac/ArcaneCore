using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Gives the Transform aura its creature data: the display id of the creature a transform spell names (vmangos
/// <c>Aura::HandleAuraTransform</c>, SpellAuras.cpp:2710-2720). The template's display is chosen by its probabilities
/// (<see cref="Creature.ChooseDisplayId"/>); an entry that is not in the data is logged and the aura uses the box model. The creature
/// content is read per call from the creature feature (an immutable snapshot, replaced whole on reload).
/// </summary>
public sealed class TransformDisplayFeature(CreatureWorldFeature creatures, ILogger<TransformDisplayFeature> logger,
    CreatureDisplayModelMetadataFeature? metadata = null) : IWorldFeature, ITransformDisplaySource
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

    /// <summary>
    /// vmangos ChooseDisplayId with its scale: the chosen display's <c>display_scale</c> of the template when set, else the display's model scale
    /// (<c>GetScaleForDisplayId</c>: CreatureDisplayInfo scale times CreatureModelData scale, 1 without those files).
    /// </summary>
    public TransformDisplay? FindTransform(uint creatureEntry)
    {
        if (FindDisplay(creatureEntry) is not { } display)
        {
            return null;
        }

        CreatureTemplate template = creatures.Content.FindTemplate(creatureEntry)!;
        int selected = -1;
        for (int i = 0; i < template.DisplayIds.Count; i++)
        {
            if (template.DisplayIds[i] == display)
            {
                selected = i;
                break;
            }
        }

        float templateScale = selected >= 0 && selected < template.DisplayScales.Count ? template.DisplayScales[selected] : 0f;
        float scale = templateScale > 0 ? templateScale : metadata?.Content.Find(display)?.NativeScale ?? 1f;
        return new TransformDisplay(display, scale);
    }

    public void ReportNoModel(uint spellId)
        => logger.LogError("Transform aura of spell {Spell} does not have a creature entry defined and has no custom model", spellId);
}
