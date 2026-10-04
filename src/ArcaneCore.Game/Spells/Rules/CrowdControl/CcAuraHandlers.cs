using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules.CrowdControl;

/// <summary>
/// The crowd-control aura handlers: ModRoot, ModStun, ModSilence, ModPacify, ModPacifySilence, ModDisarm,
/// ModFear and ModConfuse (vmangos SpellAuras.cpp HandleAuraModRoot / HandleAuraModStun :3548-3640,
/// HandleAuraModSilence :3859-3890, HandleAuraModPacify(AndSilence) :5625-5640, HandleAuraModDisarm :3502-3545,
/// HandleModFear / HandleModConfuse :3444-3463). They set the unit flags and interrupt casts; the movement
/// a fear or confuse causes belongs to the creature movement code (the map tick's crowd-control hook), which reads the
/// flags; a feared creature's caster is remembered here so it knows what to run from, and a creature's root flag follows its root and stun auras.
/// </summary>
internal static class CcAuraHandlers
{
    /// <summary>Add the crowd-control handlers to <paramref name="handlers"/> (replacing the earlier root/stun ones).</summary>
    public static Dictionary<AuraType, AuraHandler> Install(Dictionary<AuraType, AuraHandler> handlers)
    {
        handlers[AuraType.ModRoot] = new AuraHandler(Root, null);
        handlers[AuraType.ModStun] = new AuraHandler(Stun, null);
        handlers[AuraType.ModSilence] = new AuraHandler(Silence, null);
        handlers[AuraType.ModPacify] = new AuraHandler(Pacify, null);
        handlers[AuraType.ModPacifySilence] = new AuraHandler(PacifyAndSilence, null);
        handlers[AuraType.ModDisarm] = new AuraHandler(Disarm, null);
        handlers[AuraType.ModFear] = new AuraHandler(FearOrConfuse, null);
        handlers[AuraType.ModConfuse] = new AuraHandler(FearOrConfuse, null);
        return handlers;
    }

    private static void Root(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply) =>
        CcState.RefreshRoot(system, holder.Target);

    /// <summary>
    /// UNIT_FLAG_STUNNED, rooted, stand up and loot release for players (not while mounted), and the cast in
    /// progress is interrupted when another unit applied the stun (vmangos: "it appears that spell casts get
    /// interrupted even if they don't have the flag"). A stun does not land on a unit on a taxi (:3556).
    /// </summary>
    private static void Stun(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            if ((target.UnitFlags & UnitFlags.TaxiFlight) != 0)
            {
                return;
            }

            CcState.RefreshStun(system, target);
            if (holder.CasterGuid != target.Guid)
            {
                system.InterruptCurrentCast(target);
            }

            if (target is Player player)
            {
                if (!CcState.IsMounted(player))
                {
                    player.SetStandState(StandState.Stand);
                }

                system.ReleaseLoot?.Invoke(player);
            }
        }
        else
        {
            CcState.RefreshStun(system, target);
        }

        CcState.RefreshRoot(system, target);
    }

    /// <summary>UNIT_FLAG_SILENCED; the cast in progress is interrupted only when its PreventionType is SILENCE.</summary>
    private static void Silence(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        CcState.RefreshSilence(system, target);
        if (apply)
        {
            system.InterruptCurrentCast(target, static spell => spell.PreventionType == SpellConstants.PreventionTypeSilence);
        }
    }

    private static void Pacify(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply) =>
        CcState.RefreshPacify(system, holder.Target);

    private static void PacifyAndSilence(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        CcState.RefreshPacify(system, holder.Target);
        Silence(system, holder, aura, apply);
    }

    /// <summary>
    /// UNIT_FLAG_DISARMED (main hand; the off hand and ranged weapon are unaffected). The weapon-dependent
    /// aura modifiers and swing timers vmangos refreshes on a disarmed player belong to the stats area.
    /// </summary>
    private static void Disarm(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply) =>
        CcState.RefreshDisarm(system, holder.Target);

    /// <summary>
    /// vmangos Unit::ModConfuseSpell: totems ignore it; a fear on a unit that prevents fleeing does nothing;
    /// otherwise the flags follow the auras, an effect from another unit interrupts the cast in progress and a
    /// player's loot window closes.
    /// </summary>
    private static void FearOrConfuse(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (CcState.IsTotem(target))
        {
            return;
        }

        if (apply && aura.Type == AuraType.ModFear && system.HasLiveAura(target, AuraType.PreventsFleeing))
        {
            return;
        }

        if (apply && aura.Type == AuraType.ModFear && target is Creatures.Creature)
        {
            CcState.RememberFearSource(target, holder.CasterOwner.Caster);
        }

        CcState.RefreshFear(system, target);
        if (!apply)
        {
            return;
        }

        if (holder.CasterGuid != target.Guid)
        {
            system.InterruptCurrentCast(target);
        }

        if (target is Player player)
        {
            system.ReleaseLoot?.Invoke(player);
        }
    }
}
