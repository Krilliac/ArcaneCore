using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What a world channel line asks for, as a bot reads it (<see cref="PlayerbotWorldChatter.Read"/>).</summary>
internal enum WorldLineKind : byte
{
    None,

    /// <summary>"LFM SFK need heals", "lf2m dm": a group looks for players.</summary>
    LookingForMore,

    /// <summary>"WTB Linen Cloth", "LF Copper Ore": a player wants to buy something.</summary>
    WantToBuy,
}

/// <summary>
/// World channel answers (<see cref="PlayerbotConversationOptions.WorldReactions"/>): a real player's LFM line for a dungeon that fits a
/// free bot (no group, not driven by a master or the group coordinator, the asked role when one is named) gets a whispered "inv" or
/// "lvl 24 priest here, inv?"; a WTB line naming goods a bot carries spare gets a whispered offer priced from the goods' vendor value.
/// The bots' own LFG / WTS posts are the bot voice's (<see cref="PlayerbotNeedsVoice"/>). Heard lines are queued (at most
/// <see cref="PlayerbotConversationOptions.MaxHeardQueue"/>, the first bot hearing each) and read on the social pass; at most
/// <see cref="PlayerbotConversationOptions.ReactionsPerLine"/> bots answer one line, at most
/// <see cref="PlayerbotConversationOptions.WorldReactionsPerMinute"/> lines are answered a minute, and one player is answered again only
/// after <see cref="PlayerbotConversationOptions.ReactionPlayerCooldownMinutes"/>. Every line goes through
/// <see cref="PlayerbotChat.TrySpeak"/> and the central limiter.
/// </summary>
internal sealed partial class PlayerbotWorldChatter
{
    private const long MinuteMs = 60_000;

    /// <summary>The lowest level that answers LFM lines.</summary>
    internal const int LfgLevel = 13;

    private readonly Queue<BotHeardLine> _heard = new();
    private readonly Queue<long> _reactions = new();
    private readonly Dictionary<ulong, long> _answered = [];
    private PlayerbotChat? _chat;
    private int _maxHeard = 64;

    /// <summary>Lines answered so far.</summary>
    internal int Reactions { get; private set; }


    /// <summary>The social pass: answer the queued lines.</summary>
    internal void Run(PlayerbotSocialPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Chat is not { } chat) return;
        if (!ReferenceEquals(_chat, chat))
        {
            if (_chat is { } old) old.LineHeard -= OnLineHeard;
            _chat = chat;
            chat.LineHeard += OnLineHeard;
        }

        PlayerbotConversationOptions options = chat.ConversationOptions;
        _maxHeard = options.MaxHeardQueue;
        if (!chat.IsActive || !chat.SpeechOptions.Enabled)
        {
            _heard.Clear();
            return;
        }

