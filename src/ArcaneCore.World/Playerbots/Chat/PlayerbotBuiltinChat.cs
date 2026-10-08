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

    /// <summary>"help", "commands".</summary>
    Help,

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
}

/// <summary>A built-in answer: what to say (null: nothing) and the master's order to carry out (acknowledged by the party AI itself).</summary>
internal readonly record struct BuiltinChatAnswer(string? Text, PlayerbotPartyCommand? Command);

/// <summary>
/// The built-in provider: no network, no key, no cost, always the last provider of the chain. It reads the intent of the line with a
/// few patterns and answers from <see cref="PlayerbotChatTemplates"/> with the bot's real state (its goal and quest, zone, level,
/// race and class, its group). Orders are carried out only for the bot's party master, through the party commands.
/// </summary>
internal static partial class PlayerbotBuiltinChat
{
    internal static BuiltinChatIntent Classify(string text, string botName, out PlayerbotPartyCommand? command)
    {
        command = null;
        string line = Normalize(text, botName);
        if (Severe().IsMatch(line)) return BuiltinChatIntent.Ignore;
        if (Insult().IsMatch(line)) return BuiltinChatIntent.Abuse;
        if (AskBot().IsMatch(line)) return BuiltinChatIntent.BotQuestion;
        if (Order(line) is { } order) { command = order; return BuiltinChatIntent.Command; }
        if (AskHelp().IsMatch(line)) return BuiltinChatIntent.Help;
        if (AskGroup().IsMatch(line)) return BuiltinChatIntent.Group;
        if (AskDoing().IsMatch(line)) return BuiltinChatIntent.Doing;
        if (AskWhere().IsMatch(line)) return BuiltinChatIntent.Where;
        if (AskWho().IsMatch(line)) return BuiltinChatIntent.Who;
        if (Thank().IsMatch(line)) return BuiltinChatIntent.Thanks;
        if (Farewell().IsMatch(line)) return BuiltinChatIntent.Bye;
        if (Greet().IsMatch(line)) return BuiltinChatIntent.Greeting;
        return BuiltinChatIntent.Unknown;
    }

    /// <summary>
    /// The answer to <paramref name="ask"/>. <paramref name="lastLine"/> is what this bot last said to this player (never said again
    /// at once); <paramref name="naturalLanguageCommands"/> lets the master's plain-language orders through.
    /// </summary>
    internal static BuiltinChatAnswer Answer(BotChatAsk ask, string? lastLine, bool naturalLanguageCommands, Random random)
    {
        BotChatPersona p = ask.Persona;
        BuiltinChatIntent intent = Classify(ask.Text, p.Name, out PlayerbotPartyCommand? command);
        switch (intent)
        {
            case BuiltinChatIntent.Ignore:
                return new(null, null);
            case BuiltinChatIntent.Command when ask.FromMaster && naturalLanguageCommands:
                return new(null, command); // the party command answers ("Following.", "Staying here.")
            case BuiltinChatIntent.Command when ask.FromMaster:
            case BuiltinChatIntent.Help when ask.FromMaster:
                return new(PlayerbotChatCommands.Help, null);
            case BuiltinChatIntent.Command:
                return new(Pick(p.MasterName is null ? "command.nogroup" : "command.notmaster", ask, lastLine, random), null);
            case BuiltinChatIntent.Help:
                return new(Pick("help.other", ask, lastLine, random), null);
            case BuiltinChatIntent.Group:
                string key = ask.FromMaster ? "group.together" : p.InGroup ? "group.already" : ask.InviteAllowed ? "group.yes" : "group.no";
                return new(Pick(key, ask, lastLine, random), null);
            case BuiltinChatIntent.Doing:
                string doing = p.Goal == Kernel.Characters.PlayerbotGoalKind.Quest && p.QuestTitle is not null ? "doing.quest"
                    : PlayerbotChatPrompts.DescribeGoal(p) is null ? "doing.idle" : "doing";
                return new(Pick(doing, ask, lastLine, random), null);
            case BuiltinChatIntent.Where:
                return new(Pick(p.Zone is null ? "where.unknown" : p.Subzone is null ? "where.zone" : "where", ask, lastLine, random), null);
            case BuiltinChatIntent.Who:
                return new(Pick("who", ask, lastLine, random), null);
            case BuiltinChatIntent.BotQuestion:
                return new(Pick("bot", ask, lastLine, random), null);
            case BuiltinChatIntent.Abuse:
                return new(Pick("abuse", ask, lastLine, random), null);
            case BuiltinChatIntent.Thanks:
                return new(Pick("thanks", ask, lastLine, random), null);
            case BuiltinChatIntent.Bye:
                return new(Pick("bye", ask, lastLine, random), null);
            case BuiltinChatIntent.Greeting:
                return new(Pick("greeting", ask, lastLine, random), null);
            default:
                return new(Pick("unknown", ask, lastLine, random), null);
        }
    }

