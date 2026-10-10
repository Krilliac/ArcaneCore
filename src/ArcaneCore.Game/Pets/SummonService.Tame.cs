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

    /// <summary>
    /// Beast-training rules (training-point costs, family skill lines, the four-active-spell limit; see <see cref="PetTraining"/>). Null (no
    /// SkillLineAbility reqtrainpoints or CreatureFamily skill lines loaded) keeps learning free of cost and of family checks.
    /// </summary>
    public PetTraining? Training { get; set; }

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
        spells.RegisterEffectCheck(SpellEffectName.LearnPetSpell, CheckLearnPetSpell);
        spells.RegisterEffect(SpellEffectName.LearnPetSpell, EffectLearnPetSpell);
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
            TamedHappiness, (byte)ReactState.Defensive, [], CreateSpellsFor(target.Entry));
        if (SpawnCached(owner, snapshot, revive: false) is not { } pet)
        {
            return null;
        }

        // SpellEffects.cpp:3151-3154: new pets start Rebellious (Pet::CreateBaseAtCreature) and are raised to the configured default loyalty.
        PetLoyalty.InitNew(pet);

        // SpellEffects.cpp:3142 InitPetCreateSpells sets the training points to minus the create spells' cost (Pet.cpp:2103) before the loyalty
        // raise above adds the pet's level for each level gained.
        if (Training is { } training)
        {
            PetLoyalty.SetTrainingPoints(pet, -training.CreateSpellsCost(Content.GetCreateSpells(target.Entry)));
        }

        PetLoyalty.RaiseTo(pet, _options.DefaultLoyalty);

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

    /// <summary>
    /// Pet::InitPetCreateSpells (Pet.cpp:2051-2104) for a tame: the petcreateinfo_spell row of the tamed creature's entry, each "learn" spell
    /// resolved to the spell it teaches. The owner's beast-training side (learning passives, AddTeachSpell) is not modelled; the training-point
    /// cost of these spells is charged by <see cref="TameCreature"/> through <see cref="Training"/>.
    /// </summary>
    internal PersistentPetSpell[] CreateSpellsFor(uint entry)
    {
        if (_spells is not { } spells)
        {
            return [];
        }

        var learned = new List<PersistentPetSpell>();
        foreach (uint spellId in Content.GetCreateSpells(entry))
        {
            if (spells.Store.Get(spellId) is not { } learn)
            {
                continue;
            }

            uint petSpell = learn.Effects[0].Effect is SpellEffectName.LearnSpell or SpellEffectName.LearnPetSpell ? learn.Effects[0].TriggerSpell : learn.Id;
            if (spells.Store.Get(petSpell) is { } spell && learned.All(s => s.SpellId != spell.Id))
            {
                learned.Add(new PersistentPetSpell(spell.Id, Autocast: false, Passive: spell.IsPassive));
            }
        }

        return [.. learned];
    }

    /// <summary>
    /// The cast check of SPELL_EFFECT_LEARN_PET_SPELL in vmangos order (Spell.cpp:5837-5862): NoPet, NotKnown, TooManySkills, Lowlevel (the
    /// teach spell's level above the pet's), TrainingPoints. Without <see cref="Training"/> the two training checks are skipped.
    /// </summary>
    private SpellCastResult CheckLearnPetSpell(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player player || LivePet(player) is not { } pet || pet.Summon?.Charm is not { } charm)
        {
            return SpellCastResult.NoPet;
        }

        uint learned = context.Effect.TriggerSpell;
        if (context.System.Store.Get(learned) is null)
        {
            return SpellCastResult.NotKnown;
        }

        if (Training is { } training && !training.CanTakeMoreActiveSpells(charm, learned))
        {
            return SpellCastResult.TooManySkills;
        }

        if (context.Spell.SpellLevel > pet.Level)
        {
            return SpellCastResult.Lowlevel;
        }

        return Training is { } costed && !PetTraining.HasPoints(charm.TrainingPoints, costed.Cost(charm, learned))
            ? SpellCastResult.TrainingPoints
            : SpellCastResult.CastOk;
    }

    /// <summary>
    /// SPELL_EFFECT_LEARN_PET_SPELL (57; vmangos Spell::EffectLearnPetSpell, SpellEffects.cpp:3329-3353): the caster's live pet learns the trigger
    /// spell, pays its training points (Pet::SetTP before LearnSpell), is saved and the owner gets SMSG_PET_SPELLS again. With
    /// <see cref="Training"/> the spell must pass Pet::CanLearnPetSpell, a higher rank replaces the known lower rank in its bar slot and a lower
    /// rank than one already known is ignored (Pet::AddSpell, Pet.cpp:1887-1975).
    /// </summary>
    private void EffectLearnPetSpell(SpellEffectContext context)
    {
        if (context.Caster is not Player player || LivePet(player) is not { IsAlive: true } pet || pet.Summon?.Charm is not { } charm
            || context.System.Store.Get(context.Effect.TriggerSpell) is not { } spell
            || charm.HasSpell(spell.Id))
        {
            return;
        }

        ActionType state = spell.IsPassive ? ActionType.Passive : ActionType.Disabled;
        if (Training is { } training)
        {
            if (!training.CanLearn(pet, spell.Id))
            {
                return;
            }

            int cost = training.Cost(charm, spell.Id);
            uint knownRank = training.KnownRankOfChain(charm, spell.Id);
            if (knownRank != 0 && !training.IsHigherRank(spell.Id, knownRank))
            {
                return;
            }

            PetLoyalty.SetTrainingPoints(pet, charm.TrainingPoints - cost);
            bool learned = knownRank != 0 ? charm.ReplaceRank(knownRank, spell.Id) : charm.LearnSpell(spell.Id, state);
            if (!learned)
            {
                return;
            }

            if (knownRank != 0)
            {
                // Pet::AddSpell unlearns the replaced rank through RemoveSpell, which runs RemoveAurasDueToSpell (Pet.cpp:2018).
                context.System.RemoveAuras(pet, knownRank);
            }
        }
        else if (!charm.LearnSpell(spell.Id, state))
        {
            return;
        }

        if (spell.IsPassive)
        {
            context.System.CastSpell(pet, spell.Id, SpellCastTargets.ForSelf(), triggered: true);
        }

        QueueCurrentPetSave(player);
        player.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildPetSpells(pet, charm, listSpells: true, context.System.GetActiveCooldowns(pet)));
    }
}
