using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The powers of a hunter's pet (vmangos Unit::GetCreatePowers, Pet::LoadPetFromDB, Pet::CreateBaseAtCreature, Pet::RegenerateFocus):
/// the power type is Focus, the maximum focus is 100 and the maximum happiness 1 050 000, the class byte is Warrior and the gender None.
/// Focus regenerates 25 every 4 s, scaled by each MOD_POWER_REGEN_PERCENT (focus) aura. Nothing here is touched by a level-up
/// (Pet::InitStatsForLevel leaves a hunter pet's power type, focus and happiness alone: Creature::SetInitCreaturePowerType returns early
/// for a HUNTER_PET, Creature.cpp:1757-1758). World thread only; the helpers hold no state.
/// </summary>
internal static class PetPower
{
    /// <summary>vmangos Unit::GetCreatePowers(POWER_FOCUS) for a hunter pet (Unit.cpp:8255-8256).</summary>
    public const uint FocusMax = 100;

    /// <summary>vmangos Pet::m_focusTimer period (Pet.cpp:745).</summary>
    public const int FocusPeriodMs = 4_000;

    /// <summary>The focus a tick gives before the regeneration auras (Pet::RegenerateFocus, Pet.cpp:774: 25 × Rate.Focus).</summary>
    public const float FocusPerTick = 25f;

    /// <summary>UNIT_BYTES_0 gender byte value GENDER_NONE (vmangos Pet.cpp:1298).</summary>
    private const byte GenderNone = 2;

    /// <summary>Whether <paramref name="pet"/> is the permanent pet of a hunter (vmangos GetPetType() == HUNTER_PET).</summary>
    public static bool IsHunterPet(Creature pet, Unit owner)
        => owner is Player { Class: Class.Hunter } && pet.Summon is { Kind: SummonKind.Pet };

    /// <summary>
    /// vmangos Pet::LoadPetFromDB (Pet.cpp:382-384) and CreateBaseAtCreature (Pet.cpp:1245-1258): the happiness maximum and current value,
    /// the Focus power type with its maximum, the focus full (Unit::SetPowerType(POWER_FOCUS), Unit.cpp:4414-4417), the Warrior class byte
    /// and the None gender.
    /// </summary>
    public static void InitHunterPet(Creature pet, uint happiness)
    {
        pet.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        pet.SetByte(UpdateFields.UnitFieldBytes0, 2, GenderNone);
        pet.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Happiness, SummonService.HappinessMax);
        pet.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Happiness, Math.Min(happiness, SummonService.HappinessMax));
        pet.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Focus);
        pet.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Focus, FocusMax);
        pet.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Focus, FocusMax);
    }

    /// <summary>
    /// vmangos Pet::RegenerateFocus (Pet.cpp:774-781): <c>25 × rate</c>, multiplied by <c>(amount + 100) / 100</c> for each focus
    /// MOD_POWER_REGEN_PERCENT aura, truncated to whole points.
    /// </summary>
    public static uint FocusGain(IEnumerable<int> regenPercentAmounts, float rate = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(regenPercentAmounts);
        float add = FocusPerTick * rate;
        foreach (int amount in regenPercentAmounts)
        {
            add *= (amount + 100) / 100.0f;
        }

        return (uint)Math.Max(0, (int)add);
    }

    /// <summary>The amounts of the focus MOD_POWER_REGEN_PERCENT auras on <paramref name="pet"/> (none without a spell system).</summary>
    private static List<int> FocusRegenPercentAmounts(Creature pet, SpellSystem? spells)
    {
        List<int> amounts = [];
        if (spells is null)
        {
            return amounts;
        }

        foreach (SpellAuraHolder holder in spells.GetAuras(pet))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.AuraSpan)
            {
                if (aura is { Type: AuraType.ModPowerRegenPercent } && aura.MiscValue == (int)PowerType.Focus)
                {
                    amounts.Add(aura.Amount);
                }
            }
        }

        return amounts;
    }

    /// <summary>
    /// The focus half of vmangos Pet::Update for a hunter pet (Pet.cpp:738-745): every <see cref="FocusPeriodMs"/> a pet below its maximum
    /// regenerates <see cref="FocusGain"/>; the timer restarts at the period whether or not focus was needed.
    /// </summary>
    internal static void TickFocus(Creature pet, CharmInfo charm, uint diffMs, SpellSystem? spells)
    {
        charm.FocusTimerMs -= (int)diffMs;
        if (charm.FocusTimerMs > 0)
        {
            return;
        }

        charm.FocusTimerMs = FocusPeriodMs;
        int index = UpdateFields.UnitFieldPower1 + (int)PowerType.Focus;
        uint current = pet.GetUInt32(index);
        uint max = pet.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Focus);
        if (current >= max)
        {
            return;
        }

        uint gain = FocusGain(FocusRegenPercentAmounts(pet, spells));
        pet.SetUInt32(index, (uint)Math.Min((ulong)max, (ulong)current + gain));
    }
}
