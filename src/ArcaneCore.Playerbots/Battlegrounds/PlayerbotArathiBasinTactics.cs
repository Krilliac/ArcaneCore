using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>What one bot does in Arathi Basin this think (<see cref="PlayerbotArathiBasinTactics.Plan"/>).</summary>
internal enum PlayerbotAbDuty : byte
{
    /// <summary>Walk to a node its team may click (neutral, or held or contested by the enemy) and cast the banner spell at it.</summary>
    Assault,

    /// <summary>Hold one of its team's nodes: stand by the banner and fight the enemies that come near it.</summary>
    Defend,

    /// <summary>Hurt and outnumbered: fall back to the nearest node its team occupies (its graveyard), or the team's start.</summary>
    Retreat,
}

/// <summary>One node as the plan reads it: its index (0 stables .. 4 gold mine), its banner's position and its status (<see cref="ArathiBasin.NodeStatus"/>).</summary>
internal readonly record struct PlayerbotAbNode(int Node, Vector3 At, byte Status);

/// <summary>A bot of the planning team: its guid, where it stands, whether it is alive, its health (0..1) and whether its role is defender.</summary>
internal readonly record struct PlayerbotAbMember(ObjectGuid Guid, Vector3 At, bool Alive, float Health = 1f, bool Defender = false);

/// <summary>A living player on the field (an enemy, or a teammate the plan does not order: a real player).</summary>
internal readonly record struct PlayerbotAbUnit(ObjectGuid Guid, Vector3 At);

/// <summary>A bot's order: its duty, the node it is about (-1: none), where it goes, and the enemy it should fight there (empty: none).</summary>
internal readonly record struct PlayerbotAbOrder(PlayerbotAbDuty Duty, int Node, Vector3 Goal, ObjectGuid Target);

/// <summary>A battleground buff object in sight: its guid, its entry (<see cref="BattlegroundConstants.BuffEntries"/>) and where it stands.</summary>
internal readonly record struct PlayerbotBgBuff(ObjectGuid Guid, uint Entry, Vector3 At);

/// <summary>Everything the plan reads about one team's side of an Arathi Basin match, as plain values.</summary>
internal sealed class PlayerbotAbPicture
{
    public required Team Team { get; init; }

    /// <summary>The nodes whose banner is spawned (one entry per node).</summary>
    public required IReadOnlyList<PlayerbotAbNode> Nodes { get; init; }

    /// <summary>The team's managed bots (the plan orders these only).</summary>
    public required IReadOnlyList<PlayerbotAbMember> Bots { get; init; }

    /// <summary>The living enemy players on the battleground map.</summary>
    public required IReadOnlyList<PlayerbotAbUnit> Enemies { get; init; }

    /// <summary>The living teammates that are no bots of the plan (real players): they count as friends, never get orders.</summary>
    public IReadOnlyList<PlayerbotAbUnit> Friends { get; init; } = [];

    /// <summary>The team's start (its entrance graveyard), where a retreat goes when the team occupies no node.</summary>
    public required Vector3 Home { get; init; }
}

/// <summary>
/// Who does what in Arathi Basin, every think, from the match as it is (unit tested, free of world state). It follows the shape of
/// mangosbot's <c>BGTactics::selectObjective</c> for <c>BATTLEGROUND_AB</c> (cmangos/playerbots playerbot/strategy/actions/BattleGroundTactics.cpp):
/// attackers go to the nearest base the team does not own, defenders to a base it owns, and <c>BGTactics::atFlag</c> casts at the banner
/// in reach; and of vmangos <c>AB_AtFlag</c> / <c>AtFlag</c> (PlayerBots/BattleBotWaypoints.cpp:163-215: cast 21651 at a banner in
/// interaction range, unless a friend there already casts it). The click rule is the server's (vmangos BattleGroundAB::EventPlayerClickedOnFlag,
/// BattleGroundAB.cpp:338-352: <c>status == 0 || teamIndex == status % 2</c>).
/// <para>
/// On top of that the plan spreads the team: every living bot gets an order every think, so nobody idles while a node is attackable or
/// defensible (every node is always one or the other: neutral and enemy nodes are attackable, own nodes defensible).
/// </para>
/// <list type="number">
/// <item><b>Retreat.</b> A bot under <see cref="RetreatHealth"/> with more enemies than friends within <see cref="RetreatYards"/> falls back
/// to the nearest node its team occupies (vmangos BattleGroundAB::GetClosestGraveYard, BattleGroundAB.cpp:520-569: an occupied node's
/// graveyard is where its team resurrects), else to its start.</item>
/// <item><b>Defenders</b>, limited: <see cref="DefenderCount"/> of the living bots (none below three bots; more for nodes enemies are
/// at), only while the team holds a node, defender-role bots first. Each goes to the held node that needs it most (enemies within
/// <see cref="DefendYards"/> of it first, then the nearest), at most an even share per node, and fights the enemy nearest the node.</item>
/// <item><b>Attackers</b>, the rest: spread over the attackable nodes by distance and team size, each node at most an even share
/// (<c>ceil(attackers / nodes)</c>, plus one per enemy defending it), the cheapest pair first: the distance, less
/// <see cref="ContestedBonusYards"/> for a node the enemy contests (its capture timer runs: an own node assaulted is retaken before it
/// falls) and <see cref="NeutralBonusYards"/> for a neutral one.</item>
/// <item>With no attackable node, every remaining bot defends; with no held node, every remaining bot attacks.</item>
/// </list>
/// Ties go by guid, so every bot of a team computes the same plan.
/// </summary>
internal static class PlayerbotArathiBasinTactics
{
    /// <summary>An enemy this close to a node is at it (attacking or defending it).</summary>
    internal const float DefendYards = 40f;

