using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Bots.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What a world channel line asks for, as a bot reads it (<see cref="PlayerbotWorldChatter.Read"/>).</summary>
internal enum WorldLineKind : byte
{
    None,

    /// <summary>"LFM SFK need heals", "lf2m dm": a group looks for players.</summary>
    LookingForMore,

    /// <summary>"WTB Linen Cloth", "LF Copper Ore": a player wants to buy something.</summary>
    WantToBuy,

    /// <summary>"WTS Linen Cloth x20", "selling copper ore": a player offers goods; a bot that needs them for a recipe opens a buy deal.</summary>
    WantToSell,
}

/// <summary>
/// World channel answers (<see cref="PlayerbotConversationOptions.WorldReactions"/>): a real player's LFM line for a dungeon that fits a
/// free bot (no group, not driven by a master or the group coordinator, the asked role when one is named) gets a whispered "inv" or
/// "lvl 24 priest here, inv?"; a WTB line naming goods a bot carries spare (beyond what its wanted recipes need) gets a whispered offer priced from the price book; a WTS line naming a reagent
/// a bot's wanted recipe lacks gets a whispered bid, backed by a quoted buyer deal of the trade desk (<see cref="PlayerbotTradeDesk"/>).
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
        {
            foreach ((ulong key, long until) in _answered)
                if (until <= pass.NowMs) _answered.Remove(key);
        }
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
        if (Wts().IsMatch(text)) return WorldLineKind.WantToSell;
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
        if (goods.Count == 0 || string.IsNullOrWhiteSpace(text)) return null;
        return WantedNormalized(Normalize(text), goods);
    }

    private static BotChatItem? WantedNormalized(string normalizedText, IReadOnlyList<BotChatItem> goods)
    {
        if (goods.Count == 0 || normalizedText.Length < 3) return null;
        for (int i = 0; i < goods.Count; i++)
        {
            BotChatItem item = goods[i];
            string name = Normalize(item.Name);
            if (name.Length >= 3 && ContainsWholePhrase(normalizedText, name)) return item;
        }

        return null;
    }

    private static bool ContainsWholePhrase(string haystack, string needle)
    {
        int start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            int idx = haystack.IndexOf(needle, start, StringComparison.Ordinal);
            if (idx < 0) return false;
            bool leftOk = idx == 0 || haystack[idx - 1] == ' ';
            int end = idx + needle.Length;
            bool rightOk = end == haystack.Length || haystack[end] == ' ';
            if (leftOk && rightOk) return true;
            start = idx + 1;
        }

        return false;
    }

    /// <summary>The asking price of one item: twice its vendor value (a 2005 player's "fair price"), at least a silver.</summary>
    internal static string Price(BotChatItem item) => PlayerbotChatEvents.Coins(Math.Max(100u, item.SellPrice * 2));

    /// <summary>
    /// The asking price of one item from the price source (the auction price book's median, else the reference price), as the trade desk would quote it
    /// (<see cref="PlayerbotVoiceFacts.DealAsk"/>); the vendor-multiple <see cref="Price(BotChatItem)"/> when the source has no price or the item is unknown.
    /// </summary>
    internal static string Price(BotChatItem item, IPlayerbotDealPriceSource prices, ItemTemplate? template)
    {
        ArgumentNullException.ThrowIfNull(prices);
        uint reference = template is null ? 0 : prices.ReferencePerUnit(template);
        return reference == 0 ? Price(item) : PlayerbotChatEvents.Coins((uint)Math.Min(PlayerbotVoiceFacts.DealAsk(reference, item.SellPrice, item.Quality, 1), uint.MaxValue));
    }

    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        char[]? rented = null;
        Span<char> buffer = text.Length <= 256
            ? stackalloc char[256]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(text.Length));
        try
        {
            int len = 0;
            bool inSpace = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToLowerInvariant(text[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '\'')
                {
                    buffer[len++] = c;
                    inSpace = false;
                }
                else if (!inSpace)
                {
                    buffer[len++] = ' ';
                    inSpace = true;
                }
            }

            if (len > 0 && buffer[len - 1] == ' ') len--;
            return len == 0 ? string.Empty : new string(buffer[..len]);
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }
    }

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
        if (kind == WorldLineKind.None) return;
        BotChatRole? role = kind == WorldLineKind.LookingForMore ? AskedRole(line.Text) : null;
        string? normalizedWtb = kind is WorldLineKind.WantToBuy or WorldLineKind.WantToSell ? Normalize(line.Text) : null;

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
                WorldLineKind.WantToBuy => WtbAnswer(pass, bot, line, normalizedWtb!),
                WorldLineKind.WantToSell => WtsAnswer(pass, bot, line, normalizedWtb!),
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

    private static BotSpeech? WtbAnswer(PlayerbotSocialPass pass, PlayerbotSocialBot bot, BotHeardLine line, string normalizedText)
    {
        PlayerbotPersonaFacts.Bags bags = PlayerbotPersonaFacts.ReadBags(bot.Player, spells: null);
        IReadOnlyList<BotChatItem> spare = bags.Surplus;
        // Reagents the bot's wanted recipes call for are kept back: only what is held above one craft's need is offered.
        if (spare.Count > 0 && bot.Session.Services.GetService<SpellFeature>() is { } feature)
        {
            var supply = new PlayerbotVendorSupplyNeeds();
            supply.Refresh(bot.Player, feature.System.Store, feature.Spellbook);
            spare = PlayerbotTradeGifts.WithoutReserved(spare, supply.Reagents);
        }

        if (WantedNormalized(normalizedText, spare) is not { } item) return null;
        CultureInfo c = CultureInfo.InvariantCulture;
        return Answer(pass, bot, line, "react.wtb", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["surplus"] = item.Name,
            ["surpluscount"] = item.Count.ToString(c),
            ["price"] = pass.Services.GetService<ManagedPlayerbotFeature>()?.DealPrices is { } prices
                ? Price(item, prices, bot.Player.Inventory.Templates.Find(item.Entry))
                : Price(item),
        });
    }

    /// <summary>
    /// A player's WTS line naming a reagent a wanted recipe of the bot lacks (<see cref="PlayerbotVendorSupplyNeeds.Shortfalls"/>, a tradeable item): the trade desk quotes
    /// the bot as buyer for the missing count (<see cref="PlayerbotTradeDesk.Quote"/>: price book bid, money, bag space and every limit checked) and the bot whispers that
    /// price. The sender then agrees ("ok", a counter-offer) in a whisper and the desk's normal buyer flow takes over. Null when the bot needs nothing the line names,
    /// the desk is off or refuses, or the bot has no spellbook.
    /// </summary>
    internal static BotSpeech? WtsAnswer(PlayerbotSocialPass pass, PlayerbotSocialBot bot, BotHeardLine line, string normalizedText)
    {
        if (bot.Session.Services.GetService<SpellFeature>() is not { } feature || pass.Services.GetService<ManagedPlayerbotFeature>()?.TradeDesk is not { Enabled: true } desk) return null;
        Player player = bot.Player;
        var supply = new PlayerbotVendorSupplyNeeds();
        supply.Refresh(player, feature.System.Store, feature.Spellbook);
        var needed = new List<BotChatItem>(8);
        foreach ((uint itemId, uint missing) in supply.Shortfalls(player, 8))
            if (player.Inventory.Templates.Find(itemId) is { Name.Length: > 0 } template && PlayerbotVoiceFacts.Tradeable(template))
                needed.Add(new BotChatItem(itemId, template.Name, (int)Math.Min(missing, int.MaxValue), template.Quality, template.SellPrice));
        if (PlayerbotTradeGifts.NeededFromLine(normalizedText, needed) is not { } item) return null;
        if (pass.World.FindOnlinePlayer(line.SenderGuid) is not { } seller) return null;
        PlayerbotDealQuote quote = desk.Quote(bot.Id, player, seller, PlayerbotDealRole.Buyer, item.Entry, (uint)item.Count);
        if (!quote.Ok) return null;
        CultureInfo c = CultureInfo.InvariantCulture;
        return Answer(pass, bot, line, "react.wts", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item"] = item.Name,
            ["count"] = item.Count.ToString(c),
            ["price"] = quote.PriceText,
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

    [GeneratedRegex(@"\b(wts|selling)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Wts();

    [GeneratedRegex(@"\b(tank|tanks|mt)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tank();

    [GeneratedRegex(@"\b(heal|heals|healer|healers|priest|druid|shaman|paladin)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Healer();

    [GeneratedRegex(@"\b(dps|dd|damage)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Damage();
}
