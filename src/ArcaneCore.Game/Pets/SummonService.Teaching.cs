using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// Hunters learning abilities from their pets (vmangos Pet::InitPetCreateSpells AddTeachSpell and Pet::CheckLearning). A tamed beast's
/// create spells the owner does not know yet become teach spells: pet ability → the owner's "learn" spell. Each time the pet uses
/// the ability (vmangos Spell::cast with a pet caster; here once per hit) there is a 10% chance (urand(0, 100) &lt; 10) that the owner
/// learns the teaching spell, after which the entry is gone. Teach spells are not stored in the pet snapshot; they are derived again from the create spells whenever a cached pet is spawned (vmangos stores them as teachSpellData).
/// </summary>
public sealed partial class SummonService
{
    /// <summary>The learning roll: true teaches (default urand(0, 100) &lt; 10). Tests set it.</summary>
    public Func<bool> TeachRoll { get; set; } = static () => Random.Shared.Next(0, 101) < 10;

    /// <summary>The AddTeachSpell half of Pet::InitPetCreateSpells for a freshly tamed pet of <paramref name="entry"/>.</summary>
    internal void InitTeachSpells(Player owner, Creature pet, uint entry)
    {
        if (_spells is not { } spells || pet.Summon?.Charm is not { } charm)
        {
            return;
        }

        foreach (uint spellId in Content.GetCreateSpells(entry))
        {
            if (spells.Store.Get(spellId) is not { } learn
                || learn.Effects[0].Effect is not (SpellEffectName.LearnSpell or SpellEffectName.LearnPetSpell)
                || learn.Effects[0].TriggerSpell == 0
                || spells.Spellbook?.HasSpell(owner, learn.Id) == true)
            {
                continue;
            }

            charm.TeachSpells[learn.Effects[0].TriggerSpell] = learn.Id;
        }
    }

    /// <summary>vmangos Pet::CheckLearning: a hunter pet used <paramref name="spellId"/>; maybe its owner learns from it.</summary>
    internal void CheckLearning(Unit caster, uint spellId)
    {
        if (caster is not Creature { Summon: { Kind: SummonKind.Pet, Charm: { } charm } } pet || charm.TeachSpells.Count == 0
            || !charm.TeachSpells.TryGetValue(spellId, out uint teach) || pet.GetOwner() is not Player owner || owner.Class != Class.Hunter
            || _spells is not { } spells)
        {
            return;
        }

        if (TeachRoll())
        {
            spells.LearnSpell(owner, teach);
            charm.TeachSpells.Remove(spellId);
        }
    }
}
