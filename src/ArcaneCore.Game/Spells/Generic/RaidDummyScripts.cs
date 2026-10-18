using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>Stinger Charge Primer 24150 (vmangos SpellEffects.cpp:1154-1164): each enemy hit is charged with 25191 when it carries Hive'Zara Catalyst 25187, else 25190.</summary>
[SpellScript(24150)]
public sealed class StingerChargePrimerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ReferenceEquals(context.Target, context.Caster))
        {
            return;
        }

        uint charge = context.System.HasAura(context.Target, 25187) ? 25191u : 25190u;
        GadgetCasts.Cast(context, context.Caster, context.Target, charge);
    }
}

/// <summary>Atiesh, Breath of Sargeras 28352 (vmangos SpellEffects.cpp:4323-4328): each enemy hit gets 28342 (-1000 melee and ranged attack power).</summary>
[SpellScript(28352)]
public sealed class AtieshBreathOfSargerasScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Target, context.Caster))
        {
            GadgetCasts.Cast(context, context.Caster, context.Target, 28342);
        }
    }
}

/// <summary>Mana Burn Area 26626 (vmangos SpellEffects.cpp:1250-1257): a mana user hit is burned with 25779.</summary>
[SpellScript(26626)]
public sealed class ManaBurnAreaScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.PowerType == PowerType.Mana && !ReferenceEquals(context.Target, context.Caster))
        {
            GadgetCasts.Cast(context, context.Caster, context.Target, 25779);
        }
    }
}

/// <summary>Arcane Cloaking 28006 (vmangos SpellEffects.cpp:1258-1265): a player hit gets the Naxxramas entry flag 29296.</summary>
[SpellScript(28006)]
public sealed class ArcaneCloakingScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player)
        {
            GadgetCasts.Cast(context, context.Caster, context.Target, 29296);
        }
    }
}

/// <summary>Despawn Tentacles 26399 (C'Thun; vmangos SpellEffects.cpp:1243-1249): each tentacle hit dies.</summary>
[SpellScript(26399)]
public sealed class DespawnTentaclesScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.Target, context.Caster) && context.Target.IsAlive && context.Caster.Map?.FindUpdater<MapCombat>() is { } combat)
        {
            combat.Kill(context.Caster, context.Target, durabilityLoss: false, context.Spell);
        }
    }
}

/// <summary>
/// The Ahn'Qiraj Drain Mana chain (vmangos SpellEffects.cpp:3997-4015): Moam 25754 and the Obsidian Nullifier 26559 cast 25671, the Obsidian
/// Destroyer 25676 and Eradicator 26457 cast 25755, on each target; 25671 and 25755 then make the drained unit give the caster 26639.
/// </summary>
[SpellScript(25754, 26559, 25676, 26457, 25671, 25755)]
public sealed class AhnQirajDrainManaScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ReferenceEquals(context.Target, context.Caster))
        {
            return;
        }

        switch (context.Spell.Id)
        {
            case 25671 or 25755:
                GadgetCasts.Cast(context, context.Target, context.Caster, 26639);
                break;
            case 25754 or 26559:
                GadgetCasts.Cast(context, context.Caster, context.Target, 25671);
                break;
            default:
                GadgetCasts.Cast(context, context.Caster, context.Target, 25755);
                break;
        }
    }
}

/// <summary>AQ40 teleporters (vmangos SpellEffects.cpp:4302-4313): Teleport to Twin Emperors 29182 → 29181, Teleport to Final Chamber 29188 → 29190, players only.</summary>
[SpellScript(29182, 29188)]
public sealed class AhnQirajTeleportScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player player)
        {
            GadgetCasts.Cast(context, player, player, context.Spell.Id == 29182 ? 29181u : 29190u);
        }
    }
}

