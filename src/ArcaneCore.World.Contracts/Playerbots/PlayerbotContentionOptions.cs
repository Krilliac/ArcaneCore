namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Contention</c>: how bots share the mobs of one place instead of piling onto one (docs/areas/playbots.md, Contention).
/// Every key is live: <c>.reload config</c> changes this object in place and a bot reads it at its next think. Not vmangos keys.
/// </summary>
public sealed class PlayerbotContentionOptions
{
    /// <summary>
    /// Base chance (0..1) that a bot walking to a mob another player has tagged decides to help that player instead of leaving the mob
    /// alone. It is scaled by the bot's own sociability (a stable 0..1 from its guid), doubled for a real player's tag and raised by half
    /// when both share the kill quest, and only asked when the tagger is within 30 yards and losing and the bot is healthy. 0 never helps.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 1 ('.' is the decimal point); 0 never helps, 1 helps whenever the other conditions hold")]
    public float HelpChance { get; set; } = 0.35f;

    /// <summary>
    /// Yards a mob counts as farther away for each other bot already walking to it (0..200), when a bot picks among untagged mobs.
    /// 0 turns the soft claims off, so every bot goes for the nearest mob again.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 200 ('.' is the decimal point); 0 turns the soft claims off")]
    public float ClaimPenaltyYards { get; set; } = 25f;

    /// <summary>Seconds a bot leaves a mob alone after it found it to be someone else's (1..600), and the least between two decisions about the same mob.</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 600 (seconds)")]
    public int SomeoneElsesMemorySeconds { get; set; } = 15;

    /// <summary>
    /// Minutes a bot leaves a quest creature alone after three minutes of contested hunting without credit (1..120; see <c>PlayerbotContention.ObserveObjective</c>): it hunts its next
    /// unfinished objective or quest meanwhile, and asks for a group when none is left.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 120 (minutes)")]
    public int ContentionSuspendMinutes { get; set; } = 5;

    /// <summary>
    /// Seconds a bot may hold a target whose health stays at or below its death-prevention threshold (<c>InvincibilityHpThreshold</c>: a creature
    /// an EventAI script keeps at 1 health until some condition is met, such as the Blasted Lands servants of quest 2681) before it sets the
    /// creature aside as one it cannot kill (1..600).
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 600 (seconds)")]
    public int InvincibleTargetAbandonSeconds { get; set; } = 8;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Contention";
        if (CheckChance(HelpChance) is { } chance) throw new InvalidOperationException($"{section}: HelpChance {chance}.");
        if (CheckPenalty(ClaimPenaltyYards) is { } penalty) throw new InvalidOperationException($"{section}: ClaimPenaltyYards {penalty}.");
        if (CheckMemory(SomeoneElsesMemorySeconds) is { } memory) throw new InvalidOperationException($"{section}: SomeoneElsesMemorySeconds {memory}.");
        if (CheckSuspend(ContentionSuspendMinutes) is { } suspend) throw new InvalidOperationException($"{section}: ContentionSuspendMinutes {suspend}.");
        if (CheckMemory(InvincibleTargetAbandonSeconds) is { } invincible) throw new InvalidOperationException($"{section}: InvincibleTargetAbandonSeconds {invincible}.");
    }

    internal static string? CheckChance(float value) => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    internal static string? CheckPenalty(float value) => float.IsFinite(value) && value is >= 0f and <= 200f ? null : "must be 0..200";

    internal static string? CheckMemory(int value) => value is >= 1 and <= 600 ? null : "must be 1..600";

    internal static string? CheckSuspend(int value) => value is >= 1 and <= 120 ? null : "must be 1..120";
}
