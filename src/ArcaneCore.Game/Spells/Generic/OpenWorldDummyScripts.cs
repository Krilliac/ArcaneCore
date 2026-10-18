using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Dimensional Portal 6700 (vmangos SpellEffects.cpp:453-462, "Teleports Arugal to his lair"): a creature target ends the Shadowfang Keep intro
/// (SetData(TYPE_INTRO = 5, DONE)) and goes out of sight. Outside Shadowfang Keep the creature is only removed.
/// </summary>
[SpellScript(6700)]
public sealed class DimensionalPortalScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature creature)
        {
            return;
        }

        if (creature.Map?.FindUpdater<InstanceData>() is ShadowfangKeepInstance sfk)
        {
            sfk.OnDimensionalPortal(creature);
            return;
        }

        WorldDummyCasts.Despawn(creature, 0);
    }
}

/// <summary>
/// Call of the Ashbringer 28414 (vmangos SpellEffects.cpp:1317-1324): the player carrying the Corrupted Ashbringer hears one of its twelve
/// whispers (sounds 8906-8908, 8920-8928), played to that player alone.
/// </summary>
[SpellScript(28414)]
public sealed class CallOfTheAshbringerScript : ISpellScript
{
    public static readonly uint[] Whispers = [8906, 8907, 8908, 8920, 8921, 8922, 8923, 8924, 8925, 8926, 8927, 8928];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player player)
        {
            return;
        }

        var w = new PacketWriter(4);
        w.WriteUInt32(Whispers[GadgetCasts.Roll(context, Whispers.Length)]);
        player.Session.Send(WorldOpcode.SmsgPlaySound, w.ToArray());
    }
}

/// <summary>
/// Jubling Cooldown 23853 (vmangos SpellEffects.cpp:3745-3753): starts the 7-day Hatch Egg 23851 cooldown of the Unhatched Jubling Egg 19462 on
/// the target (from Dark Iron Ale Mug quest 7946 "Spawn of Jubjub").
/// </summary>
[SpellScript(23853)]
public sealed class JublingCooldownScript : ISpellScript
{
    public const uint HatchEgg = 23851, UnhatchedJublingEgg = 19462;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.System.StartItemSpellCooldown(context.Target, HatchEgg, UnhatchedJublingEgg);
    }
}

/// <summary>
/// Valentine End Check 27657 (vmangos SpellEffects.cpp:4373-4384): once Love is in the Air (game event 8) is over, the caster loses the
/// Amorous timers (26870, 27742), Amorous (26869) and Love is in the Air (27654, 27741).
/// </summary>
[SpellScript(27657)]
public sealed class ValentineEndCheckScript : ISpellScript
{
    public const ushort LoveIsInTheAirEvent = 8;
    public static readonly uint[] EventAuras = [26870, 27742, 26869, 27654, 27741];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.System.IsGameEventActive(LoveIsInTheAirEvent))
        {
            return;
        }

        foreach (uint aura in EventAuras)
        {
            context.System.RemoveAuras(context.Caster, aura);
        }
    }
}
