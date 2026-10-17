using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>What one bot does in Warsong Gulch this think (<see cref="PlayerbotWarsongTactics.Plan"/>).</summary>
internal enum PlayerbotWsgDuty : byte
{
    /// <summary>The bot carries the enemy flag: home into its own flag room (the capture needs its own flag home).</summary>
    CarryHome,

    /// <summary>Its own flag lies on the ground: click it to return it (BattleGroundWS.cpp:396-415).</summary>
    ReturnOwnFlag,

    /// <summary>The enemy flag lies on the ground: pick it up.</summary>
    TakeDroppedEnemyFlag,

    /// <summary>An enemy carries the bot's own flag: chase and kill it (then its flag drops and is returned).</summary>
    HuntCarrier,

    /// <summary>A teammate carries the enemy flag: follow and protect it (healers first).</summary>
    EscortCarrier,

    /// <summary>Hold its own flag room and fight whoever comes in.</summary>
    DefendFlagRoom,

    /// <summary>Go to the enemy flag stand and take the flag (or wait there while it respawns).</summary>
    TakeEnemyFlag,

    /// <summary>Hold the middle of the field and fight what passes.</summary>
    Midfield,
}

/// <summary>A bot of the planning team: its guid, where it stands, whether it is alive and a healer.</summary>
internal readonly record struct PlayerbotWsgMember(ObjectGuid Guid, Vector3 At, bool Alive, bool Healer);

/// <summary>A living enemy player on the field.</summary>
internal readonly record struct PlayerbotWsgEnemy(ObjectGuid Guid, Vector3 At);

/// <summary>A bot's order: its duty, the unit it is about (a carrier, an intruder; empty for none) and where it goes.</summary>
internal readonly record struct PlayerbotWsgOrder(PlayerbotWsgDuty Duty, ObjectGuid Target, Vector3 Goal);

/// <summary>Everything the plan reads about one team's side of a Warsong Gulch match, as plain values (built once per match, team and tick).</summary>
internal sealed class PlayerbotWsgPicture
{
    public required Team Team { get; init; }

    /// <summary>The team's managed bots (the plan orders these only; a real player is read, never driven).</summary>
    public required IReadOnlyList<PlayerbotWsgMember> Bots { get; init; }

    /// <summary>The living enemy players on the battleground map.</summary>
    public required IReadOnlyList<PlayerbotWsgEnemy> Enemies { get; init; }

    /// <summary>The state of the team's own flag (taken by the enemy, on the ground, home).</summary>
    public required WsgFlagState OwnFlag { get; init; }

    /// <summary>Where the team's own flag lies when it is on the ground.</summary>
    public Vector3? OwnFlagDropped { get; init; }

    /// <summary>The state of the enemy flag.</summary>
    public required WsgFlagState EnemyFlag { get; init; }

    /// <summary>The enemy flag is on its stand and may be taken now (its spawn event is active).</summary>
    public bool EnemyFlagReady { get; init; }

    /// <summary>The enemy flag stand object to click while the flag is ready (empty: not on the map).</summary>
    public ObjectGuid EnemyStandObject { get; init; }

    /// <summary>Where the enemy flag lies when it is on the ground.</summary>
    public Vector3? EnemyFlagDropped { get; init; }

    /// <summary>The enemy player carrying the team's own flag (empty: none) and where it is (null: not in sight on the map).</summary>
    public ObjectGuid EnemyCarrier { get; init; }

    public Vector3? EnemyCarrierAt { get; init; }

    /// <summary>The teammate (bot or real player) carrying the enemy flag (empty: none) and where it is.</summary>
    public ObjectGuid OwnCarrier { get; init; }

    public Vector3? OwnCarrierAt { get; init; }

    /// <summary>The centre of the team's own flag room (the capture trigger, 3646 Alliance / 3647 Horde).</summary>
    public required Vector3 OwnRoom { get; init; }

    /// <summary>The enemy flag stand (its spawn: the stand object is gone while its flag is away).</summary>
    public required Vector3 EnemyStand { get; init; }

    /// <summary>The middle of the field (between the two flag rooms).</summary>
    public required Vector3 Midfield { get; init; }
}

/// <summary>
/// Who does what in Warsong Gulch, every think, from the match as it is (unit tested, free of world state). It follows mangosbot's
/// <c>BGTactics::selectObjective</c> for <c>BATTLEGROUND_WS</c> (cmangos/playerbots playerbot/strategy/actions/BattleGroundTactics.cpp):
/// the carrier goes home to its own flag (WS_FLAG_POS_*); a supporter follows its team's carrier, else goes after the enemy carrier, else
/// to the enemy flag; the others go after the enemy carrier, else to the enemy flag or patrol the middle (1227, 1476, 307); and
/// <c>BGTactics::atFlag</c> has anyone near a dropped flag click it (pick up the enemy's, return its own). On top of that a limited number
/// of defenders hold the flag room (mangosbot's <c>getDefendersCount</c> counts them) and fight any intruder there.
/// <para>
/// Unlike the static role split (<see cref="PlayerbotBattlegroundRoles"/>), every living bot gets an order every think, so nobody idles
/// while an objective or an enemy exists: the carrier first, then the nearest bot to each dropped flag, defenders (one, two from eight
/// bots; more while enemies stand in the room), hunters for the enemy carrier (half of the rest), escorts for the own carrier (healers
/// first), and the rest to the enemy flag or the middle. Ties go by guid, so every bot computes the same plan.
/// </para>
/// </summary>
internal static class PlayerbotWarsongTactics
{
    /// <summary>An enemy this close to the own flag room is in it (the room and its stairs).</summary>
    internal const float FlagRoomYards = 40f;

