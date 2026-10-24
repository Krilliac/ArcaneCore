using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// An idle bot waving at or greeting a real player standing close by (docs/areas/playbots-social.md), only while
/// <see cref="PlayerbotSocialEmoteOptions.Enabled"/> is on. The bot is autonomous (not scripted, in no party and no bot group), alive and out of
/// combat; the player is real, of the bot's faction, alive, out of combat, within <see cref="Range"/> yards on the same map and has not put the
/// bot on its ignore list. Per bot and player at most one in <see cref="PlayerbotSocialTimings.EmotePairGapMs"/>, per player one in
/// <see cref="PlayerbotSocialTimings.EmotePlayerGapMs"/> from any bot, per bot one in <see cref="PlayerbotSocialTimings.EmoteBotGapMs"/>, and
/// <see cref="PlayerbotSocialTimings.EmotesPerMinute"/> in all. The action is a wave (CMSG_TEXT_EMOTE, TEXTEMOTE_WAVE, aimed at the player) or a
/// /say greeting taken from the built-in <c>social.greet</c> lines alone (never a model provider) and screened by the output filter.
/// </summary>
internal sealed class PlayerbotSocialEmotes
{
    /// <summary>How close (yards) a player must stand.</summary>
    internal const float Range = 8f;

    private readonly PlayerbotSocialRateLimiter<(Guid Bot, ulong Player)> _pairs = new();
    private readonly PlayerbotSocialRateLimiter<ulong> _players = new();
    private readonly PlayerbotSocialRateLimiter<Guid> _bots = new();
    private readonly PlayerbotSocialRateLimiter<int> _all = new();
    private long _sweptAtMs;

    /// <summary>Waves and greetings sent so far (tests and status).</summary>
    internal int Sent { get; private set; }

