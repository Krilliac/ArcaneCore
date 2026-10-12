using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// How the economy bot values an item for its own slots: what wearing a new item of a template would gain over the worse slot it fits (null
/// when the bot cannot use it). The default is the progression scorer (<c>PlayerbotItemScore.UpgradeGain</c> under the bot's talent build
/// weights, the same score <see cref="PlayerbotEquipment"/> equips by, so a bought item is worn once collected).
/// <para>
/// Integration note: the seam is one static delegate. A later gear scorer (the boost gear scoring of the home lane) replaces it by assigning
/// <see cref="UpgradeGain"/> at startup; nothing else in the economy folder changes. The economy bot reads the delegate at every decision and
/// treats an exception from it as "no gain" for that item.
/// </para>
/// </summary>
internal static class PlayerbotGearScoreSeam
{
    /// <summary>The scorer in use (world thread; tests replace it and restore it).</summary>
    internal static Func<Player, ItemTemplate, float?> UpgradeGain { get; set; } = DefaultUpgradeGain;

    /// <summary>The progression scorer under the bot's own talent build weights.</summary>
    internal static float? DefaultUpgradeGain(Player player, ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(template);
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        return PlayerbotItemScore.UpgradeGain(player, template, weights);
    }

    /// <summary>The seam's answer for one item, with a faulty scorer read as "no gain".</summary>
    internal static float? Gain(Player player, ItemTemplate template)
    {
        try
        {
            float? gain = UpgradeGain(player, template);
            return gain is { } value && float.IsFinite(value) ? value : null;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("playerbot.gearscore", e);
            return null;
        }
    }
}
