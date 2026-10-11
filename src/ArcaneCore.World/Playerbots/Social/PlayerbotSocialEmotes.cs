using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Party;

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

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
