using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>One loaded or created smart event with its run state (AzerothCore SmartScriptHolder: timer, active, runOnce).</summary>
public sealed class SmartHolder(SmartScriptRow row)
{
    public SmartScriptRow Row { get; } = row;
    public uint TimerMs { get; internal set; }
    public bool Active { get; internal set; }
    public bool RunOnce { get; internal set; }
    internal SmartEvent Event => (SmartEvent)Row.EventType;
    internal SmartAction Action => (SmartAction)Row.ActionType;
    internal SmartEventFlags Flags => (SmartEventFlags)Row.EventFlags;
}

/// <summary>
/// A creature's smart script (AzerothCore SmartScript.cpp: OnReset, OnUpdate, UpdateTimer, InitTimer, RecalcTimer, ProcessEvent, ProcessAction,
/// GetTargets, SetPhase/IncPhase/DecPhase/IsInPhase). Events, actions and targets outside slice 1 are reported in <see cref="Unsupported"/> and
/// their rows never run. Randomness comes from the map system, so a seeded test is deterministic.
/// </summary>
public sealed class SmartScript
{
    /// <summary>SMART_EVENT_PHASE_12, the highest phase (SmartScriptMgr.h:57).</summary>
    public const uint MaxPhase = 12;

    private readonly CreatureSmartAI _ai;
    private readonly List<SmartHolder> _events = [];
    private readonly List<SmartHolder> _stored = [];
    private readonly List<string> _unsupported = [];

    internal SmartScript(CreatureSmartAI ai, IReadOnlyList<SmartScriptRow> rows)
    {
        _ai = ai;
        foreach (SmartScriptRow row in rows)
        {
            if (((SmartEventFlags)row.EventFlags & SmartEventFlags.DebugOnly) != 0) continue;
            if (!Enum.IsDefined((SmartEvent)row.EventType)) { _unsupported.Add($"event {row.EventType} (row {row.Id})"); continue; }
            if (!Enum.IsDefined((SmartAction)row.ActionType)) { _unsupported.Add($"action {row.ActionType} (row {row.Id})"); continue; }
            if (!Enum.IsDefined((SmartTarget)row.TargetType)) { _unsupported.Add($"target {row.TargetType} (row {row.Id})"); continue; }
            _events.Add(new SmartHolder(row));
        }

        foreach (SmartHolder holder in _events) InitTimer(holder);
    }

    private Creature Me => _ai.Me;
    private CreatureMapSystem? System => _ai.Host;

    public uint Phase { get; private set; }
    public IReadOnlyList<SmartHolder> Events => _events;
    public IReadOnlyList<SmartHolder> StoredEvents => _stored;
    public IReadOnlyList<string> Unsupported => _unsupported;

    /// <summary>The unit that caused the last action (AzerothCore mLastInvoker).</summary>
    public Unit? LastInvoker { get; private set; }

    private uint Rand(uint min, uint max) => max <= min ? min : (uint)(System?.RandomInt((int)min, (int)max) ?? (int)min);

    // ---- phases ----

    /// <summary>AzerothCore IsInPhase: phase 0 matches no mask; a nonzero mask needs bit (phase - 1).</summary>
    public bool IsInPhase(uint mask) => Phase != 0 && ((1u << (int)(Phase - 1)) & mask) != 0;

    public void SetPhase(uint phase) => Phase = Math.Min(phase, MaxPhase);

    // ---- timers ----

    private void RecalcTimer(SmartHolder e, uint min, uint max)
    {
        e.TimerMs = Rand(min, max);
        e.Active = e.TimerMs == 0;
    }

    /// <summary>AzerothCore InitTimer: the update events start on their initial timer, every other event starts active.</summary>
    private void InitTimer(SmartHolder e)
    {
        if (e.Event is SmartEvent.Update or SmartEvent.UpdateInCombat or SmartEvent.UpdateOutOfCombat)
            RecalcTimer(e, e.Row.EventParam1, e.Row.EventParam2);
        else e.Active = true;
    }

