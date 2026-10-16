using System.Globalization;
using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What two bots may talk about (<see cref="PlayerbotConversationScripts"/>), most newsworthy first.</summary>
internal enum ConversationTopic : byte
{
    /// <summary>The subject just gained a level; the other congratulates.</summary>
    Ding,

    /// <summary>Their group just killed a boss.</summary>
    Boss,

    /// <summary>The subject just looted something notable.</summary>
    Loot,

    /// <summary>The subject died a moment ago.</summary>
    Death,

    /// <summary>The subject is out of food or water and the other has some.</summary>
    Need,

    /// <summary>The subject's quest, and whether the other is on it too.</summary>
    Quest,

    /// <summary>The dungeon that suits the subject's level.</summary>
    Dungeon,

    /// <summary>What each is up to.</summary>
    Chat,

    /// <summary>A hello in guild chat.</summary>
    Greet,
}

/// <summary>
/// One side of a conversation as the script sees it: the bot's real facts (<see cref="BotChatFacts"/>), its quest, and the recent events
/// it has not talked about yet (null when there is none): the level it reached, an item it looted, a boss its group killed, a death.
/// </summary>
internal sealed record ConversationSide(string Name, byte Level, BotChatFacts Facts, string? Quest = null, string? Ding = null, string? Loot = null,
    string? Boss = null, string? BossWhere = null, bool Died = false);

/// <summary>One line of a script: who says it (0: the subject, 1: the other) and its template key (<see cref="PlayerbotChatTemplates"/>).</summary>
internal readonly record struct ConversationStep(int Speaker, string Key);

