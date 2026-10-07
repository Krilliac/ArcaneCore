using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Features;
using ArcaneCore.World.Pets;

namespace ArcaneCore.World.Social;

/// <summary>Bridges the pet rename producer to grouped out-of-range member stats.</summary>
public sealed class GroupPetNameFeature(PetsFeature pets, SocialFeature social) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        pets.Controller.PetNameChanged += player => social.Context.Groups.MarkPetNameChanged(player);
    }
}
