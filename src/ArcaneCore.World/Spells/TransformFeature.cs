using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Spells;

/// <summary>Connects Transform aura display resolution to the loaded creature content.</summary>
public sealed class TransformFeature(CreatureWorldFeature creatures, CreatureDisplayModelMetadataFeature metadata, SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        spells.System.TransformDisplayResolver = (_, entry) =>
        {
            if (creatures.Content.FindTemplate(entry) is not { } template)
            {
                return new TransformDisplay(Creature.DisplayIdBox, 1.0f);
            }

            uint display = Creature.ChooseDisplayId(template, Random.Shared);
            if (display == 0)
            {
                return new TransformDisplay(Creature.DisplayIdBox, 1.0f);
            }

            float scale = metadata.Content.Find(display)?.NativeScale
                ?? (template.Scale > 0 ? template.Scale : 1.0f);
            return new TransformDisplay(display, scale);
        };

        spells.System.DisplayModelResolver = displayId =>
        {
            CreatureModelInfo? addon = creatures.Content.FindModel(displayId);
            CreatureDisplayModelMetadata? dbc = metadata.Content.Find(displayId);
            if (addon is null && dbc is null) return null;
            float nativeScale = dbc?.NativeScale ?? 1.0f;
            return new DisplayModelGeometry(nativeScale, addon?.BoundingRadius ?? 0, addon?.CombatReach ?? 0,
                dbc?.CollisionHeight ?? 0, dbc?.ModelScale ?? 1.0f, dbc?.HasModelData ?? false);
        };
    }
}
