using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private const uint BestialWrath = 19574;
    private const uint GhostWolf = 2645;
    private const uint WaterWalking = 546;

    /// <summary>The Beast Within and Bestial Wrath immunities (SpellAuras.cpp:7152-7163): charm, fear, polymorph; incapacitate, freeze, horror; root, sleep, snare; disorient.</summary>
    private static readonly uint[] s_bestialWrathBoosts = [24395, 24396, 24397, 26592];

    /// <summary>
    /// vmangos SpellAuraHolder::HandleSpellSpecificBoosts (SpellAuras.cpp:7130-7198, called by Unit::AddSpellAuraHolder after the auras apply,
    /// Unit.cpp:3280, and on removal, :3852). Bestial Wrath's target casts its four mechanic immunity spells on itself with the holder's caster as
    /// the original caster and loses them with it. Ghost Wolf removes its caster's Water Walking.
    /// </summary>
    private void HandleSpellSpecificBoosts(SpellAuraHolder holder, bool apply)
    {
        switch (holder.Spell.Id)
        {
            case GhostWolf when apply:
                if (ResolveAuraCaster(holder) is { } shaman)
                {
                    RemoveAuras(shaman, WaterWalking);
                }

                break;
            case BestialWrath:
                Unit target = holder.Target;
                Unit caster = ResolveAuraCaster(holder) ?? target;
                foreach (uint boost in s_bestialWrathBoosts)
                {
                    if (apply)
                    {
                        if (holder.IsRemoved)
                        {
                            break;
                        }

                        AddAura(target, boost, caster: caster);
                    }
                    else
                    {
                        RemoveAurasByCaster(target, boost, holder.CasterGuid);
                    }
                }

                break;
        }
    }
}