    /// <summary>AzerothCore OnReset: phase 0, timers and run-once state back (unless DONT_RESET), then SMART_EVENT_RESET.</summary>
    public void OnReset()
    {
        SetPhase(0);
        foreach (SmartHolder e in _events)
        {
            if ((e.Flags & SmartEventFlags.DontReset) != 0) continue;
            InitTimer(e);
            e.RunOnce = false;
        }

        ProcessEventsFor(SmartEvent.Reset);
        LastInvoker = null;
    }

    public void OnUpdate(uint diffMs)
    {
        foreach (SmartHolder e in _events.ToArray()) UpdateTimer(e, diffMs);
        foreach (SmartHolder e in _stored.ToArray()) UpdateTimer(e, diffMs);
        _stored.RemoveAll(e => e.RunOnce && (e.Flags & SmartEventFlags.NotRepeatable) != 0);
    }

    /// <summary>AzerothCore UpdateTimer: phase and combat gates, the cast delay while casting, then the timed events run.</summary>
    private void UpdateTimer(SmartHolder e, uint diffMs)
    {
        if (e.Event == SmartEvent.Link) return;
        if (e.Row.EventPhaseMask != 0 && !IsInPhase(e.Row.EventPhaseMask)) return;
        bool engaged = Me.Combat.IsInCombat;
        if (e.Event == SmartEvent.UpdateInCombat && !engaged) return;
        if (e.Event == SmartEvent.UpdateOutOfCombat && engaged) return;
        if (e.TimerMs >= diffMs && e.TimerMs != 0)
        {
            e.TimerMs -= diffMs;
            return;
        }

        // A cast without INTERRUPT_PREVIOUS waits for the current cast (AzerothCore RaisePriority: tried again next update).
        if (e.Action == SmartAction.Cast && ((SmartCastFlags)e.Row.ActionParam2 & SmartCastFlags.InterruptPrevious) == 0
            && (System?.AiServices.Spells?.IsCasting(Me) ?? false))
        {
            e.TimerMs = 0;
            return;
        }

        e.Active = true;
        if (e.Event is SmartEvent.Update or SmartEvent.UpdateInCombat or SmartEvent.UpdateOutOfCombat or SmartEvent.HealthPct)
            ProcessEvent(e, null);
        else e.TimerMs = 0;
    }

    // ---- events ----

    public void ProcessEventsFor(SmartEvent type, Unit? invoker = null, uint var0 = 0, SpellInfo? spell = null)
    {
        foreach (SmartHolder e in _events.ToArray())
            if (e.Event == type) ProcessEvent(e, invoker, var0, spell);
        foreach (SmartHolder e in _stored.ToArray())
            if (e.Event == type) ProcessEvent(e, invoker, var0, spell);
    }

    private void ProcessEvent(SmartHolder e, Unit? invoker, uint var0 = 0, SpellInfo? spell = null)
    {
        if (!e.Active && e.Event != SmartEvent.Link) return;
        if (e.Row.EventPhaseMask != 0 && !IsInPhase(e.Row.EventPhaseMask)) return;
        if ((e.Flags & SmartEventFlags.NotRepeatable) != 0 && e.RunOnce) return;
        SmartScriptRow r = e.Row;
        switch (e.Event)
        {
            case SmartEvent.Link:
            case SmartEvent.Aggro:
            case SmartEvent.Death:
            case SmartEvent.Evade:
            case SmartEvent.ReachedHome:
            case SmartEvent.Reset:
                ProcessAction(e, invoker);
                break;
            case SmartEvent.Update:
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.UpdateOutOfCombat:
                if (Me.Combat.IsInCombat) return;
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.UpdateInCombat:
                if (!Me.Combat.IsInCombat) return;
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.HealthPct:
            {
                if (!Me.Combat.IsInCombat || Me.MaxHealth == 0) return;
                uint pct = (uint)(Me.Health * 100UL / Me.MaxHealth);
                if (pct > r.EventParam2 || pct < r.EventParam1) return;
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            }
            case SmartEvent.SpellHit:
                if (spell is null) return;
                if ((r.EventParam1 == 0 || spell.Id == r.EventParam1)
                    && (r.EventParam2 == 0 || ((1u << (int)spell.School) & r.EventParam2) != 0))
                {
                    RecalcTimer(e, r.EventParam3, r.EventParam4);
                    ProcessAction(e, invoker, spell);
                }

                break;
            case SmartEvent.TimedEventTriggered:
                if (r.EventParam1 == var0) ProcessAction(e, invoker);
                break;
        }
    }

