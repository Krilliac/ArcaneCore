using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Drives one managed bot while the battlegrounds coordinator holds it (<see cref="PlayerbotBattlegroundCoordinator"/>): the walk to a
/// battlemaster before the queue, and the match itself, instead of <see cref="PlayerbotBrain"/>. World thread only; one per bot.
/// <para>
/// In a match the bot waits behind the gates until the match is in progress, then fights the enemy players near it (the class rotation of
/// <see cref="PlayerbotCombatSpells"/> and the melee swing, as the group AI fights creatures) and plays the objective of its role
/// (<see cref="PlayerbotBattlegroundRoles"/>):
/// </para>
/// <list type="bullet">
/// <item><b>Warsong Gulch</b> (vmangos BattleBotWaypoints.cpp:76-140, <c>WSG_AtAllianceFlag</c> / <c>WSG_AtHordeFlag</c>; BattleGroundWS.cpp:354-480 and
/// 539-548 for the server side): an attacker walks to the enemy flag stand and uses it (CMSG_GAMEOBJ_USE) once in interaction range; the
/// carrier walks home into its own flag room trigger (3646 Alliance, 3647 Horde), which its motion reports with CMSG_AREATRIGGER and the
/// match turns into a capture; an escort follows its own carrier (vmangos <c>BattleBotAI::SelectFollowTarget</c>, BattleBotAI.cpp:379-421)
/// and attacks otherwise; a defender holds its own flag room; anyone near its own dropped flag returns it, and the enemy carrier of the
/// own flag is chased and attacked.</item>
/// <item><b>Arathi Basin</b> (BattleBotWaypoints.cpp:163-215, <c>AtFlag</c>; BattleGroundAB.cpp:337-458): an attacker walks to the nearest
/// node its team may click (neutral, or held or contested by the enemy: the server's own rule) and casts the banner spell 21651 at the
/// node's banner (CMSG_CAST_SPELL at the object), standing still for its ten-second cast; a teammate already casting there is left to it.
/// A defender holds one of its team's nodes and defends it the same way once the enemy assaulted it.</item>
/// <item><b>Alterac Valley</b> (<c>PlayerbotBattlegroundAI.AlteracValley.cs</c>, <see cref="PlayerbotAlteracValleyTactics"/>): the push on the
/// enemy general through the enemy captain, towers and graveyards along the bot's lane, the own assaulted nodes clicked back and a limited
/// count of guards.</item>
/// </list>
/// <para>
/// A dead bot releases through <see cref="PlayerbotRecovery"/>: in a battleground the ghost waits at the graveyard's spirit guide for its
/// resurrection wave (the release put it at the team's nearest held graveyard) instead of running back to its body.
/// </para>
/// </summary>
internal sealed partial class PlayerbotBattlegroundAI
{
    /// <summary>vmangos INTERACTION_DISTANCE (5 yards), less a margin: a flag or a battlemaster is used from this close.</summary>
    internal const float InteractYards = 4f;

    /// <summary>An enemy player this close is fought (an enemy attacking the bot always is).</summary>
    internal const float EngageYards = 30f;

    /// <summary>The enemy carrying the bot's own flag is chased from this far (Warsong Gulch).</summary>
    internal const float ChaseCarrierYards = 80f;

    /// <summary>A bot returns its own dropped flag from this far (a defender from anywhere on the map).</summary>
    internal const float ReturnFlagYards = 60f;

    /// <summary>A defender stays within this distance of what it defends.</summary>
    internal const float GuardYards = 8f;

    /// <summary>The Arathi Basin and Alterac Valley banner spell (Spell.dbc 21651 "Opening": OPEN_LOCK misc 17, 10 s, 0-5 yards).</summary>
    internal const uint BannerSpell = 21651;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly PlayerbotRecovery _recovery;
    private readonly float _followAngle;
    private readonly float _followDistance;
    private PlayerbotRoute? _route;
    private Vector3 _routeGoal;
    private Unit? _target;
    private bool _attacking;
    private uint _thinkElapsed;
    private bool _deathRetired;

