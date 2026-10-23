using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Bots answering calls for help and lending a hand to strangers (<see cref="PlayerbotSocialHelpingOptions"/>, docs/areas/playbots-social.md,
/// Helping). Run from the social pass on the world thread. It decides; the bot's think does (<see cref="PlayerbotBrain.RequestHelp"/>).
/// <list type="number">
/// <item><b>Calls for help.</b> A targeted help emote, or a help yell or say from someone close, reaches
/// <see cref="PlayerbotSocialRecorder.NoteHelpCall"/>. A free bot (not driven by a party or a group, alive, out of combat, healthy, on a low-priority
/// goal) that the asker is close to (<c>BystanderRadiusYards</c>), of its faction, in a fight and neither disliked nor a rival, rolls
/// <see cref="PlayerbotCallForHelp.Answers"/> once per asker per minute; on yes it joins the asker's fight against its attacker.</item>
/// <item><b>The trouble scan.</b> Each pass, a bounded round of free bots looks at the players and bots in sight within
/// <c>TroubleRadiusYards</c>: one in a fight below 40% health (or a mana user below 20% mana on the run) is helped with the chance
/// <c>Helpfulness x (1 + affinity/100)</c>, deterministic per bot, victim and fight; a dead one with a fresh corpse is resurrected (once per
/// corpse) by a class that can; a healthy one is now and then buffed (once per stranger per <c>BuffCooldownMinutes</c>).</item>
/// </list>
/// Never against a fight that would flag the bot for PvP unless it is flagged or War Mode is on, never for a rival, never for a unit it dislikes
/// (a call) and never cross-faction. State is memory only and bounded; every dictionary is swept.
/// </summary>
internal sealed class PlayerbotHelpLane
{
    /// <summary>The most calls for help taken per pass.</summary>
    internal const int CallsPerPass = 16;

    /// <summary>The most bots the trouble scan looks over per pass (a round robin over the running bots).</summary>
    internal const int BotsPerPass = 24;

    /// <summary>The most players one bot looks at per scan.</summary>
    internal const int SightLimit = 64;

    /// <summary>How long (milliseconds) a bot's answer to one asker stands.</summary>
    internal const long AnswerMemoryMs = 60_000;

    /// <summary>The least time (milliseconds) before a bot that was given a request looks again.</summary>
    internal const long BotGapMs = 5_000;

    /// <summary>The share of a minute's rolls in which a free bot offers a buff to a stranger it meets (scaled by its helpfulness).</summary>
    internal const double BuffRate = 0.2;

    /// <summary>How long (milliseconds) a unit must stay out of trouble before its fight counts as over.</summary>
    private const long EpisodeGapMs = 20_000;

    private readonly Dictionary<(Guid Bot, ulong Asker), long> _answered = [];
    private readonly Dictionary<ulong, (long Since, long Seen)> _trouble = [];
    private readonly Dictionary<ulong, long> _deadSince = [];
    private readonly Dictionary<ulong, long> _rezzedFor = [];
    private readonly Dictionary<(Guid Bot, ulong Stranger), long> _buffed = [];
    private readonly Dictionary<Guid, long> _botNext = [];
    private readonly Dictionary<(Guid Bot, ulong Helper), long> _grateful = [];
    private long _sweptAtMs;
    private int _cursor;

    /// <summary>Calls for help answered with a yes (tests and status).</summary>
    internal int Answered { get; private set; }

    /// <summary>Calls for help answered with a no, or ignored (tests and status).</summary>
    internal int Declined { get; private set; }

    /// <summary>Strangers in trouble that a bot was sent to (tests and status).</summary>
    internal int Helped { get; private set; }

    /// <summary>Strangers a bot was sent to resurrect.</summary>
    internal int Resurrections { get; private set; }

    /// <summary>Strangers a bot was sent to buff.</summary>
    internal int Buffs { get; private set; }

    /// <summary>
    /// <paramref name="bot"/> owes <paramref name="helper"/> a favour until <paramref name="untilMs"/>: if the helper dies within reach, the bot
    /// resurrects it without the usual chance (<c>PlayerbotGratitude</c>).
    /// </summary>
    internal void Owe(Guid bot, ulong helper, long untilMs)
    {
        if (_grateful.Count < 1024) _grateful[(bot, helper)] = untilMs;
    }

    /// <summary>The social pass.</summary>
    internal void Run(PlayerbotSocialPass pass)
    {
        PlayerbotSocialHelpingOptions options = pass.Options.Helping;
        PlayerbotSocialRecorder? recorder = pass.Recorder;
        if (recorder is null) return;
        if (!options.Enabled || !recorder.Enabled)
        {
            while (recorder.TryTakeHelpCall(out _, out _)) { }
            return;
        }

        Sweep(pass.NowMs);
        if (options.AnswerHelp) AnswerCalls(pass, recorder);
        else
            while (recorder.TryTakeHelpCall(out _, out _)) { }

        Scan(pass, recorder, options);
    }

    // --- the bot is free -----------------------------------------------------------------------------------------------------

    /// <summary>Whether the bot may lend a hand now: its own brain drives it, it is alive, out of combat, healthy, on a free goal with no request pending.</summary>
    internal static bool IsFree(PlayerbotSocialBot bot)
    {
        if (bot.PartyDriven || bot.GroupDriven || bot.Brain is not { PendingHelp: PlayerbotHelpKind.None } brain) return false;
        Player player = bot.Player;
        return player.IsAlive && player.Map is not null && !player.Combat.IsInCombat && player.Duel is null
            && player.MaxHealth > 0 && player.Health * 100UL >= (ulong)PlayerbotHelpRules.HelperMinHealthPercent * player.MaxHealth
            && PlayerbotHelpRules.IsFreeGoal(brain.Goal);
    }

    private static (bool Known, int Affinity, bool Rival) Feelings(PlayerbotSocialRecorder recorder, Guid bot, ulong other)
    {
        if (!recorder.Memory.TryGet(bot, other, out PlayerbotRelation relation)) return (false, 0, false);
        return (true, recorder.Memory.EffectiveAffinity(relation), recorder.Memory.IsRival(relation));
    }

    // --- 1. calls for help ---------------------------------------------------------------------------------------------------

    private void AnswerCalls(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder)
    {
        PlayerbotSocialMemoryOptions memory = pass.Options.Memory;
        for (int budget = CallsPerPass; budget > 0 && recorder.TryTakeHelpCall(out Guid botId, out ObjectGuid askerGuid); budget--)
        {
            PlayerbotSocialBot? bot = null;
            foreach (PlayerbotSocialBot candidate in pass.Bots)
                if (candidate.Id == botId)
                {
                    bot = candidate;
                    break;
                }

            if (bot is null || !IsFree(bot) || pass.World.FindOnlinePlayer(askerGuid) is not { } asker) continue;
            Player self = bot.Player;
            if (ReferenceEquals(asker, self) || !ReferenceEquals(asker.Map, self.Map) || asker.Team != self.Team || !asker.IsAlive
                || !asker.Combat.IsInCombat || Distance(self, asker) > memory.BystanderRadiusYards) continue;
            if (_answered.TryGetValue((botId, askerGuid.Value), out long until) && until > pass.NowMs) continue;
            _answered[(botId, askerGuid.Value)] = pass.NowMs + AnswerMemoryMs;

            (bool known, int affinity, bool rival) = Feelings(recorder, botId, askerGuid.Value);
            float helpfulness = PlayerbotTraits.For(self.Guid.Value).Helpfulness;
            bool answers = PlayerbotCallForHelp.Askable(known, affinity, rival)
                && PlayerbotCallForHelp.Answers(self.Guid.Value, askerGuid.Value, (uint)(pass.NowMs / 15_000), helpfulness, affinity)
                && PlayerbotHelpRules.AttackerOf(self, asker, PlayerbotHelpRules.MayFlag(self, pass.Options.WarMode.Enabled)) is not null
                && bot.Brain!.RequestHelp(PlayerbotHelpKind.Assist, asker.Guid);
            if (answers)
            {
                Answered++;
                continue;
            }

            Declined++;
            if (rival || known && affinity < 0) continue; // nothing is said to someone it dislikes
            if (PlayerbotCallForHelp.SaysSorry(self.Guid.Value, askerGuid.Value, (uint)(pass.NowMs / 15_000))) Sorry(pass, bot, asker);
        }
    }

    // "Sorry, can't": now and then a bot that stays says so. Through the announcement path, so the limiter, the safety screen and the opt-in rules apply.
    private static void Sorry(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player asker)
    {
        if (pass.Chat is not { IsActive: true } chat) return;
        Player self = bot.Player;
        var speech = new BotSpeech(BotSpeechChannel.Say, string.Empty, "help-sorry", BotSpeechPriority.Low) { TargetGuid = asker.Guid };
        var topic = new BotChatTopic("help.sorry", "Someone asked you for help in a fight and you cannot join. Say sorry in one short line.",
            new Dictionary<string, string>(), string.Empty);
        var ask = new BotChatAsk(bot.Id, self.Guid, bot.Party.PersonaFor(self), asker.Guid, asker.Name, ChatType.Say, "You cannot help right now.",
            FromMaster: false, InviteAllowed: false, SenderAccountId: 0, Topic: topic, Speech: speech);
        chat.TryAnnounce(ask);
    }

    // --- 2. the trouble scan -------------------------------------------------------------------------------------------------

    private void Scan(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialHelpingOptions options)
    {
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        if (bots.Count == 0) return;
        int count = Math.Min(BotsPerPass, bots.Count);
        for (int i = 0; i < count; i++)
        {
            PlayerbotSocialBot bot = bots[(_cursor + i) % bots.Count];
            if (_botNext.TryGetValue(bot.Id, out long next) && next > pass.NowMs) continue;
            if (!IsFree(bot)) continue;
            if (LookAround(pass, recorder, options, bot)) _botNext[bot.Id] = pass.NowMs + BotGapMs;
        }

        _cursor = (_cursor + count) % bots.Count;
    }

    private bool LookAround(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialHelpingOptions options, PlayerbotSocialBot bot)
    {
        Player self = bot.Player;
        float radius = options.TroubleRadiusYards;
        float helpfulness = PlayerbotTraits.For(self.Guid.Value).Helpfulness;
        bool mayFlag = PlayerbotHelpRules.MayFlag(self, pass.Options.WarMode.Enabled);
        Player? assistVictim = null, deadVictim = null, buffTarget = null;
        float assistYards = float.MaxValue, deadYards = float.MaxValue, buffYards = float.MaxValue;
        int looked = 0;
        foreach (ObjectGuid guid in self.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == self.Guid) continue;
            if (++looked > SightLimit) break;
            if (pass.World.FindOnlinePlayer(guid) is not { } other || !ReferenceEquals(other.Map, self.Map) || other.Team != self.Team) continue;
            float yards = Distance(self, other);
            if (yards > radius) continue;
            if (!other.IsAlive)
            {
                _deadSince.TryAdd(guid.Value, pass.NowMs);
                if (yards < deadYards)
                {
                    deadVictim = other;
                    deadYards = yards;
                }

                continue;
            }

            _deadSince.Remove(guid.Value);
            _rezzedFor.Remove(guid.Value);
            var facts = new PlayerbotTroubleFacts(true, true, other.Combat.IsInCombat, PercentOf(other).Health, PercentOf(other).Mana,
                (other.Movement.Flags & MovementFlags.MaskMoving) != 0, other.Duel is not null);
            if (PlayerbotHelpRules.InTrouble(facts))
            {
                if (!_trouble.TryGetValue(guid.Value, out (long Since, long Seen) episode) || pass.NowMs - episode.Seen > EpisodeGapMs) episode = (pass.NowMs, pass.NowMs);
                _trouble[guid.Value] = (episode.Since, pass.NowMs);
                if (yards < assistYards)
                {
                    assistVictim = other;
                    assistYards = yards;
                }
            }
            else if (!other.Combat.IsInCombat && yards <= PlayerbotHelpRules.CastYards && yards < buffYards)
            {
                buffTarget = other;
                buffYards = yards;
            }
        }

        if (assistVictim is not null && TryAssist(pass, recorder, bot, assistVictim, helpfulness, mayFlag)) return true;
        if (deadVictim is not null && options.ResurrectStrangers && TryResurrect(pass, recorder, bot, deadVictim, helpfulness)) return true;
        return buffTarget is not null && options.BuffStrangers && TryBuff(pass, recorder, options, bot, buffTarget, helpfulness);
    }

    private static (int Health, int? Mana) PercentOf(Player player) => PlayerbotPersonaFacts.Vitals(player);

    private bool TryAssist(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialBot bot, Player victim, float helpfulness, bool mayFlag)
    {
        (_, int affinity, bool rival) = Feelings(recorder, bot.Id, victim.Guid.Value);
        if (rival) return false;
        Player self = bot.Player;
        if (PlayerbotHelpRules.AttackerOf(self, victim, mayFlag) is null) return false;
        uint episode = (uint)_trouble[victim.Guid.Value].Since;
        if (!PlayerbotHelpRules.Helps(self.Guid.Value, victim.Guid.Value, episode, helpfulness, affinity)) return false;
        if (!bot.Brain!.RequestHelp(PlayerbotHelpKind.Assist, victim.Guid)) return false;
        Helped++;
        return true;
    }

    private bool TryResurrect(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialBot bot, Player dead, float helpfulness)
    {
        Player self = bot.Player;
        if (!PlayerbotHelpRules.CanResurrect(self.Class) || !_deadSince.TryGetValue(dead.Guid.Value, out long since)) return false;
        if (pass.NowMs - since > PlayerbotHelpRules.CorpseFreshMs || _rezzedFor.TryGetValue(dead.Guid.Value, out long done) && done == since) return false;
        if (PlayerbotHelpRules.IsPvpFlagged(dead) && !PlayerbotHelpRules.IsPvpFlagged(self)) return false;
        if (ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(dead)) return false;
        // The body to reach: the player's unit until it released, then its corpse.
        WorldObject body = dead;
        if ((dead.Flags & PlayerFlags.Ghost) != 0)
        {
            if (dead.Combat.Corpse is not { IsInWorld: true } corpse || !ReferenceEquals(corpse.Map, self.Map)) return false;
            body = corpse;
        }

        if (Distance(self, body) > PlayerbotHelpRules.CastYards) return false;
        (_, int affinity, bool rival) = Feelings(recorder, bot.Id, dead.Guid.Value);
        if (rival) return false;
        bool owes = _grateful.TryGetValue((bot.Id, dead.Guid.Value), out long until) && until > pass.NowMs;
        if (!owes && !PlayerbotHelpRules.Helps(self.Guid.Value, dead.Guid.Value, (uint)since, helpfulness, affinity)) return false;
        if (PlayerbotGroupAI.FindResurrectionSpell(bot.Session, self) is null) return false;
        if (!bot.Brain!.RequestHelp(PlayerbotHelpKind.Resurrect, dead.Guid)) return false;
        _rezzedFor[dead.Guid.Value] = since;
        Resurrections++;
        return true;
    }

    private bool TryBuff(PlayerbotSocialPass pass, PlayerbotSocialRecorder recorder, PlayerbotSocialHelpingOptions options, PlayerbotSocialBot bot, Player stranger,
        float helpfulness)
    {
        Player self = bot.Player;
        if (PlayerbotHelpRules.BuffOf(self.Class) is not { } buffName) return false;
        if (PlayerbotHelpRules.IsPvpFlagged(stranger) && !PlayerbotHelpRules.IsPvpFlagged(self)) return false;
        if (_buffed.TryGetValue((bot.Id, stranger.Guid.Value), out long at) && pass.NowMs - at < options.BuffCooldownMinutes * 60_000L) return false;
        (_, _, bool rival) = Feelings(recorder, bot.Id, stranger.Guid.Value);
        if (rival || !PlayerbotHelpRules.Offers(self.Guid.Value, stranger.Guid.Value, pass.NowMs, helpfulness, BuffRate)) return false;
        // Not one who has the buff already, and not one the bot's own spell book cannot cast (checked again by the think).
        if (bot.Session.Services.GetService<SpellFeature>() is not { } feature) return false;
        foreach (SpellAuraHolder holder in feature.System.GetAuras(stranger))
            if (!holder.IsRemoved && string.Equals(holder.Spell.Name, buffName, StringComparison.Ordinal)) return false;
        if (bot.Brain!.CombatSpells.Abilities(self)[buffName] is null) return false;
        if (!bot.Brain.RequestHelp(PlayerbotHelpKind.Buff, stranger.Guid)) return false;
        _buffed[(bot.Id, stranger.Guid.Value)] = pass.NowMs;
        Buffs++;
        return true;
    }

    private void Sweep(long nowMs)
    {
        if (nowMs - _sweptAtMs < 60_000) return;
        _sweptAtMs = nowMs;
        Prune(_answered, static (until, now) => until <= now, nowMs);
        Prune(_botNext, static (until, now) => until <= now, nowMs);
        Prune(_grateful, static (until, now) => until <= now, nowMs);
        Prune(_buffed, static (at, now) => now - at > 24 * 3_600_000L, nowMs);
        Prune(_deadSince, static (at, now) => now - at > 30 * 60_000L, nowMs);
        Prune(_rezzedFor, static (at, now) => now - at > 30 * 60_000L, nowMs);
        foreach (KeyValuePair<ulong, (long Since, long Seen)> entry in _trouble)
            if (nowMs - entry.Value.Seen > EpisodeGapMs) _trouble.Remove(entry.Key);
    }

    private static void Prune<TKey>(Dictionary<TKey, long> map, Func<long, long, bool> stale, long now) where TKey : notnull
    {
        if (map.Count == 0) return;
        foreach (KeyValuePair<TKey, long> entry in map)
            if (stale(entry.Value, now)) map.Remove(entry.Key);
    }
}
