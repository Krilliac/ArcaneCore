using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Curse of Doom (603; "in 1.12 Curse of Doom have only 1 rank"). Two vmangos cases:
/// <list type="bullet">
/// <item>The summon (SpellAuras.cpp:5921-5924, <c>Aura::PeriodicTick</c> PERIODIC_DAMAGE): when the tick's damage killed the target, the caster casts
/// Curse of Doom Effect (18662, SUMMON_DEMON of the Doomguard 11859) on itself, triggered, one time in ten (<c>!urand(0, 9)</c>).</item>
/// <item>The target check (Spell.cpp:7584-7592, <c>Spell::CheckTargetCreatureType</c>): a player, or a unit a player owns or charms, is not a
/// target; CheckCast answers TARGET_IS_PLAYER for a player and BAD_TARGETS for the rest (Spell.cpp:5578-5585).</item>
/// </list>
/// </summary>
[SpellScript(CurseOfDoomScript.CurseOfDoom)]
public sealed class CurseOfDoomScript : ISpellScript, IPeriodicDamageScript
{
    /// <summary>Curse of Doom.</summary>
    public const uint CurseOfDoom = 603;

    /// <summary>Curse of Doom Effect: SUMMON_DEMON of the Doomguard (creature 11859).</summary>
    public const uint CurseOfDoomEffect = 18662;

    /// <summary>One chance in this many (vmangos <c>!urand(0, 9)</c>).</summary>
    public const int SummonOneIn = 10;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Target is not { } target || ReferenceEquals(target, context.Caster) || !target.IsCharmerOrOwnerPlayerOrPlayerItself)
        {
            return SpellCastResult.CastOk;
        }

        return target is Player ? SpellCastResult.TargetIsPlayer : SpellCastResult.BadTargets;
    }

    public void AfterTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura, Unit caster, uint dealt)
    {
        if (holder.Spell.Id == CurseOfDoom && !holder.Target.IsAlive && system.Random.Next(0, SummonOneIn) == 0)
        {
            system.CastSpell(caster, CurseOfDoomEffect, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}

/// <summary>Registers <see cref="CurseOfDoomScript"/>'s periodic damage half (discovered <see cref="ISpellHandlerModule"/>).</summary>
public sealed class CurseOfDoomModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterPeriodicDamageScript(CurseOfDoomScript.CurseOfDoom, new CurseOfDoomScript());
    }
}
