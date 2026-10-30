using System.Globalization;
using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.Game.WorldState;

/// <summary>
/// War Mode, a server-wide world-PvP switch (configuration section <c>World:WarMode</c>). It is a deliberate deviation from 1.12.1 and
/// nothing like retail War Mode: there is no phasing, no per-player toggle and no bonus XP or reputation. While <see cref="Enabled"/>, every
/// player and every bot is PvP-flagged outside the exempt areas (<see cref="Zones.PvpAreaState.IsWarModeExempt"/>), an honourable kill is
/// worth <see cref="HonorMultiplier"/> times as much honour, and an open-world PvP death wears <see cref="PvpDeathDurabilityLoss"/> of the
/// victim's equipment. Every key is live; the object is shared by reference with every reader (the world's <see cref="WorldStateHooks.WarMode"/>).
/// </summary>
public sealed class WarModeOptions
{
    public const string SectionName = "World:WarMode";

    /// <summary>Lowest honour multiplier.</summary>
    public const float MinHonorMultiplier = 1f;

    /// <summary>Highest honour multiplier.</summary>
    public const float MaxHonorMultiplier = 10f;

    /// <summary>
    /// Switch War Mode on for every player and bot, with no opt-out. Default false (retail). Switching it off returns the flags to
    /// the normal rules: a flag then lapses by its 5-minute timer unless the player wants it.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// While War Mode is on, the honour of an honourable kill is multiplied by this (1 to 10, default 1.5). 1 gives the same honour as with War Mode off.
    /// </summary>
    [ConfigAllowed("1 to 10")]
    public float HonorMultiplier { get; set; } = 1.5f;

    /// <summary>
    /// While War Mode is on, the fraction of every equipped item's durability a player loses when killed by another player outside a
    /// battleground (0 to 1, default 0.10). 0 costs nothing. Retail, and War Mode off: a PvP death costs no durability.
    /// <c>Items:DurabilityLossEnable</c> = false still turns all durability loss off.
    /// </summary>
    [ConfigAllowed("0 to 1")]
    public float PvpDeathDurabilityLoss { get; set; } = 0.10f;

    /// <summary>The problem with a <see cref="HonorMultiplier"/> value, or null.</summary>
    public static string? CheckHonorMultiplier(float value)
        => float.IsFinite(value) && value is >= MinHonorMultiplier and <= MaxHonorMultiplier ? null : "must be 1..10";

    /// <summary>The problem with a <see cref="PvpDeathDurabilityLoss"/> value, or null.</summary>
    public static string? CheckPvpDeathDurabilityLoss(float value)
        => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first key that is out of range.</summary>
    public void Validate()
    {
        if (CheckHonorMultiplier(HonorMultiplier) is { } honor)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{nameof(HonorMultiplier)} ({HonorMultiplier}) {honor}."));
        }

        if (CheckPvpDeathDurabilityLoss(PvpDeathDurabilityLoss) is { } loss)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{nameof(PvpDeathDurabilityLoss)} ({PvpDeathDurabilityLoss}) {loss}."));
        }
    }
}
