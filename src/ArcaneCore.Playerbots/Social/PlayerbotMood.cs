using ArcaneCore.Game;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>How a mood reads in words and templates.</summary>
internal enum PlayerbotTone : byte
{
    Neutral,
    Cheerful,
    Grumpy,
}

/// <summary>What happened to a bot that changes its mood and is not a social-memory event (those map through <see cref="PlayerbotMood.DeltaOf(PlayerbotSocialEvent)"/>).</summary>
internal enum PlayerbotMoodEvent : byte
{
    LevelUp,
    LootUpgrade,
    Death,
}

/// <summary>
/// The short-term mood of each bot (docs/areas/playbots-social.md, Mood): -1..+1, in memory, decaying toward 0 with a half-life. Thread-safe (the recorder
/// is fed from the world thread and from chat workers). Memory: one small entry per bot that has had a mood, pruned when it has faded.
/// </summary>
internal sealed class PlayerbotMood
{
    /// <summary>A mood at or beyond this either way colours the tone.</summary>
    internal const float ToneThreshold = 0.3f;

    private const int PruneAbove = 2048;

    private readonly Dictionary<Guid, (float Value, long AtMs)> _moods = [];
    private readonly object _gate = new();
    private Func<long> _now = static () => Environment.TickCount64;

    /// <summary>Whether moods exist (<see cref="PlayerbotSocialMoodOptions.Enabled"/>); the social pass sets it.</summary>
    internal bool Enabled { get; set; } = true;

    /// <summary>The half-life in minutes; the social pass sets it.</summary>
    internal int HalfLifeMinutes { get; set; } = 30;

    /// <summary>The clock (tests).</summary>
    internal Func<long> Clock { set => _now = value; }

    /// <summary>Take the options of the current pass.</summary>
    internal void Configure(PlayerbotSocialMoodOptions options)
    {
        Enabled = options.Enabled;
        HalfLifeMinutes = Math.Max(1, options.HalfLifeMinutes);
    }

    /// <summary>The mood change of a social-memory event, 0 for one that does not move the mood.</summary>
    internal static float DeltaOf(PlayerbotSocialEvent e) => e switch
    {
        PlayerbotSocialEvent.HelpedBy => 0.2f,
        PlayerbotSocialEvent.GroupSucceeded => 0.06f,
        PlayerbotSocialEvent.GroupWiped => -0.1f,
        PlayerbotSocialEvent.Kicked => -0.4f,
        PlayerbotSocialEvent.RudeEmote => -0.15f,
        PlayerbotSocialEvent.ChatStrike => -0.3f,
        _ => 0f,
    };

    /// <summary>The mood change of a bot-side event.</summary>
    internal static float DeltaOf(PlayerbotMoodEvent e) => e switch
    {
        PlayerbotMoodEvent.LevelUp => 0.3f,
        PlayerbotMoodEvent.LootUpgrade => 0.15f,
        PlayerbotMoodEvent.Death => -0.25f,
        _ => 0f,
    };

    /// <summary>The factor on the chance of the bot's own initiative: 0.5 at -1, 1 at 0, 1.5 at +1.</summary>
    internal static float ChanceMultiplier(float mood) => 1f + 0.5f * Math.Clamp(mood, -1f, 1f);

    internal static PlayerbotTone ToneOf(float mood) => mood >= ToneThreshold ? PlayerbotTone.Cheerful : mood <= -ToneThreshold ? PlayerbotTone.Grumpy : PlayerbotTone.Neutral;

    /// <summary>Move the bot's mood by <paramref name="delta"/> (from its decayed value now), clamped to -1..+1.</summary>
    internal void Nudge(Guid botId, float delta)
    {
        if (!Enabled || delta == 0f) return;
        lock (_gate)
        {
            long now = _now();
            float value = Math.Clamp(DecayedLocked(botId, now) + delta, -1f, 1f);
            if (_moods.Count > PruneAbove) PruneLocked(now);
            _moods[botId] = (value, now);
        }
    }

    internal void Nudge(Guid botId, PlayerbotMoodEvent e) => Nudge(botId, DeltaOf(e));

    /// <summary>The bot's mood now (0 when it has none or moods are off).</summary>
    internal float Get(Guid botId)
    {
        if (!Enabled) return 0f;
        lock (_gate) return DecayedLocked(botId, _now());
    }

    private float DecayedLocked(Guid botId, long now)
    {
        if (!_moods.TryGetValue(botId, out (float Value, long AtMs) entry)) return 0f;
        double minutes = Math.Max(0, now - entry.AtMs) / 60_000.0;
        return (float)(entry.Value * Math.Pow(0.5, minutes / HalfLifeMinutes));
    }

    private void PruneLocked(long now)
    {
        foreach (KeyValuePair<Guid, (float Value, long AtMs)> pair in _moods)
            if (Math.Abs(DecayedLocked(pair.Key, now)) < 0.01f) _moods.Remove(pair.Key);
    }
}

/// <summary>
/// The events of a bot's own life that move its mood and are not social-memory events (a level, a better item, a death), read on the social pass from the
/// bot's state; the first look at a bot only sets the baseline. Cheap: a level and a life flag every pass, the equipped item levels every 10 seconds.
/// </summary>
internal sealed class PlayerbotMoodLane
{
    private const long ItemCheckMs = 10_000;

    private sealed class Seen(byte level, bool alive, uint itemLevels)
    {
        public byte Level { get; set; } = level;

        public bool Alive { get; set; } = alive;

        public uint ItemLevels { get; set; } = itemLevels;

        public long NextItemCheckMs { get; set; }
    }

    private readonly Dictionary<Guid, Seen> _seen = [];
    private long _sweptAtMs;

    internal void Run(PlayerbotSocialPass pass)
    {
        PlayerbotSocialRecorder? recorder = pass.Recorder;
        if (recorder is null) return;
        PlayerbotMood mood = recorder.Mood;
        mood.Configure(pass.Options.Mood);
        if (!mood.Enabled || !recorder.Enabled) return;
        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            Player player = bot.Player;
            if (!_seen.TryGetValue(bot.Id, out Seen? seen))
            {
                _seen[bot.Id] = new Seen(player.Level, player.IsAlive, ItemLevels(player)) { NextItemCheckMs = pass.NowMs + ItemCheckMs };
                continue;
            }

            bool levelled = player.Level > seen.Level;
            if (levelled) mood.Nudge(bot.Id, PlayerbotMoodEvent.LevelUp);
            seen.Level = player.Level;
            if (seen.Alive && !player.IsAlive) mood.Nudge(bot.Id, PlayerbotMoodEvent.Death);
            seen.Alive = player.IsAlive;
            if (pass.NowMs >= seen.NextItemCheckMs)
            {
                seen.NextItemCheckMs = pass.NowMs + ItemCheckMs;
                uint levels = ItemLevels(player);
                if (levels > seen.ItemLevels && !levelled) mood.Nudge(bot.Id, PlayerbotMoodEvent.LootUpgrade);
                seen.ItemLevels = levels;
            }
        }

        if (pass.NowMs - _sweptAtMs >= 5 * 60_000L)
        {
            _sweptAtMs = pass.NowMs;
            HashSet<Guid> running = [.. pass.Bots.Select(b => b.Id)];
            foreach (Guid id in _seen.Keys)
                if (!running.Contains(id)) _seen.Remove(id);
        }
    }

    private static uint ItemLevels(Player player)
    {
        uint total = 0;
        foreach ((_, ArcaneCore.Game.Items.Item item) in player.Inventory.Equipped) total += item.Template.ItemLevel;
        return total;
    }
}