    internal PlayerbotBattlegroundAI(WorldSession session, PlayerbotOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _spells = new PlayerbotCombatSpells(session);
        _recovery = new PlayerbotRecovery(session, options);
        var random = new Random(session.AccountId);
        _followAngle = (float)(random.NextDouble() * MathF.Tau);
        _followDistance = 3f + ((float)random.NextDouble() * 2f);
    }

    /// <summary>What the bot is doing (reported and persisted as its goal).</summary>
    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Battleground;

    /// <summary>The objective or enemy the bot is after: a game object or creature entry, 0 for a player or nothing.</summary>
    internal uint TargetEntry { get; private set; }

    /// <summary>The enemy the bot fights (inspection and tests).</summary>
    internal Unit? Target => _target;

    /// <summary>A short word for the status line: what the bot does in the match now.</summary>
    internal string Activity { get; private set; } = "idle";

    /// <summary>The banner casts the bot has submitted and the server took (Arathi Basin; inspection and tests).</summary>
    internal int BannerCasts { get; private set; }

    /// <summary>The bot's corpse run (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;

    /// <summary>True when the walk to a battlemaster refused a route (or stood at the spot for 5s with no living battlemaster).</summary>
    internal bool TravelRefused { get; private set; }

    private uint _atMasterMs;
    private uint _inMatchIdleMs;
    private bool _reportedObjectiveUnreachable;
    private bool _reportedIdleInMatch;

    private long Now => (long)_session.World.Uptime.TotalMilliseconds;

    /// <summary>Drop every route, target and cast intention (the coordinator lets the bot go, or it changes its phase).</summary>
    internal void Release(Player player)
    {
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        _route = null;
        _target = null;
        _attacking = false;
        TravelRefused = false;
        _atMasterMs = 0;
        _inMatchIdleMs = 0;
        _reportedObjectiveUnreachable = false;
        _reportedIdleInMatch = false;
        _spells.GroupRole = null;
        _spells.Reset();
        _recovery.Reset();
        Activity = "idle";
    }

    // --- before the queue ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One think of the walk to a battlemaster: towards <paramref name="at"/>, stopping in interaction range. Out of combat only; a fight on
    /// the way is answered (the bot is attacked) before it walks on. True while it walks or fights.
    /// </summary>
    internal bool Travel(Player player, Vector3 at, uint elapsedMs)
    {
        if (!Think(player, elapsedMs, out uint interval)) return true;
        TravelRefused = false;
        if (!player.IsAlive) return false;
        Goal = PlayerbotGoalKind.Battleground;
        Activity = "to-battlemaster";
        if (FirstAttacker(player) is { } attacker)
        {
            Fight(player, attacker, interval);
            return true;
        }

        if (_target is not null) ClearTarget(player);
        if (Vector3.Distance(Position(player), at) <= InteractYards - 1f)
        {
            Hold(player);
            _atMasterMs = unchecked(_atMasterMs + interval);
            if (_atMasterMs >= 5_000)
            {
                TravelRefused = true;
                ReportObjectiveUnreachable(player, $"no living battlemaster at {at}");
            }
            return false;
        }

        _atMasterMs = 0;
        bool walking = WalkTo(player, at, interval, toward: true);
        TravelRefused = !walking;
        if (TravelRefused)
            ReportObjectiveUnreachable(player, $"route to battlemaster at {at} was refused");
        return walking;
    }

    // --- in the match ----------------------------------------------------------------------------------------------------

