using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Nefarian Class Call Warlock 23427 (vmangos boss_nefarian.cpp:604-630, spell script on effect 0): every warlock the call lands on summons two
/// Corrupted Infernals (23426) on itself. Which players the call reaches is the Blackwing Lair class filter.
/// </summary>
[SpellScript(23427, ExecuteEffects = [SpellEffectName.ApplyAura])]
public sealed class NefarianClassCallWarlockScript : ISpellScript
{
    public const uint SummonInfernals = 23426;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0)
        {
            return;
        }

        context.System.CastSpell(context.Target, SummonInfernals, SpellCastTargets.ForSelf(), triggered: true);
        context.System.CastSpell(context.Target, SummonInfernals, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>
/// Illusion Passive 7131 (vmangos UnitAuraProcHandler.cpp:567-572, HandleDummyAuraProc): the illusion creature that carries the passive aura
/// despawns (after 1 ms) the first time the aura procs, that is the first time it is hit or hits.
/// </summary>
public sealed class IllusionPassiveProcScript : IProcScript
{
    public const uint IllusionPassive = 7131;

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Owner is Creature creature)
        {
            WorldDummyCasts.Despawn(creature, 1);
        }

        return AuraProcResult.Ok;
    }
}

/// <summary>
/// Nefarian's warrior class call, Berserk 23397 (ModShapeshift into Berserker Stance, plus an OVERRIDE_CLASS_SCRIPTS marker, misc 3654, that
/// vmangos itself reads nowhere; it looks the spell up by id): while it lasts the player cannot cast a spell that changes shapeshift form
/// (vmangos Spell.cpp:6431-6436, SPELL_FAILED_NOT_SHAPESHIFT), and when it ends the warrior is put back into Berserker Stance 2458
/// (SpellAuras.cpp:2448-2458).
/// </summary>
public sealed class NefarianBerserkModule : ISpellHandlerModule
{
    public const uint Berserk = 23397, BerserkerStance = 2458;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterCastCheck(new BerserkNoShapeshiftCheck());
        system.RegisterProcScript(IllusionPassiveProcScript.IllusionPassive, new IllusionPassiveProcScript());
        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id == Berserk && holder.Target.IsAlive && holder.Target.IsInWorld)
            {
                system.CastSpell(holder.Target, BerserkerStance, SpellCastTargets.ForSelf(), triggered: true);
            }
        };
    }
}

/// <summary>A shapeshift spell is refused while the caster has Berserk 23397.</summary>
public sealed class BerserkNoShapeshiftCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.CasterAuraState + 60;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (!context.Spell.HasAura(AuraType.ModShapeshift) || !context.System.HasAura(context.Caster, NefarianBerserkModule.Berserk))
        {
            return SpellCastResult.CastOk;
        }

        return SpellCastResult.NotShapeshift;
    }
}
