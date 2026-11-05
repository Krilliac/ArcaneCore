using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>What a player's line asks for, as the built-in provider reads it (checked in this order; the first match wins).</summary>
internal enum BuiltinChatIntent
{
    /// <summary>Abuse past a brush-off: not answered.</summary>
    Ignore,

    /// <summary>An insult: a short brush-off.</summary>
    Abuse,

    /// <summary>"Are you a bot?": the honest answer.</summary>
    BotQuestion,

    /// <summary>An order (follow me, wait here, attack my target, stop, come here).</summary>
    Command,

    /// <summary>"help", "commands", or a question revolving around commands/controls.</summary>
    Help,

    /// <summary>GM debug / diagnostic state query ("debug", "diag", "what is your debug state?").</summary>
    Debug,

    /// <summary>"want to group?", "inv?".</summary>
    Group,

    /// <summary>"what are you doing?", "where are you going?".</summary>
    Doing,

    /// <summary>"where are you?".</summary>
    Where,

    /// <summary>"what level are you?", "what class?".</summary>
    Who,

    Thanks,
    Bye,
    Greeting,

    /// <summary>Anything else: a short in-character line.</summary>
    Unknown,

    /// <summary>"where is Goldshire?", "how do i get to booty bay": the place from the area data.</summary>
    Place,

    /// <summary>"can you help with Wolves Across the Border?": the bot's own quests decide.</summary>
    QuestHelp,

    /// <summary>"what spec are you?", "can you tank?".</summary>
    Spec,

    /// <summary>"how much gold do you have?".</summary>
    Gold,

    /// <summary>"can you spare some gold?": never given, politely.</summary>
    GoldAsk,

    /// <summary>"need anything?", "what do you need?".</summary>
    Needs,

    /// <summary>"what gear do you have?", "whats your ilvl".</summary>
    Gear,

    /// <summary>"selling anything?", "got anything spare?".</summary>
    Surplus,

    /// <summary>"trade me", "can we trade?".</summary>
    Trade,

    /// <summary>"share the quest", "can you share that?".</summary>
    Share,

    /// <summary>"nice", "gj", "good job": a short thanks.</summary>
    Praise,

    /// <summary>"and you?", "hbu", "u?": the bot's own answer to what it just asked or said.</summary>
    FollowYou,

    /// <summary>"why?", "how come": the reason behind the bot's last line.</summary>
    FollowWhy,

    /// <summary>"lol", "haha".</summary>
    Laugh,

    /// <summary>"ok", "k", "cool", "yeah".</summary>
    Ack,
}

/// <summary>
/// A built-in answer: what to say (null: nothing), the master's order to carry out (acknowledged by the party AI itself), a social act
/// (<see cref="BotChatAction"/>, checked again on the world thread) and the intent it answered (remembered for follow-ups).
/// </summary>
internal readonly record struct BuiltinChatAnswer(string? Text, PlayerbotPartyCommand? Command, BotChatAction Action = BotChatAction.None,
    BuiltinChatIntent Intent = BuiltinChatIntent.Unknown);

/// <summary>
/// The built-in provider: no network, no key, no cost, always the last provider of the chain. It reads the intent of the line with a
/// few patterns and answers from <see cref="PlayerbotChatTemplates"/> with the bot's real state (its goal and quest, zone, level,
/// race and class, its group, and its facts: health, gear, bags, needs, objective, spec, guild). Orders are carried out only for the
/// bot's party master, through the party commands; an invitation, a trade or a quest share only when the bot itself would.
/// </summary>
internal static partial class PlayerbotBuiltinChat
{
    /// <summary>How long a follow-up ("and you?", "why?") still refers to the bot's last answer.</summary>
    internal const long FollowUpMs = 3 * 60_000;