    /// <summary>One tick of a participant (world thread, within the shared action budget like the brain).</summary>
    internal void Play(Player player, Battleground match, Team team, PlayerbotBattlegroundRole role, uint elapsedMs)
    {
        if (!player.IsInWorld)
        {
            PlayerbotMovementControl.Update(_session, player);
            return;
        }

        Goal = PlayerbotGoalKind.Battleground;
        _spells.GroupRole = null;
        bool dead = !player.IsAlive;
        if (dead && !_deathRetired)
        {
            _target = null;
            _attacking = false;
            _route = null;
            _spells.Reset();
            _deathRetired = true;
        }

        if (!Think(player, elapsedMs, out uint interval)) return;
        if (dead)
        {
            Goal = PlayerbotGoalKind.Recover;
            _inMatchIdleMs = 0;
            // Not before the gates open, nor after the end: the server refuses a reclaim outside a running match (vmangos
            // HandleReclaimCorpseOpcode, the BattleGround status check) and resurrects everyone at the end.
            if (match.Status == BattlegroundStatus.InProgress) _recovery.Update(player, interval);
            Activity = _recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.AwaitAreaResurrection ? "spirit-guide" : "corpse-run";
            return;
        }

        _deathRetired = false;
        _recovery.Reset();
        PlayerbotNavigation.Guard(player, null);
        if (match.Status != BattlegroundStatus.InProgress)
        {
            // Behind the gates before the start, frozen at the end (BattleGround.cpp:290-464, 651-769).
            if (_target is not null) ClearTarget(player);
            Activity = match.Status == BattlegroundStatus.WaitLeave ? "match-over" : "waiting-start";
            _inMatchIdleMs = 0;
            Hold(player);
            return;
        }

        if (Casting(player))
        {
            Activity = "casting";
            _inMatchIdleMs = 0;
            return;
        }

        bool carrier = match is WarsongGulch carrying && carrying.FlagPicker(BattlegroundConstants.OtherTeam(team)) == player.Guid;
        Unit? enemy = carrier ? FirstAttackerIfStill(player) : SelectEnemy(player, match, team);
        if (enemy is not null)
        {
            Activity = "fighting";
            _inMatchIdleMs = 0;
            Fight(player, enemy, interval);
            return;
        }

        if (_target is not null) ClearTarget(player);
        TargetEntry = 0;
        if (!player.Combat.IsInCombat && _spells.UpdateOutOfCombat(player, interval))
        {
            _inMatchIdleMs = 0;
            return;
        }
        switch (match)
        {
            case WarsongGulch wsg:
                PlayWarsong(player, wsg, team, role, interval);
                break;
            case ArathiBasin ab:
                PlayArathi(player, ab, team, role, interval);
                break;
            case AlteracValley av:
                PlayAlteracValley(player, av, team, role, interval);
                break;
            default:
                Activity = "unsupported";
                Hold(player);
                break;
        }

        ObserveMatchOutcome(player, match, interval);
    }

    private void ObserveMatchOutcome(Player player, Battleground match, uint interval)
    {
        if (Activity is "no-flag" or "no-banners" or "no-banner-spell")
            ReportObjectiveUnreachable(player, $"battleground {match.Type} objective unavailable ({Activity})");

        bool unproductive = Activity is "idle" or "unsupported" or "no-flag" or "no-banners" or "no-banner-spell";
        if (unproductive)
        {
            _inMatchIdleMs = unchecked(_inMatchIdleMs + interval);
            if (Activity == "unsupported" || _inMatchIdleMs >= 15_000)
                ReportIdleInMatch(player, $"bot idle in {match.Type} match {match.InstanceId} (activity={Activity}, idleMs={_inMatchIdleMs})");
        }
        else
        {
            _inMatchIdleMs = 0;
        }
    }

    private void ReportObjectiveUnreachable(Player player, string detail)
    {
        if (_reportedObjectiveUnreachable || _session.IsInChaosProbe) return;
        _reportedObjectiveUnreachable = true;
        PlayerbotAnomalies.Report(player, "bg", "objective-unreachable", detail);
    }

    private void ReportIdleInMatch(Player player, string detail)
    {
        if (_reportedIdleInMatch || _session.IsInChaosProbe) return;
        _reportedIdleInMatch = true;
        PlayerbotAnomalies.Report(player, "bg", "idle-in-match", detail);
    }

    // --- Warsong Gulch ---------------------------------------------------------------------------------------------------

