namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// The <c>Battleground</c> configuration section. Every default is the vmangos value (the retail reference); a deliberate
/// deviation would be a non-default value of one of these keys. Only keys whose rule is implemented are listed
/// (docs/areas/battlegrounds.md names the vmangos keys that are not delivered).
/// </summary>
public sealed class BattlegroundOptions
{
    public const string SectionName = "Battleground";

    /// <summary>Cast Deserter on a player who leaves a running or starting match (vmangos Battleground.CastDeserter, World.cpp:781, mangosd.conf.dist.in:2929, default on).</summary>
    public bool CastDeserter { get; set; } = true;

    /// <summary>
    /// How long a side may stay below the minimum before the match ends in favour of the other side, in ms; 0 turns it off
    /// (vmangos BattleGround.PrematureFinishTimer, World.cpp:788, default 5 minutes).
    /// </summary>
    public uint PrematureFinishTimerMs { get; set; } = 5 * 60 * 1000;

    /// <summary>
    /// 0 invites from the queue in order, 1 balances the two sides (vmangos Battleground.InvitationType; World.cpp:787 reads 0 when the key is
    /// missing but the shipped mangosd.conf.dist.in:2932 sets 1, which is the retail behaviour and the default here).
    /// </summary>
    public uint InvitationType { get; set; } = 1;

    /// <summary>Wait for a premade-versus-premade match this long before premades fall back to the normal queue, ms; 0 turns it off (vmangos BattleGround.PremadeGroupWaitForMatch, default 0).</summary>
    public uint PremadeGroupWaitForMatchMs { get; set; }

    /// <summary>Smallest group that counts as a premade (vmangos BattleGround.PremadeQueue.MinGroupSize, default 6).</summary>
    public uint PremadeQueueMinGroupSize { get; set; } = 6;

    /// <summary>
    /// Queues a player may be in at once; 0 means the patch default, which is 3 from client patch 1.9 on and so 3 for 1.12.1
    /// (vmangos BattleGround.QueuesCount, World.cpp:794-803).
    /// </summary>
    public uint QueuesCount { get; set; }

    /// <summary>Whether a player inside a battleground may be tagged into a group queue (vmangos BattleGround.TagInBattleGrounds, default on).</summary>
    public bool TagInBattlegrounds { get; set; } = true;

    /// <summary>Groups larger than this are queued as individuals (vmangos BattleGround.GroupQueueLimit, default 40).</summary>
    public uint GroupQueueLimit { get; set; } = 40;

    /// <summary>The number of queues a player may use (the retail 1.12 value 3 when <see cref="QueuesCount"/> is 0).</summary>
    public uint EffectiveQueuesCount => QueuesCount == 0 ? MaxQueuesPerPlayer : Math.Min(QueuesCount, MaxQueuesPerPlayer);

    /// <summary>The queue slots a player has (vmangos <c>PLAYER_MAX_BATTLEGROUND_QUEUES</c>, 3); setConfigMinMax caps the key at 3 (World.cpp:794).</summary>
    public const uint MaxQueuesPerPlayer = 3;

    /// <summary>Apply vmangos' clamping; returns the names of the values that were reset.</summary>
    public IReadOnlyList<string> Normalize()
    {
        List<string> changed = [];
        if (QueuesCount > MaxQueuesPerPlayer)
        {
            QueuesCount = MaxQueuesPerPlayer;
            changed.Add(nameof(QueuesCount));
        }

        if (InvitationType > 1)
        {
            InvitationType = 1;
            changed.Add(nameof(InvitationType));
        }

        return changed;
    }
}
