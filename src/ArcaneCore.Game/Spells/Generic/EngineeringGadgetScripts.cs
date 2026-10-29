using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>Shared casts of the generic item and gadget scripts (vmangos Spell::EffectDummy cases, SpellEffects.cpp).</summary>
internal static class GadgetCasts
{
    /// <summary><c>caster->CastSpell(target, spellId, true, castItem)</c>.</summary>
    public static void Cast(SpellEffectContext context, Unit caster, Unit target, uint spellId, Items.Item? castItem = null)
        => context.System.CastCustomSpell(caster, spellId, ReferenceEquals(caster, target) ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid, context.Cast),
            null, castItem: castItem);

    public static int Roll(SpellEffectContext context, int maxExclusive) => context.System.Random.Next(maxExclusive);
}

/// <summary>
/// The engineering summon trinkets whose item spell is a dummy naming the real summon (vmangos SpellEffects.cpp:1039-1070): Arcanite Dragonling
/// 23074 → 19804, Mithril Mechanical Dragonling 23075 → 12749, Mechanical Dragonling 23076 → 4073, Gnomish Battle Chicken 23133 → 13166. Only
/// an item cast summons (the summon is cast with the item, so it reads as the item's).
/// </summary>
[SpellScript(23074, 23075, 23076, 23133)]
public sealed class EngineeringSummonTrinketScript : ISpellScript
{
    public static uint SummonFor(uint spellId) => spellId switch
    {
        23074 => 19804,
        23075 => 12749,
        23076 => 4073,
        _ => 13166,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Cast.CastItem is not { } item)
        {
            return;
        }

        GadgetCasts.Cast(context, context.Caster, context.Caster, SummonFor(context.Spell.Id), item);
    }
}

/// <summary>
/// Goblin Bomb Dispenser 23134 (vmangos SpellEffects.cpp:1071-1084): one use in ten blows up in the user's face (Malfunction Explosion 13261
/// cast by the target on the user), otherwise Summon Goblin Bomb 13258 with the item.
/// </summary>
[SpellScript(23134)]
public sealed class GoblinBombDispenserScript : ISpellScript
{
    public const uint Malfunction = 13261, SummonBomb = 13258;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Cast.CastItem is not { } item)
        {
            return;
        }

        if (GadgetCasts.Roll(context, 100) < 10)
        {
            GadgetCasts.Cast(context, context.Target, context.Caster, Malfunction);
        }
        else
        {
            GadgetCasts.Cast(context, context.Caster, context.Caster, SummonBomb, item);
        }
    }
}

/// <summary>
/// Explosive Sheep 4050 (vmangos SpellEffects.cpp:1085-1096): when the sheep blows up, its player owner's creating spell (UNIT_CREATED_BY_SPELL)
/// starts its cooldown, which COOLDOWN_ON_EVENT held while the sheep lived.
/// </summary>
[SpellScript(4050)]
public sealed class ExplosiveSheepScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect != SpellEffectName.Dummy || context.Caster.GetCharmerOrOwner() is not Player owner)
        {
            return;
        }

        uint created = context.Caster.GetUInt32(UpdateFields.UnitCreatedBySpell);
        if (created != 0)
        {
            context.System.StartCreatingSpellCooldown(owner, created);
        }
    }
}

/// <summary>
/// Ultrasafe Transporter: Gadgetzan (vmangos SpellEffects.cpp:1109-1142). Gnomish Transporter 23453: half the time Gadgetzan Transporter 23441,
/// otherwise a quarter Gadgetzan Transporter Failure 23446 and a quarter a drop high above Gadgetzan. Transporter Arrival 23448 (the failure's
/// trigger): 1 in 6 Transporter Malfunction 23444, 4 in 6 Evil Twin 23445, else nothing.
/// </summary>
[SpellScript(23453, 23448)]
public sealed class GadgetzanTransporterScript : ISpellScript
{
    public const uint Success = 23441, Failure = 23446, Malfunction = 23444, EvilTwin = 23445;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster;
        if (context.Spell.Id == 23448)
        {
            int r = GadgetCasts.Roll(context, 120);
            if (r < 20)
            {
                GadgetCasts.Cast(context, caster, caster, Malfunction);
            }
            else if (r < 100)
            {
                GadgetCasts.Cast(context, caster, caster, EvilTwin);
            }

            return;
        }

        if (GadgetCasts.Roll(context, 100) < 50)
        {
            GadgetCasts.Cast(context, caster, caster, Success);
        }
        else if (GadgetCasts.Roll(context, 100) < 50)
        {
            GadgetCasts.Cast(context, caster, caster, Failure);
        }
        else if (caster is Player player)
        {
            context.System.Teleports.Teleport(player, 1, -7341.38f, -3908.11f, 150.7f, 0.51f);
        }
    }
}