    private void PlayWarsong(Player player, WarsongGulch wsg, Team team, PlayerbotBattlegroundRole role, uint interval)
    {
        Team enemy = BattlegroundConstants.OtherTeam(team);
        Map map = player.Map!;
        Vector3? home = OwnFlagRoom(team);

        // The carrier runs home; inside its own flag room it waits for its own flag to come back (ForceFlagAreaTrigger captures then).
        if (wsg.FlagPicker(enemy) == player.Guid)
        {
            Activity = "carrying";
            if (home is { } room) WalkTo(player, room, interval, toward: true);
            else Hold(player);
            return;
        }

        // The own flag on the ground: return it (BattleGroundWS.cpp:396-415).
        if (wsg.FlagState(team) == WsgFlagState.OnGround && map.FindObject(wsg.DroppedFlagGuid(team)) is GameObject dropped
            && (role == PlayerbotBattlegroundRole.Defender || Distance(player, dropped) <= ReturnFlagYards))
        {
            Activity = "returning-flag";
            UseObject(player, dropped, interval);
            return;
        }

        PlayerbotBattlegroundRole acting = role;
        Player? ownCarrier = wsg.FlagState(enemy) == WsgFlagState.OnPlayer ? Online(wsg.FlagPicker(enemy)) : null;
        if (acting == PlayerbotBattlegroundRole.Escort && ownCarrier is null) acting = PlayerbotBattlegroundRole.Attacker;
        switch (acting)
        {
            case PlayerbotBattlegroundRole.Escort:
                Activity = "escorting";
                Follow(player, ownCarrier!, interval);
                return;

            case PlayerbotBattlegroundRole.Defender:
                Activity = "defending";
                if (home is { } room) Guard(player, room, interval);
                else Hold(player);
                return;
        }

        // An attacker: the enemy flag, from its stand or the ground; a teammate carrying it is escorted.
        if (wsg.FlagState(enemy) == WsgFlagState.OnGround && map.FindObject(wsg.DroppedFlagGuid(enemy)) is GameObject loose)
        {
            Activity = "taking-flag";
            UseObject(player, loose, interval);
            return;
        }

        if (ownCarrier is not null && ownCarrier.Guid != player.Guid)
        {
            Activity = "escorting";
            Follow(player, ownCarrier, interval);
            return;
        }

        uint standEntry = enemy == Team.Alliance ? WarsongGulch.AllianceFlagBaseEntry : WarsongGulch.HordeFlagBaseEntry;
        GameObject? stand = FindObject(player, go => go.Entry == standEntry);
        if (stand is null)
        {
            Activity = "no-flag";
            Hold(player);
            return;
        }

        if (wsg.FlagState(enemy) == WsgFlagState.OnBase && wsg.IsActiveEvent(enemy == Team.Alliance ? WarsongGulch.EventFlagAlliance : WarsongGulch.EventFlagHorde, 0))
        {
            Activity = "taking-flag";
            UseObject(player, stand, interval);
            return;
        }

        // The flag is away or respawning: wait at the enemy stand for it.
        Activity = "waiting-flag";
        Guard(player, new Vector3(stand.X, stand.Y, stand.Z), interval);
    }

    /// <summary>The centre of the team's own flag room trigger (3646 Alliance, 3647 Horde; BattleBotWaypoints.cpp:70-74).</summary>
    private Vector3? OwnFlagRoom(Team team)
    {
        uint id = team == Team.Alliance ? WarsongGulch.AreaTriggerAllianceFlagSpawn : WarsongGulch.AreaTriggerHordeFlagSpawn;
        return Game.Maps.Templates.WorldMaps.Of(_session.World).FindAreaTrigger(id) is { } trigger ? new Vector3(trigger.X, trigger.Y, trigger.Z) : null;
    }

    // --- Arathi Basin ----------------------------------------------------------------------------------------------------