    /// <summary>A bot that stood this long out of combat, with somewhere to go or an enemy beside it, is idle (an anomaly).</summary>
    internal const uint IdleLimitMs = 15_000;

    /// <summary>A bot that moved less than this between two thinks stood still.</summary>
    internal const float StillYards = 1f;

    /// <summary>An enemy this close to a bot that is not fighting makes standing still idle.</summary>
    internal const float IdleEnemyYards = 30f;

    /// <summary>The orders of the team's living bots (a dead bot gets none: it is recovering).</summary>
    internal static Dictionary<ObjectGuid, PlayerbotWsgOrder> Plan(PlayerbotWsgPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var orders = new Dictionary<ObjectGuid, PlayerbotWsgOrder>(picture.Bots.Count);
        List<PlayerbotWsgMember> pool = [.. picture.Bots.Where(b => b.Alive).OrderBy(b => b.Guid.Value)];
        int living = pool.Count;
        if (living == 0) return orders;

        void Give(PlayerbotWsgMember bot, PlayerbotWsgDuty duty, ObjectGuid target, Vector3 goal)
        {
            orders[bot.Guid] = new PlayerbotWsgOrder(duty, target, goal);
            pool.Remove(bot);
        }

        // The carrier runs home (BGTactics::selectObjective: a bot with the flag aura goes to its own flag position).
        if (!picture.OwnCarrier.IsEmpty && pool.FirstOrDefault(b => b.Guid == picture.OwnCarrier) is { Guid.IsEmpty: false } carrier)
            Give(carrier, PlayerbotWsgDuty.CarryHome, default, picture.OwnRoom);

        // A dropped flag is clicked by the nearest bot (BGTactics::atFlag): the own one returned, the enemy one picked up.
        if (picture.OwnFlag == WsgFlagState.OnGround && picture.OwnFlagDropped is { } ownDropped && Nearest(pool, ownDropped) is { } returner)
            Give(returner, PlayerbotWsgDuty.ReturnOwnFlag, default, ownDropped);
        if (picture.EnemyFlag == WsgFlagState.OnGround && picture.EnemyFlagDropped is { } enemyDropped && Nearest(pool, enemyDropped) is { } taker)
            Give(taker, PlayerbotWsgDuty.TakeDroppedEnemyFlag, default, enemyDropped);

        // Defenders: while the own flag is home (or respawning there) the room is held, the nearest bots first; intruders bring more.
        List<PlayerbotWsgEnemy> intruders = [.. picture.Enemies.Where(e => Vector3.Distance(e.At, picture.OwnRoom) <= FlagRoomYards)
            .OrderBy(e => Vector3.Distance(e.At, picture.OwnRoom)).ThenBy(e => e.Guid.Value)];
        if (picture.OwnFlag is WsgFlagState.OnBase or WsgFlagState.WaitRespawn)
        {
            int defenders = Math.Min(Math.Max(DefenderCount(living), Math.Min(intruders.Count, pool.Count - 1)), pool.Count);
            if (living >= 2 && intruders.Count > 0) defenders = Math.Max(defenders, 1);
            for (int i = 0; i < defenders && pool.Count > 0; i++)
            {
                PlayerbotWsgMember defender = Nearest(pool, picture.OwnRoom)!.Value;
                ObjectGuid intruder = intruders.Count == 0 ? default : NearestEnemy(intruders, defender.At).Guid;
                Give(defender, PlayerbotWsgDuty.DefendFlagRoom, intruder, picture.OwnRoom);
            }
        }

        // The enemy carrier of the own flag: half of the rest hunt it, the nearest first (mangosbot's non-supporters).
        bool hunt = !picture.EnemyCarrier.IsEmpty && picture.EnemyCarrierAt is not null;
        if (hunt)
        {
            int hunters = Math.Max(1, (pool.Count + 1) / 2);
            for (int i = 0; i < hunters && pool.Count > 0; i++)
                Give(Nearest(pool, picture.EnemyCarrierAt!.Value)!.Value, PlayerbotWsgDuty.HuntCarrier, picture.EnemyCarrier, picture.EnemyCarrierAt!.Value);
        }

        // The own carrier: escorts, healers first (mangosbot's supporters follow the team flag carrier; vmangos BattleBotAI.cpp:379-421 has a
        // healer follow a teammate).
        bool escort = !picture.OwnCarrier.IsEmpty && picture.OwnCarrierAt is not null;
        if (escort && pool.Count > 0)
        {
            int escorts = Math.Max(1, (pool.Count + 1) / 2);
            foreach (PlayerbotWsgMember healer in pool.Where(b => b.Healer).ToList())
            {
                if (escorts == 0) break;
                Give(healer, PlayerbotWsgDuty.EscortCarrier, picture.OwnCarrier, picture.OwnCarrierAt!.Value);
                escorts--;
            }

            for (; escorts > 0 && pool.Count > 0; escorts--)
                Give(Nearest(pool, picture.OwnCarrierAt!.Value)!.Value, PlayerbotWsgDuty.EscortCarrier, picture.OwnCarrier, picture.OwnCarrierAt!.Value);
        }

        // The rest: every one after the enemy carrier while there is one; else to the enemy flag (the nearest half at least one) and the
        // middle (mangosbot's patrol); with the enemy flag carried by a teammate, with the carrier.
        int toFlag = Math.Max(1, (pool.Count + 1) / 2);
        while (pool.Count > 0)
        {
            if (hunt)
            {
                Give(pool[0], PlayerbotWsgDuty.HuntCarrier, picture.EnemyCarrier, picture.EnemyCarrierAt!.Value);
            }
            else if (picture.EnemyFlag == WsgFlagState.OnPlayer && escort)
            {
                Give(pool[0], PlayerbotWsgDuty.EscortCarrier, picture.OwnCarrier, picture.OwnCarrierAt!.Value);
            }
            else if (toFlag > 0 && picture.EnemyFlag is WsgFlagState.OnBase or WsgFlagState.WaitRespawn)
            {
                Give(Nearest(pool, picture.EnemyStand)!.Value, PlayerbotWsgDuty.TakeEnemyFlag, picture.EnemyFlagReady ? picture.EnemyStandObject : default,
                    picture.EnemyStand);
                toFlag--;
            }
            else
            {
                PlayerbotWsgMember fighter = pool[0];
                ObjectGuid near = picture.Enemies.Count == 0 ? default : NearestEnemy(picture.Enemies, picture.Midfield).Guid;
                Give(fighter, PlayerbotWsgDuty.Midfield, near, picture.Midfield);
            }
        }

        return orders;
    }

