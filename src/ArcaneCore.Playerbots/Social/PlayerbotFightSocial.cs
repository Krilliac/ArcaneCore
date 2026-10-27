using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// One managed bot's social view of its current fight (world thread; one per bot, kept across brain replacements). While the bot is in combat
/// it reads, once a second, the threat lists of the creatures fighting it: every player (or player's pet) on one of them is a helper, who earns
/// affinity once per fight through <see cref="PlayerbotSocialRecorder.NoteHelp"/>. When the bot's health first drops below
/// <see cref="PlayerbotCallForHelp.LowHealthPercent"/> in a fight it opens a low-health window: it samples up to
/// <see cref="PlayerbotCallForHelp.MaxCandidates"/> nearby qualifying bystanders once, decides once (a hash of its guid and the fight start)
/// whether to call for help or go it alone, and, if it dies in the window, records <see cref="PlayerbotSocialEvent.WatchedMeDie"/> toward the
/// candidates it asked that still qualify and did not help. Cleared when combat ends. Memory: a few arrays and one set; the per-second
/// work is bounded by the attackers' threat lists and, in the window, by 16 lookups.
/// </summary>
internal sealed class PlayerbotFightSocial(Guid botId, PlayerbotSocialMemoryOptions options)
{
    /// <summary>How often the threat lists and the window's candidates are read.</summary>
    internal const uint SampleMs = 1000;

    private readonly Guid _botId = botId;
    private readonly ulong[] _candidates = new ulong[PlayerbotCallForHelp.MaxCandidates];
    private readonly bool[] _holds = new bool[PlayerbotCallForHelp.MaxCandidates];
    private readonly bool[] _asked = new bool[PlayerbotCallForHelp.MaxCandidates];
    private readonly HashSet<ulong> _helpers = [];
    private int _count;
    private bool _inFight;
    private bool _windowOpen;
    private uint _fightStartMs;
    private uint _windowStartMs;
    private uint _sampledMs;

    /// <summary>The recorder events go to (set by the feature; null in a host or a test without it).</summary>
    internal PlayerbotSocialRecorder? Recorder { get; set; }

    /// <summary>
    /// Say the call: the bot and the nearest asked unit. True when a line or the help emote was taken. Set by the feature (the chat service and the
    /// party AI's persona live there); null means the bot cannot ask.
    /// </summary>
    internal Func<Player, Player, bool>? CallForHelp { get; set; }

    /// <summary>Whether the bot is inside a fight it is tracking (tests).</summary>
    internal bool InFight => _inFight;

    /// <summary>Whether the bot's low-health window is open (tests).</summary>
    internal bool WindowOpen => _windowOpen;

    /// <summary>Whether the bot decided to ask in this fight (tests).</summary>
    internal bool AskedForHelp { get; private set; }

    /// <summary>The guids sampled as candidates when the window opened, and which of them were asked (tests and inspection).</summary>
    internal IEnumerable<(ulong Guid, bool Asked)> Candidates
    {
        get
        {
            for (int i = 0; i < _count; i++) yield return (_candidates[i], _asked[i]);
        }
    }

    /// <summary>World time the low-health window opened at; meaningful only while <see cref="WindowOpen"/> (tests).</summary>
    internal uint WindowStartMs => _windowStartMs;

    /// <summary>
    /// Whether candidate <paramref name="guid"/> still qualifies after the window's latest re-check; null when it was not sampled as a candidate
    /// (tests).
    /// </summary>
    internal bool? Holds(ulong guid)
    {
        for (int i = 0; i < _count; i++)
            if (_candidates[i] == guid) return _holds[i];
        return null;
    }