    private void PlayArathi(Player player, ArathiBasin ab, Team team, PlayerbotBattlegroundRole role, uint interval)
    {
        int teamIndex = BattlegroundConstants.TeamIndex(team);
        List<(int Node, GameObject Banner)> banners = Banners(player, ArathiBasin.NodeCount);
        if (banners.Count == 0)
        {
            Activity = "no-banners";
            Hold(player);
            return;
        }

        // vmangos BattleGroundAB::EventPlayerClickedOnFlag (BattleGroundAB.cpp:351-352): a team clicks a neutral node, or one whose status
        // belongs to the other team (status % 2 is the other team's index: contested or occupied by it). Statuses 1/2 are contested,
        // 3/4 occupied (BattleGroundAB.h); an own node the enemy contests is clicked back (defended).
        bool Clickable(int node) => ab.NodeStatus(node) == ArathiBasin.StatusNeutral || ab.NodeStatus(node) % 2 == teamIndex;
        bool Ours(int node) => ab.NodeStatus(node) != ArathiBasin.StatusNeutral && ab.NodeStatus(node) % 2 != teamIndex;
        bool EnemyContested(int node) => ab.NodeStatus(node) is ArathiBasin.StatusAllianceContested or ArathiBasin.StatusHordeContested
            && ab.NodeStatus(node) % 2 == teamIndex;

        (int Node, GameObject Banner)? goal = null;
        if (role == PlayerbotBattlegroundRole.Defender)
        {
            // The node it guards: its team's or one the enemy contests, chosen by the bot's guid so defenders spread; a contested one is
            // clicked (defended, or taken back).
            var held = banners.Where(b => Ours(b.Node) || EnemyContested(b.Node)).GroupBy(b => b.Node).Select(g => g.First()).OrderBy(b => b.Node).ToList();
            if (held.Count > 0)
            {
                (int Node, GameObject Banner) guarded = held[(int)(player.Guid.Low % (uint)held.Count)];
                if (!Clickable(guarded.Node))
                {
                    Activity = "defending";
                    TargetEntry = guarded.Banner.Entry;
                    Guard(player, new Vector3(guarded.Banner.X, guarded.Banner.Y, guarded.Banner.Z), interval);
                    return;
                }

                goal = guarded;
            }
        }

        goal ??= banners.Where(b => Clickable(b.Node) && !TeammateCasting(player, b.Banner))
            .OrderBy(b => Distance(player, b.Banner)).Select(b => ((int, GameObject)?)b).FirstOrDefault();
        if (goal is not { } target)
        {
            // Nothing to take: guard the nearest own node.
            (int Node, GameObject Banner)? own = banners.Where(b => Ours(b.Node)).OrderBy(b => Distance(player, b.Banner)).Select(b => ((int, GameObject)?)b).FirstOrDefault();
            Activity = "defending";
            if (own is { } node) Guard(player, new Vector3(node.Banner.X, node.Banner.Y, node.Banner.Z), interval);
            else Hold(player);
            return;
        }

        Activity = "assaulting";
        TargetEntry = target.Banner.Entry;
        if (Distance(player, target.Banner) > InteractYards)
        {
            WalkTo(player, new Vector3(target.Banner.X, target.Banner.Y, target.Banner.Z), interval, toward: false);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _route = null;
        if (_session.Services.GetService<SpellFeature>() is not { } feature || feature.System.Store.Get(BannerSpell) is not { } spell)
        {
            Activity = "no-banner-spell";
            return;
        }

        if (_spells.CastAtObject(player, spell, target.Banner)) BannerCasts++;
    }

    /// <summary>
    /// The node banners spawned on the bot's map now, each with its node: the objects whose battleground event (the
    /// <c>gameobject_battleground</c> row, event1 = node) names a node, that the banner spell opens (a lock id) and whose opening the match
    /// turns into the node click (<see cref="MatchRuntime.RoutesBannerUse"/>).
    /// </summary>
    private List<(int Node, GameObject Banner)> Banners(Player player, int nodeCount = ArathiBasin.NodeCount)
    {
        var banners = new List<(int, GameObject)>();
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects
            || _session.Services.GetService<BattlegroundFeature>() is not { } feature) return banners;
        foreach (GameObject go in objects.GameObjects)
        {
            // A banner the match does not route to the node click (a goober whose spell is no banner trigger) is never cast at: the cast would claim nothing.
            if (!go.IsSpawned || go.Spawn is not { } spawn || GameObjectLocks.LockIdOf(go.Template) == 0 || !MatchRuntime.RoutesBannerUse(go.Template)) continue;
            foreach ((byte e1, byte _) in feature.Events.ObjectEvents(spawn.Guid))
                if (e1 < nodeCount) banners.Add((e1, go));
        }

        return banners;
    }

