using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A deterministic, world-thread autonomous player. The feature/host owns scheduling and
/// persistence; this controller owns only bounded ordinary gameplay decisions for one session.
/// </summary>
internal sealed class PlayerbotBrain(WorldSession session, PlayerbotOptions options,
    PlayerbotLocalPlanner? planner = null, CancellationToken lifetime = default)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private PlayerbotRoute? _route;
    private Creature? _target;
    private uint _thinkElapsed;
    private bool _attacking;
    private bool _lootOpened;
    private uint _restUntilMs;
    private int _roamDirection;
    private PlayerbotGoalKind _goal = PlayerbotGoalKind.Explore;
    private long _lootStartedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private bool _lootResponseReceived;
    private readonly PlayerbotQuestGoals _quests = new(session, options);
    private readonly PlayerbotTownGoals _town = new(session, options);
    private readonly PlayerbotWorldDestinations _destinations = new(session, options);
    private readonly PlayerbotCombatSpells _combatSpells = new(session);
    private readonly PlayerbotRecovery _recovery = new(session, options);
    private readonly CancellationTokenSource _planningStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<string?>? _planTask;
    private string? _modelChoice;
    private long _nextPlanMs;
    private bool _needsRest;
    private int _stopped;

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _planningStop.Cancel();
        _planningStop.Dispose();
    }

    internal PlayerbotGoalKind Goal => _goal;
    internal Creature? InspectionTarget => _target;
    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    internal void Update(uint elapsedMs)
    {
        if (!_options.Enabled || _stopped != 0 || _session.Player is not { } player)
            return;
        if (PlayerbotMovementControl.Update(_session, player)) return;
        if (!player.IsInWorld) return;

        if (!player.IsAlive)
        {
            _thinkElapsed += elapsedMs;
            if (_thinkElapsed >= _options.ThinkIntervalMs)
            {
                uint recoveryInterval = _thinkElapsed;
                _thinkElapsed = 0;
                _goal = PlayerbotGoalKind.Recover;
                _recovery.Update(player, recoveryInterval);
            }
            return;
        }
        _recovery.Reset();

        if (_restUntilMs != 0)
        {
            if (player.Health < player.MaxHealth && !player.Combat.IsInCombat
                && unchecked(_session.World.NowMs - _restUntilMs) > int.MaxValue)
                return;
            if (!_session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return;
            _restUntilMs = 0;
            _goal = PlayerbotGoalKind.Explore;
        }

        if (_lootActions.TryPeek(out var nextLoot))
        {
            if (_session.TryManagedAction(nextLoot.Opcode, nextLoot.Payload)) _lootActions.Dequeue();
            return;
        }

        _thinkElapsed = checked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs)
            return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;

        if (!player.IsAlive)
        {
            _goal = PlayerbotGoalKind.Recover;
            _recovery.Update(player, interval);
            return;
        }

        if (_lootOpened && _target is { } pendingLoot)
        {
            if (_lootResponseReceived || _session.World.Uptime.TotalMilliseconds - _lootStartedMs >= 10_000)
            {
                if (!_lootResponseReceived) _session.TryManagedAction(WorldOpcode.CmsgLootRelease,
                    PlayerbotNavigation.GuidPayload(pendingLoot.Guid.Value));
                _lootOpened = false;
                _lootResponseReceived = false;
                _target = null;
                _route = null;
                _attacking = false;
                Rest(player);
            }
            else _lootResponseReceived = LootManagedPackets(pendingLoot.Guid.Value);
            return;
        }

        uint completedReturn = _quests.CompletedReturnQuest(player);
        if (completedReturn != 0)
        {
            _goal = PlayerbotGoalKind.Quest;
            QuestId = completedReturn;
            // Leave ongoing spell execution to its ordinary completion. Travel does
            // not clear casts/cooldowns or manufacture an out-of-combat state.
            if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
            if (_attacking || player.Combat.Victim is not null)
            {
                if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                { _attacking = false; _target = null; _route = null; }
                return;
            }
            if (!player.Combat.IsInCombat && _quests.Update(player, interval))
            { TargetEntry = _quests.TargetEntry; return; }
            _target = null;
            _route = null;
            if (_destinations.Update(player, 0, interval, completedReturn)) TargetEntry = _destinations.TargetEntry;
            // Wait/retry at the real giver while combat or a normal interaction
            // refusal remains. A completed quest must not reopen opportunistic grind.
            return;
        }

        if (!player.Combat.IsInCombat)
        {
            if (_needsRest || (player.Health * 100UL < player.MaxHealth * 45UL && player.Inventory.GetItemCount(117) > 0))
            {
                _needsRest = player.Health < player.MaxHealth;
                if (_needsRest) { Rest(player); return; }
            }
            UpdateLocalPlan(player);
            // A returned ID is only a proposal; each controller rechecks the live gameplay rules.
            if (_modelChoice == "town" && _town.Update(player, interval))
            { _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            if (_quests.Update(player, interval))
            { _goal = _quests.Goal; QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            QuestId = _quests.QuestId;
            if (_town.Update(player, interval))
            { _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            if (_modelChoice == "explore") { _target = null; Explore(player, interval); return; }
        }

        if (_target is null || _target.Map != player.Map)
        {
            Creature? previousTarget = _target;
            _target = FindTarget(player, _quests.PreferredCreatureEntry);
            // Keep an exploration route across decisions; otherwise every thought changes
            // direction after only its first terrain step and the player never travels.
            if (_target is not null || previousTarget is not null) _route = null;
            _attacking = false;
            _lootOpened = false;
            if (_target is null)
            {
                if (_destinations.Update(player, _quests.PreferredCreatureEntry, interval))
                {
                    _route = null;
                    _goal = _quests.PreferredCreatureEntry != 0 ? PlayerbotGoalKind.Grind : PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    if (_destinations.QuestId != 0) QuestId = _destinations.QuestId;
                    return;
                }
                Explore(player, interval);
                return;
            }
            TargetEntry = _target.Entry;
        }
        Creature target = _target;

        if (player.Combat.Victim is Creature victim && !ReferenceEquals(victim, _target))
        { _target = target = victim; _route = null; _attacking = false; TargetEntry = victim.Entry; }

        if (target.IsAlive)
        {
            if (_combatSpells.Update(player, target, interval))
            {
                _goal = PlayerbotGoalKind.Combat;
                return;
            }
            if (Distance(player, target) > 4f)
            {
                if (_route is null && !PlayerbotNavigation.TryPlan(player,
                        new System.Numerics.Vector3(target.X, target.Y, target.Z), _options, out _route))
                {
                    _target = null;
                    return;
                }

                _goal = PlayerbotGoalKind.Grind;
                if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, interval, _session.World.NowMs))
                    _route = null;
                return;
            }

            if (!_attacking || player.Combat.Victim is null)
            {
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
                { PlayerbotMovementControl.Stop(_session, player); return; }
                _goal = PlayerbotGoalKind.Combat;
                _attacking = _session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(target.Guid.Value));
            }
            return;
        }

        _goal = PlayerbotGoalKind.Loot;
        _lootOpened = _session.TryManagedAction(WorldOpcode.CmsgLoot,
            PlayerbotNavigation.GuidPayload(target.Guid.Value));
        if (_lootOpened) _lootStartedMs = (long)_session.World.Uptime.TotalMilliseconds;
        return;
    }

    private void Rest(Player player)
    {
        if (player.Health >= player.MaxHealth || player.Combat.IsInCombat)
        { _needsRest = false; return; }

        Item? food = player.Inventory.AllItems.FirstOrDefault(item => item.Entry == 117 && item.Count > 0);
        if (food is null)
        { _needsRest = false; return; }

        _goal = PlayerbotGoalKind.Rest;
        _needsRest = true;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        { PlayerbotMovementControl.Stop(_session, player); return; }
        if (player.StandState != StandState.Sit)
        { _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u)); return; }
        if (!_session.TryManagedAction(WorldOpcode.CmsgUseItem,
            PlayerbotNavigation.UseItemPayload(food.BagSlot, food.Slot))) return;
        _restUntilMs = unchecked(_session.World.NowMs + 20_000);
        _needsRest = false;
    }

    private bool LootManagedPackets(ulong target)
    {
        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Opcode != WorldOpcode.SmsgLootResponse || packet.Payload.Length < 14)
                continue;

            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != target || reader.ReadByte() != 1)
                continue;
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
                byte slotType = reader.ReadByte();
                if (slotType == 0)
                    _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(target)));
        }
        return opened;
    }

    internal static Creature? FindTarget(Player player, uint preferredEntry = 0)
    {
        if (player.Map is not { } map)
            return null;

        return player.VisibleObjects
            .Select(guid => map.FindObject(guid))
            .OfType<Creature>()
            .Where(creature => creature.IsAlive && map.Combat.Hooks.CanAttack(player, creature))
            .Where(creature => creature.Level <= player.Level + 1)
            .Where(creature => preferredEntry != 0 ? creature.Entry == preferredEntry : creature.Template.CreatureType != 8)
            // Ordinary idle grinding must not initiate attacks on town/service NPCs.
            // Explicit quest objectives and the existing defensive-victim path keep
            // their normal authoritative combat checks.
            .Where(creature => preferredEntry != 0 || creature.Template.NpcFlags == 0)
            .Where(creature => float.IsFinite(creature.X) && float.IsFinite(creature.Y) && float.IsFinite(creature.Z))
            .OrderBy(creature => preferredEntry != 0 && creature.Entry == preferredEntry ? 0 : 1)
            .ThenBy(creature => Distance(player, creature))
            .FirstOrDefault();
    }

    private void UpdateLocalPlan(Player player)
    {
        if (planner is null || !_options.AllowLocalLlm || _planningStop.IsCancellationRequested) return;
        if (_planTask?.IsCompleted == true)
        {
            _modelChoice = _planTask.IsCompletedSuccessfully ? _planTask.Result : null;
            _planTask = null;
        }
        long now = (long)_session.World.Uptime.TotalMilliseconds;
        if (_planTask is not null || now < _nextPlanMs) return;
        _nextPlanMs = now + 5000;
        var candidates = new List<PlayerbotPlanCandidate>();
        if (_quests.HasCandidate(player)) candidates.Add(new("quest", PlayerbotGoalKind.Quest, _quests.TargetEntry, _quests.QuestId));
        if (_town.HasCandidate(player)) candidates.Add(new("town", PlayerbotGoalKind.Vendor, _town.TargetEntry, 0));
        if (FindTarget(player, _quests.PreferredCreatureEntry) is { } target) candidates.Add(new("grind", PlayerbotGoalKind.Grind, target.Entry, QuestId));
        candidates.Add(new("explore", PlayerbotGoalKind.Explore, 0, 0));
        var facts = new PlayerbotPlannerFacts(player.Level, player.Health, player.MaxHealth, player.Combat.IsInCombat,
            player.MapId, (uint)(_session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0),
            player.Inventory.GetItemCount(117));
        CancellationToken token = _planningStop.Token;
        _planTask = Task.Run(() => planner.SelectAsync(facts, candidates, token), token);
    }

    private void Explore(Player player, uint elapsedMs)
    {
        if (_route is { Complete: false })
        {
            _goal = PlayerbotGoalKind.Explore;
            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, _session.World.NowMs))
                _route = null;
            return;
        }

        if (player.Map is null)
            return;
        float angle = (_roamDirection++ + player.Guid.Low) * 2.39996323f;
        var destination = new System.Numerics.Vector3(
            player.X + (MathF.Cos(angle) * 48f),
            player.Y + (MathF.Sin(angle) * 48f),
            player.Z);
        if (PlayerbotNavigation.TryPlan(player, destination, _options, out _route))
        {
            _goal = PlayerbotGoalKind.Explore;
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, _session.World.NowMs))
                _route = null;
        }
    }

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
}
