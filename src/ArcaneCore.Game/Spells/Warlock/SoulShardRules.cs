using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Who tapped a creature (vmangos Creature::IsTappedBy). The combat system keeps no tap list yet (the threat-and-aggro lane owns it), so no
/// source ships here: a host that has one installs it with <see cref="SoulShardRules.UseTapSource"/>. Without a source the tap condition cannot
/// be evaluated and every kill counts as tapped (documented limit, docs/areas/warlock-mage-utility.md).
/// </summary>
public interface ISoulShardTapSource
{
    bool IsTappedBy(Creature victim, Player player);
}

/// <summary>The Soul Shard conditions of vmangos Aura::HandleChannelDeathItem (SpellAuras.cpp:2833-2880) and Player::IsHonorOrXPTarget (Player.cpp:19943-19957).</summary>
public static class SoulShardRules
{
    /// <summary>The Soul Shard item (vmangos SpellAuras.cpp:2855).</summary>
    public const uint SoulShard = 6265;

    /// <summary>SPELLFAMILY_WARLOCK.</summary>
    public const uint WarlockFamily = 5;

    private static readonly ConditionalWeakTable<SpellSystem, ISoulShardTapSource> TapSources = [];

    /// <summary>Install (or replace, or with null remove) the tap source of <paramref name="system"/>.</summary>
    public static void UseTapSource(SpellSystem system, ISoulShardTapSource? source)
    {
        ArgumentNullException.ThrowIfNull(system);
        TapSources.Remove(system);
        if (source is not null)
        {
            TapSources.Add(system, source);
        }
    }

    internal static ISoulShardTapSource? TapSourceOf(SpellSystem system) => TapSources.TryGetValue(system, out ISoulShardTapSource? source) ? source : null;

    /// <summary>
    /// vmangos Player::IsHonorOrXPTarget: a victim at or below the gray level of the player gives nothing, and neither does a totem or a pet.
    /// LIMITS: the creature template's xp multiplier of 0 and UNIT_STATE_NO_KILL_REWARD are not modelled (the template data does not carry them).
    /// </summary>
    public static bool IsHonorOrXpTarget(Player player, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(victim);
        if (victim.Level <= ExperienceFormulas.GrayLevel(player.Level))
        {
            return false;
        }

        return victim is not Creature { IsTotem: true } and not Creature { IsPet: true };
    }
}