    /// <summary>vmangos <c>AtFlag</c> (BattleBotWaypoints.cpp:181-189): a friend within interaction range already casting the banner spell there.</summary>
    private bool TeammateCasting(Player player, GameObject banner)
    {
        if (_session.Services.GetService<SpellFeature>() is not { } feature || player.Map is not { } map) return false;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid == player.Guid || map.FindObject(guid) is not Player friend || friend.Team != player.Team || !friend.IsAlive) continue;
            if (Distance(friend, banner) <= InteractYards + 1f
                && feature.System.GetState(friend.Guid)?.CurrentCast is { Spell.Id: BannerSpell, State: SpellCastState.Preparing or SpellCastState.Casting })
                return true;
        }

        return false;
    }

    // --- fighting --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The enemy player or Alterac Valley NPC to fight now: the carrier of the bot's own flag (Warsong Gulch) within <see cref="ChaseCarrierYards"/>, then one
    /// attacking the bot, then the one it fights already, then the nearest within <see cref="EngageYards"/>. Teammates never: the match's
    /// teams decide, not the faction templates (a player may always attack a player as far as the combat hooks go).
    /// </summary>
    private Unit? SelectEnemy(Player player, Battleground match, Team team)
    {
        if (player.Map is not { } map) return null;
        Team enemyTeam = BattlegroundConstants.OtherTeam(team);
        if (match is WarsongGulch wsg && wsg.FlagState(team) == WsgFlagState.OnPlayer && Online(wsg.FlagPicker(team)) is { } thief
            && IsEnemy(player, thief, match, enemyTeam) && Distance(player, thief) <= ChaseCarrierYards)
            return thief;

        foreach (Unit unit in player.Combat.Attackers)
        {
            if (unit is Player attacker && IsEnemy(player, attacker, match, enemyTeam)) return attacker;
            if (match is AlteracValley && unit is Game.Creatures.Creature npcAttacker && IsEnemyCreature(player, npcAttacker, requireHostile: false)) return npcAttacker;
        }

        if (_target is Player current && IsEnemy(player, current, match, enemyTeam) && Distance(player, current) <= EngageYards * 1.5f) return current;
        if (match is AlteracValley && _target is Game.Creatures.Creature currentNpc && IsEnemyCreature(player, currentNpc) && Distance(player, currentNpc) <= EngageYards * 1.5f) return currentNpc;

        Unit? nearest = null;
        float best = EngageYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            WorldObject? obj = map.FindObject(guid);
            if (obj is Player other && IsEnemy(player, other, match, enemyTeam))
            {
                float distance = Distance(player, other);
                if (distance <= best)
                {
                    best = distance;
                    nearest = other;
                }
            }
            else if (match is AlteracValley && nearest is not Player && obj is Game.Creatures.Creature npc && IsEnemyCreature(player, npc))
            {
                float distance = Distance(player, npc);
                if (distance <= best)
                {
                    best = distance;
                    nearest = npc;
                }
            }
        }

        return nearest;
    }

    /// <summary>A living, attackable participant of the other team on the bot's map.</summary>
    private static bool IsEnemy(Player player, Player other, Battleground match, Team enemyTeam)
        => !ReferenceEquals(other, player) && other.IsInWorld && other.IsAlive && (other.Flags & PlayerFlags.Ghost) == 0
            && ReferenceEquals(other.Map, player.Map) && match.PlayerTeam(other.Guid) == enemyTeam && player.Map is { } map
            && map.Combat.Hooks.CanAttack(player, other);

    /// <summary>
    /// A living, attackable and hostile Alterac Valley NPC guard, captain, commander or general on the bot's map: the neutral wildlife of the
    /// valley (the rams and wolves) is left alone unless it attacks the bot (<paramref name="requireHostile"/> false).
    /// </summary>
    private static bool IsEnemyCreature(Player player, Game.Creatures.Creature creature, bool requireHostile = true)
        => creature.IsInWorld && creature.IsAlive && ReferenceEquals(creature.Map, player.Map)
            && (creature.NpcFlags & (uint)Game.Npc.NpcFlags.SpiritGuide) == 0 && player.Map is { } map
            && map.Combat.Hooks.CanAttack(player, creature) && (!requireHostile || map.Combat.Hooks.IsHostileTo(player, creature));

    /// <summary>A unit attacking the bot that it may hit back (the walk to a battlemaster, a carrier that is blocked).</summary>
    private static Unit? FirstAttacker(Player player)
        => player.Combat.Attackers.FirstOrDefault(u => u.IsAlive && u.IsInWorld && ReferenceEquals(u.Map, player.Map)
            && player.Map is { } map && map.Combat.Hooks.CanAttack(player, u));

    /// <summary>A carrier does not stop to fight; it only keeps hitting a target it already fights in melee reach.</summary>
    private Unit? FirstAttackerIfStill(Player player)
        => _target is { IsAlive: true } current && player.Combat.Attackers.Contains(current) && MapCombat.CanReachWithMeleeAutoAttack(player, current)
            ? current : null;

    private void Fight(Player player, Unit target, uint interval)
    {
        if (!ReferenceEquals(_target, target))
        {
            _target = target;
            _attacking = false;
            _route = null;
        }

        TargetEntry = target is Game.Creatures.Creature creature ? creature.Entry : 0;
        if (_spells.Update(player, target, interval)) return;
        float distance = Distance(player, target);
        PlayerbotFightPosition position = PlayerbotBrain.DecidePosition(_spells.PreferredRange(player), distance,
            player.Class == Class.Hunter, ReferenceEquals(target.Combat.Victim, player), idleTooLong: false,
            inMeleeReach: MapCombat.CanReachWithMeleeAutoAttack(player, target));
        switch (position)
        {
            case PlayerbotFightPosition.Hold:
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                return;
            case PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee:
                if (!WalkTo(player, Position(target), interval, toward: false)) _target = null;
                return;
        }

        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            _route = null;
            return;
        }

        // The server refuses a swing outside the auto-attack arc; a managed player turns to its victim as the client does.
        if (MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2)) > CombatConstants.NoFacingChecksDistance
            && !MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc))
        {
            PlayerbotMotion.Face(_session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X));
            return;
        }

        if (!_attacking || !ReferenceEquals(player.Combat.Victim, target))
            _attacking = Act(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(target.Guid.Value));
    }

    private void ClearTarget(Player player)
    {
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        _target = null;
        _attacking = false;
        _route = null;
    }

    private void StopAttacking(Player player)
    {
        if (player.Combat.Victim is not null) Act(WorldOpcode.CmsgAttackstop, [], budgeted: false);
        _attacking = false;
        _target = null;
        _route = null;
        TargetEntry = 0;
    }

    // --- objects, following, walking --------------------------------------------------------------------------------------

    /// <summary>Walk to <paramref name="go"/> and use it (CMSG_GAMEOBJ_USE) once in interaction range: a flag stand or a dropped flag.</summary>
    private void UseObject(Player player, GameObject go, uint interval)
    {
        TargetEntry = go.Entry;
        if (Distance(player, go) > InteractYards)
        {
            WalkTo(player, new Vector3(go.X, go.Y, go.Z), interval, toward: false);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _route = null;
        Act(WorldOpcode.CmsgGameobjUse, PlayerbotNavigation.GuidPayload(go.Guid.Value));
    }

    /// <summary>The nearest spawned object on the bot's map that <paramref name="match"/> accepts.</summary>
    private static GameObject? FindObject(Player player, Func<GameObject, bool> match)
    {
        if (player.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects) return null;
        GameObject? best = null;
        float nearest = float.MaxValue;
        foreach (GameObject go in objects.GameObjects)
        {
            if (!go.IsSpawned || !match(go)) continue;
            float distance = Distance(player, go);
            if (distance < nearest)
            {
                nearest = distance;
                best = go;
            }
        }

        return best;
    }

    private void Follow(Player player, Player leader, uint interval)
    {
        if (!ReferenceEquals(leader.Map, player.Map) || Distance(player, leader) <= 5f)
        {
            Hold(player);
            return;
        }

        Vector3 point = Party.PlayerbotParty.FollowPoint(Position(leader), leader.Orientation, _followAngle, _followDistance);
        if (!WalkTo(player, point, interval, toward: Distance(player, leader) > 60f)) WalkTo(player, Position(leader), interval, toward: true);
    }

    /// <summary>Stay within <see cref="GuardYards"/> of <paramref name="at"/>.</summary>
    private void Guard(Player player, Vector3 at, uint interval)
    {
        if (Vector3.Distance(Position(player), at) <= GuardYards) Hold(player);
        else WalkTo(player, at, interval, toward: true);
    }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this bot's plans.</summary>
    private TileWait _tileWait;

    private void Hold(Player player)
    {
        _route = null;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>Walk towards <paramref name="goal"/>, planning again when it moved more than 2 yards since the last plan.</summary>
    /// <param name="toward">A far goal: a partial route towards it is fine (<see cref="PlayerbotNavigation.TryPlanToward"/>).</param>
    private bool WalkTo(Player player, Vector3 goal, uint interval, bool toward)
    {
        if (Vector3.Distance(Position(player), goal) <= 1.5f)
        {
            Hold(player);
            return true;
        }

        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > 2f)
        {
            PlanOutcome planned = toward
                ? PlayerbotNavigation.PlanToward(player, goal, _options, ref _tileWait, _session.World.NowMs, out _route)
                : PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out _route);
            if (planned != PlanOutcome.Route || _route is null)
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                // A tile of the way is still being read: the goal is kept and planned to again next think, not reported unreachable.
                if (planned != PlanOutcome.Pending)
                    ReportObjectiveUnreachable(player, $"route to battleground objective at {goal} was refused");
                return planned == PlanOutcome.Pending;
            }

            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, Math.Max(1u, interval), _session.World.NowMs)) _route = null;
        return true;
    }

    /// <summary>
    /// The movement acknowledgements first (every tick), then one decision every <see cref="PlayerbotOptions.ThinkIntervalMs"/>
    /// (the brain's pace). False when this tick makes no decision.
    /// </summary>
    private bool Think(Player player, uint elapsedMs, out uint interval)
    {
        interval = 0;
        if (PlayerbotMovementControl.Update(_session, player)) return false;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return false;
        interval = _thinkElapsed;
        _thinkElapsed = 0;
        if (PlayerbotMotion.ConsumeLoop(player)) _route = null;
        return true;
    }

    private bool Casting(Player player)
        => _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting };

    private Player? Online(ObjectGuid guid) => guid.IsEmpty ? null : _session.World.FindOnlinePlayer(guid);

    /// <summary>
    /// Run one client opcode through its world handler. <paramref name="budgeted"/> actions draw on the shared per-tick budget like the
    /// brain's; the others are a client's own answers and never wait.
    /// </summary>
    private bool Act(WorldOpcode opcode, byte[] payload, bool budgeted = true)
        => Groups.PlayerbotGroupCoordinator.Act(_session, opcode, payload, budgeted);

    private static Vector3 Position(WorldObject o) => new(o.X, o.Y, o.Z);

    private static float Distance(WorldObject a, WorldObject b) => Vector3.Distance(Position(a), Position(b));
}
