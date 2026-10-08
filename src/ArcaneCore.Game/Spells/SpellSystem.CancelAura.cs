using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// CMSG_CANCEL_AURA (vmangos WorldSession::HandleCancelAuraOpcode, SpellHandler.cpp:333-405): ignored for NO_AURA_CANCEL,
    /// DO_NOT_DISPLAY, NO_AURA_ICON without an active icon, passive and negative spells (except, while the player moves another unit, a
    /// MOD_POSSESS or MOD_POSSESS_PET spell: the possessor ends its own Mind Control or Eyes of the Beast) and while possessed; a channelled spell
    /// stops its channel; a foreign area aura cannot be cancelled; otherwise every holder of the spell is removed.
    /// Polarity is the spell's (vmangos IsPositiveSpell by id, with no caster or victim).
    /// </summary>
    public void CancelAura(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null || spell.HasAttribute(SpellAttributes.NoAuraCancel) || spell.HasAttribute(SpellAttributes.DoNotDisplay)
            || (spell.HasAttribute(SpellAttributesEx.NoAuraIcon) && spell.ActiveIconId == 0)
            || spell.IsPassive)
        {
            return;
        }

        // "ignore for remote control state ... except own aura spells" (SpellHandler.cpp:353-373)
        if (!spell.IsPositiveSpell(Store.Get)
            && (player.IsSelfMover || !(spell.HasAura(AuraType.ModPossess) || spell.HasAura(AuraType.ModPossessPet))))
        {
            return;
        }

        if ((player.UnitFlags & UnitFlags.Possessed) != 0)
        {
            return;
        }

        if (spell.IsChanneled)
        {
            if (GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spellId)
            {
                Cancel(cast);
            }

            return;
        }

        // "not own area auras can't be cancelled" (SpellHandler.cpp:397-399).
        if (GetAuras(player).FirstOrDefault(h => h.Spell.Id == spellId) is { } holder
            && holder.CasterGuid != player.Guid && HasAreaAuraEffect(holder.Spell))
        {
            return;
        }

        RemoveAuras(player, spellId, AuraRemoveMode.Cancel);
    }

    /// <summary>vmangos SpellEntry::HasAreaAuraEffect: any of the party/pet/friend/enemy/raid/owner area aura effects.</summary>
    internal static bool HasAreaAuraEffect(SpellInfo spell) => spell.Effects.Any(e => e.Effect is SpellEffectName.ApplyAreaAuraParty
        or SpellEffectName.ApplyAreaAuraPet or SpellEffectName.ApplyAreaAuraFriend or SpellEffectName.ApplyAreaAuraEnemy
        or SpellEffectName.ApplyAreaAuraRaid or SpellEffectName.ApplyAreaAuraOwner);
}