    /// <summary>A body this close to a node counts as lying at it.</summary>
    internal const float BodyNodeYards = DefendYards;

    /// <summary>Whether a node within <paramref name="yards"/> (3D) of <paramref name="at"/> is contested: an assault under way, about to be capped or lost.</summary>
    internal static bool ContestedNodeNear(IReadOnlyList<PlayerbotAbNode> nodes, Vector3 at, float yards)
    {
        _ = (nodes, at, yards);
        return false; // PHASE-1 STUB
    }

    /// <summary>A bot below this share of its health may retreat.</summary>
    internal const float RetreatHealth = 0.25f;

    /// <summary>The enemies and friends counted for a retreat stand this close.</summary>
    internal const float RetreatYards = 20f;

    /// <summary>A node the enemy contests is this many yards "nearer" to an attacker (its capture timer runs).</summary>
    internal const float ContestedBonusYards = 60f;

    /// <summary>A neutral node is this many yards "nearer" (nobody defends it).</summary>
    internal const float NeutralBonusYards = 25f;

    /// <summary>A buff this close is worth the detour (the AB buffs stand 20-30 yards from their banners).</summary>
    internal const float BuffDetourYards = 35f;

    /// <summary>The restoration buff is taken below this share of health or mana.</summary>
    internal const float RestoreBelow = 0.8f;

    /// <summary>Whether a team of index <paramref name="teamIndex"/> may click a node of status <paramref name="status"/> (BattleGroundAB.cpp:351-352).</summary>
    internal static bool Clickable(byte status, int teamIndex) => status == ArathiBasin.StatusNeutral || status % 2 == teamIndex;

    /// <summary>Whether the node is the team's: contested by it (its capture runs) or occupied by it.</summary>
    internal static bool Held(byte status, int teamIndex) => status != ArathiBasin.StatusNeutral && status % 2 != teamIndex;

    /// <summary>Whether the node is occupied by the team (its graveyard is the team's).</summary>
    internal static bool Occupied(byte status, int teamIndex) => status == ArathiBasin.StatusAllianceOccupied + teamIndex;

    /// <summary>The defenders a team of <paramref name="living"/> bots keeps on its nodes without threats: none below three, then 30%.</summary>
    internal static int DefenderCount(int living) => living < 3 ? 0 : (int)Math.Floor(living * 0.3);

