using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.ClassScripts;

/// <summary>
/// vmangos Player::RemoveSomeCooldown: every running spell cooldown whose spell matches finishes at once (SMSG_CLEAR_COOLDOWN per spell).
/// <c>SpellEntry::GetRecoveryTime</c> is the larger of the spell's own and its category recovery.
/// </summary>
public static class CooldownReset
{
    public static uint RecoveryTime(SpellInfo spell) => Math.Max(spell.RecoveryTime, spell.CategoryRecoveryTime);

    public static void RemoveSomeCooldown(SpellSystem system, Player player, Func<SpellInfo, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(matches);
        foreach (InitialSpellCooldown cooldown in system.GetActiveCooldowns(player).ToArray())
        {
            if (system.Store.Get(cooldown.SpellId) is { } spell && matches(spell))
            {
                system.ClearCooldown(player, cooldown.SpellId);
            }
        }
    }
}

/// <summary>Preparation (14185; vmangos SpellEffects.cpp:842-850): the rogue's spells with a recovery time are ready again.</summary>
[SpellScript(14185)]
public sealed class PreparationScript : ISpellScript
{
    public const uint RogueFamily = 8;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Effect.Effect == SpellEffectName.Dummy && context.Caster is Player player)
        {
            CooldownReset.RemoveSomeCooldown(context.System, player, s => s.SpellFamilyName == RogueFamily && CooldownReset.RecoveryTime(s) > 0);
        }
    }
}

/// <summary>Cold Snap (12472; vmangos scripts/spells/spell_mage.cpp:20-43): the mage's frost spells with a recovery time are ready again.</summary>
[SpellScript(12472)]
public sealed class ColdSnapScript : ISpellScript
{
    public const uint MageFamily = 3;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Caster is Player player)
        {
            CooldownReset.RemoveSomeCooldown(context.System, player,
                s => s.SpellFamilyName == MageFamily && s.School == SpellSchool.Frost && CooldownReset.RecoveryTime(s) > 0);
        }
    }
}

/// <summary>Readiness (23989; vmangos scripts/spells/spell_hunter.cpp:58-75): the hunter's abilities with a recovery time, Readiness excepted, are ready again.</summary>
[SpellScript(Readiness)]
public sealed class ReadinessScript : ISpellScript
{
    public const uint Readiness = 23989;
    public const uint HunterFamily = 9;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Caster is Player player)
        {
            CooldownReset.RemoveSomeCooldown(context.System, player,
                s => s.SpellFamilyName == HunterFamily && s.Id != Readiness && CooldownReset.RecoveryTime(s) > 0);
        }
    }
}
