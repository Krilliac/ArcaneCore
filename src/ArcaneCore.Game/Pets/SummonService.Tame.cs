using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// SPELL_EFFECT_TAMECREATURE (55) and SPELL_EFFECT_FEED_PET (101), the hunter's Tame Beast and Feed Pet (vmangos Spell::EffectTameCreature,
/// SpellEffects.cpp:3106-3166; Spell::EffectFeedPet, :5091-5131; their cast checks, Spell.cpp:5791-5806 and 5864-5887, CheckTamingSpell :6836-6859).
/// </summary>
public sealed partial class SummonService
{
    /// <summary>CREATURE_TYPEFLAGS_TAMEABLE (vmangos CreatureInfo::IsTameable).</summary>
    public const uint TypeFlagTameable = 0x1;

    /// <summary>CREATURE_TYPE_BEAST.</summary>
    public const uint CreatureTypeBeast = 1;

    /// <summary>The happiness a freshly tamed pet starts with (vmangos Pet::CreateBaseAtCreature, Pet.cpp:1246).</summary>
    public const uint TamedHappiness = 166_500;

    /// <summary>
    /// CreatureFamily.dbc petFoodMask by family id (vmangos Pet::HaveInDiet). Null (no client data loaded) skips the diet check; the food type
    /// must still be set and the food level must still give a benefit.
    /// </summary>
    public Func<uint, uint?>? PetFoodMask { get; set; }

    private void InstallTaming(SpellSystem spells)
    {
        spells.RegisterEffectCheck(SpellEffectName.Tamecreature, CheckTame);
        spells.RegisterEffect(SpellEffectName.Tamecreature, context =>
        {
            if (context.Caster is Player hunter && context.Target is Creature beast)
            {
                TameCreature(hunter, beast, context.Spell.Id);
            }
        });
        spells.RegisterEffectCheck(SpellEffectName.FeedPet, CheckFeedPet);
        spells.RegisterEffect(SpellEffectName.FeedPet, EffectFeedPet);
    }

    /// <summary>vmangos Spell::CheckTamingSpell: the failure reason, or null when the target can be tamed.</summary>
    public PetTameFailureReason? CheckTaming(Player caster, Unit? target, bool gm = false)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (caster.Class != Class.Hunter && !gm)
        {
            return PetTameFailureReason.UnitsCantTame;
        }

        if (!caster.PetGuid.IsEmpty || caster.GetUInt64(UpdateFields.UnitFieldCharm) != 0
            || (TryGetCachedCurrentPet(caster, out PersistentPetSnapshot saved) && saved.PetNumber != 0))
        {
            return PetTameFailureReason.AnotherSummonActive;
        }

        if (target is not Creature creature)
        {
            return PetTameFailureReason.InvalidCreature;
        }

        if (creature.Summon is not null || creature.GetUInt64(UpdateFields.UnitFieldCharmedby) != 0)
        {
            return PetTameFailureReason.CreatureAlreadyOwned;
        }

        if (creature.Level > caster.Level && !gm)
        {
            return PetTameFailureReason.TooHighLevel;
        }

