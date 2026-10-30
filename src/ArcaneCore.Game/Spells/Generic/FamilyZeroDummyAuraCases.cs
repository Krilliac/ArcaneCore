using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// The family-0 cases of vmangos <c>Aura::HandleAuraDummy</c> (SpellAuras.cpp:1631-2215, a switch on the spell id) that fire a follow-up spell or end
/// a possession when the DUMMY aura is applied or removed. Each case is a <see cref="SpellSystem.RegisterDummyAuraHandler"/> registration; the
/// handler runs once per spell (on its first DUMMY effect), as the vmangos switch runs once per DUMMY aura.
/// </summary>
public sealed class FamilyZeroDummyAuraModule : ISpellHandlerModule
{
    /// <summary>Eye of Kilrogg, Eye of Yesmur, Dream Vision: the DUMMY aura on the player ends the possessed minion when it goes (SpellAuras.cpp:1873-1881).</summary>
    public static readonly uint[] PossessAuras = [126, 6272, 11403];

    /// <summary>The creature-specific Tame spells (19548 ... 19700) and the spell a channel that runs its full length casts on the beast (SpellAuras.cpp:1826-1915).</summary>
    public static readonly (uint Tame, uint Final)[] Tames =
    [
        (19548, 19597), (19674, 19677), (19687, 19676), (19688, 19678), (19689, 19679), (19692, 19680),
        (19693, 19684), (19694, 19681), (19696, 19682), (19697, 19683), (19699, 19685), (19700, 19686),
    ];

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        foreach (uint id in PossessAuras)
        {
            system.RegisterDummyAuraHandler(id, (_, holder, aura, apply) =>
            {
                // Player::UnsummonPossessedMinion: the minion the caster possesses goes.
                if (!apply && IsFirstDummy(holder, aura) && Caster(holder) is Player { CharmGuid.IsEmpty: false } player
                    && player.Map?.FindObject(player.CharmGuid) is Creature minion)
                {
                    WorldDummyCasts.Despawn(minion, 0);
                }
            });
        }

        foreach ((uint tame, uint final) in Tames)
        {
            system.RegisterDummyAuraHandler(tame, (spells, holder, aura, apply) =>
            {
                // A tame channel that ran to its end (AURA_REMOVE_BY_CHANNEL) casts the real taming spell on the living beast.
                if (apply || !IsFirstDummy(holder, aura) || holder.RemoveMode != AuraRemoveMode.Channel || !holder.Target.IsAlive
                    || Caster(holder) is not { IsAlive: true } caster)
                {
                    return;
                }

                Cast(spells, caster, holder.Target, final);
            });
        }

        // Electromagnetic Gigaflux Reactivator (SpellAuras.cpp:1891-1898): when the aura runs out the player casts 11828 on the target with the item.
        system.RegisterDummyAuraHandler(11826, (spells, holder, aura, apply) =>
        {
            if (!apply && IsFirstDummy(holder, aura) && holder.RemoveMode == AuraRemoveMode.Expire && Caster(holder) is Player player)
            {
                Cast(spells, player, holder.Target, 11828, player.Inventory.GetItemByGuid(holder.CastItemGuid));
            }
        });

        // Hex of Jammal'an (SpellAuras.cpp:1899-1905): at the end the target is cast Hex Reward effects 12480 and 12483 by the caster.
        system.RegisterDummyAuraHandler(12479, (spells, holder, aura, apply) =>
        {
            if (!apply && IsFirstDummy(holder, aura) && Caster(holder) is { } caster)
            {
                Cast(spells, caster, holder.Target, 12480);
                Cast(spells, caster, holder.Target, 12483);
            }
        });

        // (DND) Belnistrasz Idol Shutdown Visual (SpellAuras.cpp:1906-1917): the caster shakes the camera (12816) unless the target died.
        system.RegisterDummyAuraHandler(12774, (spells, holder, aura, apply) =>
        {
            if (!apply && IsFirstDummy(holder, aura) && holder.RemoveMode != AuraRemoveMode.Death && Caster(holder) is { } caster)
            {
                Cast(spells, caster, caster, 12816);
            }
        });

