namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Memory</c>: each managed bot's bounded, persisted memory of the players and bots it has dealt with
/// (affinity, familiarity, trust, flags, last topic and a short summary). Every key is live: the memory reads this object at each event.
/// The memory records only what the bot saw and did; the stored topic and summary come from bot-side text, never from a player's line.
/// </summary>
public sealed class PlayerbotSocialMemoryOptions
{
    /// <summary>Record and use the memory (on by default). Off: nothing is noted and the speech ignores any stored relations.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most relations one bot keeps (16..1024). Past it the least familiar, oldest entry that is not a strong bond or grudge is dropped; with War Mode on and RemoveCaps, one the war layer holds a grudge against stays as a war-only entry that only War Mode sees (a bot holds at most four times this many entries in all).</summary>
    public int MaxRelationsPerBot { get; set; } = 256;

    /// <summary>Affinity gained when a chat exchange with the bot happens (0..20).</summary>
    public int AffinityPerChat { get; set; } = 1;

    /// <summary>Affinity gained when the bot is grouped with someone (0..30).</summary>
    public int AffinityPerGroup { get; set; } = 3;

    /// <summary>Affinity gained when a group the bot was in succeeds at its run (0..30).</summary>
    public int AffinityPerGroupSuccess { get; set; } = 5;

    /// <summary>Affinity gained when a trade with the bot completes (0..30).</summary>
    public int AffinityPerTrade { get; set; } = 3;

    /// <summary>Affinity gained toward someone who helped the bot, and toward someone the bot helped (0..50).</summary>
    public int AffinityPerHelp { get; set; } = 8;

    /// <summary>Affinity change when the chat safety screen strikes a player's line to the bot (-100..0).</summary>
    public int AffinityPerStrike { get; set; } = -15;

    /// <summary>Affinity change toward a player who kicks the bot from a group (-100..0).</summary>
    public int AffinityPerKick { get; set; } = -10;

    /// <summary>Days after which a relation's familiarity has halved when it is not refreshed (1..365).</summary>
    public int FamiliarityHalfLifeDays { get; set; } = 30;

    /// <summary>Affinity change toward someone who killed the bot in PvP (-100..0).</summary>
    public int AffinityPerKilledBy { get; set; } = -20;

    /// <summary>Affinity change toward someone the bot killed in PvP (-100..0); rivalry cuts both ways.</summary>
    public int AffinityPerKilledThem { get; set; } = -5;

    /// <summary>Affinity gained when someone makes a friendly emote at the bot (0..20).</summary>
    public int AffinityPerFriendlyEmote { get; set; } = 2;

    /// <summary>Affinity change when someone makes a rude emote at the bot (-100..0).</summary>
    public int AffinityPerRudeEmote { get; set; } = -5;

    /// <summary>At or below this affinity the Rival flag is set; above it again, the flag is cleared (-100..0).</summary>
    public int RivalAffinity { get; set; } = -50;

    /// <summary>A chat exchange or emote raises familiarity at most once per pair in this window, in minutes (0..1440; 0 counts every one).</summary>
    public int ChatFamiliarityCooldownMinutes { get; set; } = 10;

    /// <summary>Affinity change toward a same-faction bystander who watched the bot die without helping (-100..0).</summary>
    public int AffinityPerWatchedDie { get; set; } = -12;

    /// <summary>How close, in yards, a unit must stay to the bot through its low-health window to count as a bystander (5..60).</summary>
    public int BystanderRadiusYards { get; set; } = 30;

    /// <summary>The least health percent a bystander needs to count as able to help (1..100).</summary>
    public int BystanderMinHealthPercent { get; set; } = 50;

    /// <summary>The shortest low-health window, in seconds, in which a bystander's inaction counts (1..60).</summary>
    public int BystanderMinSeconds { get; set; } = 5;

    /// <summary>
    /// Let a bot that is losing a fight decide, once per fight, to call for help from a healthy same-faction passer-by (a line and a help
    /// emote) or go it alone. Only the units it asked can later be blamed (<see cref="AffinityPerWatchedDie"/>).
    /// </summary>
    public bool CallForHelpEnabled { get; set; } = true;

    /// <summary>The base chance that a bot in a losing fight with a candidate nearby asks for help (0..1); sociability and liking add to it.</summary>
    public float CallForHelpBaseChance { get; set; } = 0.25f;

    /// <summary>How much the bot's sociability trait adds to the chance of asking for help (0..1).</summary>
    public float CallForHelpSociabilityWeight { get; set; } = 0.6f;

    /// <summary>Days after which a relation's affinity has drifted half-way back to 0 when it is not refreshed (1..365). Rivals use <see cref="RivalHalfLifeDays"/>.</summary>
    public int AffinityHalfLifeDays { get; set; } = 14;

    /// <summary>The same half-life for a relation flagged Rival: a grudge fades slower (1..3650).</summary>
    public int RivalHalfLifeDays { get; set; } = 45;

    /// <summary>Affinity gained when someone the bot dislikes says sorry to it (a chat line or the sorry emote), at most once per pair per UTC day (0..50).</summary>
    public int AffinityPerApology { get; set; } = 10;

    /// <summary>The most affinity one pair can gain in one UTC day, whatever the sources (0..200; 0 is unlimited). Kept in memory only: a restart resets the day.</summary>
    public int MaxDailyAffinityGainPerPair { get; set; } = 20;

    /// <summary>Affinity change toward a groupmate who won a Need roll on an item it could not use while the bot also needed it (-100..0).</summary>
    public int AffinityPerNinjaLoot { get; set; } = -8;

    /// <summary>Affinity change toward a player who took a mob the bot was already fighting (-100..0).</summary>
    public int AffinityPerKillSteal { get; set; } = -6;

