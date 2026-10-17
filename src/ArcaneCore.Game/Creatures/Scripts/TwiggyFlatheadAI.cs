using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Twiggy Flathead (entry 6248, the Barrens), "The Affray" (quest 1719): mangos-classic ScriptDev2 <c>npc_twiggy_flathead</c> and
/// <c>at_twiggy_flathead</c> (kalimdor/the_barrens.cpp:273-505, registered :1121-1129). A living player with the quest incomplete who enters the
/// Affray area trigger (522) within 30 yd of Twiggy starts the event, unless one runs: Twiggy calls it, a second later six Affray Challengers
/// (6240, friendly, roaring) appear in the ring, and from five seconds on one at a time turns hostile (faction 14), roars and attacks the
/// player, every 25 s or 15 s after the last one died, each with Twiggy's "fray" call. Five seconds after the sixth, Big Will (6238) runs in
/// friendly and turns hostile (32) where he stops, with his line at the player; his death ends it with Twiggy's last line. The player dead or
/// gone fails the event: Big Will and the living challengers despawn.
/// </summary>
public sealed class TwiggyFlatheadAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 6248, NpcBigWill = 6238, NpcAffrayChallenger = 6240, QuestAffray = 1719, AreaTriggerAffray = 522;
    public const uint FactionFriendly = 35, FactionHostileWill = 32, FactionHostileChallenger = 14, EmoteOneshotRoar = 15;
    public const int SayBigWillReady = -1000123, SayBegin = -1000124, SayFray = -1000125, SayDown = -1000126, SayOver = -1000127;
    public const int MaxChallengers = 6;
    public const uint ChallengerLifetimeMs = 600_000, BigWillLifetimeMs = 300_000;
    public const float TriggerSearchRange = 30f;

    /// <summary>aAffrayChallengerLoc: the six challenger spots, Big Will's spawn (6) and where he runs to (7).</summary>
    public static readonly (float X, float Y, float Z, float O)[] Spots =
    [
        (-1683.0f, -4326.0f, 2.79f, 0.00f), (-1682.0f, -4329.0f, 2.79f, 0.00f), (-1683.0f, -4330.0f, 2.79f, 0.00f),
        (-1680.0f, -4334.0f, 2.79f, 1.49f), (-1674.0f, -4326.0f, 2.79f, 3.49f), (-1677.0f, -4334.0f, 2.79f, 1.66f),
        (-1713.79f, -4342.09f, 6.05f, 6.15f), (-1682.31f, -4329.68f, 2.78f, 0.0f),
    ];

    private bool _inProgress, _bigWillReady;
    private uint _eventMs, _step;
    private int _challengerCount;
    private ObjectGuid _player, _bigWill;
    private readonly List<ObjectGuid> _challengers = [];

    public bool EventInProgress => _inProgress;

    public int ChallengerCount => _challengerCount;

    public IReadOnlyList<ObjectGuid> Challengers => _challengers;

    public ObjectGuid BigWill => _bigWill;

    /// <summary>Reset (:322-334).</summary>
    protected override void Reset()
    {
        _inProgress = false;
        _bigWillReady = false;
        _eventMs = 1000;
        _challengerCount = 0;
        _step = 0;
        _player = default;
        _bigWill = default;
        _challengers.Clear();
    }

    /// <summary>
    /// AreaTrigger_at_twiggy_flathead (:490-505): a living, non-GM player with the quest incomplete, Twiggy within 30 yd. Returns true when the
    /// event started (the source then returns false so the core goes on with the trigger).
    /// </summary>
    public static bool OnAreaTrigger(CreatureMapSystem creatures, Player player, Func<Player, uint, bool> questIncomplete)
    {
        ArgumentNullException.ThrowIfNull(creatures);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(questIncomplete);
        if (!player.IsAlive || player.IsGameMaster || !questIncomplete(player, QuestAffray))
        {
            return false;
        }

        return creatures.CreaturesOfEntryInRange(player, Entry, TriggerSearchRange).FirstOrDefault(c => c.IsAlive) is { AI: TwiggyFlatheadAI twiggy }
            && twiggy.CanStartEvent(player);
    }

    /// <summary>CanStartEvent (:350-365).</summary>
    public bool CanStartEvent(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_inProgress)
        {
            return false;
        }

        System?.SayText(Me, SayBegin, player);
        _player = player.Guid;
        _inProgress = true;
        return true;
    }

    /// <summary>FailEvent (:336-348).</summary>
    private void FailEvent()
    {
        if (System is { } system)
        {
            if (system.FindCreature(_bigWill) is { IsAlive: true } bigWill)
            {
                system.ForcedDespawn(bigWill, 0);
            }

            foreach (ObjectGuid guid in _challengers)
            {
                if (system.FindCreature(guid) is { IsAlive: true } challenger)
                {
                    system.ForcedDespawn(challenger, 0);
                }
            }
        }

        Reset();
    }

    /// <summary>JustSummoned (:378-393).</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        summoned.FactionTemplate = FactionFriendly;
        if (summoned.Entry == NpcBigWill)
        {
            _bigWill = summoned.Guid;
            (float x, float y, float z, _) = Spots[7];
            summoned.Motion.MovePoint(1, x, y, z, run: true);
        }
        else
        {
            System?.PlayEmote(summoned, EmoteOneshotRoar);
            _challengers.Add(summoned.Guid);
        }
    }

    /// <summary>SummonedCreatureJustDied (:409-421).</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Entry == NpcBigWill)
        {
            System?.SayText(Me, SayOver);
            Reset();
        }
        else
        {
            System?.SayText(Me, SayDown);
            _eventMs = 15_000;
        }
    }

    /// <summary>
    /// UpdateAI (:423-482). Deviation: the base victim update runs first (the source never selects a victim), so he fights back when attacked,
    /// as Regthar does in batch 6.
    /// </summary>
    public override void OnUpdate(uint diffMs)
    {
        UpdateVictim();
        if (!_inProgress || System is not { } system)
        {
            return;
        }

        WatchBigWill(system);
        if (_eventMs >= diffMs)
        {
            _eventMs -= diffMs;
            return;
        }

        if (system.Map.FindPlayer(_player) is not { IsAlive: true } player)
        {
            FailEvent();
            return;
        }

        switch (_step)
        {
            case 0:
                for (int i = 0; i < MaxChallengers; i++)
                {
                    (float x, float y, float z, float o) = Spots[i];
                    system.SummonAt(Me, NpcAffrayChallenger, x, y, z, o, null, ChallengerLifetimeMs);
                }

                _eventMs = 5000;
                ++_step;
                break;
            case 1:
                system.SayText(Me, SayFray);
                if (_challengerCount >= _challengers.Count || system.FindCreature(_challengers[_challengerCount]) is not { } challenger)
                {
                    FailEvent(); // "this should never happen"
                    return;
                }

                SetChallengerReady(system, challenger, player);
                if (_challengerCount == MaxChallengers)
                {
                    ++_step;
                    _eventMs = 5000;
                }
                else
                {
                    _eventMs = 25_000;
                }

                break;
            case 2:
                (float wx, float wy, float wz, float wo) = Spots[6];
                system.SummonAt(Me, NpcBigWill, wx, wy, wz, wo, null, BigWillLifetimeMs);
                _eventMs = 15_000;
                ++_step;
                break;
            default:
                _eventMs = 5000;
                break;
        }
    }

    /// <summary>SetChallengerReady (:367-376).</summary>
    private void SetChallengerReady(CreatureMapSystem system, Creature challenger, Player player)
    {
        challenger.FactionTemplate = FactionHostileChallenger;
        system.PlayEmote(challenger, EmoteOneshotRoar);
        ++_challengerCount;
        challenger.AI?.AttackStart(player);
    }

    /// <summary>
    /// SummonedMovementInform (:395-407), read from Big Will each update: at his point (within a yard of it, not fighting) he turns hostile and
    /// says his line facing the player.
    /// </summary>
    private void WatchBigWill(CreatureMapSystem system)
    {
        if (_bigWillReady || _bigWill.IsEmpty || system.FindCreature(_bigWill) is not { IsAlive: true } bigWill)
        {
            return;
        }

        (float x, float y, _, _) = Spots[7];
        if (bigWill.Combat.Victim is not null || MathF.Abs(bigWill.X - x) >= 1f || MathF.Abs(bigWill.Y - y) >= 1f)
        {
            return;
        }

        _bigWillReady = true;
        bigWill.FactionTemplate = FactionHostileWill;
        if (system.Map.FindPlayer(_player) is { } player)
        {
            system.SayText(bigWill, SayBigWillReady, player);
            system.SetFacingTo(bigWill, MathF.Atan2(player.Y - bigWill.Y, player.X - bigWill.X));
        }
    }
}
