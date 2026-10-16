namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>Where a think's goal choice came from (telemetry <c>src</c>).</summary>
internal enum PlayerbotPolicySource : byte
{
    /// <summary>The scores agreed with the ladder's choice: the ladder decides.</summary>
    Agreed,
    /// <summary>The scores chose another open goal, which the brain tried first.</summary>
    Applied,
    /// <summary>An exploration snapshot: a random runner-up was tried first (<see cref="PlayerbotPolicyOptions.ExplorationRate"/>).</summary>
    Explored,
    /// <summary>No scores yet for the current snapshot (not scored, dropped, or over the tick budget): the ladder decides.</summary>
    Missing,
    /// <summary>The current snapshot is older than <see cref="PlayerbotPolicyOptions.StaleAfterMs"/>: the ladder decides.</summary>
    Stale,
    /// <summary>A score was NaN or infinite: the ladder decides.</summary>
    NonFinite,
    /// <summary>The scores chose <see cref="PlayerbotPolicyGoal.Grind"/>, the ladder's floor, which cannot be moved ahead: the ladder decides.</summary>
    Floor,
    /// <summary>
    /// The promoted goal's controller did not act (its rules or a jumped rung's guard said no): the ladder decides, for the rest of the
    /// snapshot too, so a promotion is refused once, not on every think.
    /// </summary>
    Refused,
}

/// <summary>One snapshot of a bot's state taken for scoring (world thread).</summary>
internal sealed class PlayerbotPolicyWindow(long sequence, uint takenAtMs, float[] features, PlayerbotPolicyGoalMask open, PlayerbotPolicyGoal? explore)
{
    internal long Sequence { get; } = sequence;
    internal uint TakenAtMs { get; } = takenAtMs;
    internal float[] Features { get; } = features;
    internal PlayerbotPolicyGoalMask Open { get; } = open;
    internal PlayerbotPolicyGoal? LadderPick => Open.LadderPick;
    internal PlayerbotPolicyGoal? Explore { get; } = explore;

    /// <summary>The first think that used this snapshot has been told to the telemetry.</summary>
    internal bool Recorded { get; set; }

    /// <summary>Its promotion was refused (<see cref="PlayerbotPolicySource.Refused"/>): no think on it promotes again.</summary>
    internal bool Refused { get; set; }
}

/// <summary>A think's goal decision: the goal to try before the ladder (null: the ladder decides) and why.</summary>
internal readonly record struct PlayerbotPolicyVerdict(PlayerbotPolicyGoal? Promote, PlayerbotPolicySource Source, PlayerbotPolicyGoal? Pick,
    PlayerbotPolicyWindow? Window);

/// <summary>
/// One bot's side of the <see cref="PlayerbotPolicyEngine"/>: its current snapshot (world thread) and the latest scores the worker published
/// for it (any thread). <see cref="Decide"/> turns them into a goal to try first, or none.
/// </summary>
internal sealed class PlayerbotPolicyAgent
{
    private readonly PlayerbotPolicyEngine _engine;
    private readonly Random _random;
    private PlayerbotPolicyScores? _latest;
    private long _sequence;
    private bool _requested;
    private uint _nextRequestMs;

    internal PlayerbotPolicyAgent(PlayerbotPolicyEngine engine, ulong botGuid)
    {
        _engine = engine;
        BotGuid = botGuid;
        _random = new Random(unchecked((int)(botGuid ^ (botGuid >> 32)) ^ 0x5eed));
    }

    internal ulong BotGuid { get; }

    /// <summary>The bot's current snapshot (null before the first).</summary>
    internal PlayerbotPolicyWindow? Window { get; private set; }

    /// <summary>The latest think's decision on <see cref="Window"/> (telemetry reads it after the think).</summary>
    internal PlayerbotPolicyVerdict? LastVerdict { get; private set; }

    /// <summary>How many thinks decided (<see cref="Decide"/>; telemetry tells a new think from the last one by it).</summary>
    internal long DecideCount { get; private set; }

    /// <summary>A new snapshot is due (world thread).</summary>
    internal bool RequestDue(uint nowMs) => !_requested || unchecked((int)(nowMs - _nextRequestMs)) >= 0;

