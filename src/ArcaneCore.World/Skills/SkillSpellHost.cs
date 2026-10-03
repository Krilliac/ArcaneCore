using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Skills;

/// <summary>
/// One player's <see cref="ISkillSpellHost"/> over the daemon's spellbook (<see cref="SpellFeature.Spellbook"/>)
/// and spell system. While the character is loading (not in a map) spells are added silently; in the world they
/// are learned with the client messages, like the .learn and .unlearn commands do.
/// </summary>
internal sealed class SkillSpellHost(SpellFeature spells, Player player) : ISkillSpellHost
{
    /// <summary>
    /// The spells of the stored spellbook that the load has not run through the learn path yet. vmangos reads the
    /// skills and then adds the stored spells one by one (Player.cpp:14963-14977), so a skill change at that
    /// moment can only remove spells that were already added. Our book is complete from the start, so removals of
    /// a spell still listed here are ignored; the load removes each spell from the set just before it processes it.
    /// </summary>
    public HashSet<uint> NotYetLoaded { get; } = [];

    public bool HasSpell(uint spellId) => spells.Spellbook.HasSpell(player, spellId);

    public void LearnSpell(uint spellId)
    {
        if (spells.System.Store.Get(spellId) is null)
        {
            return; // a SkillLineAbility row of a spell this server does not have (vmangos AddSpell: unknown spell)
        }

        if (player.IsInWorld)
        {
            spells.System.LearnSpell(player, spellId);
        }
        else
        {
            spells.Spellbook.LearnSpell(player, spellId);
        }
    }

    public void RemoveSpell(uint spellId)
    {
        if (NotYetLoaded.Contains(spellId) || !spells.Spellbook.ForgetSpell(player, spellId))
        {
            return;
        }

        if (player.IsInWorld)
        {
            // vmangos Player::RemoveSpell: SMSG_REMOVED_SPELL, and passive auras go with the spell.
            player.Session.Send(WorldOpcode.SmsgRemovedSpell, Game.Spells.SpellPackets.BuildRemovedSpell(spellId));
            spells.System.RemoveAuras(player, spellId);
        }
    }

    public void WeaponSkillRemoved() => player.Inventory.AutoUnequipWeaponsIfNeeded();
}
