using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>What a battleground buff object gives (<see cref="PlayerbotBattlegroundBuffs"/>).</summary>
internal enum PlayerbotBattlegroundBuffKind : byte
{
    /// <summary>"Speed Buff" (179871 and its alternates): spell 23451 / 23978 "Speed", +100% run speed for 10 s.</summary>
    Speed,

    /// <summary>"Food Buff" (179904 and its alternates): spell 23493 / 24379 "Restoration", health and mana back over 10 s.</summary>
    Restoration,

    /// <summary>"Berserk Buff" (179905 and its alternates): spell 23505 / 24378 "Berserking", more damage done (and taken) for 60 s.</summary>
    Berserking,
}

/// <summary>One spawned buff object of a match, as the board last saw it.</summary>
internal readonly record struct PlayerbotBattlegroundBuffSpot(ObjectGuid Guid, uint Entry, uint SpellId, PlayerbotBattlegroundBuffKind Kind, Vector3 Position);

/// <summary>What the bot needs now, the input of <see cref="PlayerbotBattlegroundBuffs.Choose"/> (free of world state).</summary>
/// <param name="HealthFraction">Health over max health (0-1).</param>
/// <param name="ManaFraction">Mana over max mana (0-1), or 1 for a bot without mana.</param>
/// <param name="Carrier">The bot carries the enemy flag (Warsong Gulch).</param>
/// <param name="ChasingCarrier">The bot chases the enemy carrying its own flag (Warsong Gulch).</param>
/// <param name="Fighting">The bot has an enemy to fight now, or is in combat.</param>
/// <param name="Urgent">An objective that must not wait: carrying, chasing the enemy carrier, a flag on the ground.</param>
/// <param name="Goal">Where the bot is heading anyway (the carrier's flag room, the chased carrier), or null: the detour of a buff is then its distance.</param>
internal readonly record struct PlayerbotBattlegroundBuffNeeds(
    float HealthFraction, float ManaFraction, bool Carrier, bool ChasingCarrier, bool Fighting, bool Urgent, Vector3? Goal);

/// <summary>
/// The buff objects of one match instance and who of each team is heading to which (one per <see cref="Map"/>, shared by every bot on it).
/// The spawned buffs are read by one scan of the map's objects at most every <see cref="RefreshMs"/>, not per think; between scans a bot
/// checks only its own target by guid. World thread only.
/// </summary>
internal sealed class PlayerbotBattlegroundBuffBoard
{
    /// <summary>The board's scan of the map's objects is at most this old.</summary>
    internal const long RefreshMs = 3_000;

    /// <summary>A claim lapses this long after its bot last renewed it (it renews it on every think it still heads there).</summary>
    internal const long ClaimMs = 3_000;

    private static readonly ConditionalWeakTable<Map, PlayerbotBattlegroundBuffBoard> Boards = [];

    private readonly List<PlayerbotBattlegroundBuffSpot> _spots = [];
    private readonly Dictionary<(ObjectGuid Buff, Team Team), (ObjectGuid Bot, long Until)> _claims = [];
    private long _refreshedAt = long.MinValue;

    /// <summary>The board of a match's map.</summary>
    internal static PlayerbotBattlegroundBuffBoard Of(Map map) => Boards.GetValue(map, _ => new PlayerbotBattlegroundBuffBoard());

    /// <summary>The full scans of the map's objects so far (tests: a think does not scan).</summary>
    internal int Scans { get; private set; }

    /// <summary>The spawned buffs as of the last scan.</summary>
    internal IReadOnlyList<PlayerbotBattlegroundBuffSpot> Spots => _spots;

    /// <summary>The spawned buffs, scanning <paramref name="map"/> again when the last scan is older than <see cref="RefreshMs"/>.</summary>
    internal IReadOnlyList<PlayerbotBattlegroundBuffSpot> Current(Map map, long nowMs)
    {
        if (nowMs - _refreshedAt < RefreshMs) return _spots;
        _refreshedAt = nowMs;
        _spots.Clear();
        Scans++;
        if (map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            foreach (GameObject go in objects.GameObjects)
            {
                if (go.IsSpawned && Classify(go.Template) is { } kind)
                    _spots.Add(new PlayerbotBattlegroundBuffSpot(go.Guid, go.Entry, go.Template.GetData(3), kind, new Vector3(go.X, go.Y, go.Z)));
            }
        }

        return _spots;
    }

    /// <summary>Put the spots in directly (tests).</summary>
    internal void Load(IEnumerable<PlayerbotBattlegroundBuffSpot> spots, long nowMs)
    {
        _spots.Clear();
        _spots.AddRange(spots);
        _refreshedAt = nowMs;
    }

