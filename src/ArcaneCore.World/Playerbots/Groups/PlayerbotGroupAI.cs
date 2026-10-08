using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// Drives one managed bot while it is a member of a bot-led group (<see cref="PlayerbotGroupCoordinator"/>), instead of
/// <see cref="PlayerbotBrain"/>. World thread only; one per member.
/// <para>
/// The leader takes the group where its goal is: it holds until the members have gathered, walks to the meeting point (the
/// objective's spawn, or the instance entrance) without leaving anyone more than <see cref="WaitForMembersYards"/> behind, walks
/// into the entrance (the members follow it through the same trigger, into the group's instance) and clears towards the objective:
/// whatever hostile creature stands within <see cref="ClearYards"/> of its way is pulled first. The members follow it at 2-5 yards
/// (the party bots' follow, vmangos PartyBotAI MoveFollow) and fight by role: the tank takes whatever attacks someone else (its
/// rotation taunts what turned away from it), the healer heals (its rotation's group heals) and fights when nobody needs healing, the
/// damage dealers assist the leader's target. Out of combat a member eats and drinks (the leader waits for it), resurrects a dead
/// member when its class can, and loots the corpses whose round-robin turn is its own. A dead member waits for a resurrection while a
/// living member could give one, then releases and runs back (<see cref="PlayerbotRecovery"/>, the ghost through the entrance when it
/// died inside). A wiping group retreats (<see cref="PlayerbotRetreat"/>) and regroups.
/// </para>
/// </summary>
internal sealed class PlayerbotGroupAI
{
    /// <summary>The leader waits while a living member on its map is farther than this.</summary>
    internal const float WaitForMembersYards = 30f;

    /// <summary>A hostile creature this close to the leader's way is cleared before the leader walks on.</summary>
    internal const float ClearYards = 20f;

    /// <summary>A member assists another member's attacker within this distance (vmangos PartyBotAI SelectPartyAttackTarget).</summary>
    internal const float AssistYards = 50f;

    /// <summary>A healer walks to a dead member to resurrect it from this far (the resurrection spells' 30 yards, less a margin).</summary>
    internal const float ResurrectYards = 25f;

    /// <summary>Round-robin loot the bot holds is taken from corpses within this distance (the group reward distance, vmangos 74).</summary>
    internal const float LootYards = 40f;

    /// <summary>Health or mana below this percentage: eat or drink between fights; the leader does not pull meanwhile.</summary>
    internal const uint RestBelowPct = 50;

    /// <summary>The player resurrection spells (vmangos IsResurrectionSpell; classic names, any rank).</summary>
    internal static readonly string[] ResurrectionSpells = ["Resurrection", "Redemption", "Ancestral Spirit"];

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly PlayerbotRecovery _recovery;
    private readonly PlayerbotRisk _risk;
    private readonly Random _random;
    private PlayerbotRoute? _route;
    private Vector3 _routeGoal;
    private Creature? _target;
    private bool _attacking;
    private uint _thinkElapsed;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private long _deadSinceMs = -1;
    private bool _deathRetired;
    private ObjectGuid _lootOpened;
    private long _lootOpenedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private readonly Dictionary<ObjectGuid, int> _lootTries = [];
    private readonly float _followAngle;
    private readonly float _followDistance;
    private bool _retreatStarted;
    private long _resurrectAtMs;

