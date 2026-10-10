using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Hunter pet loyalty (vmangos Pet.cpp). The level (1 Rebellious … 6 Best Friend) is UNIT_FIELD_BYTES_1 byte 1 (Pet::SetLoyaltyLevel, :879-882);
/// the points move between <see cref="StartPoints"/> and <see cref="LevelUpPoints"/> (Pet::ModifyLoyalty, :795-837), and each level gained or lost
/// adds or takes the pet's level in training points. Training points show as UNIT_TRAINING_POINTS (Pet::SetTP / GetDispTP, :983-996).
/// The loyalty ticks (happiness decay, kill bonus) belong to the pet happiness system, which ArcaneCore does not run yet.
/// </summary>
public static class PetLoyalty
{
    public const byte Rebellious = 1;
    public const byte BestFriend = 6;

    private static readonly int[] LevelUp = [5500, 11500, 17000, 23500, 31000, 39500];
    private static readonly int[] Start = [2000, 4500, 7000, 10000, 13500, 17500];

    public static int LevelUpPoints(int level) => LevelUp[Math.Clamp(level, 1, 6) - 1];

    public static int StartPoints(int level) => Start[Math.Clamp(level, 1, 6) - 1];

    public static byte Level(Creature pet) => pet.GetByte(UpdateFields.UnitFieldBytes1, 1);

    /// <summary>Pet::CreateBaseAtCreature (Pet.cpp:1253, 1269): a new beast pet is Rebellious with 1000 points.</summary>
    public static void InitNew(Creature pet) => Restore(pet, Rebellious, 1000, 0);

    public static void Restore(Creature pet, byte level, int points, int trainingPoints)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Summon?.Charm is not { } charm)
        {
            return;
        }

        pet.SetByte(UpdateFields.UnitFieldBytes1, 1, Math.Clamp(level, Rebellious, BestFriend));
        charm.LoyaltyPoints = points;
        SetTrainingPoints(pet, trainingPoints);
    }

    public static void SetTrainingPoints(Creature pet, int trainingPoints)
    {
        if (pet.Summon?.Charm is not { } charm)
        {
            return;
        }

        charm.TrainingPoints = trainingPoints;
        pet.SetUInt32(UpdateFields.UnitTrainingPoints, (uint)DisplayTrainingPoints(trainingPoints));
    }

    /// <summary>Pet::GetDispTP.</summary>
    public static int DisplayTrainingPoints(int trainingPoints) => trainingPoints < 0 ? -trainingPoints : -(trainingPoints + 1);

    /// <summary>
    /// Pet::ModifyLoyalty without the RATE_LOYALTY multiplier (1 by default). Returns false when the pet ran away: a Rebellious pet whose points
    /// fall below zero (the caller unsummons it as deleted and sends SMSG_PET_BROKEN).
    /// </summary>
    public static bool Modify(Creature pet, int add)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (pet.Summon?.Charm is not { } charm)
        {
            return true;
        }

        int level = Level(pet);
        if (level >= BestFriend && add + charm.LoyaltyPoints > LevelUpPoints(level))
        {
            return true;
        }

        charm.LoyaltyPoints += add;
        if (charm.LoyaltyPoints < 0)
        {
            if (level <= Rebellious)
            {
                charm.LoyaltyPoints = 0;
                return false;
            }

            level--;
            pet.SetByte(UpdateFields.UnitFieldBytes1, 1, (byte)level);
            charm.LoyaltyPoints = StartPoints(level);
            SetTrainingPoints(pet, charm.TrainingPoints - (int)pet.Level);
        }
        else if (charm.LoyaltyPoints > LevelUpPoints(level))
        {
            level++;
            pet.SetByte(UpdateFields.UnitFieldBytes1, 1, (byte)level);
            charm.LoyaltyPoints = StartPoints(level);
            SetTrainingPoints(pet, charm.TrainingPoints + (int)pet.Level);
        }

        return true;
    }

    /// <summary>Spell::EffectTameCreature (SpellEffects.cpp:3151-3154): raise a new pet to the configured default loyalty.</summary>
    public static void RaiseTo(Creature pet, byte target)
    {
        target = Math.Clamp(target, Rebellious, BestFriend);
        for (int guard = 0; Level(pet) < target && guard < 16; guard++)
        {
            Modify(pet, StartPoints(target));
        }
    }
}
