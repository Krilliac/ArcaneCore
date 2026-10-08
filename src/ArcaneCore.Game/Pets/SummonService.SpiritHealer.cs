using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;

namespace ArcaneCore.Game.Pets;

// The pet a battleground spirit guide brings back with its owner (vmangos Player::AutoReSummonPet, Player.cpp:1580-1628, called by
// Spell::EffectSpiritHeal, SpellEffects.cpp:5821-5846) and the soul shards a demon gives back when its owner dies or it is lost
// (Pet::Unsummon(PET_SAVE_REAGENTS), Pet.cpp:1052-1075).
public sealed partial class SummonService
{
    private readonly Dictionary<ObjectGuid, SpiritHealerPet> _spiritHealerPets = [];

    /// <summary>The pet the spirit healer would bring back (vmangos <c>m_petEntry</c>, <c>m_petSpell</c>), or null.</summary>
    public (uint Entry, uint SpellId)? PetForSpiritHealer(Player owner)
        => _spiritHealerPets.TryGetValue(owner.Guid, out SpiritHealerPet? pet) ? (pet.Entry, pet.SpellId) : null;

    /// <summary>
    /// vmangos <c>m_petEntry</c> / <c>m_petSpell</c>: a new demon (SpellEffects.cpp:3322-3323) or a permanent pet loaded for its owner
    /// (Pet::LoadPetFromDB, "save pet for resurrection by spirit healer", Pet.cpp:418-423).
    /// </summary>
    internal void RememberPetForSpiritHealer(Player owner, Creature pet)
    {
        uint petNumber = pet.Summon?.Charm?.PetNumber ?? 0;
        _spiritHealerPets[owner.Guid] = new SpiritHealerPet(pet.Entry, pet.GetUInt32(UpdateFields.UnitCreatedBySpell), petNumber,
            Hunter: owner.Class == Class.Hunter && pet.Summon?.SpellId == 0);
    }

    /// <summary>vmangos Pet::Unsummon(PET_SAVE_AS_DELETED) for a hunter pet: "do not rez the pet in BG" (Pet.cpp:1076-1081).</summary>
    internal void ForgetPetForSpiritHealer(Player owner) => _spiritHealerPets.Remove(owner.Guid);

    /// <summary>
    /// vmangos Player::AutoReSummonPet: the remembered pet comes back with a player the spirit guide resurrected. It is forgotten first;
    /// the summoning spell's reagents must be in the bags and are taken (a soul shard for a Voidwalker); the pet is summoned again, and
    /// only a pet that was dead is brought back to life at full health ("We may want to resurrect the pet", Player.cpp:1619-1628): a
    /// living one keeps the health it was saved with. A warlock gets a fresh demon of the remembered entry at its level (demons are not
    /// stored here, see <see cref="EffectSummonPet"/>); a hunter its current pet from the saved snapshot (a hunter pet here carries no
    /// taming spell, so the vmangos "no spell, no pet" guard is not applied to it). Returns the pet, or null.
    /// </summary>
    public Creature? AutoReSummonPet(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_spiritHealerPets.Remove(player.Guid, out SpiritHealerPet? remembered) || remembered.Entry == 0
            || (!remembered.Hunter && remembered.SpellId == 0) || !player.IsInWorld || player.Map is null)
        {
            return null;
        }

        if (remembered.SpellId != 0 && _spells?.Store.Get(remembered.SpellId) is { } spell)
        {
            // 1. Ensure that we have the needed items (Soul shard, ...); 2. Remove reagents.
            SpellReagent[] reagents = [.. spell.Reagents.Where(r => r.Item > 0)];
            if (reagents.Any(r => player.Inventory.GetItemCount((uint)r.Item) < r.Count))
            {
                return null;
            }

            foreach (SpellReagent reagent in reagents)
            {
                player.Inventory.DestroyItemCount((uint)reagent.Item, reagent.Count);
            }
        }

        // 3. Execute the pet summon spell effect.
        bool wasDead = false;
        Creature? pet = remembered.Hunter
            ? SummonRememberedHunterPet(player, remembered, out wasDead)
            : SummonDemon(player, remembered.SpellId, remembered.Entry, player.Level);
        if (pet is null)
        {
            return null;
        }

        // 4. We may want to resurrect the pet: a remembered hunter pet saved dead comes back alive (SpawnCached revives it at 1) and at full
        // health. A living one keeps its saved health; a fresh demon is summoned alive at full health already.
        if (wasDead)
        {
            pet.Health = pet.MaxHealth;
        }

        if (remembered.Hunter)
        {
            QueueCurrentPetSave(player);
        }

        return pet;
    }

    private Creature? SummonRememberedHunterPet(Player owner, SpiritHealerPet remembered, out bool wasDead)
    {
        wasDead = false;
        if (!owner.PetGuid.IsEmpty || !TryGetCachedCurrentPet(owner, out PersistentPetSnapshot snapshot) || snapshot.PetNumber != remembered.PetNumber
            || snapshot.Entry != remembered.Entry)
        {
            return null;
        }

        wasDead = snapshot.Health == 0;
        return SpawnCached(owner, snapshot with { IsCurrent = true }, revive: true);
    }

    /// <summary>
    /// The owner leaves the world (vmangos: <c>m_petEntry</c>, <c>m_petSpell</c> and <c>m_temporaryUnsummonedPetNumber</c> are members of the
    /// Player object a logout destroys): what the service remembers of it is forgotten, the spirit healer's pet and a temporarily
    /// unsummoned pet (its saved hunter pet is the logout's to keep). The world daemon calls it from <c>WorldRuntime.PlayerLoggingOut</c>.
    /// </summary>
    public void ForgetOwner(Player owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _spiritHealerPets.Remove(owner.Guid);
        ForgetTemporarilyUnsummonedPet(owner);
    }

    /// <summary>
    /// vmangos Pet::Unsummon(PET_SAVE_REAGENTS) (Pet.cpp:1052-1075): the reagents of the spell that created the pet go back into its
    /// owner's bags (a Voidwalker, Succubus or Felhunter "credit soulshard when despawn reason other than death"), when they fit; then
    /// the pet is unsummoned. Used when the owner dies (Player::SetDeathState, Player.cpp:1527) and when the pet loses its owner or is
    /// left behind (Pet::Update, Pet.cpp:668-674).
    /// </summary>
    internal void UnsummonReturningReagents(Creature pet)
    {
        if (pet.Summon is { Kind: SummonKind.Pet } links && pet.GetOwner() is Player owner && links.SpellId != 0
            && _spells?.Store.Get(links.SpellId) is { } spell)
        {
            foreach (SpellReagent reagent in spell.Reagents.Where(r => r.Item > 0))
            {
                owner.Inventory.AddItem((uint)reagent.Item, reagent.Count, out _, received: true);
            }
        }

        Unsummon(pet);
    }

    private sealed record SpiritHealerPet(uint Entry, uint SpellId, uint PetNumber, bool Hunter);
}