    /// <summary>
    /// What a bot sent to take the enemy flag is doing. <paramref name="ready"/> is the match calling the flag on its base with its
    /// spawn event active; <paramref name="standFound"/> is the stand object being in the world to click. That object is created only
    /// when its grid is loaded (<c>GameObjectMapSystem.RefreshSpawns</c> skips an unloaded grid), so a flag the match already calls
    /// ready often has no object until someone walks there. A bot still short of the stand is going to the flag (<c>waiting-flag</c>),
    /// not idle. Only a bot that has arrived (<paramref name="yardsFromStand"/> within <paramref name="arrivedYards"/>) and still has
    /// nothing to click has no flag (<c>no-flag</c>): the stand is missing content, and standing there is idle.
    /// </summary>
    internal static string EnemyStandActivity(bool ready, bool standFound, float yardsFromStand, float arrivedYards)
    {
        if (standFound && ready) return "taking-flag";
        if (ready && yardsFromStand <= arrivedYards) return "no-flag";
        return "waiting-flag";
    }

    /// <summary>The defenders a team of <paramref name="living"/> bots keeps at home without intruders: none below three, one, two from eight.</summary>
    internal static int DefenderCount(int living) => living < 3 ? 0 : living < 8 ? 1 : 2;

    /// <summary>
    /// Whether a bot is idle in the match: alive, out of combat, standing still for <see cref="IdleLimitMs"/> although it has somewhere to
    /// go (its goal farther than <paramref name="arrivedYards"/>) or an enemy within <see cref="IdleEnemyYards"/>.
    /// </summary>
    internal static bool IsIdle(bool alive, bool inCombat, uint stillMs, float goalDistance, float arrivedYards, float nearestEnemyYards)
        => alive && !inCombat && stillMs >= IdleLimitMs && (goalDistance > arrivedYards || nearestEnemyYards <= IdleEnemyYards);

    private static PlayerbotWsgMember? Nearest(List<PlayerbotWsgMember> pool, Vector3 to)
    {
        PlayerbotWsgMember? best = null;
        float nearest = float.MaxValue;
        foreach (PlayerbotWsgMember bot in pool)
        {
            float distance = Vector3.Distance(bot.At, to);
            if (distance < nearest || (distance == nearest && best is { } b && bot.Guid.Value < b.Guid.Value))
            {
                nearest = distance;
                best = bot;
            }
        }

        return best;
    }

    private static PlayerbotWsgEnemy NearestEnemy(IReadOnlyList<PlayerbotWsgEnemy> enemies, Vector3 to)
    {
        PlayerbotWsgEnemy best = enemies[0];
        float nearest = Vector3.Distance(best.At, to);
        for (int i = 1; i < enemies.Count; i++)
        {
            float distance = Vector3.Distance(enemies[i].At, to);
            if (distance < nearest)
            {
                nearest = distance;
                best = enemies[i];
            }
        }

        return best;
    }
}