    /// <summary>One look at the bot, every think. <paramref name="nowMs"/> is world time.</summary>
    internal void Observe(Player bot, WorldRuntime world, uint nowMs)
    {
        PlayerbotSocialRecorder? recorder = Recorder;
        if (recorder is not { Enabled: true })
        {
            if (_inFight) Reset();
            return;
        }

        if (!bot.IsAlive)
        {
            if (_inFight) Died(bot, world, recorder, nowMs);
            return;
        }

        if (!bot.Combat.IsInCombat)
        {
            if (_inFight)
            {
                recorder.EndFight(bot.Guid);
                Reset();
            }

            return;
        }

        if (!_inFight)
        {
            _inFight = true;
            _fightStartMs = nowMs;
            _sampledMs = nowMs;
        }

        if (unchecked(nowMs - _sampledMs) >= SampleMs)
        {
            _sampledMs = nowMs;
            // Static lambdas over a state tuple: nothing is captured, so a think that does not sample allocates nothing.
            recorder.Guarded((Fight: this, bot, world, recorder), static state =>
            {
                state.Fight.SampleHelpers(state.bot, state.recorder);
                if (state.Fight._windowOpen) state.Fight.Reverify(state.bot, state.world);
            });
        }

        if (!_windowOpen && bot.MaxHealth > 0 && bot.Health * 100f < PlayerbotCallForHelp.LowHealthPercent * bot.MaxHealth)
            recorder.Guarded((Fight: this, bot, world, recorder, nowMs), static state => state.Fight.OpenWindow(state.bot, state.world, state.recorder, state.nowMs));
    }

    private void Died(Player bot, WorldRuntime world, PlayerbotSocialRecorder recorder, uint nowMs)
    {
        try
        {
            if (_windowOpen && bot.Duel is null
                && PlayerbotCallForHelp.WindowLongEnough(options, unchecked(nowMs - _windowStartMs)))
            {
                recorder.Guarded(() =>
                {
                    SampleHelpers(bot, recorder);
                    Reverify(bot, world);
                    for (int i = 0; i < _count; i++)
                        if (_asked[i] && _holds[i]) recorder.Note(bot.Guid, new ObjectGuid(_candidates[i]), PlayerbotSocialEvent.WatchedMeDie);
                });
            }
        }
        finally
        {
            recorder.EndFight(bot.Guid);
            Reset();
        }
    }

    private void Reset()
    {
        _inFight = false;
        _windowOpen = false;
        AskedForHelp = false;
        _count = 0;
        Array.Clear(_holds);
        Array.Clear(_asked);
        _helpers.Clear();
    }

    private void SampleHelpers(Player bot, PlayerbotSocialRecorder recorder)
    {
        ReadThreats(bot, bot.Combat.Attackers, recorder);
        ReadThreats(bot, bot.Combat.ThreatenedBy, recorder);
    }

    // The sets are HashSet<Unit> behind IReadOnlySet: matched to the concrete type, whose struct enumerator allocates nothing (an interface foreach boxes one every sample).
    private void ReadThreats(Player bot, IReadOnlySet<Unit> units, PlayerbotSocialRecorder recorder)
    {
        if (units.Count == 0) return;
        if (units is HashSet<Unit> set)
        {
            foreach (Unit unit in set) ReadThreat(bot, unit, recorder);
            return;
        }

        foreach (Unit unit in units) ReadThreat(bot, unit, recorder);
    }