    internal static BuiltinChatIntent Classify(string text, string botName, out PlayerbotPartyCommand? command)
    {
        command = null;
        string line = Normalize(text, botName);
        if (Severe().IsMatch(line)) return BuiltinChatIntent.Ignore;
        if (Insult().IsMatch(line)) return BuiltinChatIntent.Abuse;
        if (AskBot().IsMatch(line)) return BuiltinChatIntent.BotQuestion;
        if (AskDebug().IsMatch(line)) return BuiltinChatIntent.Debug;
        if (AskCommandQuestion().IsMatch(line)) return BuiltinChatIntent.Help;
        if (Order(line) is { } order) { command = order; return BuiltinChatIntent.Command; }
        if (AskQuestHelp().IsMatch(line)) return BuiltinChatIntent.QuestHelp;
        if (AskHelp().IsMatch(line)) return BuiltinChatIntent.Help;
        if (AskTrade().IsMatch(line)) return BuiltinChatIntent.Trade;
        if (AskShare().IsMatch(line)) return BuiltinChatIntent.Share;
        if (AskGroup().IsMatch(line)) return BuiltinChatIntent.Group;
        if (AskGoldGift().IsMatch(line)) return BuiltinChatIntent.GoldAsk;
        if (AskGold().IsMatch(line)) return BuiltinChatIntent.Gold;
        if (AskNeeds().IsMatch(line)) return BuiltinChatIntent.Needs;
        if (AskSpec().IsMatch(line)) return BuiltinChatIntent.Spec;
        if (AskGear().IsMatch(line)) return BuiltinChatIntent.Gear;
        if (AskSurplus().IsMatch(line)) return BuiltinChatIntent.Surplus;
        if (AskDoing().IsMatch(line)) return BuiltinChatIntent.Doing;
        if (PlayerbotPersonaFacts.AskedPlace(text ?? string.Empty) is not null) return BuiltinChatIntent.Place;
        if (AskWhere().IsMatch(line)) return BuiltinChatIntent.Where;
        if (AskWho().IsMatch(line)) return BuiltinChatIntent.Who;
        if (Thank().IsMatch(line)) return BuiltinChatIntent.Thanks;
        if (Farewell().IsMatch(line)) return BuiltinChatIntent.Bye;
        if (Greet().IsMatch(line)) return BuiltinChatIntent.Greeting;
        if (FollowYou().IsMatch(line)) return BuiltinChatIntent.FollowYou;
        if (FollowWhy().IsMatch(line)) return BuiltinChatIntent.FollowWhy;
        if (Praise().IsMatch(line)) return BuiltinChatIntent.Praise;
        if (Laugh().IsMatch(line)) return BuiltinChatIntent.Laugh;
        if (Ack().IsMatch(line)) return BuiltinChatIntent.Ack;
        return BuiltinChatIntent.Unknown;
    }

    /// <summary>
    /// The answer to <paramref name="ask"/>. <paramref name="lastLine"/> is what this bot last said to this player (never said again
    /// at once); <paramref name="naturalLanguageCommands"/> lets the master's plain-language orders through.
    /// </summary>
    internal static BuiltinChatAnswer Answer(BotChatAsk ask, string? lastLine, bool naturalLanguageCommands, Random random)
        => Answer(ask, lastLine, null, naturalLanguageCommands, random);