    /// <summary>A buff is gone (despawned, used): out of the spots and every claim, until the next scan sees it again.</summary>
    internal void Forget(ObjectGuid buff)
    {
        _spots.RemoveAll(s => s.Guid == buff);
        _claims.Remove((buff, Team.Alliance));
        _claims.Remove((buff, Team.Horde));
    }

    /// <summary>Another bot of <paramref name="team"/> heads to <paramref name="buff"/> (its claim is live).</summary>
    internal bool ClaimedByOther(ObjectGuid buff, Team team, ObjectGuid bot, long nowMs)
        => _claims.TryGetValue((buff, team), out (ObjectGuid Bot, long Until) claim) && claim.Bot != bot && claim.Until > nowMs;

    /// <summary>Claim (or renew) <paramref name="buff"/> for <paramref name="bot"/>; false when a teammate holds it.</summary>
    internal bool TryClaim(ObjectGuid buff, Team team, ObjectGuid bot, long nowMs)
    {
        if (ClaimedByOther(buff, team, bot, nowMs)) return false;
        _claims[(buff, team)] = (bot, nowMs + ClaimMs);
        return true;
    }

    /// <summary>Drop <paramref name="bot"/>'s claim on <paramref name="buff"/> (a claim of someone else stays).</summary>
    internal void Release(ObjectGuid buff, Team team, ObjectGuid bot)
    {
        if (_claims.TryGetValue((buff, team), out (ObjectGuid Bot, long Until) claim) && claim.Bot == bot) _claims.Remove((buff, team));
    }

    /// <summary>
    /// The kind of a battleground buff object, or null: a trap whose radius (data2) is 0 and whose cooldown (data5) is 3 (the same test the
    /// match's trap update makes, vmangos GameObject::Update GameObject.cpp:476-490 <c>IsBattleGroundTrap</c>), by its spell (data3) as
    /// classic-db's gameobject_template has them: 179871/179899 and 180146/180147/180381/180384 cast 23451/23978 "Speed", 179904/179906 and
    /// 180144/180145/180362/180383 cast 23493/24379 "Restoration", 179905/179907 and 180148/180380/180382 cast 23505/24378 "Berserking".
    /// </summary>
    internal static PlayerbotBattlegroundBuffKind? Classify(GameObjectTemplate template)
    {
        if ((GameObjectType)template.Type != GameObjectType.Trap || template.GetData(2) != 0
            || template.GetData(5) != BattlegroundConstants.BattlegroundTrapCooldownSeconds) return null;
        return template.GetData(3) switch
        {
            23451 or 23978 => PlayerbotBattlegroundBuffKind.Speed,
            23493 or 24379 => PlayerbotBattlegroundBuffKind.Restoration,
            23505 or 24378 => PlayerbotBattlegroundBuffKind.Berserking,
            _ => template.Entry switch
            {
                BattlegroundConstants.SpeedBuffEntry => PlayerbotBattlegroundBuffKind.Speed,
                BattlegroundConstants.RegenBuffEntry => PlayerbotBattlegroundBuffKind.Restoration,
                BattlegroundConstants.BerserkBuffEntry => PlayerbotBattlegroundBuffKind.Berserking,
                _ => null,
            },
        };
    }
}

/// <summary>
/// One bot's use of the battleground buff objects (Warsong Gulch's speed, restoration and berserking buffs, the Arathi Basin node buffs):
/// which spawned buff near it is worth a detour now, and the claim that keeps a teammate from running to the same one. The buffs are traps:
/// a living player within 3 yards sets one off, the trap casts its spell on that player and the match uses the object up (vmangos
/// GameObject::Update, GameObject.cpp:476-551, then BattleGround::HandleTriggerBuff, BattleGround.cpp:1713-1757; here
/// <see cref="ArcaneCore.World.Battlegrounds.MatchRuntime"/>'s buff trap update). So a bot takes one exactly as a player does: it walks onto
/// it; no aura is applied from here. A database buff comes back after its respawn time (classic-db spawns them with 180 s), a match buff
/// of Arathi Basin after <see cref="BattlegroundConstants.BuffRespawnTimeSeconds"/>.
/// <para>
/// <b>Which buff.</b> Speed for the flag carrier and for a bot chasing the enemy carrier (others only one right beside them); restoration
/// when the bot is hurt or low on mana; berserking when it fights or is about to (not the carrier, who does not stop to fight). A buff whose
/// aura the bot has is not taken again. <b>How far.</b> A buff within <see cref="SearchYards"/>, and its detour (the extra way against the
/// goal the bot heads to anyway, or its distance) under a cap: <see cref="DetourYards"/>, <see cref="UrgentDetourYards"/> when an objective
/// is urgent, <see cref="FightingDetourYards"/> in a fight; a chase that takes longer than <see cref="PursuitMs"/> is given up and that buff
/// is left alone for <see cref="IgnoreMs"/>, so objectives still win.
/// </para>
/// </summary>
internal sealed class PlayerbotBattlegroundBuffs
{
    /// <summary>Buffs further than this are not considered.</summary>
    internal const float SearchYards = 60f;

