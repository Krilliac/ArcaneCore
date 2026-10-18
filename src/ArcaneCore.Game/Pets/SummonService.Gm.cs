using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Pets;

/// <summary>The seams the GM <c>.pet</c> commands need.</summary>
public sealed partial class SummonService
{
    /// <summary>vmangos Creature::IsPet for a summoned pet with a charm (a hunter pet or a warlock demon), not a guardian or minipet.</summary>
    public static bool IsPet(Creature creature) => creature?.Summon is { Kind: SummonKind.Pet, Charm: not null };

    /// <summary>
    /// vmangos HandlePetLoyaltyCommand -> Pet::ModifyLoyalty: add <paramref name="add"/> loyalty points to a live hunter pet. A Rebellious pet
    /// pushed below zero runs away as it would on its own (SMSG_PET_BROKEN, deleted). Returns false when it ran away.
    /// </summary>
    public bool ModifyLoyaltyByCommand(Creature pet, Player owner, int add)
    {
        ArgumentNullException.ThrowIfNull(pet);
        ArgumentNullException.ThrowIfNull(owner);
        return ModifyLoyalty(pet, owner, add);
    }
}