    /// <summary>
    /// Take a new snapshot (world thread): it replaces the current one and goes to its map's next batch. With exploration on, the snapshot
    /// may instead name a random open runner-up of the ladder's choice, which its thinks then try first whatever the scores say.
    /// </summary>
    internal PlayerbotPolicyWindow Request(uint mapId, ReadOnlySpan<float> features, PlayerbotPolicyGoalMask open, uint nowMs)
    {
        _requested = true;
        _nextRequestMs = unchecked(nowMs + (uint)_engine.Options.IntervalMs);
        long sequence = ++_sequence;
        PlayerbotPolicyGoal? explore = null;
        double rate = _engine.Options.ExplorationRate;
        // Grind is no runner-up: it is the ladder's floor and cannot be tried first.
        if (rate > 0 && open.LadderPick is { } ladder && open.Without(ladder).Without(PlayerbotPolicyGoal.Grind) is { Count: > 0 } runnersUp
            && _random.NextDouble() < rate)
        {
            int nth = _random.Next(runnersUp.Count);
            for (int goal = 0; goal < PlayerbotPolicySchema.GoalCount; goal++)
                if (runnersUp.Contains((PlayerbotPolicyGoal)goal) && nth-- == 0) { explore = (PlayerbotPolicyGoal)goal; break; }
        }
        var window = new PlayerbotPolicyWindow(sequence, nowMs, features.ToArray(), open, explore);
        Window = window;
        // An exploration snapshot needs no scores. Any other goes to the batch; refused (a full batch), its scores are just missing.
        if (explore is null) _engine.Submit(this, mapId, features, sequence);
        return window;
    }

    /// <summary>The worker's scores for a snapshot (any thread). An older snapshot's late scores never replace a newer one's.</summary>
    internal void Publish(PlayerbotPolicyScores scores)
    {
        PlayerbotPolicyScores? current = Volatile.Read(ref _latest);
        while (current is null || current.Sequence < scores.Sequence)
        {
            PlayerbotPolicyScores? seen = Interlocked.CompareExchange(ref _latest, scores, current);
            if (ReferenceEquals(seen, current)) return;
            current = seen;
        }
    }

    /// <summary>
    /// The goal this think should try before the ladder (world thread), from the current snapshot: its exploration goal, else the best open
    /// goal by its scores when that is not the ladder's own choice. Late, missing and non-finite scores give null (the ladder decides).
    /// </summary>
    internal PlayerbotPolicyVerdict Decide(uint nowMs)
    {
        PlayerbotPolicyVerdict verdict = DecideCore(nowMs);
        LastVerdict = verdict;
        DecideCount++;
        switch (verdict.Source)
        {
            case PlayerbotPolicySource.Applied: _engine.CountApplied(); break;
            case PlayerbotPolicySource.Agreed: _engine.CountAgreed(); break;
            case PlayerbotPolicySource.Explored: _engine.CountExplored(); break;
            case PlayerbotPolicySource.Missing: _engine.CountMissing(); break;
            case PlayerbotPolicySource.Stale: _engine.CountStale(); break;
            case PlayerbotPolicySource.NonFinite: _engine.CountNonFinite(); break;
            case PlayerbotPolicySource.Floor: _engine.CountFloor(); break;
            case PlayerbotPolicySource.Refused: _engine.CountRefused(); break;
        }
        return verdict;
    }

    /// <summary>
    /// This think's promotion was refused (world thread): the think's verdict becomes <see cref="PlayerbotPolicySource.Refused"/> and the
    /// snapshot promotes nothing more.
    /// </summary>
    internal void Refuse()
    {
        if (LastVerdict is not { Promote: not null, Window: { } window } verdict) return;
        window.Refused = true;
        LastVerdict = verdict with { Promote = null, Source = PlayerbotPolicySource.Refused };
        _engine.CountRefused();
    }

    private PlayerbotPolicyVerdict DecideCore(uint nowMs)
    {
        if (Window is not { } window) return new(null, PlayerbotPolicySource.Missing, null, Window);
        uint age = unchecked(nowMs - window.TakenAtMs);
        if (age > int.MaxValue || age > (uint)_engine.Options.StaleAfterMs) return new(null, PlayerbotPolicySource.Stale, null, window);
        if (window.Refused) return new(null, PlayerbotPolicySource.Refused, null, window);
        if (window.Explore is { } explore) return new(explore, PlayerbotPolicySource.Explored, explore, window);
        if (Volatile.Read(ref _latest) is not { } scores || scores.Sequence != window.Sequence) return new(null, PlayerbotPolicySource.Missing, null, Window);
        PlayerbotPolicyGoal? best = null;
        float bestScore = float.NegativeInfinity;
        for (int goal = 0; goal < PlayerbotPolicySchema.GoalCount; goal++)
        {
            float score = scores.Values[goal];
            if (!float.IsFinite(score)) return new(null, PlayerbotPolicySource.NonFinite, null, window);
            // Strictly greater: a tie keeps the goal earlier in the ladder.
            if (window.Open.Contains((PlayerbotPolicyGoal)goal) && (best is null || score > bestScore))
            {
                best = (PlayerbotPolicyGoal)goal;
                bestScore = score;
            }
        }
        if (best is null || best == window.LadderPick) return new(null, PlayerbotPolicySource.Agreed, best, window);
        return best == PlayerbotPolicyGoal.Grind
            ? new(null, PlayerbotPolicySource.Floor, best, window)
            : new(best, PlayerbotPolicySource.Applied, best, window);
    }
}
