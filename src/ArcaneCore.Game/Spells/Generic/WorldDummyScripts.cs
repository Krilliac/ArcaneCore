using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Tame Beast 13535 (vmangos SpellEffects.cpp:804-820): the Tame Beast channel's (1515) periodic trigger at the end of its 20 seconds. The
/// hunter casts the real Tame Beast 13481 (SPELL_EFFECT_TAMECREATURE) at the channel's beast, triggered. Without it the channel ran out and
/// nothing was tamed. The beast is the trigger cast's explicit unit (the 1515 aura sits on it), else the hunter's channel object.
/// </summary>
[SpellScript(13535)]
public sealed class TameBeastTriggerScript : ISpellScript
{
    public const uint TameBeastChannel = 1515, TameBeastEffect = 13481;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player hunter || hunter.Map is not { } map)
        {
            return;
        }

        ObjectGuid beastGuid = context.Cast.Targets.Unit is { IsEmpty: false } explicitUnit && explicitUnit != hunter.Guid
            ? explicitUnit
            : new ObjectGuid(hunter.GetUInt64(UpdateFields.UnitFieldChannelObject));
        if (beastGuid.IsEmpty || map.FindObject(beastGuid) is not Creature beast)
        {
            return;
        }

        context.System.CastSpell(hunter, TameBeastEffect, SpellCastTargets.ForUnit(beast.Guid), triggered: true, context.System.Store.Get(TameBeastChannel));
    }
}

/// <summary>Hate to Zero 9204 (vmangos SpellEffects.cpp:723-730): the caster forgets all threat on the target.</summary>
[SpellScript(9204)]
public sealed class HateToZeroScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Target, context.Caster))
        {
            context.Caster.Combat.Threat.ModifyThreatPercent(context.Target, -100);
        }
    }
}

/// <summary>Ranshalla's Torch Trap 18955 / Altar Trap 18993 end her Light Torch 18953; Ranshalla Despawn 18954 despawns her (vmangos SpellEffects.cpp:327-339).</summary>
[SpellScript(18955, 18993, 18954)]
public sealed class RanshallaTrapScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spell.Id == 18954)
        {
            WorldDummyCasts.Despawn(context.Target, 0);
        }
        else
        {
            context.System.RemoveAuras(context.Target, 18953);
        }
    }
}

/// <summary>Zero Mana/Full Health DND 23777 (vmangos SpellEffects.cpp:371-379): the caster is set to full health and no mana.</summary>
[SpellScript(23777)]
public sealed class ZeroManaFullHealthScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster;
        caster.Health = caster.MaxHealth;
        SpellSystem.SetPower(caster, PowerType.Mana, 0);
    }
}

/// <summary>
/// The Wildkin/Hippogryph/Faerie Dragon/Mountain Giant captures (11886-11889) and Summon Screecher Spirit 12699 (vmangos SpellEffects.cpp:461-474):
/// the dead creature they are used on despawns a second later. Capture Worg Pup 15998 and Despawn Caster 19614 despawn their (living) creature
/// target at once (:848-855, :485-491).
/// </summary>
[SpellScript(11886, 11887, 11888, 11889, 12699, 15998, 19614)]
public sealed class CaptureDespawnScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spell.Id is 15998 or 19614)
        {
            WorldDummyCasts.Despawn(context.Target, 0);
        }
        else if (!context.Target.IsAlive)
        {
            WorldDummyCasts.Despawn(context.Target, 1000);
        }
    }
}

/// <summary>Force Target - Bow 6655 and Force Self - Bow 25716 (vmangos SpellEffects.cpp:492-503): the target (or the caster) bows.</summary>
[SpellScript(6655, 25716)]
public sealed class ForceBowScript : ISpellScript
{
    public const uint EmoteOneshotBow = 2;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit unit = context.Spell.Id == 6655 ? context.Target : context.Caster;
        unit.Map?.BroadcastToObservers(unit, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(EmoteOneshotBow, unit.Guid));
    }
}

/// <summary>Nature's Bounty 27798 (vmangos SpellEffects.cpp:504-519): the target restores its own power type: rage 27783, energy 27784, mana 27782.</summary>
[SpellScript(27798)]
public sealed class NaturesBountyScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint spell = context.Target.PowerType switch
        {
            PowerType.Rage => 27783,
            PowerType.Energy => 27784,
            PowerType.Mana => 27782,
            _ => 0,
        };
        if (spell != 0)
        {
            GadgetCasts.Cast(context, context.Target, context.Target, spell);
        }
    }
}

/// <summary>Polly Eats the E.C.A.C. 9976 (vmangos SpellEffects.cpp:731-739): Polly summons Polly Jr. (9998) and despawns.</summary>
[SpellScript(9976)]
public sealed class PollyEatsEcacScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature polly)
        {
            GadgetCasts.Cast(context, polly, polly, 9998);
            WorldDummyCasts.Despawn(polly, 100);
        }
    }
}

/// <summary>Test Fetid Skull 17271 (vmangos SpellEffects.cpp:871-884): half Create Resonating Skull 17269, half Create Bone Dust 17270.</summary>
[SpellScript(17271)]
public sealed class TestFetidSkullScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Caster, GadgetCasts.Roll(context, 100) < 50 ? 17269u : 17270u);
    }
}

internal static class WorldDummyCasts
{
    /// <summary>vmangos Creature::ForcedDespawn(ms) for a creature target; other units are left alone.</summary>
    public static void Despawn(Unit unit, uint delayMs)
    {
        if (unit is Creature creature && creature.System is { } creatures)
        {
            creatures.ForcedDespawn(creature, delayMs);
        }
    }
}