    /// <summary>Affinity change toward a member who left the group during a dungeon or raid run (-100..0).</summary>
    public int AffinityPerAbandonedRun { get; set; } = -10;

    /// <summary>Trust (permille) gained toward every member of a group that finished its run (-500..500).</summary>
    public int TrustPerGroupSuccess { get; set; } = 30;

    /// <summary>Trust (permille) change toward the member who died first in a wipe (-500..500).</summary>
    public int TrustPerFirstDeathInWipe { get; set; } = -30;

    /// <summary>
    /// Count a PvP kill in organised play as a grudge (off by default). Off: a kill on a battleground map, or with either side in a group-coordinator
    /// siege group, records neither KilledBy nor KilledThem, so players met in a battleground or a GM-ordered siege are not taunted or remembered
    /// as rivals in the open world. On: those kills are recorded like any other.
    /// </summary>
    public bool OrganisedPvpKills { get; set; }
    /// <summary>Affinity gained when a trade deal completes at the agreed terms (0..30).</summary>
    public int AffinityPerFairDeal { get; set; } = 3;

    /// <summary>Trust (permille) gained toward a partner whose deal completed at the agreed terms (-500..500).</summary>
    public int TrustPerFairDeal { get; set; } = 20;

    /// <summary>Affinity change toward a partner who changed a deal's value after matching it (-100..0).</summary>
    public int AffinityPerTradeScam { get; set; } = -15;

    /// <summary>Trust (permille) change toward that partner (-500..500).</summary>
    public int TrustPerTradeScam { get; set; } = -200;

    /// <summary>Affinity gained for a gift worth at least the trading gift minimum (0..30).</summary>
    public int AffinityPerGiftReceived { get; set; } = 4;

    /// <summary>Affinity change toward a partner who agreed a deal and never completed it (-100..0).</summary>
    public int AffinityPerDealFlaked { get; set; } = -1;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Memory";
        Check(MaxRelationsPerBot, 16, 1024, nameof(MaxRelationsPerBot));
        Check(AffinityPerChat, 0, 20, nameof(AffinityPerChat));
        Check(AffinityPerGroup, 0, 30, nameof(AffinityPerGroup));
        Check(AffinityPerGroupSuccess, 0, 30, nameof(AffinityPerGroupSuccess));
        Check(AffinityPerTrade, 0, 30, nameof(AffinityPerTrade));
        Check(AffinityPerHelp, 0, 50, nameof(AffinityPerHelp));
        Check(AffinityPerStrike, -100, 0, nameof(AffinityPerStrike));
        Check(AffinityPerKick, -100, 0, nameof(AffinityPerKick));
        Check(FamiliarityHalfLifeDays, 1, 365, nameof(FamiliarityHalfLifeDays));
        Check(AffinityPerKilledBy, -100, 0, nameof(AffinityPerKilledBy));
        Check(AffinityPerKilledThem, -100, 0, nameof(AffinityPerKilledThem));
        Check(AffinityPerFriendlyEmote, 0, 20, nameof(AffinityPerFriendlyEmote));
        Check(AffinityPerRudeEmote, -100, 0, nameof(AffinityPerRudeEmote));
        Check(RivalAffinity, -100, 0, nameof(RivalAffinity));
        Check(ChatFamiliarityCooldownMinutes, 0, 1440, nameof(ChatFamiliarityCooldownMinutes));
        Check(AffinityPerWatchedDie, -100, 0, nameof(AffinityPerWatchedDie));
        Check(BystanderRadiusYards, 5, 60, nameof(BystanderRadiusYards));
        Check(BystanderMinHealthPercent, 1, 100, nameof(BystanderMinHealthPercent));
        Check(BystanderMinSeconds, 1, 60, nameof(BystanderMinSeconds));
        Check(AffinityHalfLifeDays, 1, 365, nameof(AffinityHalfLifeDays));
        Check(RivalHalfLifeDays, 1, 3650, nameof(RivalHalfLifeDays));
        Check(AffinityPerApology, 0, 50, nameof(AffinityPerApology));
        Check(MaxDailyAffinityGainPerPair, 0, 200, nameof(MaxDailyAffinityGainPerPair));
        Check(AffinityPerNinjaLoot, -100, 0, nameof(AffinityPerNinjaLoot));
        Check(AffinityPerKillSteal, -100, 0, nameof(AffinityPerKillSteal));
        Check(AffinityPerAbandonedRun, -100, 0, nameof(AffinityPerAbandonedRun));
        Check(TrustPerGroupSuccess, -500, 500, nameof(TrustPerGroupSuccess));
        Check(TrustPerFirstDeathInWipe, -500, 500, nameof(TrustPerFirstDeathInWipe));
        Check(AffinityPerFairDeal, 0, 30, nameof(AffinityPerFairDeal));
        Check(AffinityPerGiftReceived, 0, 30, nameof(AffinityPerGiftReceived));
        Check(AffinityPerTradeScam, -100, 0, nameof(AffinityPerTradeScam));
        Check(AffinityPerDealFlaked, -100, 0, nameof(AffinityPerDealFlaked));
        Check(TrustPerFairDeal, -500, 500, nameof(TrustPerFairDeal));
        Check(TrustPerTradeScam, -500, 500, nameof(TrustPerTradeScam));
        CheckUnit(CallForHelpBaseChance, nameof(CallForHelpBaseChance));
        CheckUnit(CallForHelpSociabilityWeight, nameof(CallForHelpSociabilityWeight));

        void CheckUnit(float value, string name)
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f) throw new InvalidOperationException($"{section}: {name} must be 0..1.");
        }

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}