    internal PlayerbotGroupAI(WorldSession session, PlayerbotOptions options)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _spells = new PlayerbotCombatSpells(session);
        _recovery = new PlayerbotRecovery(session, options);
        _risk = new PlayerbotRisk(session, options, _spells);
        _recovery.Hazards = _risk.HazardThreats;
        _random = new Random(session.AccountId);
        _followAngle = (float)(_random.NextDouble() * MathF.Tau);
        _followDistance = 2f + ((float)_random.NextDouble() * 2.5f);
    }

    /// <summary>What the bot is doing (persisted and reported as its goal).</summary>
    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Group;

    internal uint TargetEntry { get; private set; }

    /// <summary>The fight the bot is in (inspection and tests).</summary>
    internal Creature? Target => _target;

    /// <summary>The bot's retreat (a wiping group).</summary>
    internal PlayerbotRetreat Retreat => _risk.Retreat;

    /// <summary>The bot's spells (its group role is set here).</summary>
    internal PlayerbotCombatSpells Spells => _spells;

    /// <summary>The bot's corpse run (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;

    /// <summary>Resting (eating or drinking) now.</summary>
    internal bool Resting => _restUntilMs != 0;

    /// <summary>The last resurrection cast (inspection and tests: the dead member's name).</summary>
    internal string? LastResurrection { get; private set; }

    /// <summary>How long a dead member waits for a resurrection before it releases (a test seam, never configuration-bound).</summary>
    internal long DeadWaitMs { get; set; } = PlayerbotGroupCoordinator.DeadWaitMs;

    private long Now => (long)_session.World.Uptime.TotalMilliseconds;

    /// <summary>Drop every route, target and rest (the group let the bot go, or it changed its state).</summary>
    internal void Release(Player player)
    {
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        if (_restUntilMs != 0) Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u), budgeted: false);
        _route = null;
        _target = null;
        _attacking = false;
        _restUntilMs = 0;
        _lootActions.Clear();
        _lootOpened = ObjectGuid.Empty;
        _spells.GroupRole = null;
        _spells.Reset();
        _recovery.Reset();
        if (_risk.Retreat.Active) _risk.Retreat.Stop("released");
    }

    /// <summary>One tick of a member (world thread, inside the shared action budget like the brain).</summary>
    internal void Update(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member me, uint elapsedMs)
    {
        if (!player.IsInWorld)
        {
            PlayerbotMovementControl.Update(_session, player);
            return;
        }

        _spells.GroupRole = PlayerbotGroupContent.CombatRole(me.Role, me.TalentRole);
        bool dead = !player.IsAlive;
        if (dead && !_deathRetired)
        {
            _risk.OnDeath(player);
            _target = null;
            _attacking = false;
            _route = null;
            _restUntilMs = 0;
            _lootActions.Clear();
            _spells.Reset();
            _deathRetired = true;
        }

        if (PlayerbotMovementControl.Update(_session, player)) return;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;
        if (PlayerbotMotion.ConsumeLoop(player)) _route = null;

        if (dead)
        {
            UpdateDead(player, group, interval);
            return;
        }

        _deathRetired = false;
        _deadSinceMs = -1;
        _recovery.Reset();
        _risk.Track(player);
        PlayerbotNavigation.Guard(player, null); // a group goes where its leader goes; the leader chose the way

        // A wiping group gets out: back the way it came, past the creatures' leash (the solo retreat).
        if (_risk.UpdateRetreat(player, interval))
        {
            Goal = PlayerbotGoalKind.Retreat;
            return;
        }

        if (group.State == PlayerbotGroupState.Wiped)
        {
            if (!_retreatStarted && player.Combat.IsInCombat && Enemies(player) is { Count: > 0 } enemies)
            {
                _retreatStarted = true;
                StopAttacking(player);
                _risk.StartRetreat(player, enemies, "group-wipe");
                Goal = PlayerbotGoalKind.Retreat;
                _risk.UpdateRetreat(player, interval);
                return;
            }
        }
        else _retreatStarted = false;

        if (_lootActions.TryPeek(out var next))
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (Act(next.Opcode, next.Payload)) _lootActions.Dequeue();
            return;
        }

        if (_restUntilMs != 0 && ContinueRest(player)) return;

        Player? leader = group.LeaderPlayer(_session.World);
        bool isLeader = leader is not null && leader.Guid == player.Guid;
        Creature? target = group.State is PlayerbotGroupState.Wiped or PlayerbotGroupState.Forming ? null
            : SelectTarget(player, group, me, leader, isLeader);
        if (target is not null)
        {
            Fight(player, target, interval);
            return;
        }

        if (_target is not null)
        {
            if (_attacking) StopAttacking(player);
            _target = null;
            _attacking = false;
            _route = null;
        }

        TargetEntry = 0;
        if (!player.Combat.IsInCombat)
        {
            if (FinishLoot(player)) return;
            if (Resurrect(player, group)) return;
            if (LootHeld(player, interval)) return;
            if (_spells.UpdateOutOfCombat(player, interval)) { Goal = PlayerbotGoalKind.Group; return; }
            if (Rest(player)) return;
        }

        if (isLeader) Lead(player, group, interval);
        else Follow(player, group, leader, interval);
    }

    // --- leading --------------------------------------------------------------------------------------------------------

    private void Lead(Player player, PlayerbotGroupCoordinator.BotGroup group, uint interval)
    {
        Goal = PlayerbotGoalKind.Group;
        switch (group.State)
        {
            case PlayerbotGroupState.Forming:
            case PlayerbotGroupState.Gathering:
            case PlayerbotGroupState.Regrouping:
            case PlayerbotGroupState.Wiped:
            case PlayerbotGroupState.Disbanding:
                Hold(player);
                return;
            case PlayerbotGroupState.Travelling:
                if (player.MapId != group.Goal.MeetingMap) { Hold(player); return; }
                if (MembersBehind(player, group) || group.AnyResting(_session.World)) { Hold(player); return; }
                PlayerbotNavigation.Guard(player, _options.Risk.Enabled ? _risk : null); // the leader keeps out of known hazards
                WalkTo(player, TravelPoint(group), interval, toward: true);
                return;
            case PlayerbotGroupState.Entering:
                if (player.MapId == group.Goal.MapId) { Hold(player); return; }
                if (MembersBehind(player, group)) { Hold(player); return; }
                WalkTo(player, group.Goal.Meeting, interval, toward: true);
                return;
            case PlayerbotGroupState.Engaging:
                if (MembersBehind(player, group) || group.AnyResting(_session.World) || !Ready(player)) { Hold(player); return; }
                WalkTo(player, ObjectivePoint(player, group), interval, toward: true);
                return;
            case PlayerbotGroupState.Leaving:
                if (PlayerbotGroupCoordinator.ExitOf(_session.World, player.MapId) is { } exit)
                    WalkTo(player, new Vector3(exit.X, exit.Y, exit.Z), interval, toward: true);
                else Hold(player);
                return;
        }
    }

    /// <summary>Where the leader walks to before the content: the objective's spawn (stopping in reach of it) or the instance entrance.</summary>
    private static Vector3 TravelPoint(PlayerbotGroupCoordinator.BotGroup group) => group.Approach;

    /// <summary>Where the leader walks while engaging: the nearest visible objective creature, else the objective's spawn.</summary>
    private Vector3 ObjectivePoint(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        if (FindObjectiveCreature(player, group) is { } creature) return new Vector3(creature.X, creature.Y, creature.Z);
        return group.Goal.Objective;
    }

    private static Creature? FindObjectiveCreature(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        if (player.Map is not { } map || group.Goal.ObjectiveEntry == 0) return null;
        Creature? best = null;
        float nearest = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature { IsAlive: true } creature || creature.Entry != group.Goal.ObjectiveEntry) continue;
            if (!map.Combat.Hooks.CanAttack(player, creature)) continue;
            float distance = Distance(player, creature);
            if (distance < nearest) { nearest = distance; best = creature; }
        }

        return best;
    }

    /// <summary>A living member on the leader's map lags more than <see cref="WaitForMembersYards"/> behind.</summary>
    private bool MembersBehind(Player leader, PlayerbotGroupCoordinator.BotGroup group)
    {
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            if (member.Guid == leader.Guid || !member.Joined || _session.World.FindOnlinePlayer(member.Guid) is not { IsAlive: true } other) continue;
            if (!ReferenceEquals(other.Map, leader.Map)) continue; // between maps or still outside: the state machine waits for them
            if (Distance(leader, other) > WaitForMembersYards) return true;
        }

        return false;
    }

    /// <summary>The leader is fit to pull: health and mana at <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> or more (it rests otherwise).</summary>
    private bool Ready(Player player) => !_options.Risk.Enabled || _risk.Recovered(player) || !CanRest(player);

    // --- following ------------------------------------------------------------------------------------------------------

    private void Follow(Player player, PlayerbotGroupCoordinator.BotGroup group, Player? leader, uint interval)
    {
        Goal = PlayerbotGoalKind.Follow;
        // Into the instance after the leader: walk into the entrance it took (its trigger brings the member into the group's instance).
        if (group.Goal.InInstance && player.MapId == group.Goal.MeetingMap
            && (leader is null || leader.MapId == group.Goal.MapId || !ReferenceEquals(leader.Map, player.Map))
            && group.State is PlayerbotGroupState.Entering or PlayerbotGroupState.Engaging or PlayerbotGroupState.Regrouping)
        {
            Goal = PlayerbotGoalKind.Group;
            WalkTo(player, group.Goal.Meeting, interval, toward: true);
            return;
        }

        // Out after the leader: walk to the instance's exit.
        if (group.State == PlayerbotGroupState.Leaving && player.MapId == group.Goal.MapId
            && (leader is null || !ReferenceEquals(leader.Map, player.Map))
            && PlayerbotGroupCoordinator.ExitOf(_session.World, player.MapId) is { } exit)
        {
            Goal = PlayerbotGoalKind.Group;
            WalkTo(player, new Vector3(exit.X, exit.Y, exit.Z), interval, toward: true);
            return;
        }

        if (leader is null || !leader.IsInWorld || !ReferenceEquals(leader.Map, player.Map))
        {
            Hold(player);
            return;
        }

        float distance = Distance(player, leader);
        if (distance <= 5f)
        {
            Hold(player);
            return;
        }

        Vector3 point = Party.PlayerbotParty.FollowPoint(new Vector3(leader.X, leader.Y, leader.Z), leader.Orientation, _followAngle, _followDistance);
        if (!WalkTo(player, point, interval, toward: distance > 60f)) WalkTo(player, new Vector3(leader.X, leader.Y, leader.Z), interval, toward: true);
    }

    // --- fighting -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What this member fights now, by role. Everyone first answers a creature attacking a member (the tank especially one that
    /// attacks someone else: its rotation taunts it), then assists the leader's victim; the leader also pulls the objective and what
    /// stands in the way while engaging.
    /// </summary>
    private Creature? SelectTarget(Player player, PlayerbotGroupCoordinator.BotGroup group, PlayerbotGroupCoordinator.Member me,
        Player? leader, bool isLeader)
    {
        if (player.Map is not { } map) return null;
        Creature? current = _target is not null && IsValidTarget(player, _target) ? _target : null;
        var loose = new List<(Creature Creature, float Distance, bool OnSelf)>();
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            Player? other = member.Guid == player.Guid ? player : _session.World.FindOnlinePlayer(member.Guid);
            if (other is null || !ReferenceEquals(other.Map, map)) continue;
            foreach (Unit unit in other.Combat.Attackers)
                if (unit is Creature attacker && IsValidTarget(player, attacker) && Distance(player, attacker) <= AssistYards)
                    loose.Add((attacker, Distance(player, attacker), ReferenceEquals(other, player)));
        }

        if (me.Role == PlayerbotGroupRole.Tank)
        {
            // Peel: a creature on someone else first (the nearest), then the one being fought, then whatever attacks the tank.
            Creature? peel = loose.Where(l => !l.OnSelf && !ReferenceEquals(l.Creature.Combat.Victim, player))
                .OrderBy(l => l.Distance).Select(l => l.Creature).FirstOrDefault();
            if (peel is not null) return peel;
            if (current is not null && player.Combat.IsInCombat) return current;
            if (loose.Count > 0) return loose.OrderBy(l => l.Distance).First().Creature;
        }
        else
        {
            if (current is not null && player.Combat.IsInCombat && (!isLeader || current.Combat.IsInCombat)) return current;
            if (!isLeader && leader is not null && ReferenceEquals(leader.Map, map) && leader.Combat.Victim is Creature victim
                && IsValidTarget(player, victim) && Distance(player, victim) <= AssistYards)
                return victim;
            if (loose.Count > 0) return loose.OrderBy(l => l.OnSelf ? 0 : 1).ThenBy(l => l.Distance).First().Creature;
        }

        // The leader pulls: the objective, or what stands in its way.
        if (isLeader && group.State == PlayerbotGroupState.Engaging && !MembersBehind(player, group) && !group.AnyResting(_session.World)
            && Ready(player))
        {
            if (current is not null) return current;
            if (FindObjectiveCreature(player, group) is { } objective && Distance(player, objective) <= 60f)
            {
                group.NoteObjective(objective);
                return objective;
            }

            return FindObstacle(player, group);
        }

        return null;
    }

    /// <summary>The nearest hostile creature within <see cref="ClearYards"/> of the leader that would attack the group (the way is cleared).</summary>
    private Creature? FindObstacle(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        if (player.Map is not { } map) return null;
        Creature? best = null;
        float nearest = ClearYards;
        foreach (PlayerbotThreat threat in PlayerbotRecovery.Threats(player))
        {
            if (threat.Source is not { IsAlive: true } creature || !IsValidTarget(player, creature)) continue;
            if (PlayerbotBrain.IsSomeoneElses(player, creature, Groups())) continue;
            float distance = Distance(player, creature);
            if (distance <= nearest)
            {
                nearest = distance;
                best = creature;
            }
        }

        return best;
    }

    private void Fight(Player player, Creature target, uint interval)
    {
        if (!ReferenceEquals(_target, target))
        {
            _target = target;
            _attacking = false;
            _route = null;
        }

        Goal = PlayerbotGoalKind.Assist;
        TargetEntry = target.Entry;
        if (_restUntilMs != 0) { _restUntilMs = 0; Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u), budgeted: false); }
        if (_spells.Update(player, target, interval)) return;
        float distance = Distance(player, target);
        PlayerbotFightPosition position = PlayerbotBrain.DecidePosition(_spells.PreferredRange(player), distance,
            player.Class == Class.Hunter, ReferenceEquals(target.Combat.Victim, player), idleTooLong: false);
        switch (position)
        {
            case PlayerbotFightPosition.Hold:
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                return;
            case PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee:
                if (!WalkTo(player, new Vector3(target.X, target.Y, target.Z), interval, toward: false)) _target = null;
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

    private void StopAttacking(Player player)
    {
        if (player.Combat.Victim is not null) Act(WorldOpcode.CmsgAttackstop, [], budgeted: false);
        _attacking = false;
        _target = null;
        _route = null;
        TargetEntry = 0;
    }

    private static List<Creature> Enemies(Player player)
    {
        var enemies = new List<Creature>();
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            if (unit is Creature creature && creature.IsAlive && ReferenceEquals(creature.Map, player.Map) && !enemies.Contains(creature))
                enemies.Add(creature);
        return enemies;
    }

    /// <summary>vmangos IsValidHostileTarget: in the world, alive, on the bot's map and attackable by it.</summary>
    private static bool IsValidTarget(Player player, Creature target)
        => target.IsInWorld && target.IsAlive && player.Map is { } map && ReferenceEquals(target.Map, map) && !target.IsInEvadeMode
            && float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z) && map.Combat.Hooks.CanAttack(player, target);

    // --- between fights -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A healer resurrects a dead member it can see (the member's body, not yet released) through the ordinary cast; the member's
    /// party intake accepts a group member's offer (CMSG_RESURRECT_RESPONSE). True while it walks there or casts.
    /// </summary>
    private bool Resurrect(Player player, PlayerbotGroupCoordinator.BotGroup group)
    {
        if (player.Map is not { } map || Now < _resurrectAtMs || ResurrectionSpell(player) is not { } spell) return false;
        foreach (PlayerbotGroupCoordinator.Member member in group.Members)
        {
            if (member.Guid == player.Guid || _session.World.FindOnlinePlayer(member.Guid) is not { } dead) continue;
            if (dead.IsAlive || (dead.Flags & PlayerFlags.Ghost) != 0 || !ReferenceEquals(dead.Map, map)) continue;
            if (ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(dead)) continue;
            Goal = PlayerbotGoalKind.Group;
            if (Distance(player, dead) > ResurrectYards)
            {
                WalkTo(player, new Vector3(dead.X, dead.Y, dead.Z), _options.ThinkIntervalMs == 0 ? 1u : (uint)_options.ThinkIntervalMs, toward: false);
                return true;
            }

            if (!PlayerbotMovementControl.Stop(_session, player)) return true;
            _route = null;
            if (_spells.CastAt(player, spell, dead)) LastResurrection = dead.Name;
            _resurrectAtMs = Now + 3_000;
            return true;
        }

        return false;
    }

    /// <summary>The resurrection spell the bot knows (its highest rank), or null.</summary>
    internal SpellInfo? ResurrectionSpell(Player player)
    {
        if (!PlayerbotGroupContent.CanHeal(player.Class) || _session.Services.GetService<SpellFeature>() is not { } feature) return null;
        SpellInfo? best = null;
        foreach (uint id in feature.Spellbook.GetSpells(player))
            if (feature.System.Store.Get(id) is { } spell && ResurrectionSpells.Contains(spell.Name, StringComparer.Ordinal)
                && (best is null || spell.Id > best.Id))
                best = spell;
        return best;
    }

    /// <summary>The nearest corpse whose round-robin loot this bot holds: walk to it and open it.</summary>
    private bool LootHeld(Player player, uint interval)
    {
        if (player.Map is not { } map || _session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot is not { } loot) return false;
        Creature? nearest = null;
        float best = LootYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature { IsAlive: false } corpse) continue;
            if (loot.FindLoot(guid) is not { } bag || bag.Owner != player.Guid || bag.IsClosed || bag.IsEmpty) continue;
            if (_lootTries.TryGetValue(guid, out int tries) && tries >= 3) continue;
            float distance = Distance(player, corpse);
            if (distance <= best) { best = distance; nearest = corpse; }
        }

        if (nearest is null) return false;
        Goal = PlayerbotGoalKind.Loot;
        if (best > 4f)
        {
            if (!WalkTo(player, new Vector3(nearest.X, nearest.Y, nearest.Z), interval, toward: false)) CountLootTry(nearest.Guid);
            return true;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        _route = null;
        _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse);
        if (!Act(WorldOpcode.CmsgLoot, PlayerbotNavigation.GuidPayload(nearest.Guid.Value))) return true;
        _lootOpened = nearest.Guid;
        _lootOpenedMs = Now;
        CountLootTry(nearest.Guid);
        return true;
    }

    /// <summary>After CMSG_LOOT: take every item and the money from the loot window, then release it (the brain's looting).</summary>
    private bool FinishLoot(Player player)
    {
        if (_lootOpened.IsEmpty) return false;
        ObjectGuid source = _lootOpened;
        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Payload.Length < 14) continue;
            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != source.Value || reader.ReadByte() != 1) continue;
            opened = true;
            _ = reader.ReadUInt32();
            byte count = reader.ReadByte();
            for (int index = 0; index < count && reader.Remaining >= 22; index++)
            {
                byte slot = reader.ReadByte();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadInt32();
                if (reader.ReadByte() == 0) _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(source.Value)));
        }

        if (!opened && Now - _lootOpenedMs < 5_000) return true;
        if (!opened) Act(WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(source.Value), budgeted: false);
        _lootOpened = ObjectGuid.Empty;
        return opened;
    }

    private void CountLootTry(ObjectGuid corpse)
    {
        if (_lootTries.Count >= 64) _lootTries.Clear();
        _lootTries[corpse] = _lootTries.GetValueOrDefault(corpse) + 1;
    }

    private bool CanRest(Player player)
        => _session.Services.GetService<SpellFeature>() is { } spells
            && PlayerbotConsumables.TryFindRecovery(player, spells.System, RestBelowPct, RestBelowPct, out _);

    /// <summary>Eat or drink below <see cref="RestBelowPct"/> out of combat (PlayerbotConsumables); the leader waits meanwhile.</summary>
    private bool Rest(Player player)
    {
        SpellFeature? spells = _session.Services.GetService<SpellFeature>();
        if (spells is null || player.Combat.IsInCombat
            || spells.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            || PlayerbotConsumables.HasActiveFoodDrink(player, spells.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spells.System, RestBelowPct, RestBelowPct, out PlayerbotConsumable consumable)) return false;
        Goal = PlayerbotGoalKind.Rest;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return true;
        }

        if (player.StandState != StandState.Sit)
        {
            Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u));
            return true;
        }

        Item item = consumable.Item;
        if (!Act(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot))) return true;
        _restKind = consumable.Kind;
        _restUntilMs = Math.Max(1u, unchecked(_session.World.NowMs + 20_000));
        return true;
    }

    private bool ContinueRest(Player player)
    {
        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        SpellFeature? spells = _session.Services.GetService<SpellFeature>();
        bool eating = spells is not null && PlayerbotConsumables.HasActiveFoodDrink(player, spells.System);
        if (!player.Combat.IsInCombat && eating && PlayerbotConsumables.NeedsRecovery(player, _restKind)
            && unchecked((int)(_restUntilMs - _session.World.NowMs)) > 0)
        {
            Goal = PlayerbotGoalKind.Rest;
            return true;
        }

        if (!Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return true;
        _restUntilMs = 0;
        return false;
    }

    // --- death ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Dead: wait (unreleased) for a living member's resurrection while one could give it and the group is not wiping, at most
    /// <see cref="DeadWaitMs"/>; then release and run back (<see cref="PlayerbotRecovery"/>: to the body, or through the instance
    /// entrance when it died inside).
    /// </summary>
    private void UpdateDead(Player player, PlayerbotGroupCoordinator.BotGroup group, uint interval)
    {
        Goal = PlayerbotGoalKind.Recover;
        long now = Now;
        if (_deadSinceMs < 0) _deadSinceMs = now;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (!ghost && now - _deadSinceMs < DeadWaitMs && group.State != PlayerbotGroupState.Wiped && group.CanResurrect(_session.World, player))
        {
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        _risk.ScanHazards(player);
        _recovery.Update(player, interval);
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    private void Hold(Player player)
    {
        _route = null;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>Walk towards <paramref name="goal"/>, planning again when it moved more than 2 yards since the last plan.</summary>
    /// <param name="toward">A far goal: a partial route towards it is fine (<see cref="PlayerbotNavigation.TryPlanToward"/>).</param>
    private bool WalkTo(Player player, Vector3 goal, uint interval, bool toward)
    {
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), goal) <= 1.5f)
        {
            Hold(player);
            return true;
        }

        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > 2f)
        {
            bool planned = toward ? PlayerbotNavigation.TryPlanToward(player, goal, _options, out _route)
                : PlayerbotNavigation.TryPlan(player, goal, _options, out _route);
            if (!planned || _route is null)
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                return false;
            }

            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, Math.Max(1u, interval), _session.World.NowMs)) _route = null;
        return true;
    }

    private GroupManager? Groups()
    {
        try
        {
            return _session.Services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Run one client opcode through its world handler. <paramref name="budgeted"/> actions draw on the shared per-tick budget like
    /// the brain's; the others are a client's own answers and never wait.
    /// </summary>
    private bool Act(WorldOpcode opcode, byte[] payload, bool budgeted = true)
        => PlayerbotGroupCoordinator.Act(_session, opcode, payload, budgeted);

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