        int budget = _heard.Count;
        while (budget-- > 0 && _heard.TryDequeue(out BotHeardLine? line))
            if (options.WorldReactions) React(pass, chat, options, line);
        if (_answered.Count > 512)
            foreach (KeyValuePair<ulong, long> stale in _answered.Where(p => p.Value <= pass.NowMs).ToArray()) _answered.Remove(stale.Key);
    }

    internal void Stop()
    {
        if (_chat is { } chat) chat.LineHeard -= OnLineHeard;
        _chat = null;
        _heard.Clear();
    }

    private void OnLineHeard(BotHeardLine line)
    {
        // One entry per real player's channel line (the first bot to hear it); a line addressed to a bot is the chat service's to answer.
        if (line.SenderIsBot || line.Type != ChatType.Channel || !line.FirstHearing || line.AddressesBot || line.ChannelName is null) return;
        if (_heard.Count >= _maxHeard) return;
        if (Read(line.Text) == WorldLineKind.None) return;
        _heard.Enqueue(line);
    }

    // --- reading ---------------------------------------------------------------------------------------------------------

    /// <summary>What <paramref name="text"/> asks for: more players for a group, goods to buy, or nothing a bot answers.</summary>
    internal static WorldLineKind Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WorldLineKind.None;
        if (Lfm().IsMatch(text)) return WorldLineKind.LookingForMore;
        if (Wtb().IsMatch(text)) return WorldLineKind.WantToBuy;
        return WorldLineKind.None;
    }

    /// <summary>The role an LFM line asks for (null: any).</summary>
    internal static BotChatRole? AskedRole(string text)
    {
        if (Tank().IsMatch(text)) return BotChatRole.Tank;
        if (Healer().IsMatch(text)) return BotChatRole.Healer;
        if (Damage().IsMatch(text)) return BotChatRole.Damage;
        return null;
    }

    /// <summary>The first of <paramref name="goods"/> a WTB line names (whole words, any case); null when none.</summary>
    internal static BotChatItem? Wanted(string text, IReadOnlyList<BotChatItem> goods)
    {
        if (goods.Count == 0) return null;
        string lower = " " + Normalize(text) + " ";
        foreach (BotChatItem item in goods)
        {
            string name = Normalize(item.Name);
            if (name.Length >= 3 && lower.Contains(" " + name + " ", StringComparison.Ordinal)) return item;
        }

        return null;
    }

    /// <summary>The asking price of one item: twice its vendor value (a 2005 player's "fair price"), at least a silver.</summary>
    internal static string Price(BotChatItem item) => PlayerbotChatEvents.Coins(Math.Max(100u, item.SellPrice * 2));

    private static string Normalize(string text) => Spaces().Replace(NotWord().Replace(text.ToLowerInvariant(), " "), " ").Trim();

    // --- reacting --------------------------------------------------------------------------------------------------------

    private void React(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotConversationOptions options, BotHeardLine line)
    {
        long now = pass.NowMs;
        while (_reactions.Count > 0 && now - _reactions.Peek() >= MinuteMs) _reactions.Dequeue();
        if (_reactions.Count >= options.WorldReactionsPerMinute) return;
        if (_answered.TryGetValue(line.SenderGuid.Value, out long until) && until > now) return;
        if (pass.World.FindOnlinePlayer(line.SenderGuid) is not { } sender || sender.Session is not WorldSession { IsManaged: false }) return;
        if (pass.Social.Channels.Find(sender.Team, line.ChannelName!) is not { } channel) return;
        WorldLineKind kind = Read(line.Text);
        BotChatRole? role = kind == WorldLineKind.LookingForMore ? AskedRole(line.Text) : null;

        // A bounded look at the bots in that channel, from a random start.
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count == 0) return;
        int answered = 0, looks = 0, offset = pass.Random.Next(bots.Count);
        for (int i = 0; i < bots.Count && looks < options.CandidatesPerPass && answered < options.ReactionsPerLine; i++)
        {
            PlayerbotSocialBot bot = bots[(offset + i) % bots.Count];
            Player player = bot.Player;
            if (player.Team != sender.Team || !channel.IsOn(player.Guid) || !player.IsAlive || player.Combat.IsInCombat) continue;
            looks++;
            BotSpeech? speech = kind switch
            {
                WorldLineKind.LookingForMore => LfmAnswer(pass, bot, line, role),
                WorldLineKind.WantToBuy => WtbAnswer(pass, bot, line),
                _ => null,
            };
            if (speech is null || chat.TrySpeak(bot.Id, player.Guid, speech) != BotSpeechAdmission.Queued) continue;
            answered++;
        }

        if (answered == 0) return;
        Reactions++;
        _reactions.Enqueue(now);
        _answered[line.SenderGuid.Value] = now + (options.ReactionPlayerCooldownMinutes * MinuteMs);
    }

    private static BotSpeech? LfmAnswer(PlayerbotSocialPass pass, PlayerbotSocialBot bot, BotHeardLine line, BotChatRole? role)
    {
        Player player = bot.Player;
        if (bot.PartyDriven || bot.GroupDriven || player.Level < LfgLevel || pass.Social.Groups.GetGroup(player.Guid) is not null) return null;
        if (PlayerbotDungeons.Named(line.Text, player.Level) is not { } dungeon || !PlayerbotDungeons.Fits(dungeon, player.Level)) return null;
        if (role is { } wanted)
        {
            PlayerbotTalentBuild? build = player.Level >= 10 ? PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low) : null;
            if (PlayerbotPersonaFacts.RoleOf(player.Class, build) != wanted) return null;
        }

        return Answer(pass, bot, line, "react.lfm", new Dictionary<string, string>(StringComparer.Ordinal) { ["dungeon"] = dungeon.Short });
    }

    private static BotSpeech? WtbAnswer(PlayerbotSocialPass pass, PlayerbotSocialBot bot, BotHeardLine line)
    {
        PlayerbotPersonaFacts.Bags bags = PlayerbotPersonaFacts.ReadBags(bot.Player, spells: null);
        if (Wanted(line.Text, bags.Surplus) is not { } item) return null;
        CultureInfo c = CultureInfo.InvariantCulture;
        return Answer(pass, bot, line, "react.wtb", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["surplus"] = item.Name,
            ["surpluscount"] = item.Count.ToString(c),
            ["price"] = Price(item),
        });
    }

    /// <summary>A whisper to the line's sender (a reply: the sender is its audience), filled from the bot's facts and <paramref name="values"/>.</summary>
    private static BotSpeech? Answer(PlayerbotSocialPass pass, PlayerbotSocialBot bot, BotHeardLine line, string key, Dictionary<string, string> values)
    {
        BotChatPersona persona = bot.Party.PersonaFor(bot.Player);
        var ask = new BotChatAsk(bot.Id, bot.Player.Guid, persona, line.SenderGuid, line.SenderName, ChatType.Whisper, line.Text, FromMaster: false,
            InviteAllowed: false, Topic: new BotChatTopic(key, string.Empty, values, string.Empty), Addressed: false);
        if (PlayerbotBuiltinChat.Pick(key, ask, null, pass.Random) is not { } text) return null;
        return BotSpeech.WhisperTo(line.SenderName, line.SenderGuid, text, key) with { IsReply = true };
    }

    [GeneratedRegex(@"\b(lf\d?m|lfm|looking for (more|\d))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Lfm();

    [GeneratedRegex(@"\b(wtb|buying)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Wtb();

    [GeneratedRegex(@"\b(tank|tanks|mt)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tank();

    [GeneratedRegex(@"\b(heal|heals|healer|healers|priest|druid|shaman|paladin)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Healer();

    [GeneratedRegex(@"\b(dps|dd|damage)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Damage();

    [GeneratedRegex(@"[^a-z0-9']+")] private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
