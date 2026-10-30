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
    /// The pet share of a kill. Alone (RewardSinglePlayerAtKill) the pet gets MaNGOS::XP::Gain for its own level when the kill is worth
    /// experience to the owner; in a group (RewardGroupAtKill) each member's pet gets half the member's share before rested bonus
    /// (itr_xp / 2). Shares come from <see cref="KillExperience.Distribute"/> with the inputs <see cref="KillRewards.AwardExperience"/> uses,
    /// so an owner at the maximum level (who is given nothing himself) still levels his pet. Totems, PvP victims and CREATURE_STATIC_FLAG_NO_XP victims give nothing (Formulas.h:112).
    /// </summary>
    public void AwardKillExperience(IReadOnlyList<Player> recipients, Creature victim, bool nonRaidDungeon, float killRate, float eliteRate)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(victim);
        if (recipients.Count == 0 || Totems.TotemQuery.IsTotem(victim) || KillRewards.IsPvpVictim(victim) || KillRewards.GrantsNoExperience(victim))
        {
            return;
        }

        KillCandidate[] candidates = recipients
            .Select(p => new KillCandidate(p.Level, p.IsAlive, (p.Flags & PlayerFlags.Ghost) != 0)).ToArray();
        bool elite = KillRewards.IsElite(victim);
        float rate = killRate * victim.KillXpModifier;
        foreach (KillShare share in KillExperience.Distribute(candidates, victim.Level, elite, nonRaidDungeon, rate, eliteRate))
        {
            Player member = recipients[share.MemberIndex];
            if (share.Experience == 0 || member.Map?.FindObject(member.PetGuid) is not Creature pet || pet.OwnerGuid != member.Guid)
            {
                continue;
            }

            uint xp = recipients.Count == 1
                ? ExperienceFormulas.KillGain(pet.Level, victim.Level, elite, nonRaidDungeon, rate, eliteRate)
                : share.Experience / 2;
            GivePetXp(pet, xp);
            GrantKillLoyalty(pet, member, victim.Level); // Pet::KillLoyaltyBonus
        }
    }
}
