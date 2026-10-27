using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Who tapped a creature (vmangos Creature::IsTappedBy). The world ships one source: <c>LootSoulShardTapSource</c> (ArcaneCore.Game.Loot),
/// which reads the loot tap through <c>LootService.IsTappedBy</c>, installed with <see cref="SoulShardRules.UseTapSource"/> by
/// <c>SpellRulesSoulShardFeature</c> (ArcaneCore.World.Spells). A creature with no recorded tap (a scripted
/// kill, or one no damage ever tapped) is untapped and gives no shard, as in vmangos, unless SpellRules:SoulShardUntappedCountsAsTapped is set
/// (default false); one on a map without a loot service counts as tapped, as it did before the source shipped. With no source installed (a bare SpellSystem) every kill counts as tapped (docs/areas/warlock-mage-utility.md).
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

    private static readonly InstanceAttachments<SpellSystem, ISoulShardTapSource> TapSources = new();

    /// <summary>Install (or replace, or with null remove) the tap source of <paramref name="system"/>.</summary>
    public static void UseTapSource(SpellSystem system, ISoulShardTapSource? source)
    {
        ArgumentNullException.ThrowIfNull(system);
        TapSources.Remove(system);
        if (source is not null)
        {
            TapSources.Attach(system, source);
        }
    }

    internal static ISoulShardTapSource? TapSourceOf(SpellSystem system) => TapSources.Find(system);

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