    /// <summary>The orders of the team's living bots (a dead bot gets none: it waits for the spirit guide).</summary>
    internal static Dictionary<ObjectGuid, PlayerbotAbOrder> Plan(PlayerbotAbPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        int teamIndex = BattlegroundConstants.TeamIndex(picture.Team);
        var orders = new Dictionary<ObjectGuid, PlayerbotAbOrder>(picture.Bots.Count);
        List<PlayerbotAbMember> pool = [.. picture.Bots.Where(b => b.Alive).OrderBy(b => b.Guid.Value)];
        int living = pool.Count;
        if (living == 0 || picture.Nodes.Count == 0) return orders;

        List<PlayerbotAbNode> attackable = [.. picture.Nodes.Where(n => Clickable(n.Status, teamIndex)).OrderBy(n => n.Node)];
        List<PlayerbotAbNode> held = [.. picture.Nodes.Where(n => Held(n.Status, teamIndex)).OrderBy(n => n.Node)];
        List<PlayerbotAbNode> occupied = [.. held.Where(n => Occupied(n.Status, teamIndex))];

        // 1. Retreat: hurt and outnumbered, back to the nearest own graveyard (an occupied node) or the start.
        foreach (PlayerbotAbMember bot in pool.ToList())
        {
            if (bot.Health >= RetreatHealth) continue;
            int enemies = picture.Enemies.Count(e => Vector3.Distance(e.At, bot.At) <= RetreatYards);
            int friends = picture.Bots.Count(b => b.Alive && b.Guid != bot.Guid && Vector3.Distance(b.At, bot.At) <= RetreatYards)
                + picture.Friends.Count(f => Vector3.Distance(f.At, bot.At) <= RetreatYards);
            if (enemies == 0 || enemies <= friends) continue;
            PlayerbotAbNode? refuge = occupied.Count == 0 ? null : occupied.MinBy(n => Vector3.Distance(n.At, bot.At));
            orders[bot.Guid] = new PlayerbotAbOrder(PlayerbotAbDuty.Retreat, refuge?.Node ?? -1, refuge?.At ?? picture.Home, default);
            pool.Remove(bot);
        }

        if (pool.Count == 0) return orders;

        // 2. Defenders: limited, only while something is held; threatened nodes first (at least one defender for each while bots are left
        // for the attack), defender-role bots first.
        int Threat(PlayerbotAbNode node) => picture.Enemies.Count(e => Vector3.Distance(e.At, node.At) <= DefendYards);
        if (held.Count > 0)
        {
            int threatened = held.Count(n => Threat(n) > 0);
            int defenders = attackable.Count == 0
                ? pool.Count
                : Math.Min(Math.Max(DefenderCount(living), Math.Min(threatened, pool.Count - 1)), pool.Count - 1);
            if (defenders > 0)
            {
                int perNode = Math.Max(1, (int)Math.Ceiling(defenders / (double)held.Count));
                var load = held.ToDictionary(n => n.Node, _ => 0);
                for (int i = 0; i < defenders && pool.Count > 0; i++)
                {
                    // The cheapest (bot, node): a node nobody defends yet before a covered one, defender-role bots before the others, the
                    // most threatened node, then the nearest.
                    (PlayerbotAbMember Bot, PlayerbotAbNode Node)? best = null;
                    (int Covered, int Role, int Threat, float Distance, ulong Guid) bestKey = default;
                    foreach (PlayerbotAbMember bot in pool)
                    {
                        foreach (PlayerbotAbNode node in held)
                        {
                            if (load[node.Node] >= perNode + Threat(node)) continue;
                            (int, int, int, float, ulong) key = (load[node.Node] > 0 ? 1 : 0, bot.Defender ? 0 : 1, -Threat(node), Vector3.Distance(bot.At, node.At), bot.Guid.Value);
                            if (best is null || Compare(key, bestKey) < 0)
                            {
                                best = (bot, node);
                                bestKey = key;
                            }
                        }
                    }

                    if (best is not { } pick) break;
                    load[pick.Node.Node]++;
                    orders[pick.Bot.Guid] = new PlayerbotAbOrder(PlayerbotAbDuty.Defend, pick.Node.Node, pick.Node.At, NearestEnemy(picture.Enemies, pick.Node.At, DefendYards));
                    pool.Remove(pick.Bot);
                }
            }
        }

        // 3. Attackers: spread over the attackable nodes, the cheapest pair first, each node at most an even share plus its defenders.
        if (attackable.Count > 0 && pool.Count > 0)
        {
            int perNode = Math.Max(1, (int)Math.Ceiling(pool.Count / (double)attackable.Count));
            var load = attackable.ToDictionary(n => n.Node, _ => 0);
            float Cost(PlayerbotAbMember bot, PlayerbotAbNode node)
                => Vector3.Distance(bot.At, node.At)
                    - (node.Status == ArathiBasin.StatusNeutral ? NeutralBonusYards : node.Status is ArathiBasin.StatusAllianceContested or ArathiBasin.StatusHordeContested ? ContestedBonusYards : 0f);
            while (pool.Count > 0)
            {
                (PlayerbotAbMember Bot, PlayerbotAbNode Node)? best = null;
                float bestCost = float.MaxValue;
                foreach (PlayerbotAbMember bot in pool)
                {
                    foreach (PlayerbotAbNode node in attackable)
                    {
                        if (load[node.Node] >= perNode + Threat(node)) continue;
                        float cost = Cost(bot, node);
                        if (best is null || cost < bestCost || (cost == bestCost && bot.Guid.Value < best.Value.Bot.Guid.Value))
                        {
                            best = (bot, node);
                            bestCost = cost;
                        }
                    }
                }

                // Every node full (more attackers than the shares hold): the rest join the node cheapest for them.
                best ??= (pool[0], attackable.MinBy(n => Cost(pool[0], n)));
                (PlayerbotAbMember Bot, PlayerbotAbNode Node) pick = best.Value;
                load[pick.Node.Node]++;
                orders[pick.Bot.Guid] = new PlayerbotAbOrder(PlayerbotAbDuty.Assault, pick.Node.Node, pick.Node.At, NearestEnemy(picture.Enemies, pick.Node.At, DefendYards));
                pool.Remove(pick.Bot);
            }
        }

        // 4. Nothing attackable (every node the team's): the rest defend the held nodes, spread, threatened ones first.
        if (pool.Count > 0 && held.Count > 0)
        {
            var load = held.ToDictionary(n => n.Node, n => orders.Values.Count(o => o.Duty == PlayerbotAbDuty.Defend && o.Node == n.Node));
            foreach (PlayerbotAbMember bot in pool.ToList())
            {
                PlayerbotAbNode node = held.OrderBy(n => load[n.Node] - (2 * Threat(n))).ThenBy(n => Vector3.Distance(bot.At, n.At)).First();
                load[node.Node]++;
                orders[bot.Guid] = new PlayerbotAbOrder(PlayerbotAbDuty.Defend, node.Node, node.At, NearestEnemy(picture.Enemies, node.At, DefendYards));
                pool.Remove(bot);
            }
        }

        return orders;
    }

