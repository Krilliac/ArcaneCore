using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Hunter pet experience (vmangos Pet::GivePetXP, ObjectMgr::GetXPForPetLevel with PET_XP_FACTOR, and the pet share of
/// Player::RewardSinglePlayerAtKill / Group::RewardGroupAtKill). A pet levels up to its owner's level at most; each level runs
/// <see cref="PetLoyalty.GivePetLevel"/> (training points) and Pet::InitStatsForLevel.
/// </summary>
public sealed partial class SummonService
{
    /// <summary>PET_XP_FACTOR (vmangos Pet.h): a pet needs 5% of the player experience of its level.</summary>
    public const float PetXpFactor = 0.05f;

    /// <summary>The highest level a pet can reach regardless of its owner (CONFIG_UINT32_MAX_PLAYER_LEVEL).</summary>
    public uint MaxPetLevel { get; set; } = 60;

    /// <summary>ObjectMgr::GetXPForPetLevel: uint32(GetXPForLevel(level) * PET_XP_FACTOR).</summary>
    public static uint PetXpForLevel(uint level) => (uint)(PlayerXpTable.XpForLevel(level) * PetXpFactor);

    /// <summary>
    /// vmangos Pet::GivePetXP: a live hunter pet below min(max level, owner level) gains <paramref name="xp"/>; every full
    /// UNIT_FIELD_PETNEXTLEVELEXP is a level. At the cap the experience is 0. Returns the levels gained.
    /// </summary>
    public int GivePetXp(Creature pet, uint xp)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (xp == 0 || !pet.IsAlive || pet.Summon is not { Kind: SummonKind.Pet, Charm: not null } || pet.GetOwner() is not Player owner)
        {
            return 0;
        }

        uint maxLevel = Math.Min(MaxPetLevel, (uint)owner.Level);
        uint level = pet.Level;
        if (level >= maxLevel)
        {
            return 0;
        }

        uint next = PetXpForLevel(level);
        pet.SetUInt32(UpdateFields.UnitFieldPetnextlevelexp, next);
        ulong newXp = (ulong)pet.GetUInt32(UpdateFields.UnitFieldPetexperience) + xp;
        int gained = 0;
        while (next != 0 && newXp >= next && level < maxLevel)
        {
            newXp -= next;
            GivePetLevel(pet, owner, (int)level + 1);
            gained++;
            level = pet.Level;
            next = pet.GetUInt32(UpdateFields.UnitFieldPetnextlevelexp);
        }

        pet.SetUInt32(UpdateFields.UnitFieldPetexperience, level < maxLevel ? (uint)Math.Min(newXp, uint.MaxValue) : 0);
        if (gained > 0)
        {
            QueueCurrentPetSave(owner);
        }

        return gained;
    }

    /// <summary>
    /// vmangos Pet::GivePetLevel: the experience fields (via <see cref="PetLoyalty.GivePetLevel"/>, which also adds the training points)
    /// and Pet::InitStatsForLevel for the new level.
    /// </summary>
    public void GivePetLevel(Creature pet, Player owner, int level)
    {
        PetLoyalty.GivePetLevel(pet, level, PetXpForLevel((uint)level));
        PetInitializer.InitStatsForLevel(pet, owner, level, Content); // clears the unit flags and sets the level again, as vmangos
    }

    /// <summary>
    /// The pet share of a kill. Alone (RewardSinglePlayerAtKill) the pet gets MaNGOS::XP::Gain for its own level; in a group
    /// (RewardGroupAtKill) each member's pet gets half of what the member was given (itr_xp / 2). <paramref name="granted"/> is
    /// <see cref="KillRewards.AwardExperience"/>'s result; a member given nothing (a totem, a PvP or grey kill) gives his pet nothing.
    /// </summary>
    public void AwardKillExperience(IReadOnlyList<Player> recipients, IReadOnlyList<uint> granted, Creature victim, bool nonRaidDungeon,
        float killRate, float eliteRate)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(victim);
        for (int i = 0; i < recipients.Count && i < granted.Count; i++)
        {
            Player member = recipients[i];
            if (granted[i] == 0 || member.Map?.FindObject(member.PetGuid) is not Creature pet || pet.OwnerGuid != member.Guid)
            {
                continue;
            }

            uint xp = recipients.Count == 1
                ? ExperienceFormulas.KillGain(pet.Level, victim.Level, KillRewards.IsElite(victim), nonRaidDungeon, killRate * victim.KillXpModifier, eliteRate)
                : granted[i] / 2;
            GivePetXp(pet, xp);
        }
    }
}