    /// <summary>
    /// The answer to <paramref name="ask"/>, with <paramref name="lastIntent"/> the intent this bot last answered for this player within
    /// <see cref="FollowUpMs"/> (null: none), so "and you?" or "why?" refers to it.
    /// </summary>
    internal static BuiltinChatAnswer Answer(BotChatAsk ask, string? lastLine, BuiltinChatIntent? lastIntent, bool naturalLanguageCommands, Random random)
    {
        BotChatPersona p = ask.Persona;
        BotChatFacts? f = p.Facts;
        BuiltinChatIntent intent = Classify(ask.Text, p.Name, out PlayerbotPartyCommand? command);
        if (!SmallTalkAllowed(ask, intent)) return new(null, null, Intent: intent);
        string? Say(string key) => Pick(key, ask, lastLine, random);
        BuiltinChatAnswer Text(string key) => new(Say(key), null, Intent: intent);
        switch (intent)
        {
            case BuiltinChatIntent.Ignore:
                return new(null, null, Intent: intent);
            case BuiltinChatIntent.Command when ask.FromMaster && naturalLanguageCommands:
                return new(null, command, Intent: intent); // the party command answers ("Following.", "Staying here.")
            case BuiltinChatIntent.Command when ask.FromMaster:
            case BuiltinChatIntent.Help:
                return new(PlayerbotChatCommands.HelpFor(ask.FromGm), null, Intent: intent);
            case BuiltinChatIntent.Debug when ask.FromGm:
                return new(PlayerbotChatPrompts.Sanitize(p.DebugSummary ?? "Debug: no state available.") ?? "Debug: no state available.", null, Intent: intent);
            case BuiltinChatIntent.Debug:
            case BuiltinChatIntent.Command:
                return Text(p.MasterName is null ? "command.nogroup" : "command.notmaster");
            case BuiltinChatIntent.Group:
                return GroupAnswer(ask, intent, Say);
            case BuiltinChatIntent.Doing:
                return Text(DoingKey(p));
            case BuiltinChatIntent.Where:
                return Text(f?.Instance is not null ? "where.instance" : p.Zone is null ? "where.unknown" : p.Subzone is null ? "where.zone" : "where");
            case BuiltinChatIntent.Place:
                return Text(f?.Place is not { } place ? "place.unknown" : place.SameZone && place.Zone is not null ? "place.here"
                    : place.Zone is null ? "place.zone" : "place.area");
            case BuiltinChatIntent.Who:
                return Text("who");
            case BuiltinChatIntent.Spec:
                return Text(f?.Spec is null ? "spec.none" : "spec");
            case BuiltinChatIntent.Gold:
                return f is null ? Text("unknown") : Text(f.Money < (uint)Math.Max(1, (int)p.Level) * 1000 ? "gold.poor" : "gold");
            case BuiltinChatIntent.GoldAsk:
                return Text("gold.ask");
            case BuiltinChatIntent.Needs:
                return Text(f is { Needs.Count: > 0 } ? "needs" : "needs.none");
            case BuiltinChatIntent.Gear:
                return Text(f is { NotableItems.Count: > 0 } ? "gear" : "gear.none");
            case BuiltinChatIntent.Surplus:
                return Text(f is { Surplus.Count: > 0 } ? "surplus" : "surplus.none");
            case BuiltinChatIntent.QuestHelp:
                return QuestHelpAnswer(ask, intent, Say);
            case BuiltinChatIntent.Trade:
                if (ask.SenderIsBot || f is null) return Text("trade.no");
                if (!f.SpeakerNearby) return Text(f.SpeakerInGroup || ask.TradeAllowed ? "trade.far" : "trade.no");
                return f.SpeakerInGroup || ask.TradeAllowed ? new(Say("trade.yes"), null, BotChatAction.OpenTrade, intent) : Text("trade.no");
            case BuiltinChatIntent.Share:
                return ask.FromMaster && (f is { Quests.Count: > 0 } || p.QuestTitle is not null)
                    ? new(Say("share.yes"), null, BotChatAction.ShareQuest, intent)
                    : Text("share.no");
            case BuiltinChatIntent.BotQuestion:
                return Text("bot");
            case BuiltinChatIntent.Abuse:
                return Text("abuse");
            case BuiltinChatIntent.Thanks:
                return Text("thanks");
            case BuiltinChatIntent.Praise:
                return Text("praise");
            case BuiltinChatIntent.Bye:
                return Text("bye");
            case BuiltinChatIntent.Greeting:
                return GreetingVariant(p.Relation) is { } variant && Say(variant) is { } known ? new(known, null, Intent: intent) : Text("greeting");
            case BuiltinChatIntent.FollowYou:
                return Text(lastIntent is null ? DoingKey(p) : "followup.you");
            case BuiltinChatIntent.FollowWhy:
                return lastIntent is BuiltinChatIntent.Doing or BuiltinChatIntent.Needs or BuiltinChatIntent.FollowYou ? Text("followup.why") : Text("unknown");
            case BuiltinChatIntent.Laugh:
                return lastIntent is null ? Text("unknown") : Text("followup.laugh");
            case BuiltinChatIntent.Ack:
                return lastIntent is null ? Text("unknown") : Text(random.Next(2) == 0 ? "followup.ok" : "followup.agree");
            default:
                return Text("unknown");
        }
    }

    private static string DoingKey(BotChatPersona p)
    {
        BotChatFacts? f = p.Facts;
        if (f is { Dead: true }) return "doing.dead";
        if (f is { InCombat: true }) return "doing.combat";
        if (f?.Instance is not null && !f.InBattleground) return "doing.instance";
        if (p.Goal == Kernel.Characters.PlayerbotGoalKind.Quest && p.QuestTitle is not null)
            return f is { QuestReady: true } ? "doing.ready" : f?.Objective is not null ? "doing.objective" : "doing.quest";
        return PlayerbotChatPrompts.DescribeGoal(p) is null ? "doing.idle" : "doing";
    }