    internal void Run(PlayerbotSocialPass pass)
    {
        if (!pass.Options.Emotes.Enabled) return;
        PlayerbotSocialTimings timings = pass.Timings;
        if (pass.NowMs - _sweptAtMs >= 60_000)
        {
            _sweptAtMs = pass.NowMs;
            _pairs.Sweep(pass.NowMs, timings.EmotePairGapMs);
            _players.Sweep(pass.NowMs, timings.EmotePlayerGapMs);
            _bots.Sweep(pass.NowMs, timings.EmoteBotGapMs);
            _all.Sweep(pass.NowMs, 60_000);
        }

        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            if (!_all.HasRoom(0, timings.EmotesPerMinute, pass.NowMs, 60_000)) return;
            if (bot.PartyDriven || bot.GroupDriven || !bot.Player.IsAlive || bot.Player.Combat.IsInCombat || bot.Player.Map is null) continue;
            if (!_bots.GapElapsed(bot.Id, timings.EmoteBotGapMs, pass.NowMs)) continue;
            if (FindPlayer(pass, bot) is { } target) Greet(pass, bot, target);
        }
    }

    private Player? FindPlayer(PlayerbotSocialPass pass, PlayerbotSocialBot bot)
    {
        Player self = bot.Player;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (pass.World.FindOnlinePlayer(guid) is not { Session: WorldSession { IsManaged: false } } other) continue;
            if (!ReferenceEquals(other.Map, self.Map) || other.Team != self.Team || !other.IsAlive || other.Combat.IsInCombat) continue;
            if (Distance(self, other) > Range || pass.Social.IsIgnoring(other, self.Guid)) continue;
            if (!_players.GapElapsed(guid.Value, pass.Timings.EmotePlayerGapMs, pass.NowMs)) continue;
            if (!_pairs.GapElapsed((bot.Id, guid.Value), pass.Timings.EmotePairGapMs, pass.NowMs)) continue;
            return other;
        }

        return null;
    }

    private void Greet(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player target)
    {
        bool sent = pass.Random.Next(2) == 0 ? Say(pass, bot, target) || Wave(bot, target) : Wave(bot, target) || Say(pass, bot, target);
        if (!sent) return;
        Sent++;
        long now = pass.NowMs;
        _all.TryTake(0, pass.Timings.EmotesPerMinute, now, 60_000);
        _bots.MarkSpoke(bot.Id, now);
        _players.MarkSpoke(target.Guid.Value, now);
        _pairs.MarkSpoke((bot.Id, target.Guid.Value), now);
    }

    private static bool Wave(PlayerbotSocialBot bot, Player target)
        => PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgTextEmote, PlayerbotSocialPackets.TextEmote(PlayerbotSocialPackets.TextEmoteWave, target.Guid));

    private static bool Say(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player target)
    {
        Player self = bot.Player;
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), target.Guid, target.Name, ChatType.Say, string.Empty, false, false);
        if (PlayerbotBuiltinChat.Pick("social.greet", ask, null, pass.Random) is not { } text) return false;
        PlayerbotChatSafetyFilter filter = pass.Chat?.Safety.Filter() ?? PlayerbotChatSafetyFilter.Default;
        SafetyVerdict verdict = filter.Screen(text, PlayerbotChatSafetyCategories.All);
        if (verdict.Flagged || verdict.TimedOut) return false;
        Language language = self.Team == Team.Horde ? Language.Orcish : Language.Common;
        return PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(ChatType.Say, language, text));
    }

    /// <summary>
    /// The text emote a bot answers <paramref name="textEmote"/> with, as a player would (vmangos TEXTEMOTE_* ids): a wave, hello or greeting
    /// with a wave or hello, a bow with a bow, a hug or kiss with a blush or a hug, a salute with a salute, a cheer with a cheer, thanks with
    /// "you're welcome", a dance with a dance, a rude gesture or a chicken with a rude one now and then. Null: no answer (most emotes).
    /// </summary>
    internal static uint? ReplyTo(uint textEmote, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        uint[]? answers = AnswersFor(textEmote);
        return answers is null ? null : answers[random.Next(answers.Length)];
    }

    /// <summary>The answers <see cref="ReplyTo"/> picks among for <paramref name="textEmote"/> (a repeated one is likelier); null when the emote gets none. A new array on every call.</summary>
    internal static uint[]? AnswersFor(uint textEmote)
        => textEmote switch
        {
            TextEmotes.Wave or TextEmotes.Hello or TextEmotes.Greet or TextEmotes.Hail => [TextEmotes.Wave, TextEmotes.Wave, TextEmotes.Hello, TextEmotes.Smile],
            TextEmotes.Bow or TextEmotes.Curtsey => [TextEmotes.Bow, TextEmotes.Bow, TextEmotes.Curtsey],
            TextEmotes.Hug => [TextEmotes.Blush, TextEmotes.Hug, TextEmotes.Smile],
            TextEmotes.Kiss => [TextEmotes.Blush, TextEmotes.Giggle, TextEmotes.Shy],
            TextEmotes.Wink or TextEmotes.Flirt => [TextEmotes.Blush, TextEmotes.Giggle],
            TextEmotes.Salute => [TextEmotes.Salute],
            TextEmotes.Cheer or TextEmotes.Victory or TextEmotes.Congratulate or TextEmotes.Applaud or TextEmotes.Clap
                => [TextEmotes.Cheer, TextEmotes.Bow, TextEmotes.Thank],
            TextEmotes.Thank => [TextEmotes.Welcome, TextEmotes.Smile, TextEmotes.Nod],
            TextEmotes.Dance => [TextEmotes.Dance, TextEmotes.Dance, TextEmotes.Cheer],
            TextEmotes.Bye => [TextEmotes.Wave, TextEmotes.Bye],
            TextEmotes.Laugh or TextEmotes.Rofl or TextEmotes.Giggle => [TextEmotes.Grin, TextEmotes.Laugh],
            TextEmotes.Poke => [TextEmotes.Shrug, TextEmotes.Sigh, TextEmotes.Poke],
            TextEmotes.Point => [TextEmotes.Shrug],
            TextEmotes.Flex => [TextEmotes.Applaud, TextEmotes.Laugh],
            TextEmotes.Threaten => [TextEmotes.Chicken, TextEmotes.Laugh, TextEmotes.Flex],
            TextEmotes.Rude or TextEmotes.Chicken => [TextEmotes.Rude, TextEmotes.Shrug, TextEmotes.Sigh],
            TextEmotes.Apologize => [TextEmotes.Nod, TextEmotes.Smile],
            TextEmotes.Beckon => [TextEmotes.Nod, TextEmotes.No],
            TextEmotes.Kneel => [TextEmotes.Bow, TextEmotes.Salute],
            _ => null,
        };

    /// <summary>The 1.12 text emote ids used here (EmotesText.dbc; vmangos TextEmotes).</summary>
    internal static class TextEmotes
    {
        internal const uint Agree = 1, Apologize = 4, Applaud = 5, Beckon = 7, Blush = 12, Bow = 17, Bye = 19, Cheer = 21, Chicken = 22,
            Clap = 24, Congratulate = 26, Curtsey = 33, Dance = 34, Flex = 41, Flirt = 42, Giggle = 45, Greet = 48, Grin = 49, Hail = 53,
            Hello = 55, Hug = 56, Kiss = 58, Kneel = 59, Laugh = 60, No = 66, Nod = 67, Point = 72, Poke = 73, Rofl = 76, Rude = 77,
            Salute = 78, Shrug = 83, Shy = 84, Sigh = 85, Thank = 97, Threaten = 98, Victory = 100, Wave = 101, Welcome = 102,
            Mock = 119, Violin = 143, Smile = 163, Wink = 363;
    }

}
