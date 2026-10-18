namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos <c>Aura::ApplyModifier(apply, Real, skipCheckExclusive)</c> (SpellAuras.cpp:921-950): every aura handler call goes through here.
    /// An aura already in the asked state is left alone; an exclusive aura (<see cref="ExclusiveAuras"/>) only applies when no stronger one of
    /// its kind is applied on the unit, switching a weaker one off, and on removal the strongest waiting one of its kind comes back.
    /// </summary>
    private void ApplyAuraModifier(SpellAuraHolder holder, SpellAura aura, bool apply, bool skipCheckExclusive = false)
    {
        if (aura.IsApplied == apply)
        {
            return;
        }

        aura.InUse = true;
        try
        {
            if (apply && !skipCheckExclusive && IsExclusive(holder, aura) && !ExclusiveAuraCanApply(holder, aura))
            {
                return;
            }

            aura.IsApplied = apply;
            AuraHandlers.GetValueOrDefault(aura.Type)?.Apply?.Invoke(this, holder, aura, apply);
            if (!apply && !skipCheckExclusive && IsExclusive(holder, aura))
            {
                ExclusiveAuraUnapply(holder, aura);
            }
        }
        finally
        {
            aura.InUse = false;
        }
    }

    private static bool IsExclusive(SpellAuraHolder holder, SpellAura aura)
        => aura.Exclusive ??= ExclusiveAuras.IsExclusive(holder.Spell, aura.EffectIndex, aura.Type);

    /// <summary>
    /// vmangos <c>Aura::ExclusiveAuraCanApply</c> (SpellAuras.cpp:8373-8409): the applied aura of the same kind, if any, is compared; a weaker
    /// or equal one is switched off (without its own exclusive check) and this one applies, a stronger one keeps this one waiting. Unlike
    /// vmangos only applied auras are searched, so an aura of the kind that is still being added does not hold this one back.
    /// </summary>
    private bool ExclusiveAuraCanApply(SpellAuraHolder holder, SpellAura aura)
    {
        if (FindExclusiveRival(holder, aura, applied: true) is not { } rival)
        {
            return true;
        }

        if (rival.Aura.InUse || ExclusiveAuras.Compare(holder, aura, rival.Holder, rival.Aura) != 1)
        {
            return false;
        }

        ApplyAuraModifier(rival.Holder, rival.Aura, apply: false, skipCheckExclusive: true);
        return true;
    }

    /// <summary>vmangos <c>Aura::ExclusiveAuraUnapply</c> (SpellAuras.cpp:8411-8434): the strongest waiting aura of the kind applies again.</summary>
    private void ExclusiveAuraUnapply(SpellAuraHolder holder, SpellAura aura)
    {
        if (FindExclusiveRival(holder, aura, applied: true) is null
            && FindExclusiveRival(holder, aura, applied: false) is { } waiting && !waiting.Aura.InUse)
        {
            ApplyAuraModifier(waiting.Holder, waiting.Aura, apply: true, skipCheckExclusive: true);
        }
    }

    /// <summary>
    /// vmangos <c>Unit::GetMostImportantAuraAfter</c> (Unit.cpp:10586-10620): the most important other exclusive aura of the same kind as
    /// <paramref name="like"/> on its unit, among the applied or among the waiting ones.
    /// </summary>
    private (SpellAuraHolder Holder, SpellAura Aura)? FindExclusiveRival(SpellAuraHolder likeHolder, SpellAura like, bool applied)
    {
        if (GetState(likeHolder.Target.Guid) is not { } state || !ReferenceEquals(state.Unit, likeHolder.Target))
        {
            return null;
        }

        (SpellAuraHolder Holder, SpellAura Aura)? best = null;
        foreach (SpellAuraHolder holder in state.Auras)
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? candidate in holder.Auras)
            {
                if (candidate is null || ReferenceEquals(candidate, like) || candidate.Type != like.Type || candidate.IsApplied != applied
                    || !IsExclusive(holder, candidate))
                {
                    continue;
                }

                if (best is null)
                {
                    if (ExclusiveAuras.Compare(holder, candidate, likeHolder, like) > 0)
                    {
                        best = (holder, candidate);
                    }
                }
                else if (ExclusiveAuras.Compare(holder, candidate, best.Value.Holder, best.Value.Aura) == 1)
                {
                    best = (holder, candidate);
                }
            }
        }

        return best;
    }
}