        return IsTameable(creature) ? null : PetTameFailureReason.NotTameable;
    }

    /// <summary>vmangos CreatureInfo::IsTameable: a beast with CREATURE_TYPEFLAGS_TAMEABLE.</summary>
    public static bool IsTameable(Creature creature)
        => creature.Template.CreatureType == CreatureTypeBeast && (creature.Template.TypeFlags & TypeFlagTameable) != 0;

    private SpellCastResult CheckTame(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player player)
        {
            return SpellCastResult.BadTargets;
        }

        // m_triggeredBySpellInfo == nullptr is CheckTamingSpell's gm flag: the cast Tame Beast 13481 is triggered by the 1515 channel.
        if (CheckTaming(player, context.UnitTarget, gm: !context.Triggered) is { } failure)
        {
            player.Session.Send(WorldOpcode.SmsgPetTameFailure, PetPackets.BuildTameFailure(failure));
            return SpellCastResult.DontReport;
        }

        return SpellCastResult.CastOk;
    }

    /// <summary>
    /// vmangos Spell::EffectTameCreature: the creature becomes the hunter's current pet at its own level and place (full health, the start
    /// happiness, defensive), the wild creature despawns (a database spawn respawns later) and the new pet is saved as current. Null when it
    /// could not be made.
    /// </summary>
    public Creature? TameCreature(Player owner, Creature target, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAlive || !owner.PetGuid.IsEmpty || owner.Class != Class.Hunter || target.Summon is not null)
        {
            return null;
        }

        var snapshot = new PersistentPetSnapshot((int)owner.Guid.Low, NextPetNumber(), target.Entry, (byte)target.Level, 0, uint.MaxValue, uint.MaxValue,
            TamedHappiness, (byte)ReactState.Defensive, [], []);
        if (SpawnCached(owner, snapshot, revive: false) is not { } pet)
        {
            return null;
        }

        pet.SetUInt32(UpdateFields.UnitCreatedBySpell, spellId);
        pet.Health = pet.MaxHealth;
        if (target.System is CreatureMapSystem creatures)
        {
            creatures.ForcedDespawn(target, 0); // "kill" original creature
        }

        QueueCurrentPetSave(owner);
        return pet;
    }

    /// <summary>vmangos Pet::GetCurrentFoodBenefitLevel (Pet.cpp:1508-1522): the happiness a food of <paramref name="itemLevel"/> gives the pet.</summary>
    public static int FoodBenefit(uint petLevel, uint itemLevel)
        => petLevel <= itemLevel + 5 ? 35_000 : petLevel <= itemLevel + 10 ? 17_000 : petLevel <= itemLevel + 14 ? 8_000 : 0;

    /// <summary>vmangos Pet::HaveInDiet: a food type in the family's diet (unknown diet: any food).</summary>
    public bool HaveInDiet(Creature pet, Item food)
    {
        uint foodType = food.Template.FoodType;
        if (foodType == 0)
        {
            return false;
        }

        if (PetFoodMask is not { } masks)
        {
            return true;
        }

        return masks(pet.Template.Family) is { } diet && (diet & (1u << (int)(foodType - 1))) != 0;
    }

    private static Item? FoodItem(Player player, SpellCastTargets targets)
        => targets.Item.IsEmpty ? null : player.Inventory.GetItemByGuid(targets.Item);

    private Creature? LivePet(Player player)
        => player.Map?.FindObject(player.PetGuid) is Creature { Summon.Kind: SummonKind.Pet } pet && pet.OwnerGuid == player.Guid ? pet : null;

    private SpellCastResult CheckFeedPet(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player player || FoodItem(player, context.Targets) is not { } food)
        {
            return SpellCastResult.BadTargets;
        }

        if (LivePet(player) is not { } pet)
        {
            return SpellCastResult.NoPet;
        }

        if (!HaveInDiet(pet, food))
        {
            return SpellCastResult.WrongPetFood;
        }

        if (FoodBenefit(pet.Level, food.Template.ItemLevel) == 0)
        {
            return SpellCastResult.FoodLowlevel;
        }

        return pet.Combat.IsInCombat ? SpellCastResult.AffectingCombat : SpellCastResult.CastOk;
    }

    /// <summary>vmangos Spell::EffectFeedPet: one food item goes, and the owner casts the effect's trigger spell (Feed Pet Effect) with the benefit.</summary>
    private void EffectFeedPet(SpellEffectContext context)
    {
        if (context.Caster is not Player player || FoodItem(player, context.Cast.Targets) is not { } food
            || LivePet(player) is not { IsAlive: true } pet)
        {
            return;
        }

        int benefit = FoodBenefit(pet.Level, food.Template.ItemLevel);
        if (benefit <= 0)
        {
            return;
        }

        player.Inventory.DestroyItemCount(food, 1);
        context.System.CastCustomSpell(player, context.Effect.TriggerSpell, SpellCastTargets.ForSelf(), benefit);
    }
}