/// <summary>Universal Remote 8344 (vmangos SpellEffects.cpp:520-543): Control Machine 8345, 8346, or the target casts 8599 on itself.</summary>
[SpellScript(8344)]
public sealed class UniversalRemoteScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (GadgetCasts.Roll(context, 3))
        {
            case 0: GadgetCasts.Cast(context, context.Caster, context.Target, 8345); break;
            case 1: GadgetCasts.Cast(context, context.Caster, context.Target, 8346); break;
            default: GadgetCasts.Cast(context, context.Target, context.Target, 8599); break;
        }
    }
}

/// <summary>
/// Gnomish Shrink Ray 13006 (vmangos SpellEffects.cpp:544-581): 84% Shrink Ray 13003 on the target; else the user grows (13004) or shrinks
/// (13010), the target grows, or the user's party shrinks or grows.
/// </summary>
[SpellScript(13006)]
public sealed class GnomishShrinkRayScript : ISpellScript
{
    public const uint ShrinkRay = 13003, Grow = 13004, Shrink = 13010;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster, target = context.Target;
        int r = GadgetCasts.Roll(context, 100);
        if (r > 15) GadgetCasts.Cast(context, caster, target, ShrinkRay);
        else if (r > 13) context.System.AddAura(caster, Grow, caster: caster);
        else if (r > 11) context.System.AddAura(caster, Shrink, caster: caster);
        else if (r > 9) context.System.AddAura(target, Grow, caster: caster);
        else if (r > 4) GadgetCasts.Cast(context, caster, target, Shrink);
        else GadgetCasts.Cast(context, caster, target, Grow);
    }
}

/// <summary>
/// Gnomish Mind Control Cap 13180 (vmangos SpellEffects.cpp:582-604): 1 in 6 nothing, 1 in 6 the target mind controls the user (not while
/// shapeshifted), otherwise the user mind controls the target (13181).
/// </summary>
[SpellScript(13180)]
public sealed class GnomishMindControlCapScript : ISpellScript
{
    public const uint MindControl = 13181;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (GadgetCasts.Roll(context, 6))
        {
            case 0:
                break;
            case 1:
                if (FormQueries.GetForm(context.Caster) == 0)
                {
                    GadgetCasts.Cast(context, context.Target, context.Caster, MindControl);
                }

                break;
            default:
                GadgetCasts.Cast(context, context.Caster, context.Target, MindControl);
                break;
        }
    }
}

/// <summary>
/// Six Demon Bag 14537 (vmangos SpellEffects.cpp:615-647): Fireball 11921 25%, Frostbolt 13322 25%, Chain Lightning 21179 20%, Polymorph 13323 on
/// the target 7% or on the user 3%, Enveloping Winds 25189 15%, Summon Felhound Minion 14642 5%; cast with the item.
/// </summary>
[SpellScript(14537)]
public sealed class SixDemonBagScript : ISpellScript
{
    public static (uint Spell, bool OnSelf) Pick(int roll) => roll switch
    {
        < 25 => (11921, false),
        < 50 => (13322, false),
        < 70 => (21179, false),
        < 77 => (13323, false),
        < 80 => (13323, true),
        < 95 => (25189, false),
        _ => (14642, true),
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        (uint spell, bool onSelf) = Pick(GadgetCasts.Roll(context, 100));
        GadgetCasts.Cast(context, context.Caster, onSelf ? context.Caster : context.Target, spell, context.Cast.CastItem);
    }
}

/// <summary>Voodoo 17009 (vmangos SpellEffects.cpp:648-656): a player target gets one of seven random hexes.</summary>
[SpellScript(17009)]
public sealed class VoodooScript : ISpellScript
{
    internal static readonly uint[] Hexes = [16707, 16708, 16709, 16711, 16712, 16713, 16716];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player)
        {
            GadgetCasts.Cast(context, context.Caster, context.Target, Hexes[GadgetCasts.Roll(context, Hexes.Length)]);
        }
    }
}

/// <summary>Net-o-Matic 13120 (vmangos SpellEffects.cpp:786-803): 10% the user roots itself (16566), 10% 13119, otherwise the net 13099.</summary>
[SpellScript(13120)]
public sealed class NetOMaticScript : ISpellScript
{
    public static uint Pick(int roll) => roll < 10 ? 16566u : roll < 20 ? 13119u : 13099u;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Target, Pick(GadgetCasts.Roll(context, 100)));
    }
}

/// <summary>Gift of Life 23725, the Lifegiving Gem (vmangos SpellEffects.cpp:1147-1153): 23782 with 15% of the user's maximum health.</summary>
[SpellScript(23725)]
public sealed class GiftOfLifeScript : ISpellScript
{
    public const uint GiftOfLifeAura = 23782;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int health = (int)(context.Caster.MaxHealth * 0.15f);
        context.System.CastCustomSpell(context.Caster, GiftOfLifeAura, SpellCastTargets.ForSelf(), health);
    }
}