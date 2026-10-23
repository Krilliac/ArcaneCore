namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What the bystander rules read about one unit near a bot in trouble (world thread; filled from the live player).</summary>
internal readonly record struct PlayerbotBystanderFacts(bool Alive, bool SameFaction, bool InCombat, float HealthPercent, int Level, float Distance, bool InDuel);

/// <summary>
/// The pure rules of the bot's low-health window: who counts as a deliberate bystander, whether a losing bot calls for help or goes it alone, and
/// whether an asked bot answers. Every roll is a hash, never a <see cref="Random"/>, so a given bot and fight always decide the same way.
/// </summary>
internal static class PlayerbotCallForHelp
{
    /// <summary>The bot's health, in percent, below which its low-health window opens (the same trigger as the call for help).</summary>
    internal const float LowHealthPercent = 40f;

    /// <summary>The most candidates one window keeps.</summary>
    internal const int MaxCandidates = 16;

    /// <summary>How many levels below the bot a unit may be and still have mattered.</summary>
    internal const int LevelSlack = 5;

    /// <summary>Whether <paramref name="unit"/> is a deliberate bystander candidate at one moment: alive, of the bot's faction, out of combat, healthy, of a level that could have mattered, within range, and not in a duel.</summary>
    internal static bool Qualifies(PlayerbotSocialMemoryOptions options, int botLevel, in PlayerbotBystanderFacts unit)
        => unit.Alive && unit.SameFaction && !unit.InCombat && !unit.InDuel
            && unit.HealthPercent >= options.BystanderMinHealthPercent
            && unit.Level >= botLevel - LevelSlack
            && unit.Distance <= options.BystanderRadiusYards;

    /// <summary>Whether the window lasted long enough for a bystander's inaction to count.</summary>
    internal static bool WindowLongEnough(PlayerbotSocialMemoryOptions options, long windowMs) => windowMs >= options.BystanderMinSeconds * 1000L;

    /// <summary>
    /// Whether the fight looks lost: some attacker has a higher health fraction than the bot (the cheapest signal that is always readable; the
    /// risk model is not consulted).
    /// </summary>
    internal static bool LooksLost(float botHealthFraction, float strongestAttackerFraction)
        => strongestAttackerFraction > botHealthFraction;

    /// <summary>
    /// Whether a candidate may be asked: the bot does not beg a unit it dislikes (affinity below 0) or sees as a rival. Cross-faction units never
    /// reach this (they are not candidates).
    /// </summary>
    internal static bool Askable(bool known, int affinity, bool rival) => !rival && (!known || affinity >= 0);

    /// <summary>The chance a losing bot with a candidate nearby asks for help: base plus sociability times its weight plus 0.002 per point of the best affinity, clamped to 0..1.</summary>
    internal static double AskProbability(PlayerbotSocialMemoryOptions options, float sociability, int maxAffinity)
        => Math.Clamp(options.CallForHelpBaseChance + (options.CallForHelpSociabilityWeight * sociability) + (0.002 * maxAffinity), 0.0, 1.0);

    /// <summary>The bot's roll for the fight that began at <paramref name="fightStartMs"/>: below the probability it asks. Same inputs, same answer.</summary>
    internal static bool Asks(ulong botGuid, uint fightStartMs, double probability)
        => Roll(botGuid, 0xCA11F0A5UL ^ fightStartMs) < probability;

    /// <summary>The chance an asked bot answers: its helpfulness scaled by how it feels about the asker (1 + affinity/100), clamped to 0..1.</summary>
    internal static double AnswerProbability(float helpfulness, int affinity)
        => Math.Clamp(helpfulness * (1.0 + (affinity / 100.0)), 0.0, 1.0);

    /// <summary>Whether this bot answers this asker's call (the call made at <paramref name="askedAtMs"/>). Deterministic.</summary>
    internal static bool Answers(ulong botGuid, ulong askerGuid, uint askedAtMs, float helpfulness, int affinity)
        => Roll(PlayerbotContention.Mix(botGuid, askerGuid), 0xA45E12C0UL ^ askedAtMs) < AnswerProbability(helpfulness, affinity);

    private static double Roll(ulong a, ulong b) => (PlayerbotContention.Mix(a, b) >> 11) * (1.0 / 9007199254740992.0);
}