    /// <summary>AzerothCore ProcessTimedAction (no conditions table in slice 1): the action, then the repeat timer.</summary>
    private void ProcessTimedAction(SmartHolder e, uint min, uint max, Unit? invoker)
    {
        ProcessAction(e, invoker);
        RecalcTimer(e, min, max);
    }

    // ---- actions ----

    /// <summary>AzerothCore ProcessAction: run-once marked, the chance rolled, the action on its targets, then the linked row.</summary>
    private void ProcessAction(SmartHolder e, Unit? invoker, SpellInfo? spell = null)
    {
        e.RunOnce = true;
        SmartScriptRow r = e.Row;
        if (r.EventChance is > 0 and < 100 && r.EventChance <= Rand(1, 100)) return;
        if (invoker is not null) LastInvoker = invoker;
        List<WorldObject> targets = GetTargets(r, invoker ?? LastInvoker);
        CreatureMapSystem? system = System;
        switch (e.Action)
        {
            case SmartAction.Talk:
                if (system is null) break;
                Unit? talkTarget = targets.OfType<Unit>().FirstOrDefault() ?? invoker;
                system.SayText(Me, (int)r.ActionParam1, talkTarget);
                break;
            case SmartAction.Cast:
                DoCast(r, targets);
                break;
            case SmartAction.SummonCreature:
                if (system is null) break;
                Unit? attack = r.ActionParam4 != 0 ? invoker : null;
                if (r.TargetType == (byte)SmartTarget.Position || targets.Count == 0)
                {
                    (float x, float y, float z, float o) = r.TargetType == (byte)SmartTarget.Position
                        ? (r.TargetX, r.TargetY, r.TargetZ, r.TargetO) : (Me.X, Me.Y, Me.Z, Me.Orientation);
                    system.SummonAt(Me, r.ActionParam1, x, y, z, o, attack, r.ActionParam3);
                }
                else
                {
                    foreach (WorldObject t in targets)
                        system.SummonAt(Me, r.ActionParam1, t.X + r.TargetX, t.Y + r.TargetY, t.Z + r.TargetZ, t.Orientation, attack, r.ActionParam3);
                }

                break;
            case SmartAction.SetEventPhase:
                SetPhase(r.ActionParam1);
                break;
            case SmartAction.IncEventPhase:
                if (r.ActionParam1 != 0) SetPhase(Math.Min(MaxPhase, Phase + r.ActionParam1));
                else if (r.ActionParam2 != 0) SetPhase(r.ActionParam2 >= Phase ? 0 : Phase - r.ActionParam2);
                break;
            case SmartAction.RandomPhase:
            {
                uint[] phases = [.. new[] { r.ActionParam1, r.ActionParam2, r.ActionParam3, r.ActionParam4, r.ActionParam5, r.ActionParam6 }.Where(p => p != 0)];
                if (phases.Length > 0) SetPhase(phases[Rand(0, (uint)phases.Length - 1)]);
                break;
            }
            case SmartAction.RandomPhaseRange:
                SetPhase(Rand(r.ActionParam1, r.ActionParam2));
                break;
            case SmartAction.CreateTimedEvent:
            {
                bool repeats = r.ActionParam4 != 0 || r.ActionParam5 != 0;
                var timed = new SmartHolder(new SmartScriptRow
                {
                    EntryOrGuid = r.EntryOrGuid, Id = (ushort)r.ActionParam1, EventType = (byte)SmartEvent.Update,
                    EventChance = (byte)(r.ActionParam6 == 0 ? 100 : r.ActionParam6),
                    EventFlags = repeats ? 0 : (uint)SmartEventFlags.NotRepeatable,
                    EventParam1 = r.ActionParam2, EventParam2 = r.ActionParam3, EventParam3 = r.ActionParam4, EventParam4 = r.ActionParam5,
                    ActionType = (byte)SmartAction.TriggerTimedEvent, ActionParam1 = r.ActionParam1,
                    TargetType = r.TargetType, TargetParam1 = r.TargetParam1, TargetParam2 = r.TargetParam2,
                    TargetParam3 = r.TargetParam3, TargetParam4 = r.TargetParam4,
                    TargetX = r.TargetX, TargetY = r.TargetY, TargetZ = r.TargetZ, TargetO = r.TargetO,
                });
                InitTimer(timed);
                _stored.Add(timed);
                break;
            }
            case SmartAction.TriggerTimedEvent:
                ProcessEventsFor(SmartEvent.TimedEventTriggered, null, r.ActionParam1);
                break;
            case SmartAction.RemoveTimedEvent:
                _stored.RemoveAll(s => s.Row.Id == r.ActionParam1);
                break;
            case SmartAction.MoveToPos:
            {
                (float x, float y, float z)? point = r.TargetType == (byte)SmartTarget.Position
                    ? (r.TargetX, r.TargetY, r.TargetZ)
                    : targets.FirstOrDefault() is { } t ? (t.X + r.TargetX, t.Y + r.TargetY, t.Z + r.TargetZ) : null;
                if (point is { } p) Me.Motion.MovePoint(r.ActionParam1, p.x, p.y, p.z, run: Me.Combat.IsInCombat);
                break;
            }
        }

        if (r.Link != 0 && r.Link != r.Id && _events.FirstOrDefault(l => l.Row.Id == r.Link) is { } linked)
        {
            if (linked.Event == SmartEvent.Link) ProcessEvent(linked, invoker);
            else _unsupported.Add($"row {r.Id} links to row {r.Link}, which is not a SMART_EVENT_LINK row");
        }
    }

