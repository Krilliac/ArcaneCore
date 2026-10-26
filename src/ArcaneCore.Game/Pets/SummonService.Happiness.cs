using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Hunter pet happiness and the loyalty it drives (vmangos Pet::Update, Pet::LooseHappiness, Pet::GetHappinessState,
/// Pet::TickLoyaltyChange, Pet::KillLoyaltyBonus). Happiness is POWER_HAPPINESS (UNIT_FIELD_POWER5, 0..1 050 000); every 7.5 s
/// it drops by (140 &gt;&gt; loyalty level) × 125, half again in combat; every 12 s loyalty moves +20 happy, +10 content, −20 unhappy.
/// A Rebellious pet whose loyalty falls below zero runs away: SMSG_PET_BROKEN, deleted (PET_SAVE_AS_DELETED).
/// </summary>
public sealed partial class SummonService
{
    public const int HappinessPeriodMs = 7_500;
    public const int LoyaltyPeriodMs = 12_000;
    public const uint HappinessMax = 1_050_000;

    /// <summary>vmangos HAPPINESS_LEVEL_SIZE (Pet.h:130): the happiness bar is split at 333000 and 666000, not at a third of the maximum.</summary>
    public const uint HappinessLevelSize = 333_000;

    public enum HappinessState : byte
    {
        Unhappy = 1,
        Content = 2,
        Happy = 3,
    }

    /// <summary>vmangos Pet::GetHappinessState.</summary>
    public static HappinessState StateOf(uint happiness)
        => happiness < HappinessLevelSize ? HappinessState.Unhappy : happiness >= HappinessLevelSize * 2 ? HappinessState.Happy : HappinessState.Content;

    /// <summary>vmangos Pet::LooseHappiness: the happiness a tick takes at <paramref name="loyaltyLevel"/> (×1.5 in combat).</summary>
    public static int HappinessLoss(int loyaltyLevel, bool inCombat)
    {
        int loss = (140 >> Math.Clamp(loyaltyLevel, 1, 6)) * 125;
        return inCombat ? (int)(loss * 1.5) : loss;
    }

    /// <summary>vmangos Pet::TickLoyaltyChange: the loyalty points a tick gives in a happiness state.</summary>
    public static int LoyaltyTick(HappinessState state) => state switch
    {
        HappinessState.Happy => 20,
        HappinessState.Content => 10,
        _ => -20,
    };

    /// <summary>vmangos Pet::KillLoyaltyBonus: ((100 − victim level) / 10) + (6 − loyalty level); nothing above level 100.</summary>
    public static int KillLoyaltyBonus(uint victimLevel, int loyaltyLevel)
        => victimLevel > 100 ? 0 : (int)((100 - victimLevel) / 10) + (6 - loyaltyLevel);

    /// <summary>
    /// One pet update of a live hunter pet. Returns false when the pet ran away (it is then deleted and unsummoned).
    /// </summary>
    internal bool TickHappiness(Creature pet, Player owner, uint diffMs)
    {
        if (pet.Summon?.Charm is not { } charm || !pet.IsAlive)
        {
            return true;
        }

        charm.HappinessTimerMs -= (int)diffMs;
        if (charm.HappinessTimerMs <= 0)
        {
            charm.HappinessTimerMs += HappinessPeriodMs;
            uint happiness = pet.GetUInt32(UpdateFields.UnitFieldPower5);
            if (happiness > 0)
            {
                int loss = HappinessLoss(PetLoyalty.Level(pet), pet.Combat.IsInCombat);
                pet.SetUInt32(UpdateFields.UnitFieldPower5, (uint)Math.Max(0, (long)happiness - loss));
            }
        }

        charm.LoyaltyTimerMs -= (int)diffMs;
        if (charm.LoyaltyTimerMs <= 0)
        {
            charm.LoyaltyTimerMs += LoyaltyPeriodMs;
            if (!ModifyLoyalty(pet, owner, LoyaltyTick(StateOf(pet.GetUInt32(UpdateFields.UnitFieldPower5)))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Pet::KillLoyaltyBonus for the owner's live hunter pet after a kill worth experience.</summary>
    internal void GrantKillLoyalty(Creature pet, Player owner, uint victimLevel)
    {
        if (owner.Class == Class.Hunter && pet.IsAlive && pet.Summon is { Kind: SummonKind.Pet, Charm: not null })
        {
            ModifyLoyalty(pet, owner, KillLoyaltyBonus(victimLevel, PetLoyalty.Level(pet)));
        }
    }

    /// <summary><see cref="PetLoyalty.Modify"/>, and the runaway when it fails (vmangos Pet::ModifyLoyalty: SMSG_PET_BROKEN, Unsummon(PET_SAVE_AS_DELETED)).</summary>
    private bool ModifyLoyalty(Creature pet, Player owner, int add)
    {
        if (PetLoyalty.Modify(pet, add))
        {
            return true;
        }

        // vmangos Pet::ModifyLoyalty: the pet "runs away" (SMSG_PET_BROKEN, Unsummon(PET_SAVE_AS_DELETED)). It is deleted for good, so say so.
        _logger.LogInformation("hunter pet ran away: character {CharacterId} pet number {PetNumber} entry {Entry} loyalty level {LoyaltyLevel} happiness {Happiness}",
            owner.Guid.Low, pet.Summon?.Charm?.PetNumber ?? 0, pet.Entry, PetLoyalty.Level(pet), pet.GetUInt32(UpdateFields.UnitFieldPower5));
        owner.Session.Send(WorldOpcode.SmsgPetBroken, ReadOnlySpan<byte>.Empty);
        QueueDeletePet(owner);
        ForgetPetForSpiritHealer(owner);
        Unsummon(pet, "loyalty runaway");
        return false;
    }
}