    private static BuiltinChatAnswer GroupAnswer(BotChatAsk ask, BuiltinChatIntent intent, Func<string, string?> say)
    {
        BotChatPersona p = ask.Persona;
        if (ask.FromMaster) return new(say("group.together"), null, Intent: intent);
        if (!ask.InviteAllowed) return new(say(p.InGroup ? "group.already" : "group.no"), null, Intent: intent);
        if (p.InGroup)
        {
            // A bot that leads its group and has room brings the speaker in itself; one that is only a member says so.
            return p.Facts is { CanInvite: true, SpeakerInGroup: false } && !ask.SenderIsBot
                ? new(say("group.inviting"), null, BotChatAction.Invite, intent)
                : new(say("group.already"), null, Intent: intent);
        }

        if (p.Facts is not null && PlayerbotDungeons.Named(ask.Text, p.Level) is { } dungeon && PlayerbotDungeons.Fits(dungeon, p.Level)
            && say("group.dungeon") is { } dungeonLine)
            return new(dungeonLine.Replace("{dungeon}", dungeon.Name, StringComparison.Ordinal), null, Intent: intent);
        return new(say("group.yes"), null, Intent: intent);
    }

    private static BuiltinChatAnswer QuestHelpAnswer(BotChatAsk ask, BuiltinChatIntent intent, Func<string, string?> say)
    {
        BotChatPersona p = ask.Persona;
        if (ask.SenderIsBot) return new(null, null, Intent: intent);
        string? asked = AskedQuest(ask.Text, p.Name);
        bool shared = asked is not null && (Same(asked, p.QuestTitle) || (p.Facts?.Quests.Any(title => Same(asked, title)) ?? false));
        if (shared)
        {
            BotChatAction act = p.Facts is { CanInvite: true, SpeakerInGroup: false } && ask.InviteAllowed && p.InGroup ? BotChatAction.Invite : BotChatAction.None;
            return new(say("helpquest.same"), null, act, intent);
        }

        if (!ask.InviteAllowed || p.InGroup || p.Facts is { InCombat: true } or { Dead: true }) return new(say("helpquest.busy") ?? say("help.other"), null, Intent: intent);
        return new(p.Goal == Kernel.Characters.PlayerbotGoalKind.Quest && p.QuestTitle is not null ? say("helpquest.busy") : say("helpquest.yes"), null, Intent: intent);

        static bool Same(string asked, string? title)
            => title is { Length: > 0 } && (title.Contains(asked, StringComparison.OrdinalIgnoreCase) || asked.Contains(title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The quest a "help with ..." line names (as typed, at least four characters), or null.</summary>
    internal static string? AskedQuest(string text, string botName)
    {
        Match match = QuestName().Match(Normalize(text, botName));
        if (!match.Success) return null;
        string name = match.Groups["quest"].Value.Trim();
        if (name.StartsWith("the quest ", StringComparison.Ordinal)) name = name[10..];
        return name.Length >= 4 && name is not ("this" or "that" or "it" or "something" or "a quest" or "my quest") ? name : null;
    }

    /// <summary>
    /// Whether the built-in answers <paramref name="intent"/> for this kind of line. A line put to the bot by a real player gets every
    /// answer. Another bot's line gets small talk only (a greeting, what it is doing, where it is, who it is, thanks, goodbye, and the
    /// short follow-ups): never an order, a help text, a group offer or the "say again?" of an unknown line, so two bots never loop on
    /// misunderstandings. A guild question put to nobody in particular is answered only when the bot has something real to say (a group
    /// offer, a greeting, thanks, a place it knows, help with a quest it shares).
    /// </summary>
    internal static bool SmallTalkAllowed(BotChatAsk ask, BuiltinChatIntent intent)
    {
        if (ask.SenderIsBot)
            return intent is BuiltinChatIntent.Greeting or BuiltinChatIntent.Doing or BuiltinChatIntent.Where or BuiltinChatIntent.Who
                or BuiltinChatIntent.Thanks or BuiltinChatIntent.Bye or BuiltinChatIntent.Praise or BuiltinChatIntent.Spec or BuiltinChatIntent.Needs;
        if (!ask.Addressed)
            return intent is BuiltinChatIntent.Group or BuiltinChatIntent.Greeting or BuiltinChatIntent.Thanks
                || (intent is BuiltinChatIntent.Place && ask.Persona.Facts?.Place is not null)
                || (intent is BuiltinChatIntent.QuestHelp && AskedQuest(ask.Text, ask.Persona.Name) is { } quest
                    && (ask.Persona.QuestTitle?.Contains(quest, StringComparison.OrdinalIgnoreCase) == true
                        || (ask.Persona.Facts?.Quests.Any(title => title.Contains(quest, StringComparison.OrdinalIgnoreCase)) ?? false)));
        return true;
    }

    /// <summary>
    /// One phrasing of <paramref name="key"/> and its race, class and level variants, with every placeholder filled; never
    /// <paramref name="lastLine"/> while another fits. Null when no phrasing can be filled.
    /// </summary>
    internal static string? Pick(string key, BotChatAsk ask, string? lastLine, Random random)
    {
        BotChatPersona p = ask.Persona;
        var candidates = new List<string>();
        var toned = new List<string>();
        string? tone = BotSocial.PlayerbotMood.ToneOf(p.Mood) switch { BotSocial.PlayerbotTone.Cheerful => ".mood.cheerful", BotSocial.PlayerbotTone.Grumpy => ".mood.grumpy", _ => null };
        foreach (string variant in Variants(key, p))
        {
            // The tone variants (key.mood.cheerful, key.mood.grumpy) replace the plain lines when the bot's mood is clear and the key has some.
            if (tone is not null && BotSocial.PlayerbotSocialTemplates.Lines.TryGetValue(variant + tone, out string[]? mooded)) AddFilled(toned, mooded, ask);
            // The chat templates, then the social ones (guild and greeting lines, BotSocial.PlayerbotSocialTemplates).
            if (PlayerbotChatTemplates.Lines.TryGetValue(variant, out string[]? lines)) AddFilled(candidates, lines, ask);
            if (BotSocial.PlayerbotSocialTemplates.Lines.TryGetValue(variant, out string[]? social)) AddFilled(candidates, social, ask);
            if (PlayerbotVoiceTemplates.Lines.TryGetValue(variant, out string[]? voice)) AddFilled(candidates, voice, ask);
        }

        if (toned.Count > 0) candidates = toned;
        if (candidates.Count == 0) return null;
        List<string> fresh = candidates.Where(line => !string.Equals(line, lastLine, StringComparison.Ordinal)).ToList();
        List<string> from = fresh.Count > 0 ? fresh : candidates;
        return from[random.Next(from.Count)];
    }

    /// <summary>Met at least this many times (and not disliked) for the "welcome back" greeting.</summary>
    internal const int WelcomeBackFamiliarity = 3;

    /// <summary>At or below this affinity the greeting is curt.</summary>
    internal const int CurtAffinity = -40;

    /// <summary>
    /// The greeting key for a person the bot remembers: "welcome back" at familiarity 3 or more and affinity 0 or more, curt at affinity -40 or
    /// less, else null (the plain greeting).
    /// </summary>
    internal static string? GreetingVariant(BotChatRelation? relation)
        => relation is null ? null
            : relation.AffinityScore <= CurtAffinity ? "greeting.curt"
            : relation.Familiarity >= WelcomeBackFamiliarity && relation.AffinityScore >= 0 ? "greeting.back" : null;

    private static void AddFilled(List<string> candidates, string[] lines, BotChatAsk ask)
    {
        foreach (string line in lines)
            if (Fill(line, ask) is { } filled && PlayerbotChatPrompts.Sanitize(filled) is { } clean) candidates.Add(clean);
    }

    private static IEnumerable<string> Variants(string key, BotChatPersona p)
    {
        yield return key;
        yield return key + ".race." + p.Race;
        yield return key + ".class." + p.Class;
        if (p.Level < 10) yield return key + ".level.novice";
        if (p.Level >= 60) yield return key + ".level.veteran";
    }

    /// <summary>
    /// A template with its placeholders filled, or null when one of them has no value: the persona's, then its facts', then the topic's
    /// values (<see cref="BotChatTopic.Values"/>) for any other name.
    /// </summary>
    internal static string? Fill(string template, BotChatAsk ask)
    {
        if (!template.Contains('{', StringComparison.Ordinal)) return template;
        BotChatPersona p = ask.Persona;
        BotChatFacts? f = p.Facts;
        IReadOnlyDictionary<string, string>? topic = ask.Topic?.Values;
        bool failed = false;
        string result = Placeholder().Replace(template, match =>
        {
            string name = match.Groups[1].Value;
            string? value = topic?.GetValueOrDefault(name) is { Length: > 0 } given ? given
                : topic is not null && TopicOnly.Contains(name) ? null
                : Value(name, ask, p, f);
            if (string.IsNullOrWhiteSpace(value))
            {
                failed = true;
                return match.Value;
            }

            return value;
        });
        return failed ? null : result;
    }

    /// <summary>
    /// Names an announcement (an ask with a <see cref="BotChatTopic"/>: guild remarks, the bot voice, conversations) fills only from its
    /// topic's values, never from the bot's own facts: a phrasing naming one the topic has no value for is not used.
    /// </summary>
    private static readonly HashSet<string> TopicOnly = new(StringComparer.Ordinal)
    {
        "guild", "who", "newlevel", "boss", "battleground", "instance", "item", "place", "dungeon", "price", "count", "roles", "task",
        "where", "node", "newzone", "minlevel", "loot", "wanted", "topic", "event",
    };

    private static string? Value(string name, BotChatAsk ask, BotChatPersona p, BotChatFacts? f)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        switch (name)
        {
            case "player": return ask.SenderName;
            case "name": return p.Name;
            case "level": return p.Level.ToString(c);
            case "race": return PlayerbotChatPrompts.Words(p.Race);
            case "class": return PlayerbotChatPrompts.Words(p.Class);
            case "zone": return p.Zone;
            case "subzone": return p.Subzone;
            case "goal": return PlayerbotChatPrompts.DescribeGoal(p);
            case "Goal": return PlayerbotChatPrompts.DescribeGoal(p) is { Length: > 0 } goal ? char.ToUpperInvariant(goal[0]) + goal[1..] : null;
            case "quest": return p.QuestTitle;
            case "master": return p.MasterName;
        }

        if (f is null) return null;
        return name switch
        {
            "hp" => f.HealthPct.ToString(c),
            "mana" => f.ManaPct?.ToString(c),
            "ilvl" => f.AverageItemLevel > 0 ? f.AverageItemLevel.ToString(c) : null,
            "item" => f.NotableItems.Count > 0 ? f.NotableItems[0] : null,
            "spec" => f.Spec,
            "role" => RoleWord(f.Role),
            "guild" => f.Guild,
            "objective" => f.Objective,
            "need" => f.Needs.Count > 0 ? NeedWords(f.Needs[0]) : null,
            "surplus" => f.Surplus.Count > 0 ? f.Surplus[0].Name : null,
            "surpluscount" => f.Surplus.Count > 0 ? f.Surplus[0].Count.ToString(c) : null,
            "gold" => PlayerbotChatEvents.Coins(f.Money),
            "bagfree" => f.BagFree.ToString(c),
            "instance" => f.Instance,
            "mob" => f.NearbyCreature,
            "dungeon" => f.SuitedDungeon,
            "otherquest" => f.Quests.Count > 0 ? f.Quests[0] : null,
            "place" => f.Place?.Name,
            "placezone" => f.Place?.Zone,
            "placemap" => f.Place?.Map,
            _ => null,
        };
    }

    /// <summary>The role as a 2005 player would say it.</summary>
    internal static string RoleWord(BotChatRole role) => role switch
    {
        BotChatRole.Tank => "tank",
        BotChatRole.Healer => "heal",
        _ => "dps",
    };

    /// <summary>A need as a player would say it ("water", "bag space", "a mount").</summary>
    internal static string NeedWords(BotChatNeed need) => need switch
    {
        BotChatNeed.Food => "food",
        BotChatNeed.Water => "water",
        BotChatNeed.BagSpace => "bag space",
        BotChatNeed.Mount => "gold for my mount",
        BotChatNeed.Heal => "a heal",
        BotChatNeed.Mana => "mana",
        BotChatNeed.Gold => "gold",
        BotChatNeed.Group => "a group",
        _ => need.ToString().ToLowerInvariant(),
    };

    private static PlayerbotPartyCommand? Order(string line)
    {
        if (OrderStop().IsMatch(line)) return PlayerbotPartyCommand.Stop;
        if (OrderStay().IsMatch(line)) return PlayerbotPartyCommand.Stay;
        if (OrderAttack().IsMatch(line)) return PlayerbotPartyCommand.Attack;
        if (OrderFollow().IsMatch(line)) return PlayerbotPartyCommand.Follow;
        if (OrderCome().IsMatch(line)) return PlayerbotPartyCommand.Come;
        return null;
    }

    /// <summary>Lower case, the bot's own name and punctuation out, single spaces ("Bob, follow me!" is "follow me").</summary>
    private static string Normalize(string text, string botName)
    {
        string lower = (text ?? string.Empty).ToLowerInvariant();
        if (botName.Length > 0) lower = Regex.Replace(lower, @"\b" + Regex.Escape(botName.ToLowerInvariant()) + @"\b", " ");
        lower = Punctuation().Replace(lower, " ");
        return Spaces().Replace(lower, " ").Trim();
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")] private static partial Regex Placeholder();
    [GeneratedRegex(@"[^a-z0-9' ]")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b(kys|kill (yo)?urself|kill yourself|go die)\b")] private static partial Regex Severe();
    [GeneratedRegex(@"\b(idiot|stupid|moron|dumb|noob|n00b|loser|retard\w*|trash|garbage|ugly|fuck\w*|shit\w*|bitch\w*|stfu|shut up|you suck|u suck)\b")] private static partial Regex Insult();
    [GeneratedRegex(@"\b(are|r)( you| u)( a| an)? (bot|ai|robot|real person|real player|human|npc)\b|\byou a bot\b|\bu a bot\b|\bis this a bot\b")] private static partial Regex AskBot();
    [GeneratedRegex(@"^(debug|diag|diagnostics|state|brain|trace|motion|route|stall|stalls)\b|\b(debug|diagnostics?|diag|stack ?trace|internal state|brain state|motion state|route state|stall state|home state|telemetry)\b|\b(what is|what's|show|dump|print|inspect) (your )?(debug|diag|diagnostics?|state|brain|trace|motion|route|stall)\b")] private static partial Regex AskDebug();
    [GeneratedRegex(@"\b(what|which|list|show|tell me)\b.*\b(commands?|orders?|controls?|options)\b|\bhow (do|can) i (control|command|use|order|direct) (you|u|bots?)\b|\bwhat can (i|we) (say|tell|ask|whisper|order|command)\b|\bcommand list\b|\bhelp with commands\b")] private static partial Regex AskCommandQuestion();
    [GeneratedRegex(@"\bstop (attacking|fighting)\b|^stop\b|\bstand down\b|\bback off\b|\bpassive\b|\bdon'?t attack\b")] private static partial Regex OrderStop();
    [GeneratedRegex(@"^(please )?(stay|wait|hold)\b|\b(stay|wait) (here|there)\b|\bhold (position|here)\b|\bdon'?t move\b")] private static partial Regex OrderStay();
    [GeneratedRegex(@"\battack\b|\bassist me\b|\bkill (it|that|this|my target)\b|\bhelp me (kill|fight)\b")] private static partial Regex OrderAttack();
    [GeneratedRegex(@"\bfollow me\b|^(please )?follow\b|\bcome with me\b|\bstick with me\b")] private static partial Regex OrderFollow();
    [GeneratedRegex(@"\bcome (here|to me|back|over)\b|\bover here\b|^(please )?come\b")] private static partial Regex OrderCome();
    [GeneratedRegex(@"^help\b|\bcommands?\b|\bcmds?\b|\bwhat (can|do) (you|u) (do|respond to|understand|accept)\b|\bhelp me\b|\bneed help\b")] private static partial Regex AskHelp();
    [GeneratedRegex(@"\b(help|assist)( me| us)? (with|on|for) (?!commands?\b)\w|\bhelp (me|us) (do|finish|complete) \w")] private static partial Regex AskQuestHelp();
    [GeneratedRegex(@"\b(help|assist)( me| us)? (with|on|for|do|finish|complete) (?<quest>[a-z0-9' ]{4,60})$")] private static partial Regex QuestName();
    [GeneratedRegex(@"\btrade me\b|\bopen (a )?trade\b|\b(can|could|wanna|want to|let'?s) (we |u |you )?trade\b|^trade\b|\btrade (pls|please|plz)\b")] private static partial Regex AskTrade();
    [GeneratedRegex(@"\bshare (the |that |this |your |ur )?(quests?|it)\b|\bcan (you|u) share\b|^share\b")] private static partial Regex AskShare();
    [GeneratedRegex(@"\b(want to|wanna|can you|can i|could you|let'?s|like to) (group|join|party)\b|\binvite\b|\binv\b|\bgroup up\b|\bjoin (me|us|my group|our group|my party)\b|\blfg\b|\blooking for (a )?group\b|\bparty up\b")] private static partial Regex AskGroup();
    [GeneratedRegex(@"\b(spare|give|lend|loan) (me )?(some |a few |any )?(gold|money|silver|copper|coins?|g)\b|\bcan i (have|get) (some )?(gold|money)\b")] private static partial Regex AskGoldGift();
    [GeneratedRegex(@"\bhow much (gold|money|cash)\b|\bhow rich\b|\b(are|r) (you|u) rich\b|\bhow much (do|have) (you|u) got\b|\byour gold\b|\bur gold\b")] private static partial Regex AskGold();
    [GeneratedRegex(@"\b(do )?(you|u) need (anything|something|any help|stuff)\b|\bneed anything\b|\bwhat (do|d) (you|u) need\b|\banything (you|u) need\b")] private static partial Regex AskNeeds();
    [GeneratedRegex(@"\bwhat spec\b|\b(your|ur) spec\b|\bwhat talents\b|\bwhat build\b|\bcan (you|u) (tank|heal)\b|\b(are|r) (you|u) (a )?(tank|healer|heals|dps)\b|\bwhat role\b")] private static partial Regex AskSpec();
    [GeneratedRegex(@"\bwhat gear\b|\b(your|ur) gear\b|\bilvl\b|\bitem level\b|\bwhat weapon\b|\b(nice|cool) (gear|weapon|sword|staff|bow|armor)\b")] private static partial Regex AskGear();
    [GeneratedRegex(@"\b(selling|wts) anything\b|\banything (to sell|for sale|spare)\b|\bgot anything (spare|to sell)\b|\bwhat (are|r) (you|u) selling\b")] private static partial Regex AskSurplus();
    [GeneratedRegex(@"\bwhat (are|r) (you|u) (doing|up to|working on)\b|\bwhere (are|r) (you|u) (going|headed|heading|off to)\b|\bwhat quest\b|\bwhat'?s up\b|\bwhatcha\b|\bwyd\b|\bwhat you doing\b")] private static partial Regex AskDoing();
    [GeneratedRegex(@"\bwhere (are|r) (you|u)\b|\bwhere (you|u) at\b|\bwhat zone\b|\bwhere is this\b")] private static partial Regex AskWhere();
    [GeneratedRegex(@"\bwhat level\b|\bwhat lvl\b|\bwhat class\b|\bwhat race\b|\bwho (are|r) (you|u)\b|\bwhat (are|r) (you|u)\b|\byour level\b|\byour class\b")] private static partial Regex AskWho();
    [GeneratedRegex(@"\b(thanks|thank you|thank u|thx|ty|tyvm|cheers|much appreciated)\b")] private static partial Regex Thank();
    [GeneratedRegex(@"\b(bye|goodbye|cya|see you|see ya|farewell|gtg|good night|take care)\b")] private static partial Regex Farewell();
    [GeneratedRegex(@"\b(hi|hello|hey|heya|hiya|yo|greetings|sup|hail|well met|good (morning|evening|day|afternoon))\b")] private static partial Regex Greet();
    [GeneratedRegex(@"^(and )?(you|u|urself|yourself)$|\band (you|u)\b|\bhbu\b|\bwhat about (you|u)\b|\bhow about (you|u)\b")] private static partial Regex FollowYou();
    [GeneratedRegex(@"^(why|how come|whys that|why's that|why tho)$|^why\b")] private static partial Regex FollowWhy();
    [GeneratedRegex(@"^(nice|gj|good job|well done|wp|well played|awesome|sweet|grats|gz|nice one)$")] private static partial Regex Praise();
    [GeneratedRegex(@"^(lol|lmao|rofl|haha+|hehe+|heh)$")] private static partial Regex Laugh();
    [GeneratedRegex(@"^(ok|okay|k|kk|cool|alright|sure|yeah|ya|yep|true|nice)$")] private static partial Regex Ack();
}
