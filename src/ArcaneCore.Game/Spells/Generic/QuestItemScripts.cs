using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Deviate Fish 8063 (vmangos src/scripts/spells/spell_item.cpp, DeviateFishScript): the player casts one of six effects on itself, picked with one
/// uniform draw: Sleepy 8064, Invigorate 8065, Shrink 8066, Party Time! 8067, Healthy Spirit 8068, Rejuvenation 8070.
/// </summary>
[SpellScript(8063)]
public sealed class DeviateFishScript : ISpellScript
{
    public static readonly uint[] Effects = [8064, 8065, 8066, 8067, 8068, 8070];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Caster is Player player)
        {
            GadgetCasts.Cast(context, player, player, Effects[GadgetCasts.Roll(context, Effects.Length)]);
        }
    }
}

/// <summary>
/// Cooked Deviate Fish 8213 (spell_item.cpp CookedDeviateFishScript): for client builds after 1.5.1 the draw is urand(0, 1) over the first two entries,
/// Flip Out (ninja) 8219/8220 or Yaaarrrr (pirate) 8221/8222 by the player's gender.
/// </summary>
[SpellScript(8213)]
public sealed class CookedDeviateFishScript : ISpellScript
{
    public static uint[] Choices(Gender gender) => gender == Gender.Male ? [8219, 8221] : [8220, 8222];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Caster is Player player)
        {
            uint[] choices = Choices(player.Gender);
            GadgetCasts.Cast(context, player, player, choices[GadgetCasts.Roll(context, 2)]);
        }
    }
}

/// <summary>Noggenfogger Elixir 16589 (spell_item.cpp): skeleton 16591 (60%), mini 16595 (20%), slow fall 16593 (20%) from one draw of 1 to 10.</summary>
[SpellScript(16589)]
public sealed class NoggenfoggerElixirScript : ISpellScript
{
    public static uint Pick(int oneToTen) => oneToTen switch
    {
        1 or 2 => 16595,
        3 or 4 => 16593,
        _ => 16591,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Caster is Player player)
        {
            GadgetCasts.Cast(context, player, player, Pick(GadgetCasts.Roll(context, 10) + 1));
        }
    }
}

/// <summary>Curse of the Eye 3360 (vmangos SpellEffects.cpp:657-665): the caster casts Curse of the Eye 10651 on a male target, 10653 on a female one.</summary>
[SpellScript(3360)]
public sealed class CurseOfTheEyeScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Target, context.Target.Gender == Gender.Male ? 10651u : 10653u);
    }
}
