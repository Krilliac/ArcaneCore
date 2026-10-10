using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Holidays;

/// <summary>Shared casts of the holiday spell scripts (vmangos Spell::EffectDummy / EffectScriptEffect cases).</summary>
internal static class HolidayCasts
{
    public static void Self(SpellEffectContext context, Unit unit, uint spellId, bool triggered = true)
        => context.System.CastSpell(unit, spellId, SpellCastTargets.ForSelf(), triggered);

    public static void On(SpellEffectContext context, Unit caster, Unit target, uint spellId)
        => context.System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    public static bool Male(Unit unit) => unit.Gender == Gender.Male;
}

/// <summary>Hallow's End Treat 24930 (vmangos SpellEffects.cpp:1174-1182, EffectDummy): the caster gets one of four random treats.</summary>
[SpellScript(24930)]
public sealed class HallowsEndTreatScript : ISpellScript
{
    internal static readonly uint[] Treats = [24924, 24925, 24926, 24927];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HolidayCasts.Self(context, context.Caster, Treats[context.System.Random.Next(Treats.Length)]);
    }
}

/// <summary>Trick 24714 (vmangos SpellEffects.cpp:3846-3867): a player caster is turned into one of eight costumes, by the target's gender.</summary>
[SpellScript(24714)]
public sealed class HallowsEndTrickScript : ISpellScript
{
    internal static uint[] Costumes(bool male) =>
    [
        male ? 24708u : 24709u, // Pirate
        male ? 24711u : 24710u, // Ninja
        male ? 24712u : 24713u, // Leper
        male ? 24735u : 24736u, // Ghost
        24723u,                 // Skeleton
        24732u,                 // Bat
        24740u,                 // Wisp
        24753u,                 // Critter
    ];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player) return;
        uint[] costumes = Costumes(HolidayCasts.Male(context.Target));
        HolidayCasts.Self(context, context.Caster, costumes[context.System.Random.Next(costumes.Length)]);
    }
}

/// <summary>
/// The Hallowed Wand costumes (vmangos SpellEffects.cpp:3868-3965): Pirate 24717, Ninja 24718, Leper Gnome 24719, Random 24720, Ghost 24737.
/// Only a player out of combat is costumed.
/// </summary>
[SpellScript(24717, 24718, 24719, 24720, 24737)]
public sealed class HallowedWandScript : ISpellScript
{
    internal static uint CostumeFor(uint spellId, bool male, int roll) => spellId switch
    {
        24717 => male ? 24708u : 24709u,
        24718 => male ? 24711u : 24710u,
        24719 => male ? 24712u : 24713u,
        24737 => male ? 24735u : 24736u,
        _ => roll switch // 24720: urand(0, 6)
        {
            0 => male ? 24708u : 24709u,
            1 => male ? 24711u : 24710u,
            2 => male ? 24712u : 24713u,
            3 => 24723u,
            4 => 24732u,
            5 => male ? 24735u : 24736u,
            _ => 24740u,
        },
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Player target || target.Combat.IsInCombat) return;
        HolidayCasts.On(context, context.Caster, target,
            CostumeFor(context.Spell.Id, HolidayCasts.Male(target), context.System.Random.Next(7)));
    }
}

/// <summary>Trick or Treat 24751 (vmangos SpellEffects.cpp:3976-3988): the player is marked Tricked or Treated (24755), then a 50% Treat or Trick.</summary>
[SpellScript(24751)]
public sealed class TrickOrTreatScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Player target) return;
        HolidayCasts.Self(context, target, 24755);
        HolidayCasts.Self(context, target, context.System.Random.Next(100) < 50 ? 24714u : 24715u);
    }
}

/// <summary>Mistletoe 26004 (vmangos SpellEffects.cpp:4016-4023): the target casts 26005 on itself.</summary>
[SpellScript(26004)]
public sealed class MistletoeScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HolidayCasts.Self(context, context.Target, 26005);
    }
}

/// <summary>Mistletoe 26218 (vmangos SpellEffects.cpp:4032-4039): a player target gets 26206 or 26207 from the caster.</summary>
[SpellScript(26218)]
public sealed class MistletoeKissScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Player target) return;
        HolidayCasts.On(context, context.Caster, target, context.System.Random.Next(2) == 0 ? 26206u : 26207u);
    }
}

/// <summary>
/// PX-238 Winter Wondervolt TRAP 26275 (vmangos SpellEffects.cpp:4040-4054): a target within a yard loses any Wondervolt form and takes a
/// random one of the four.
/// </summary>
[SpellScript(26275)]
public sealed class WinterWondervoltScript : ISpellScript
{
    internal static readonly uint[] Forms = [26272, 26157, 26273, 26274];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        float dx = context.Caster.X - target.X, dy = context.Caster.Y - target.Y, dz = context.Caster.Z - target.Z;
        if (dx * dx + dy * dy + dz * dz > 1f) return;
        foreach (uint form in Forms)
            if (context.System.HasAura(target, form)) context.System.RemoveAuras(target, form, AuraRemoveMode.Cancel);
        HolidayCasts.Self(context, target, Forms[context.System.Random.Next(Forms.Length)]);
    }
}

/// <summary>Test Ribbon Pole Channel Trigger 29710 (vmangos SpellEffects.cpp:4407-4412): a player target channels one of the three ribbon spells.</summary>
[SpellScript(29710)]
public sealed class RibbonPoleTriggerScript : ISpellScript
{
    internal static readonly uint[] Channels = [29705, 29726, 29727];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player target)
            HolidayCasts.Self(context, target, Channels[context.System.Random.Next(Channels.Length)], triggered: false);
    }
}