    private void ReadThreat(Player bot, Unit unit, PlayerbotSocialRecorder recorder)
    {
        if (unit is not Creature creature || creature.CharmerOrOwnerGuid.IsPlayer || !creature.Combat.HasThreatList) return;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++)
        {
            ThreatEntry entry = entries[index];
            if (entry.Threat <= 0f || DuelRules.ControllingPlayer(entry.Target) is not { } helper || ReferenceEquals(helper, bot)) continue;
            if (_helpers.Add(helper.Guid.Value)) recorder.NoteHelp(helper, bot);
        }
    }

    private PlayerbotBystanderFacts FactsOf(Player bot, Player other)
        => new(other.IsAlive, other.Team == bot.Team && ReferenceEquals(other.Map, bot.Map), other.Combat.IsInCombat,
            other.MaxHealth == 0 ? 0f : other.Health * 100f / other.MaxHealth, other.Level, Distance(bot, other), other.Duel is not null);

    private void OpenWindow(Player bot, WorldRuntime world, PlayerbotSocialRecorder recorder, uint nowMs)
    {
        _windowOpen = true;
        _windowStartMs = nowMs;
        _count = 0;
        foreach (ObjectGuid guid in bot.VisibleObjects)
        {
            if (_count == _candidates.Length) break;
            if (!guid.IsPlayer || guid == bot.Guid || world.FindOnlinePlayer(guid) is not { } other) continue;
            if (!PlayerbotCallForHelp.Qualifies(options, bot.Level, FactsOf(bot, other))) continue;
            _candidates[_count] = guid.Value;
            _holds[_count] = true;
            _count++;
        }

        if (_count == 0 || !options.CallForHelpEnabled || CallForHelp is null || !LosingFight(bot)) return;

        // A unit the bot dislikes, or sees as a rival, is never begged.
        int askable = 0;
        int maxAffinity = int.MinValue;
        Span<bool> mayAsk = stackalloc bool[PlayerbotCallForHelp.MaxCandidates];
        for (int i = 0; i < _count; i++)
        {
            bool known = recorder.Memory.TryGet(_botId, _candidates[i], out PlayerbotRelation relation);
            int affinity = known ? recorder.Memory.EffectiveAffinity(relation) : 0;
            mayAsk[i] = PlayerbotCallForHelp.Askable(known, affinity, known && recorder.Memory.IsRival(relation));
            if (!mayAsk[i]) continue;
            askable++;
            maxAffinity = Math.Max(maxAffinity, affinity);
        }

        if (askable == 0) return;
        double chance = PlayerbotCallForHelp.AskProbability(options, PlayerbotTraits.For(bot.Guid.Value).Sociability, maxAffinity);
        if (!PlayerbotCallForHelp.Asks(bot.Guid.Value, _fightStartMs, chance)) return; // goes it alone: says nothing, blames nobody

        Player? nearest = null;
        float nearestYards = float.MaxValue;
        for (int i = 0; i < _count; i++)
        {
            if (!mayAsk[i] || world.FindOnlinePlayer(new ObjectGuid(_candidates[i])) is not { } other) continue;
            float yards = Distance(bot, other);
            if (yards < nearestYards)
            {
                nearest = other;
                nearestYards = yards;
            }
        }

        if (nearest is null || CallForHelp(bot, nearest) != true) return;
        AskedForHelp = true;
        for (int i = 0; i < _count; i++) _asked[i] = mayAsk[i];
    }

    private bool LosingFight(Player bot)
    {
        if (bot.MaxHealth == 0) return false;
        float strongest = Math.Max(Strongest(bot.Combat.Attackers), Strongest(bot.Combat.ThreatenedBy));
        return PlayerbotCallForHelp.LooksLost((float)bot.Health / bot.MaxHealth, strongest);

        static float Strongest(IReadOnlySet<Unit> units)
        {
            float strongest = 0f;
            if (units.Count == 0) return strongest;
            if (units is HashSet<Unit> set)
            {
                foreach (Unit unit in set) strongest = Math.Max(strongest, Fraction(unit));
                return strongest;
            }

            foreach (Unit unit in units) strongest = Math.Max(strongest, Fraction(unit));
            return strongest;
        }

        static float Fraction(Unit unit) => unit is Creature { MaxHealth: > 0 } creature && !creature.CharmerOrOwnerGuid.IsPlayer
            ? (float)creature.Health / creature.MaxHealth : 0f;
    }

    // Every second of the window: a candidate that stopped qualifying (left, got hurt, joined a fight) or helped is out for good.
    private void Reverify(Player bot, WorldRuntime world)
    {
        for (int i = 0; i < _count; i++)
        {
            if (!_holds[i]) continue;
            if (_helpers.Contains(_candidates[i])
                || world.FindOnlinePlayer(new ObjectGuid(_candidates[i])) is not { } other
                || !PlayerbotCallForHelp.Qualifies(options, bot.Level, FactsOf(bot, other)))
                _holds[i] = false;
        }
    }
}