    /// <summary>
    /// The buff a bot should step on now, if any: a spawned buff within <see cref="BuffDetourYards"/>, out of combat; restoration only when
    /// its health or mana is under <see cref="RestoreBelow"/>, speed and berserking always (vmangos BattleGround buffs: g_buffEntries, the
    /// trap spells cast on whoever steps on them). The nearest first.
    /// </summary>
    internal static PlayerbotBgBuff? ChooseBuff(Vector3 at, bool inCombat, float health, float mana, IReadOnlyList<PlayerbotBgBuff> buffs)
    {
        ArgumentNullException.ThrowIfNull(buffs);
        if (inCombat) return null;
        PlayerbotBgBuff? best = null;
        float nearest = BuffDetourYards;
        foreach (PlayerbotBgBuff buff in buffs)
        {
            if (buff.Entry == BattlegroundConstants.RegenBuffEntry && health >= RestoreBelow && mana >= RestoreBelow) continue;
            if (buff.Entry != BattlegroundConstants.RegenBuffEntry && buff.Entry != BattlegroundConstants.SpeedBuffEntry
                && buff.Entry != BattlegroundConstants.BerserkBuffEntry) continue;
            float distance = Vector3.Distance(buff.At, at);
            if (distance <= nearest)
            {
                nearest = distance;
                best = buff;
            }
        }

        return best;
    }

    private static int Compare((int Covered, int Role, int Threat, float Distance, ulong Guid) a, (int Covered, int Role, int Threat, float Distance, ulong Guid) b)
    {
        int c = a.Covered.CompareTo(b.Covered);
        if (c != 0) return c;
        c = a.Role.CompareTo(b.Role);
        if (c != 0) return c;
        c = a.Threat.CompareTo(b.Threat);
        if (c != 0) return c;
        c = a.Distance.CompareTo(b.Distance);
        return c != 0 ? c : a.Guid.CompareTo(b.Guid);
    }

    private static ObjectGuid NearestEnemy(IReadOnlyList<PlayerbotAbUnit> enemies, Vector3 to, float within)
    {
        ObjectGuid best = default;
        float nearest = within;
        foreach (PlayerbotAbUnit enemy in enemies)
        {
            float distance = Vector3.Distance(enemy.At, to);
            if (distance <= nearest)
            {
                nearest = distance;
                best = enemy.Guid;
            }
        }

        return best;
    }
}
