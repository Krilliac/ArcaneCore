using ArcaneCore.Game.Combat;
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

    /// <summary>vmangos Pet::RegenerateAll: m_focusTimer is reset to 4000 ms after each focus tick (Pet.cpp:743-749).</summary>
    public const int FocusPeriodMs = 4_000;

    /// <summary>GetCreatePowers(POWER_FOCUS): the maximum and starting focus of a hunter pet (Unit::SetPowerType, Unit.cpp:4414-4416).</summary>
    public const uint FocusMax = 100;

    /// <summary>The focus a tick gives before Rate.Focus and the regen auras: Pet::RegenerateFocus "25 * CONFIG_FLOAT_RATE_POWER_FOCUS" (Pet.cpp:774). Rate.Focus is 1 (mangosd.conf.dist.in:2797); ArcaneCore has no option for it.</summary>
    public const float FocusPerTick = 25.0f;

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

        TickFocus(pet, charm, diffMs);

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

    /// <summary>
    /// vmangos Pet::RegenerateAll + Pet::RegenerateFocus (Pet.cpp:743-749, 766-781): every 4 s a hunter pet below its maximum gains
    /// 25 × Rate.Focus focus, times (amount + 100) / 100 of each SPELL_AURA_MOD_POWER_REGEN_PERCENT aura on focus, truncated to an integer.
    /// World thread; no allocation.
    /// </summary>
    private static void TickFocus(Creature pet, CharmInfo charm, uint diffMs)
    {
        if (charm.FocusTimerMs > (int)diffMs)
        {
            charm.FocusTimerMs -= (int)diffMs;
            return;
        }

        charm.FocusTimerMs = FocusPeriodMs;
        uint current = pet.GetUInt32(UpdateFields.UnitFieldPower3);
        uint max = pet.GetUInt32(UpdateFields.UnitFieldMaxpower3);
        if (current >= max)
        {
            return;
        }

        float factor = pet.Map is { } map ? CombatEnvironment.For(map.World).GetPowerRegenFactor(pet, PowerType.Focus) : 1.0f;
        int add = (int)(FocusPerTick * factor);
        pet.SetUInt32(UpdateFields.UnitFieldPower3, (uint)Math.Clamp((long)current + add, 0L, max));
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
