namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:Voice</c>: what bots say on their own about their goals and needs (<c>PlayerbotNeedsVoice</c>,
/// docs/areas/playbots.md, Bot voice): LFG and LFM calls for the group goals they wait on, WTS for real surplus in their bags and WTB for
/// the reagents their recipes lack, party and raid needs ("oom", "need heals", "rez pls", "inc 3", "&lt;mob&gt; on me", "brb", "ty"), the
/// plan of the bot that leads a dungeon or raid run, battleground objective callouts, help with a group quest, and the odd "ding", "gz" and
/// "first time here". Every line is built from the bot's real state, written by the bot chat service (its built-in templates, or a
/// configured model), screened by the chat safety, held by the speech limits (<see cref="PlayerbotSpeechOptions"/>: per bot, per channel and
/// zone, per group) and said by the bot's own session. The topics run on the social pass (once a second), at most
/// <see cref="BotsPerPass"/> bots and <see cref="LinesPerPass"/> lines a pass. Every key is live (<c>.reload config</c>); all on by default.
/// </summary>
public sealed class PlayerbotVoiceOptions
{
    /// <summary>Let bots voice their goals and needs (on by default). Off: no topic is produced and no LFG or trade whisper is answered here.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most bots whose state is looked at in one social pass (1..1000), round robin over the running bots.</summary>
    public int BotsPerPass { get; set; } = 24;

    /// <summary>The most lines the voice hands to the chat service in one social pass, every topic together (1..100).</summary>
    public int LinesPerPass { get; set; } = 4;

    /// <summary>
    /// LFG and LFM calls (on by default): a bot waiting for partners for a dungeon, raid or elite quest posts "LFG &lt;dungeon&gt;, &lt;lvl&gt;
    /// &lt;class&gt;" (alone) or "LFM &lt;dungeon&gt; need &lt;role&gt;" (the oldest of several waiting for the same goal) in LookingForGroup,
    /// or now and then in General (<see cref="GeneralSharePercent"/>); a real player who whispers the poster "inv" is matched into the group.
    /// </summary>
    public bool LookingForGroup { get; set; } = true;

    /// <summary>The least seconds between two LFG or LFM calls of one bot (30..3600).</summary>
    public int LookingForGroupRepeatSeconds { get; set; } = 180;

    /// <summary>
    /// A bot advertises a dungeon, raid or group quest in chat only when it is near the place (on by default): in the same or a bordering
    /// zone, within <see cref="LfgMaxTravelSeconds"/> of travel by the planner (flight paths, hearthstone, mage portals, ships), or when a
    /// warlock waiting for the same goal can summon it. Otherwise the bot keeps quiet and another waiting bot may post. Off: the bot that
    /// waited longest always posts.
    /// </summary>
    public bool LfgNearDungeonOnly { get; set; } = true;

    /// <summary>The longest planned trip, in seconds, from which a bot still advertises a place under <see cref="LfgNearDungeonOnly"/> (0..7200; 0 = only the same or a bordering zone, or a summon).</summary>
    public int LfgMaxTravelSeconds { get; set; } = 600;

    /// <summary>
    /// Trade calls (on by default): WTS for tradeable surplus in the bot's bags (no grey, no bind-on-pickup or bound item, no quest item,
    /// nothing it would wear itself) at <see cref="PriceMultiplier"/> times the vendor price, and WTB for the reagents of the recipes it
    /// wants; in Trade in a capital, now and then in General elsewhere (<see cref="GeneralSharePercent"/>). A real player who whispers the
    /// seller about the item is told the price and where the bot is.
    /// </summary>
    public bool Trade { get; set; } = true;

    /// <summary>The least seconds between two trade calls of one bot (60..86400).</summary>
    public int TradeRepeatSeconds { get; set; } = 900;

    /// <summary>A WTS price is the item's vendor price times this (1..50), rounded the way players price.</summary>
    public int PriceMultiplier { get; set; } = 4;

    /// <summary>Items worth less than this many copper at a vendor (per stack) are not offered (0..1000000).</summary>
    public int MinSellCopper { get; set; } = 200;

    /// <summary>The share of LFG and trade calls said in General instead (LFG outside a capital, trade outside a capital; 0..100).</summary>
    public int GeneralSharePercent { get; set; } = 25;

    /// <summary>
    /// Party and raid needs (on by default), each once per fight, death or stop: "oom" (a mana user below 10% in a fight), "need heals"
    /// (below 30% with a healer in the group), "rez pls" (dead, a resurrecting class alive in the group), "inc &lt;n&gt;" (a bot group's
    /// tank pulling more than one), "&lt;mob&gt; on me" (a creature on a bot that is not the tank), "brb" (the bot stops to drink, vendor or
    /// train), "ty" (after a resurrection or a long buff from a group member), "ding" (a level up), "gz" (a real player's "ding"), a quest
    /// share request in a bot group, and "first time here" (a zone the bot had never explored).
    /// </summary>
    public bool GroupCalls { get; set; } = true;

    /// <summary>The bot that leads a dungeon or raid run says the plan in party or raid chat: "pulling &lt;boss&gt;", "wait for mana", "wipe, run back", "gg" (on by default).</summary>
    public bool RunCalls { get; set; } = true;

    /// <summary>
    /// Battleground callouts in battleground chat from the match's real state (on by default): Warsong Gulch "our flag taken" and "EFC
    /// &lt;where&gt;", Arathi Basin "inc &lt;base&gt;", "&lt;base&gt; is ours", "lost &lt;base&gt;", Alterac Valley towers and graveyards. One bot
    /// says each event; the same callout is not repeated within <see cref="BattlegroundCallSeconds"/>.
    /// </summary>
    public bool BattlegroundCalls { get; set; } = true;

    /// <summary>Seconds within which one battleground callout (the same event or the same carrier's whereabouts) is not said again (5..600).</summary>
    public int BattlegroundCallSeconds { get; set; } = 20;

    /// <summary>
    /// Quest help (on by default): a bot waiting for partners for an elite or group quest asks General now and then ("anyone for
    /// &lt;quest&gt;?") at most once per <see cref="QuestHelpRepeatSeconds"/>.
    /// </summary>
    public bool QuestHelp { get; set; } = true;

    /// <summary>The least seconds between two quest help calls of one bot (60..86400).</summary>
    public int QuestHelpRepeatSeconds { get; set; } = 1200;

    /// <summary>Answer a real player's whisper to a poster ("inv" to an LFG or LFM call, a price question to a WTS call) here (on by default).</summary>
    public bool AnswerPosts { get; set; } = true;

    /// <summary>How long a call stays open for whispered answers (60..3600 seconds).</summary>
    public int PostLifeSeconds { get; set; } = 600;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Voice";
        Check(BotsPerPass, 1, 1000, nameof(BotsPerPass));
        Check(LinesPerPass, 1, 100, nameof(LinesPerPass));
        Check(LookingForGroupRepeatSeconds, 30, 3600, nameof(LookingForGroupRepeatSeconds));
        Check(LfgMaxTravelSeconds, 0, 7200, nameof(LfgMaxTravelSeconds));
        Check(TradeRepeatSeconds, 60, 86_400, nameof(TradeRepeatSeconds));
        Check(PriceMultiplier, 1, 50, nameof(PriceMultiplier));
        Check(MinSellCopper, 0, 1_000_000, nameof(MinSellCopper));
        Check(GeneralSharePercent, 0, 100, nameof(GeneralSharePercent));
        Check(BattlegroundCallSeconds, 5, 600, nameof(BattlegroundCallSeconds));
        Check(QuestHelpRepeatSeconds, 60, 86_400, nameof(QuestHelpRepeatSeconds));
        Check(PostLifeSeconds, 60, 3600, nameof(PostLifeSeconds));

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}