        // Net-o-matic (SpellAuras.cpp:1708-1717): the caster is rooted by 13138 and the marker aura goes at once.
        system.RegisterDummyAuraHandler(13139, (spells, holder, aura, apply) =>
        {
            if (apply && IsFirstDummy(holder, aura) && Caster(holder) is { } caster)
            {
                Cast(spells, caster, caster, 13138);
                spells.RemoveAuras(caster, 13139);
            }
        });

        // Goblin Rocket Helmet (SpellAuras.cpp:1761-1766): the caster casts 13360 on itself.
        system.RegisterDummyAuraHandler(22646, (spells, holder, aura, apply) =>
        {
            if (apply && IsFirstDummy(holder, aura) && Caster(holder) is { } caster)
            {
                Cast(spells, caster, caster, 13360);
            }
        });

        // Ragnaros Submerge Visual (SpellAuras.cpp:1751-1760): Submerge Fade 21107 and Submerge Effect 21859, then the visual goes.
        system.RegisterDummyAuraHandler(26234, (spells, holder, aura, apply) =>
        {
            if (apply && IsFirstDummy(holder, aura) && Caster(holder) is { } caster)
            {
                Cast(spells, caster, caster, 21107);
                Cast(spells, caster, caster, 21859);
                spells.RemoveAuras(caster, 26234);
            }
        });

        // Emeriss Aura 24906 -> 24904 and Mark of Nature 25042 -> 25040: the target dying casts the follow-up on itself (SpellAuras.cpp:1958-1971).
        foreach ((uint auraId, uint onDeath) in new[] { (24906u, 24904u), (25042u, 25040u) })
        {
            system.RegisterDummyAuraHandler(auraId, (spells, holder, aura, apply) =>
            {
                if (!apply && IsFirstDummy(holder, aura) && holder.RemoveMode == AuraRemoveMode.Death)
                {
                    Cast(spells, holder.Target, holder.Target, onDeath);
                }
            });
        }

        // Mark of Frost 23183 (SpellAuras.cpp:1989-2002): the target dying casts Mark of Frost 23182 on itself (the caster must still exist).
        system.RegisterDummyAuraHandler(23183, (spells, holder, aura, apply) =>
        {
            if (!apply && IsFirstDummy(holder, aura) && holder.RemoveMode == AuraRemoveMode.Death && Caster(holder) is not null)
            {
                Cast(spells, holder.Target, holder.Target, 23182);
            }
        });

        // Itch 25185 -> 25187 and 26077 -> 26078 (SpellAuras.cpp:1971-1988): running out casts the follow-up on the target.
        foreach ((uint auraId, uint onExpire) in new[] { (25185u, 25187u), (26077u, 26078u) })
        {
            system.RegisterDummyAuraHandler(auraId, (spells, holder, aura, apply) =>
            {
                if (!apply && IsFirstDummy(holder, aura) && holder.RemoveMode == AuraRemoveMode.Expire && Caster(holder) is { } caster)
                {
                    Cast(spells, caster, holder.Target, onExpire);
                }
            });
        }
    }

    private static Unit? Caster(SpellAuraHolder holder)
        => holder.CasterGuid == holder.Target.Guid ? holder.Target : holder.Target.Map?.FindObject(holder.CasterGuid) as Unit;

    /// <summary>Whether <paramref name="aura"/> is the spell's first DUMMY effect, so a spell with two DUMMY effects runs its case once.</summary>
    private static bool IsFirstDummy(SpellAuraHolder holder, SpellAura aura)
    {
        for (int i = 0; i < holder.Spell.Effects.Count; i++)
        {
            if (holder.Spell.Effects[i].AuraType == AuraType.Dummy)
            {
                return aura.EffectIndex == i;
            }
        }

        return false;
    }

    /// <summary>vmangos <c>caster->CastSpell(target, spellId, true, item)</c>.</summary>
    private static void Cast(SpellSystem spells, Unit caster, Unit target, uint spellId, Items.Item? item = null)
        => spells.CastCustomSpell(caster, spellId, ReferenceEquals(caster, target) ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid),
            null, castItem: item);
}