    /// <summary>The largest detour for a buff when nothing urgent waits.</summary>
    internal const float DetourYards = 40f;

    /// <summary>The largest detour while an objective is urgent (carrying, chasing the enemy carrier, a flag on the ground).</summary>
    internal const float UrgentDetourYards = 20f;

    /// <summary>The largest detour in a fight (not chasing the carrier).</summary>
    internal const float FightingDetourYards = 15f;

    /// <summary>A bot that wants no speed for itself takes a speed buff only this close.</summary>
    internal const float BesideYards = 10f;

    /// <summary>Hurt: restoration is worth it under this fraction of health (or of mana).</summary>
    internal const float HurtFraction = 0.6f;

    /// <summary>The time a bot may spend heading to one buff.</summary>
    internal const long PursuitMs = 15_000;

    /// <summary>A buff given up on is left alone this long by that bot.</summary>
    internal const long IgnoreMs = 30_000;

    private readonly Dictionary<ObjectGuid, long> _ignored = [];
    private PlayerbotBattlegroundBuffBoard? _board;
    private Team _team;
    private long _pursuitSince;

    /// <summary>The buff the bot heads to, or null.</summary>
    internal PlayerbotBattlegroundBuffSpot? Pursuit { get; private set; }

    /// <summary>The buffs the bot reached and that were used up as it got there (inspection and tests).</summary>
    internal int Taken { get; private set; }

    /// <summary>Drop the pursuit and its claim (death, the end of the match, the coordinator letting the bot go).</summary>
    internal void Reset(ObjectGuid bot)
    {
        if (Pursuit is { } spot) _board?.Release(spot.Guid, _team, bot);
        Pursuit = null;
        _board = null;
    }

    /// <summary>The bot could not walk to its buff: leave it alone for a while.</summary>
    internal void GiveUp(ObjectGuid bot, long nowMs)
    {
        if (Pursuit is { } spot) _ignored[spot.Guid] = nowMs + IgnoreMs;
        Reset(bot);
    }

    /// <summary>
    /// One think of a participant: the buff to walk onto now, or null. <paramref name="enemy"/> is what the bot is about to fight (null
    /// for none); <paramref name="carrier"/> whether it carries the enemy flag.
    /// </summary>
    internal PlayerbotBattlegroundBuffSpot? Update(WorldSession session, Player player, Battleground match, Team team, Unit? enemy, bool carrier, long nowMs)
    {
        if (player.Map is not { } map) return null;
        PlayerbotBattlegroundBuffBoard board = PlayerbotBattlegroundBuffBoard.Of(map);
        if (!ReferenceEquals(board, _board)) Reset(player.Guid);
        IReadOnlyList<PlayerbotBattlegroundBuffSpot> spots = board.Current(map, nowMs);
        SpellSystem? spells = session.Services.GetService<SpellFeature>()?.System;
        return Decide(player.Guid, team, new Vector3(player.X, player.Y, player.Z), NeedsOf(session, player, match, team, enemy, carrier), board, spots,
            guid => map.FindObject(guid) is GameObject { IsSpawned: true }, spell => spells?.HasAura(player, spell) == true, nowMs);
    }

    /// <summary>
    /// The decision of one think against a board: keep heading to the current buff while it is still spawned, still worth it and not
    /// overdue; else choose again (<see cref="Choose"/>) and claim the choice. A buff found gone is forgotten on the board.
    /// </summary>
    internal PlayerbotBattlegroundBuffSpot? Decide(ObjectGuid bot, Team team, Vector3 at, PlayerbotBattlegroundBuffNeeds needs,
        PlayerbotBattlegroundBuffBoard board, IReadOnlyList<PlayerbotBattlegroundBuffSpot> spots, Func<ObjectGuid, bool> spawned,
        Func<uint, bool> hasAura, long nowMs)
    {
        _board = board;
        _team = team;
        if (Pursuit is { } current)
        {
            if (!spawned(current.Guid))
            {
                // Used up (by this bot when it stood on it, or by anyone) or despawned: forget it.
                if (Vector3.Distance(at, current.Position) <= BattlegroundConstants.BattlegroundTrapCooldownSeconds + 1f) Taken++;
                board.Forget(current.Guid);
                Pursuit = null;
            }
            else if (nowMs - _pursuitSince > PursuitMs)
            {
                GiveUp(bot, nowMs);
                _board = board;
            }
            else if (Score(current, at, needs, hasAura) is not null && board.TryClaim(current.Guid, team, bot, nowMs))
            {
                return current;
            }
            else
            {
                board.Release(current.Guid, team, bot);
                Pursuit = null;
            }
        }

        foreach (ObjectGuid stale in _ignored.Where(i => i.Value <= nowMs).Select(i => i.Key).ToList()) _ignored.Remove(stale);
        PlayerbotBattlegroundBuffSpot? chosen = Choose(at, needs, spots.Where(s => !_ignored.ContainsKey(s.Guid) && spawned(s.Guid)),
            guid => board.ClaimedByOther(guid, team, bot, nowMs), hasAura);
        if (chosen is not { } next || !board.TryClaim(next.Guid, team, bot, nowMs)) return null;
        Pursuit = next;
        _pursuitSince = nowMs;
        return next;
    }

