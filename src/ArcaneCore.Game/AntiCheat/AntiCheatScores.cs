namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// The live score of every character with findings (the fork's AntiCheatMgr ScoreState map): each finding adds its weight,
/// the score decays linearly by <see cref="AntiCheatOptions.DecayPerSecond"/>, and a fully decayed entry is pruned. Decay is
/// settled lazily on every read and write, so nothing ticks per player. Times are the world clock in milliseconds.
/// <para>World thread only (every caller is a packet handler, a command or the world tick).</para>
/// </summary>
public sealed class AntiCheatScores
{
    /// <summary>The most one finding may add (the fork clamps a detector's weight to 0..100 so one bug cannot spike a score).</summary>
    public const float MaxWeight = 100f;

    private readonly Dictionary<int, Entry> _entries = [];

    /// <summary>Characters with a live entry.</summary>
    public int Count => _entries.Count;

    /// <summary>Add a finding's weight (clamped to 0..<see cref="MaxWeight"/>) and return the new, decayed score.</summary>
    public float Add(int characterId, float weight, uint nowMs, float decayPerSecond)
    {
        Entry entry = GetOrCreate(characterId);
        Settle(entry, nowMs, decayPerSecond);
        entry.Score += float.IsFinite(weight) ? Math.Clamp(weight, 0f, MaxWeight) : 0f;
        entry.Findings++;
        return entry.Score;
    }

    /// <summary>The decayed score now (0 when the character has no entry).</summary>
    public float Score(int characterId, uint nowMs, float decayPerSecond)
    {
        if (!_entries.TryGetValue(characterId, out Entry? entry))
        {
            return 0f;
        }

        Settle(entry, nowMs, decayPerSecond);
        return entry.Score;
    }

    /// <summary>Findings counted for the character since its entry was created.</summary>
    public int Findings(int characterId) => _entries.TryGetValue(characterId, out Entry? entry) ? entry.Findings : 0;

    /// <summary>Overwrite the score (<c>.anticheat score</c>); negative becomes 0.</summary>
    public void Set(int characterId, float score, uint nowMs, float decayPerSecond)
    {
        Entry entry = GetOrCreate(characterId);
        Settle(entry, nowMs, decayPerSecond);
        entry.Score = float.IsFinite(score) ? Math.Max(0f, score) : 0f;
    }

    /// <summary>Forget the character (logout, <c>.anticheat delete</c>).</summary>
    public bool Remove(int characterId) => _entries.Remove(characterId);

    /// <summary>The highest live scores, highest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<(int CharacterId, float Score)> Top(int limit, uint nowMs, float decayPerSecond)
    {
        foreach (Entry entry in _entries.Values)
        {
            Settle(entry, nowMs, decayPerSecond);
        }

        return [.. _entries
            .Where(e => e.Value.Score > 0f)
            .OrderByDescending(e => e.Value.Score)
            .ThenBy(e => e.Key)
            .Take(Math.Max(0, limit))
            .Select(e => (e.Key, e.Value.Score))];
    }

    /// <summary>Drop every fully decayed entry (the world tick), so the map never grows with players who stopped offending.</summary>
    public int Prune(uint nowMs, float decayPerSecond)
    {
        var dead = new List<int>();
        foreach ((int id, Entry entry) in _entries)
        {
            Settle(entry, nowMs, decayPerSecond);
            if (entry.Score <= 0f)
            {
                dead.Add(id);
            }
        }

        foreach (int id in dead)
        {
            _entries.Remove(id);
        }

        return dead.Count;
    }

    private Entry GetOrCreate(int characterId)
    {
        if (!_entries.TryGetValue(characterId, out Entry? entry))
        {
            entry = new Entry();
            _entries[characterId] = entry;
        }

        return entry;
    }

    private static void Settle(Entry entry, uint nowMs, float decayPerSecond)
    {
        if (entry.Settled && decayPerSecond > 0f)
        {
            int elapsed = unchecked((int)(nowMs - entry.LastMs));
            if (elapsed > 0)
            {
                entry.Score = Math.Max(0f, entry.Score - (elapsed / 1000f * decayPerSecond));
            }
        }

        entry.LastMs = nowMs;
        entry.Settled = true;
    }

    private sealed class Entry
    {
        public float Score;
        public int Findings;
        public uint LastMs;
        public bool Settled;
    }
}

/// <summary>The escalation decision (the fork's AntiCheatMgr::Apply): the highest action the score warrants, capped by the ceiling.</summary>
public static class AntiCheatEscalation
{
    public static AntiCheatAction Decide(float score, AntiCheatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AntiCheatAction warranted = score >= options.ScoreKick ? AntiCheatAction.Kick
            : score >= options.ScoreRubberband ? AntiCheatAction.Rubberband
            : score >= options.ScoreGmAlert ? AntiCheatAction.GmAlert
            : AntiCheatAction.Log;
        return (AntiCheatAction)Math.Min((int)warranted, (int)options.Action);
    }
}

/// <summary>
/// The account side of the autoban (the fork's AccountState): every anticheat kick adds points to the account, the points
/// decay per hour of wall-clock time, and crossing the threshold resets them and asks for a ban. The ban step itself comes
/// from the ban history (<see cref="AntiCheatAutobanOptions.DurationFor"/>), so only the points live here. World thread only.
/// </summary>
public sealed class AutobanLedger
{
    private readonly Dictionary<int, (float Points, long LastUnix)> _accounts = [];

    /// <summary>Accounts with points.</summary>
    public int Count => _accounts.Count;

    /// <summary>The decayed points of the account now.</summary>
    public float Points(int accountId, long nowUnix, AntiCheatAutobanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _accounts.TryGetValue(accountId, out (float Points, long LastUnix) state) ? Decayed(state, nowUnix, options.DecayPerHour) : 0f;
    }

    /// <summary>Count one kick. True when the account crossed the threshold (its points are reset; the caller bans it).</summary>
    public bool AddKick(int accountId, long nowUnix, AntiCheatAutobanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        float points = (_accounts.TryGetValue(accountId, out (float Points, long LastUnix) state) ? Decayed(state, nowUnix, options.DecayPerHour) : 0f)
            + options.KickPoints;
        if (points >= options.Threshold)
        {
            _accounts.Remove(accountId);
            return true;
        }

        _accounts[accountId] = (points, nowUnix);
        return false;
    }

    private static float Decayed((float Points, long LastUnix) state, long nowUnix, float decayPerHour)
    {
        long elapsed = nowUnix - state.LastUnix;
        return elapsed <= 0 || decayPerHour <= 0f ? state.Points : Math.Max(0f, state.Points - (elapsed / 3600f * decayPerHour));
    }
}