/// <summary>Despawn Ice Block 30132 (Sapphiron; vmangos SpellEffects.cpp:4314-4322): a player loses Icebolt 28522 and its immunity 31800.</summary>
[SpellScript(30132)]
public sealed class DespawnIceBlockScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player player)
        {
            context.System.RemoveAuras(player, 31800);
            context.System.RemoveAuras(player, 28522);
        }
    }
}

/// <summary>Summon Bone Minions 27687 (27690-27693) and Summon Bone Mages 27695 (27696-27699) (vmangos SpellEffects.cpp:4206-4228): the target casts all four.</summary>
[SpellScript(27687, 27695)]
public sealed class SummonBoneServantsScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint first = context.Spell.Id == 27687 ? 27690u : 27696u;
        for (uint id = first; id < first + 4; id++)
        {
            GadgetCasts.Cast(context, context.Target, context.Target, id);
        }
    }
}

/// <summary>Poisonous Blood 24320 (Hakkar; vmangos SpellEffects.cpp:3806-3810): the target gives itself 24321.</summary>
[SpellScript(24320)]
public sealed class PoisonousBloodScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Target, context.Target, 24321);
    }
}

/// <summary>Brittle Armor 24590 (24575) and Mercurial Shield 26465 (26464) (vmangos SpellEffects.cpp:3829-3831, :4055-4057): one stack of the trinket buff goes.</summary>
[SpellScript(24590, 26465)]
public sealed class TrinketStackDropScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.System.RemoveScriptAuraStack(context.Target, context.Spell.Id == 24590 ? 24575u : 26464u);
    }
}

/// <summary>
/// Summon Black Qiraji Battle Tank 26656 (vmangos SpellEffects.cpp:4058-4077): other mounts go, then 26655 where mounting is allowed and 25863
/// (the AQ40-only tank) elsewhere, cast with the item.
/// </summary>
[SpellScript(26656)]
public sealed class BlackQirajiBattleTankScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        context.System.RemoveSpellsCausingAura(target, AuraType.Mounted);
        bool mountAllowed = target.Map?.Template is not { } template || MountCastCheck.IsMountAllowed(template);
        GadgetCasts.Cast(context, target, target, mountAllowed ? 26655u : 25863u, context.Cast.CastItem);
    }
}

/// <summary>Destroy Rocket Boots 8897 (vmangos SpellEffects.cpp:1292-1296): 8893 on the target.</summary>
[SpellScript(8897)]
public sealed class DestroyRocketBootsScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Target, 8893);
    }
}

/// <summary>
/// Aura of Frost 23185 (Azuregos) and Aura of Nature 25044 (the Dragons of Nightmare) (vmangos SpellEffects.cpp:1297-1316): a target with Mark
/// of Nature 25040 gets 25043, one with Mark of Frost 23182 gets 23186; bandaging and stealth end and a sitting target stands.
/// </summary>
[SpellScript(23185, 25044)]
public sealed class AuraOfFrostNatureScript : ISpellScript
{
    private const uint MechanicBandageMask = 1u << (16 - 1);

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        if (ReferenceEquals(target, context.Caster))
        {
            return;
        }

        if (context.System.HasAura(target, 25040))
        {
            GadgetCasts.Cast(context, target, target, 25043);
        }

        if (context.System.HasAura(target, 23182))
        {
            GadgetCasts.Cast(context, target, target, 23186);
        }

        foreach (SpellAuraHolder holder in context.System.GetAuras(target).ToArray())
        {
            if (!holder.IsRemoved && !holder.Spell.IgnoresImmunities() && (holder.Spell.AllMechanicMask() & MechanicBandageMask) != 0)
            {
                context.System.RemoveAuras(target, holder.Spell.Id);
            }
        }

        context.System.RemoveSpellsCausingAura(target, AuraType.ModStealth);
        if (target.StandState != StandState.Stand)
        {
            if (target is Player player)
            {
                player.SetStandState(StandState.Stand);
            }
            else
            {
                target.StandState = StandState.Stand;
            }
        }
    }
}