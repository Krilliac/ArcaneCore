using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

// The owner's pet while the owner rides (vmangos Player::Mount / Player::Unmount, Player.cpp:18204-18216 and 18248-18251, with the default
// PetUnsummonAtMount = 0, World.cpp:820, mangosd.conf.dist.in:1236-1239): a mount aura leaves the pet out with its bar greyed out, and
// dismounting gives the bar back, or brings back the pet that was put away when there is none. MountService raises the SpellSystem events.
public sealed partial class SummonService
{
    /// <summary>
    /// vmangos Pet::SetEnabled (Pet.cpp:2362-2377): grey the pet's bar out or back in and tell the owner with SMSG_PET_MODE. A disabled pet
    /// ignores the owner's commands (HandlePetAction) and its AI does not pick fights (PetAI); a unit without a charm info is left alone.
    /// </summary>
    public void SetPetEnabled(Creature pet, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Summon?.Charm is not { } charm)
        {
            return;
        }

        charm.Enabled = enabled;
        if (pet.GetOwner() is Player owner)
        {
            owner.Session.Send(WorldOpcode.SmsgPetMode, PetPackets.BuildPetMode(pet, charm));
        }
    }

    /// <summary>Player::Mount for a mount aura (:18204-18216): without PetUnsummonAtMount even a permanent pet stays out, greyed out.</summary>
    private void OnOwnerMounted(Player owner)
    {
        if (owner.GetPet() is { } pet)
        {
            SetPetEnabled(pet, false);
        }
    }

    /// <summary>Player::Unmount (:18248-18251): the pet gets its bar back; without one, ResummonPetTemporaryUnSummonedIfAny.</summary>
    private void OnOwnerDismounted(Player owner)
    {
        if (owner.GetPet() is { } pet)
        {
            SetPetEnabled(pet, true);
        }
        else
        {
            ResummonTemporarilyUnsummonedPet(owner);
        }
    }

    /// <summary>
    /// Pet::LoadPetFromDB (Pet.cpp:401-404): a pet that comes back while its owner rides starts greyed out, before its bar is sent
    /// (Player::PetSpellInitialize carries the enabled byte); no SMSG_PET_MODE is sent for it. A taxi flight's display does not count:
    /// vmangos loads no pet during a flight (Pet.cpp:216-221, IsPetNeedBeTemporaryUnsummoned's IsTaxiFlying, Player.cpp:20944-20945),
    /// and TaxiFlightSystem lands without a dismount event, so a pet greyed out then (a hunter pet loaded at login while the saved
    /// route resumes) would stay greyed out after landing.
    /// </summary>
    private static void GreyOutForMountedOwner(Player owner, CharmInfo charm)
    {
        if (MountService.IsMounted(owner) && (owner.UnitFlags & UnitFlags.TaxiFlight) == 0)
        {
            charm.Enabled = false;
        }
    }
}