/// <summary>A planned conversation: the steps and the values its lines are filled with (besides each speaker's own facts).</summary>
internal sealed record ConversationScript(ConversationTopic Topic, IReadOnlyList<ConversationStep> Steps, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// The scripts of bot-to-bot conversations (pure; <see cref="PlayerbotConversations"/> runs them). A script exists only when the facts it
/// talks about are real: a ding the subject reached, food the other carries, a quest both are on. Lines are the <c>convo.&lt;topic&gt;.&lt;step&gt;</c>
/// templates; <c>{player}</c> in a line is the bot spoken to.
/// </summary>
internal static class PlayerbotConversationScripts
{
    /// <summary>The fewest lines a conversation has (an opener and an answer).</summary>
    internal const int MinLines = 2;

    /// <summary>The least food or drink the other carries to offer some.</summary>
    internal const int OfferAtLeast = 4;

    /// <summary>The lowest level that talks about dungeons.</summary>
    internal const int DungeonLevel = 15;

    /// <summary>
    /// The topics the subject <paramref name="a"/> and <paramref name="b"/> can talk about, news first (ding, boss, loot, death, a need),
    /// then small talk in a random order. <paramref name="guild"/>: the line goes to guild chat (the only place for a greeting);
    /// <paramref name="sameGroup"/>: they are in one group (the only place for a boss).
    /// </summary>
    internal static List<ConversationTopic> Topics(ConversationSide a, ConversationSide b, bool sameGroup, bool guild, Random random)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(random);
        var news = new List<ConversationTopic>(5);
        if (a.Ding is not null) news.Add(ConversationTopic.Ding);
        if (a.Boss is not null && sameGroup) news.Add(ConversationTopic.Boss);
        if (a.Loot is not null) news.Add(ConversationTopic.Loot);
        if (a.Died) news.Add(ConversationTopic.Death);
        if (Wanted(a, b) is not null) news.Add(ConversationTopic.Need);
        var talk = new List<ConversationTopic>(4);
        if (a.Quest is not null) talk.Add(ConversationTopic.Quest);
        if (a.Level >= DungeonLevel && a.Facts.SuggestedDungeonFree()) talk.Add(ConversationTopic.Dungeon);
        talk.Add(ConversationTopic.Chat);
        if (guild) talk.Add(ConversationTopic.Greet);
        for (int i = talk.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (talk[i], talk[j]) = (talk[j], talk[i]);
        }

        news.AddRange(talk);
        return news;
    }

    /// <summary>
    /// The script of <paramref name="topic"/> between the subject <paramref name="a"/> and <paramref name="b"/>, at most
    /// <paramref name="maxLines"/> lines (never fewer than <see cref="MinLines"/>); null when the facts do not hold it.
    /// </summary>
    internal static ConversationScript? Script(ConversationTopic topic, ConversationSide a, ConversationSide b, bool sameGroup, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        CultureInfo c = CultureInfo.InvariantCulture;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<ConversationStep>(6);
        switch (topic)
        {
            case ConversationTopic.Ding when a.Ding is { } level:
                values["newlevel"] = level;
                steps.AddRange([new(1, "convo.ding.grats"), new(0, "convo.ding.thanks"), new(1, "convo.ding.more"), new(0, "convo.ding.close")]);
                break;
            case ConversationTopic.Boss when a.Boss is { } boss && sameGroup:
                values["boss"] = boss;
                steps.AddRange([new(0, "convo.boss.cheer"), new(1, "convo.boss.reply"), new(0, "convo.boss.loot")]);
                if (b.Loot is { } got)
                {
                    values["loot"] = got;
                    steps.Add(new(1, "convo.boss.answerloot"));
                }
                else
                {
                    steps.Add(new(1, "convo.boss.answer"));
                }

                break;
            case ConversationTopic.Loot when a.Loot is { } loot:
                values["loot"] = loot;
                steps.AddRange([new(1, "convo.loot.grats"), new(0, "convo.loot.thanks"), new(1, "convo.loot.close")]);
                break;
            case ConversationTopic.Death when a.Died:
                steps.AddRange([new(1, "convo.death.ask"), new(0, a.Facts.NearbyCreature is null ? "convo.death.plain" : "convo.death.mob"),
                    new(1, "convo.death.close")]);
                break;
            case ConversationTopic.Need when Wanted(a, b) is { } wanted:
                values["wanted"] = wanted.Word;
                values["count"] = Math.Clamp(wanted.Has / 2, 2, 10).ToString(c);
                steps.AddRange([new(0, "convo.need.ask"), new(1, "convo.need.offer"), new(0, "convo.need.thanks"), new(1, "convo.need.close")]);
                break;
            case ConversationTopic.Quest when a.Quest is { } quest:
                steps.Add(new(0, "convo.quest.ask"));
                if (string.Equals(b.Quest, quest, StringComparison.OrdinalIgnoreCase))
                {
                    if (b.Facts.Objective is null) return null;
                    steps.Add(new(1, "convo.quest.same"));
                    if (!sameGroup) steps.AddRange([new(0, "convo.quest.group"), new(1, "convo.quest.close")]);
                }
                else if (b.Quest is not null)
                {
                    steps.AddRange([new(1, "convo.quest.other"), new(0, "convo.quest.close")]);
                }
                else
                {
                    return null;
                }

                break;
            case ConversationTopic.Dungeon when a.Level >= DungeonLevel && a.Facts.SuitedDungeon is { } dungeon && a.Facts.Instance is null:
                values["dungeon"] = dungeon;
                bool fits = string.Equals(b.Facts.SuitedDungeon, dungeon, StringComparison.Ordinal) && b.Facts.Instance is null;
                steps.AddRange([new(0, "convo.dungeon.ask"), new(1, fits ? "convo.dungeon.yes" : "convo.dungeon.no"), new(0, "convo.dungeon.close")]);
                break;
            case ConversationTopic.Chat:
                steps.AddRange([new(0, "convo.chat.ask"), new(1, "convo.chat.answer"), new(0, "convo.chat.back"), new(1, "convo.chat.close")]);
                break;
            case ConversationTopic.Greet:
                steps.AddRange([new(0, "convo.greet.hello"), new(1, "convo.greet.reply")]);
                break;
            default:
                return null;
        }

        int cap = Math.Max(MinLines, maxLines);
        if (steps.Count > cap) steps.RemoveRange(cap, steps.Count - cap);
        return new ConversationScript(topic, steps, values);
    }

    /// <summary>What the subject lacks that the other carries enough of: water for a caster out of drink, else food; null when nothing fits.</summary>
    internal static (string Word, int Has)? Wanted(ConversationSide a, ConversationSide b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Facts.Needs.Contains(BotChatNeed.Water) && b.Facts.Drink >= OfferAtLeast) return ("water", b.Facts.Drink);
        if (a.Facts.Needs.Contains(BotChatNeed.Food) && b.Facts.Food >= OfferAtLeast) return ("food", b.Facts.Food);
        return null;
    }

    private static bool SuggestedDungeonFree(this BotChatFacts facts) => facts.SuitedDungeon is not null && facts.Instance is null && !facts.InBattleground;
}
