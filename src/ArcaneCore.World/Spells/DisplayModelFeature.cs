using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Gives the spell system the display model data (creature_model_info bounding radius and combat reach, CreatureDisplayInfo scale and
/// model height) that vmangos <c>Unit::UpdateModelData</c> reads after a display or scale change (ModScale and Transform auras).
/// </summary>
public sealed class DisplayModelFeature(CreatureWorldFeature creatures, CreatureDisplayModelMetadataFeature metadata, SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
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