    /// <summary>
    /// One phrasing of <paramref name="key"/> and its race, class and level variants, with every placeholder filled; never
    /// <paramref name="lastLine"/> while another fits. Null when no phrasing can be filled.
    /// </summary>
    internal static string? Pick(string key, BotChatAsk ask, string? lastLine, Random random)
    {
        BotChatPersona p = ask.Persona;
        var candidates = new List<string>();
        foreach (string variant in Variants(key, p))
        {
            if (!PlayerbotChatTemplates.Lines.TryGetValue(variant, out string[]? lines)) continue;
            foreach (string line in lines)
                if (Fill(line, ask) is { } filled && PlayerbotChatPrompts.Sanitize(filled) is { } clean) candidates.Add(clean);
        }

        if (candidates.Count == 0) return null;
        List<string> fresh = candidates.Where(line => !string.Equals(line, lastLine, StringComparison.Ordinal)).ToList();
        List<string> from = fresh.Count > 0 ? fresh : candidates;
        return from[random.Next(from.Count)];
    }

    private static IEnumerable<string> Variants(string key, BotChatPersona p)
    {
        yield return key;
        yield return key + ".race." + p.Race;
        yield return key + ".class." + p.Class;
        if (p.Level < 10) yield return key + ".level.novice";
        if (p.Level >= 60) yield return key + ".level.veteran";
    }

    /// <summary>A template with its placeholders filled, or null when one of them has no value.</summary>
    internal static string? Fill(string template, BotChatAsk ask)
    {
        BotChatPersona p = ask.Persona;
        string? goal = PlayerbotChatPrompts.DescribeGoal(p);
        string result = template;
        foreach ((string name, string? value) in new (string, string?)[]
        {
            ("{player}", ask.SenderName), ("{name}", p.Name), ("{level}", p.Level.ToString(CultureInfo.InvariantCulture)),
            ("{race}", PlayerbotChatPrompts.Words(p.Race)), ("{class}", PlayerbotChatPrompts.Words(p.Class)), ("{zone}", p.Zone),
            ("{subzone}", p.Subzone), ("{goal}", goal), ("{Goal}", goal is null ? null : char.ToUpperInvariant(goal[0]) + goal[1..]),
            ("{quest}", p.QuestTitle), ("{master}", p.MasterName),
        })
        {
            if (!result.Contains(name, StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(value)) return null;
            result = result.Replace(name, value, StringComparison.Ordinal);
        }

        return result;
    }

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

    [GeneratedRegex(@"[^a-z0-9' ]")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\b(kys|kill (yo)?urself|kill yourself|go die)\b")] private static partial Regex Severe();
    [GeneratedRegex(@"\b(idiot|stupid|moron|dumb|noob|n00b|loser|retard\w*|trash|garbage|ugly|fuck\w*|shit\w*|bitch\w*|stfu|shut up|you suck|u suck)\b")] private static partial Regex Insult();
    [GeneratedRegex(@"\b(are|r)( you| u)( a| an)? (bot|ai|robot|real person|real player|human|npc)\b|\byou a bot\b|\bu a bot\b|\bis this a bot\b")] private static partial Regex AskBot();
    [GeneratedRegex(@"\bstop (attacking|fighting)\b|^stop\b|\bstand down\b|\bback off\b|\bpassive\b|\bdon'?t attack\b")] private static partial Regex OrderStop();
    [GeneratedRegex(@"^(please )?(stay|wait|hold)\b|\b(stay|wait) (here|there)\b|\bhold (position|here)\b|\bdon'?t move\b")] private static partial Regex OrderStay();
    [GeneratedRegex(@"\battack\b|\bassist me\b|\bkill (it|that|this|my target)\b|\bhelp me (kill|fight)\b")] private static partial Regex OrderAttack();
    [GeneratedRegex(@"\bfollow me\b|^(please )?follow\b|\bcome with me\b|\bstick with me\b")] private static partial Regex OrderFollow();
    [GeneratedRegex(@"\bcome (here|to me|back|over)\b|\bover here\b|^(please )?come\b")] private static partial Regex OrderCome();
    [GeneratedRegex(@"^help\b|\bcommands\b|\bwhat can you do\b|\bhelp me\b")] private static partial Regex AskHelp();
    [GeneratedRegex(@"\b(want to|wanna|can you|can i|could you|let'?s|like to) (group|join|party)\b|\binvite\b|\binv\b|\bgroup up\b|\bjoin (me|us|my group|our group|my party)\b|\blfg\b|\blooking for (a )?group\b|\bparty up\b")] private static partial Regex AskGroup();
    [GeneratedRegex(@"\bwhat (are|r) (you|u) (doing|up to|working on)\b|\bwhere (are|r) (you|u) (going|headed|heading|off to)\b|\bwhat quest\b|\bwhat'?s up\b|\bwhatcha\b|\bwyd\b|\bwhat you doing\b")] private static partial Regex AskDoing();
    [GeneratedRegex(@"\bwhere (are|r) (you|u)\b|\bwhere (you|u) at\b|\bwhat zone\b|\bwhere is this\b")] private static partial Regex AskWhere();
    [GeneratedRegex(@"\bwhat level\b|\bwhat lvl\b|\bwhat class\b|\bwhat race\b|\bwho (are|r) (you|u)\b|\bwhat (are|r) (you|u)\b|\byour level\b|\byour class\b")] private static partial Regex AskWho();
    [GeneratedRegex(@"\b(thanks|thank you|thank u|thx|ty|cheers|much appreciated)\b")] private static partial Regex Thank();
    [GeneratedRegex(@"\b(bye|goodbye|cya|see you|see ya|farewell|gtg|good night|take care)\b")] private static partial Regex Farewell();
    [GeneratedRegex(@"\b(hi|hello|hey|heya|hiya|yo|greetings|sup|hail|well met|good (morning|evening|day|afternoon))\b")] private static partial Regex Greet();
}