    /// <summary>
    /// The best buff for <paramref name="needs"/> among <paramref name="spots"/> (free of world state): each kind's worth to the bot, less a
    /// little per yard of detour; a buff a teammate heads to, one whose aura the bot has, one out of <see cref="SearchYards"/> or over its
    /// detour cap is never chosen.
    /// </summary>
    internal static PlayerbotBattlegroundBuffSpot? Choose(Vector3 at, PlayerbotBattlegroundBuffNeeds needs, IEnumerable<PlayerbotBattlegroundBuffSpot> spots,
        Func<ObjectGuid, bool> claimedByOther, Func<uint, bool> hasAura)
    {
        PlayerbotBattlegroundBuffSpot? best = null;
        float bestScore = float.MinValue;
        foreach (PlayerbotBattlegroundBuffSpot spot in spots)
        {
            if (claimedByOther(spot.Guid) || Score(spot, at, needs, hasAura) is not { } score || score <= bestScore) continue;
            best = spot;
            bestScore = score;
        }

        return best;
    }

    /// <summary>The worth of a buff to the bot now, or null when it is not wanted or too far out of its way.</summary>
    private static float? Score(PlayerbotBattlegroundBuffSpot spot, Vector3 at, PlayerbotBattlegroundBuffNeeds needs, Func<uint, bool> hasAura)
    {
        float distance = Vector3.Distance(at, spot.Position);
        if (distance > SearchYards || (spot.SpellId != 0 && hasAura(spot.SpellId))) return null;
        float detour = needs.Goal is { } goal
            ? Math.Max(0f, distance + Vector3.Distance(spot.Position, goal) - Vector3.Distance(at, goal))
            : distance;
        float cap = needs.Fighting && !needs.ChasingCarrier ? FightingDetourYards : needs.Urgent ? UrgentDetourYards : DetourYards;
        float worth;
        switch (spot.Kind)
        {
            case PlayerbotBattlegroundBuffKind.Speed:
                if (needs.Carrier || needs.ChasingCarrier) worth = 4f;
                else if (!needs.Fighting && distance <= BesideYards) worth = 1f;
                else return null;
                break;

            case PlayerbotBattlegroundBuffKind.Restoration:
                float low = Math.Min(needs.HealthFraction, needs.ManaFraction);
                if (low >= HurtFraction) return null;
                worth = 2f + (2f * (1f - low));
                break;

            case PlayerbotBattlegroundBuffKind.Berserking:
                if (needs.Carrier || !needs.Fighting) return null;
                worth = 2.5f;
                break;

            default:
                return null;
        }

        if (detour > cap) return null;
        return worth - (detour / 20f);
    }

    /// <summary>The bot's needs as the match and its body have them now.</summary>
    private static PlayerbotBattlegroundBuffNeeds NeedsOf(WorldSession session, Player player, Battleground match, Team team, Unit? enemy, bool carrier)
    {
        float health = player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 1f;
        uint maxMana = player.PowerType == PowerType.Mana ? player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) : 0u;
        float mana = maxMana > 0 ? (float)SpellSystem.GetPower(player, PowerType.Mana) / maxMana : 1f;
        bool fighting = enemy is not null || player.Combat.IsInCombat;
        bool chasing = false;
        bool urgent = carrier;
        Vector3? goal = null;
        if (match is WarsongGulch wsg)
        {
            Team other = BattlegroundConstants.OtherTeam(team);
            chasing = enemy is not null && wsg.FlagState(team) == WsgFlagState.OnPlayer && wsg.FlagPicker(team) == enemy.Guid;
            urgent |= chasing || wsg.FlagState(team) == WsgFlagState.OnGround || wsg.FlagState(other) == WsgFlagState.OnGround;
            if (carrier)
            {
                uint trigger = team == Team.Alliance ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn;
                if (Game.Maps.Templates.WorldMaps.Of(session.World).FindAreaTrigger(trigger) is { } room) goal = new Vector3(room.X, room.Y, room.Z);
            }
            else if (chasing)
            {
                goal = new Vector3(enemy!.X, enemy.Y, enemy.Z);
            }
        }

        return new PlayerbotBattlegroundBuffNeeds(health, mana, carrier, chasing, fighting, urgent, goal);
    }
}