    private void DoCast(SmartScriptRow r, List<WorldObject> targets)
    {
        if (System is not { } system) return;
        var flags = (SmartCastFlags)r.ActionParam2;
        if ((flags & SmartCastFlags.ThreatListNotSingle) != 0 && (!Me.Combat.HasThreatList || Me.Combat.Threat.Entries.Count <= 1)) return;
        bool triggered = (flags & SmartCastFlags.Triggered) != 0 || r.ActionParam3 != 0;
        foreach (Unit target in targets.OfType<Unit>())
        {
            if ((flags & SmartCastFlags.AuraNotPresent) != 0 && system.HasAura(target, r.ActionParam1)) continue;
            if ((flags & SmartCastFlags.InterruptPrevious) != 0) system.InterruptCast(Me);
            system.CastSpell(Me, r.ActionParam1, target, triggered);
        }
    }

    // ---- targets ----

    private static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>AzerothCore SmartScript::GetTargets for the slice-1 target types. Position (8) yields no object; its action reads target_x/y/z.</summary>
    public List<WorldObject> GetTargets(SmartScriptRow r, Unit? invoker)
    {
        var result = new List<WorldObject>();
        IReadOnlyList<ThreatEntry> threat = Me.Combat.HasThreatList ? Me.Combat.Threat.Entries : [];
        bool playerOnly = r.TargetParam2 != 0;
        float maxDist = r.TargetParam1;
        IEnumerable<Unit> Hostile(int skip) => threat.Skip(skip).Select(t => t.Target)
            .Where(u => u.IsAlive && (!playerOnly || u is Player) && (maxDist == 0 || Distance(Me, u) <= maxDist));
        CreatureMapSystem? system = System;
        switch ((SmartTarget)r.TargetType)
        {
            case SmartTarget.Self:
                result.Add(Me);
                break;
            case SmartTarget.Victim:
                if (Me.Combat.Victim is { } victim) result.Add(victim);
                break;
            case SmartTarget.HostileSecondAggro:
                if (Hostile(1).FirstOrDefault() is { } second) result.Add(second);
                break;
            case SmartTarget.HostileLastAggro:
                if (Hostile(0).LastOrDefault() is { } last) result.Add(last);
                break;
            case SmartTarget.HostileRandom:
            case SmartTarget.HostileRandomNotTop:
            {
                Unit[] pool = [.. Hostile(r.TargetType == (byte)SmartTarget.HostileRandom ? 0 : 1)];
                if (pool.Length > 0) result.Add(pool[Rand(0, (uint)pool.Length - 1)]);
                break;
            }
            case SmartTarget.ActionInvoker:
                if (invoker is not null) result.Add(invoker);
                break;
            case SmartTarget.ThreatList:
                result.AddRange(Hostile(0));
                break;
            case SmartTarget.CreatureRange:
            case SmartTarget.CreatureDistance:
            case SmartTarget.ClosestCreature:
            {
                if (system is null) break;
                bool range = r.TargetType == (byte)SmartTarget.CreatureRange;
                float min = range ? r.TargetParam2 : 0;
                float max = range ? r.TargetParam3 : r.TargetParam2;
                if (r.TargetType == (byte)SmartTarget.ClosestCreature && max == 0) max = 100;
                uint alive = r.TargetType switch { (byte)SmartTarget.CreatureRange => r.TargetParam4, (byte)SmartTarget.CreatureDistance => r.TargetParam3, _ => r.TargetParam3 != 0 ? 2u : 1u };
                Creature[] found = [.. system.Creatures.Where(c => !ReferenceEquals(c, Me) && c.IsInWorld
                    && (r.TargetParam1 == 0 || c.Entry == r.TargetParam1)
                    && (alive == 0 || (alive == 1) == c.IsAlive)
                    && Distance(Me, c) >= min && Distance(Me, c) <= max)
                    .OrderBy(c => Distance(Me, c))];
                if (r.TargetType == (byte)SmartTarget.ClosestCreature) { if (found.Length > 0) result.Add(found[0]); }
                else result.AddRange(found);
                break;
            }
            case SmartTarget.PlayerRange:
            case SmartTarget.PlayerDistance:
            case SmartTarget.ClosestPlayer:
            {
                if (Me.Map is not { } map) break;
                float min = r.TargetType == (byte)SmartTarget.PlayerRange ? r.TargetParam1 : 0;
                float max = r.TargetType == (byte)SmartTarget.PlayerRange ? r.TargetParam2 : r.TargetParam1;
                if (r.TargetType == (byte)SmartTarget.ClosestPlayer && max == 0) max = 100;
                Player[] found = [.. map.Players.Where(p => p.IsAlive && Distance(Me, p) >= min && Distance(Me, p) <= max).OrderBy(p => Distance(Me, p))];
                if (r.TargetType == (byte)SmartTarget.ClosestPlayer) { if (found.Length > 0) result.Add(found[0]); }
                else result.AddRange(found);
                break;
            }
            case SmartTarget.OwnerOrSummoner:
                if (system?.SummonerOf(Me) is { } summoner) result.Add(summoner);
                else if (!Me.OwnerGuid.IsEmpty && Me.Map?.FindObject(Me.OwnerGuid) is Unit owner) result.Add(owner);
                break;
        }

        return result;
    }
}
