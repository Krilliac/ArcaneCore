using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Beast training rules of a hunter pet: the training-point cost of an ability rank, the family check, the four-active-spell limit and the
/// tame-time cost of the create spells (vmangos Pet::GetTPForSpell, HasTPForSpell, CanLearnPetSpell, CanTakeMoreActiveSpells and the
/// usedtrainpoints sum of Pet::InitPetCreateSpells). Costs come from SkillLineAbility.dbc field 14 (reqtrainpoints), rank chains from its
/// forward_spellid links (the same source Talents/IRankChain uses), families from CreatureFamily.dbc skillLine[0].
/// <para>Thread affinity: world thread. Immutable after construction.</para>
/// </summary>
public sealed class PetTraining
{
    /// <summary>ACTIVE_SPELLS_MAX (vmangos Pet.h:132): distinct non-passive ability chains a pet can know.</summary>
    public const int ActiveSpellsMax = 4;

    private readonly SkillLineAbilityCatalog _abilities;
    private readonly IReadOnlyDictionary<uint, uint> _familySkillLine;
    private readonly Func<uint, SpellInfo?> _spell;

    public PetTraining(SkillLineAbilityCatalog abilities, IReadOnlyDictionary<uint, uint> familySkillLine, Func<uint, SpellInfo?> spell)
    {
        _abilities = abilities ?? throw new ArgumentNullException(nameof(abilities));
        _familySkillLine = familySkillLine ?? throw new ArgumentNullException(nameof(familySkillLine));
        _spell = spell ?? throw new ArgumentNullException(nameof(spell));
    }

    public SkillLineAbilityCatalog Abilities => _abilities;

    /// <summary>
    /// Pet::GetTPForSpell (Pet.cpp:923-962): the points the rank costs, its reqtrainpoints minus the highest reqtrainpoints of any rank of the same
    /// chain the pet already knows. 0 when the spell is free (no ability row or a zero cost).
    /// </summary>
    public int Cost(CharmInfo charm, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(charm);
        uint need = _abilities.TrainingPoints(spellId);
        if (need == 0)
        {
            return 0;
        }

        uint chain = _abilities.FirstInChain(spellId);
        uint spent = 0;
        foreach (uint known in charm.SpellStates.Keys)
        {
            if (_abilities.FirstInChain(known) == chain)
            {
                spent = Math.Max(spent, _abilities.TrainingPoints(known));
            }
        }

        return (int)need - (int)spent;
    }

    /// <summary>Pet::HasTPForSpell (Pet.cpp:964-981): a free spell always fits; otherwise the pet needs at least the cost left and a non-negative cost.</summary>
    public static bool HasPoints(int trainingPoints, int need) => !((trainingPoints - need < 0 || need < 0) && need != 0);

    /// <summary>
    /// Pet::CanLearnPetSpell (Pet.cpp:998-1021): the spell is on the family's own skill line (CreatureFamily skillLine[0]), or on
    /// SKILL_PET_TALENTS (270) for a hunter pet. A family without a skill line entry learns nothing.
    /// </summary>
    public bool CanLearn(Creature pet, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        if (!_familySkillLine.TryGetValue(pet.Template.Family, out uint familyLine))
        {
            return false;
        }

        bool hunterPet = pet.Summon is { Kind: SummonKind.Pet } && pet.GetOwner() is Player { Class: Class.Hunter };
        foreach (SkillLineAbilityRecord ability in _abilities.Abilities(spellId))
        {
            if (ability.SkillId == familyLine || (hunterPet && ability.SkillId == SkillIds.PetTalents))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pet::CanTakeMoreActiveSpells (Pet.cpp:884-920): a passive spell or a rank of a chain the pet already has is always fine; a new chain
    /// fits while the pet knows fewer than <see cref="ActiveSpellsMax"/> distinct non-passive chains.
    /// </summary>
    public bool CanTakeMoreActiveSpells(CharmInfo charm, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(charm);
        if (_spell(spellId)?.IsPassive == true)
        {
            return true;
        }

        uint chain = _abilities.FirstInChain(spellId);
        var chains = new HashSet<uint>();
        foreach (uint known in charm.SpellStates.Keys)
        {
            if (_spell(known)?.IsPassive == true)
            {
                continue;
            }

            uint knownChain = _abilities.FirstInChain(known);
            if (knownChain == chain)
            {
                return true;
            }

            chains.Add(knownChain);
        }

        return chains.Count < ActiveSpellsMax;
    }

    /// <summary>
    /// The usedtrainpoints sum of Pet::InitPetCreateSpells (Pet.cpp:2087-2095): the reqtrainpoints of each create spell's taught ability (the first
    /// effect's trigger spell). Duplicates are summed as vmangos does.
    /// </summary>
    public int CreateSpellsCost(IEnumerable<uint> createSpellIds)
    {
        ArgumentNullException.ThrowIfNull(createSpellIds);
        int total = 0;
        foreach (uint id in createSpellIds)
        {
            if (_spell(id) is { } info)
            {
                total += (int)_abilities.TrainingPoints(info.Effects[0].TriggerSpell);
            }
        }

        return total;
    }

    /// <summary>True when <paramref name="higher"/> is a later rank of the same chain as <paramref name="lower"/> (walking previous ranks reaches it).</summary>
    public bool IsHigherRank(uint higher, uint lower)
    {
        uint current = higher;
        for (int hop = 0; hop < 32 && current != 0; hop++)
        {
            current = _abilities.PreviousRank(current);
            if (current == lower)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The known spell in the same chain as <paramref name="spellId"/> other than itself (the rank a new rank replaces or loses to), or 0.</summary>
    public uint KnownRankOfChain(CharmInfo charm, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(charm);
        uint chain = _abilities.FirstInChain(spellId);
        foreach (uint known in charm.SpellStates.Keys)
        {
            if (known != spellId && _abilities.FirstInChain(known) == chain)
            {
                return known;
            }
        }

        return 0;
    }
}
