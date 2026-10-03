using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_WATER_WALK (104), FEATHER_FALL (105), HOVER (106) and SAFE_FALL (144), after vmangos
/// <c>Aura::HandleAuraWaterWalk</c> / <c>HandleAuraFeatherFall</c> / <c>HandleAuraHover</c> (SpellAuras.cpp:2278-2303) and
/// <c>HandleAuraSafeFall</c> (:209, "implemented in WorldSession::HandleMovementOpcodes").
/// <para>
/// The first three order the matching movement change on the target (the client acknowledges it, see
/// <see cref="MovementControl"/>); removal orders the opposite. As in vmangos, removing one aura clears the state even
/// if another aura of the same type is still on the unit. Safe Fall only carries a distance that the fall rules read
/// from the <see cref="AuraLedger"/>. All four record themselves in the ledger, which is how map code (the fall
/// observer) sees the unit's auras. Handlers act on players only: creatures are server-moved and have no client to ask.
/// </para>
/// </summary>
public sealed class MovementFlagAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.WaterWalk, new AuraHandler((s, h, a, apply) => Apply(h, a, apply, MovementChangeType.WaterWalk), null));
        system.RegisterAura(AuraType.FeatherFall, new AuraHandler((s, h, a, apply) => Apply(h, a, apply, MovementChangeType.FeatherFall), null));
        system.RegisterAura(AuraType.Hover, new AuraHandler((s, h, a, apply) => Apply(h, a, apply, MovementChangeType.Hover), null));
        system.RegisterAura(AuraType.SafeFall, new AuraHandler((s, h, a, apply) => Record(h.Target, a, apply), null));
    }

    private static void Apply(SpellAuraHolder holder, SpellAura aura, bool apply, MovementChangeType type)
    {
        Record(holder.Target, aura, apply);
        if (holder.Target is Player player)
        {
            MovementControl.Request(player, type, apply);
        }
    }

    private static void Record(Unit target, SpellAura aura, bool apply)
    {
        if (apply)
        {
            target.Locomotion.Auras.Add(aura);
        }
        else
        {
            target.Locomotion.Auras.Remove(aura);
        }
    }
}
