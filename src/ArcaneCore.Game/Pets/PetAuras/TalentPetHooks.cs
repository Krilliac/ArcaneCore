using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;

namespace ArcaneCore.Game.Pets.PetAuras;

/// <summary>
/// What a talent change does to the owner's pet (world thread):
/// <list type="bullet">
/// <item><see cref="TalentService.TalentsReset"/>: the pet is removed (vmangos Player::ResetTalents ends with <c>RemovePet(PET_SAVE_REAGENTS)</c>,
/// Player.cpp:4144-4146, after the talents are unlearned "to allow removing of talent related, pet affecting auras"). A hunter's pet is saved out of
/// its slot first, as Dismiss Pet saves it (PET_SAVE_REAGENTS is PET_SAVE_NOT_IN_SLOT with the reagent return), so Call Pet brings it back.</item>
/// <item><see cref="TalentService.TalentLearned"/>: the owner's talent pet auras the pet does not carry are cast on it (mangos-classic
/// WorldSession::HandleLearnTalentOpcode, <c>GetPet()->CastOwnerTalentAuras()</c>).</item>
/// </list>
/// LIMITS: a warlock's demon removed by a respec does not give its soul shard back (vmangos returns the summon spell's reagents to the player); the
/// summon here does not charge the shard either (SummonService.Demons.cs).
/// </summary>
public sealed class TalentPetHooks : IDisposable
{
    private readonly TalentService _talents;
    private readonly SummonService _pets;
    private readonly PetAuraService _auras;

    private TalentPetHooks(TalentService talents, SummonService pets, SpellSystem spells)
    {
        _talents = talents;
        _pets = pets;
        _auras = PetAuraService.For(spells);
        talents.TalentsReset += OnTalentsReset;
        talents.TalentLearned += OnTalentLearned;
    }

    /// <summary>Subscribe to <paramref name="talents"/>' events; dispose to unsubscribe.</summary>
    public static TalentPetHooks Attach(TalentService talents, SummonService pets, SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(talents);
        ArgumentNullException.ThrowIfNull(pets);
        ArgumentNullException.ThrowIfNull(spells);
        return new TalentPetHooks(talents, pets, spells);
    }

    /// <summary>vmangos Player::RemovePet(PET_SAVE_REAGENTS) after a respec.</summary>
    public void OnTalentsReset(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.GetPet() is not { Summon.Kind: SummonKind.Pet } pet || pet.OwnerGuid != player.Guid)
        {
            return;
        }

        if (player.Class == Class.Hunter && (!_pets.DetachedPersistenceConfigured || _pets.DetachedPersistenceSupported))
        {
            _pets.QueueDetachedPetSave(player); // read from the live pet, so before it goes
        }

        _pets.Unsummon(pet);
    }

    /// <summary>The owner learned a talent rank: its pet takes the talent pet auras it is missing.</summary>
    public void OnTalentLearned(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _auras.RecastMissing(player);
    }

    public void Dispose()
    {
        _talents.TalentsReset -= OnTalentsReset;
        _talents.TalentLearned -= OnTalentLearned;
    }
}
