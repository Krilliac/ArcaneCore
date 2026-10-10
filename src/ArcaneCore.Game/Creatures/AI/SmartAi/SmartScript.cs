using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>One loaded or created smart event with its run state (AzerothCore SmartScriptHolder: timer, active, runOnce, enableTimed).</summary>
public sealed class SmartHolder(SmartScriptRow row)
{
    public SmartScriptRow Row { get; } = row;
    public uint TimerMs { get; internal set; }
    public bool Active { get; internal set; }
    public bool RunOnce { get; internal set; }

    /// <summary>A timed action list row: only the enabled one counts down (AzerothCore SmartScriptHolder::enableTimed).</summary>
    internal bool TimedEnabled { get; set; }

    /// <summary>The row belongs to the running timed action list rather than the script's own events.</summary>
    internal bool InTimedList { get; set; }

    internal SmartEvent Event => (SmartEvent)Row.EventType;
    internal SmartAction Action => (SmartAction)Row.ActionType;
    internal SmartEventFlags Flags => (SmartEventFlags)Row.EventFlags;
}

/// <summary>
/// A smart script (AzerothCore SmartScript.cpp: OnReset, OnUpdate, UpdateTimer, InitTimer, RecalcTimer, ProcessEvent, ProcessAction,
/// GetTargets, SetScript9, SetPhase/IncPhase/DecPhase/IsInPhase) on a creature, a game object or an area trigger (<see cref="SmartScriptOwner"/>).
/// Events, actions and targets the engine does not run are reported in <see cref="Unsupported"/> and their rows never run (the catalog already
/// rejects them at load; this is the defence in depth). Randomness comes from the owner's map system, so a seeded test is deterministic.
/// </summary>
public sealed class SmartScript
{
    /// <summary>SMART_EVENT_PHASE_12, the highest phase (SmartScriptMgr.h:57).</summary>
    public const uint MaxPhase = 12;

    /// <summary>The re-check delay of an event whose condition failed (SmartScript.cpp:4316-4318, RecalcTimer(e, 5000, 5000)).</summary>
    private const uint FailedConditionRecheckMs = 5000;

    private readonly SmartScriptOwner _owner;
    private readonly List<SmartHolder> _events = [];
    private readonly List<SmartHolder> _stored = [];
    private readonly List<SmartHolder> _timedList = [];
    private readonly List<string> _unsupported = [];
    private bool _processingTimedList;

    internal SmartScript(CreatureSmartAI ai, IReadOnlyList<SmartScriptRow> rows, SmartScriptCatalog? catalog = null)
        : this(SmartScriptOwner.ForCreature(ai, catalog ?? SmartScriptCatalog.Empty), rows)
    {
    }

    internal SmartScript(SmartScriptOwner owner, IReadOnlyList<SmartScriptRow> rows)
    {
        _owner = owner;
        foreach (SmartScriptRow row in rows)
        {
            if (Unrunnable(row) is { } why)
            {
                if (why.Length > 0) _unsupported.Add(why);
                continue;
            }

            _events.Add(new SmartHolder(row));
        }

        foreach (SmartHolder holder in _events) InitTimer(holder);
    }

    private Creature? Me => _owner.Me;
    private CreatureMapSystem? System => _owner.CreatureSystem;

    public uint Phase { get; private set; }
    public IReadOnlyList<SmartHolder> Events => _events;
    public IReadOnlyList<SmartHolder> StoredEvents => _stored;

    /// <summary>The rows of the timed action list that is running (empty when none).</summary>
    public IReadOnlyList<SmartHolder> TimedActionList => _timedList;
    public IReadOnlyList<string> Unsupported => _unsupported;

    /// <summary>The unit that caused the last action (AzerothCore mLastInvoker).</summary>
    public Unit? LastInvoker { get; private set; }

    private uint Rand(uint min, uint max) => _owner.Rand(min, max);

    /// <summary>The reason a row cannot be held by this script, or null: DEBUG_ONLY rows are skipped silently (as AzerothCore does); an undefined id is reported.</summary>
    private string? Unrunnable(SmartScriptRow row)
    {
        if (((SmartEventFlags)row.EventFlags & SmartEventFlags.DebugOnly) != 0) return string.Empty;
        if (!Enum.IsDefined((SmartEvent)row.EventType)) return $"event {row.EventType} (row {row.Id})";
        if (!Enum.IsDefined((SmartAction)row.ActionType)) return $"action {row.ActionType} (row {row.Id})";
        if (!Enum.IsDefined((SmartTarget)row.TargetType)) return $"target {row.TargetType} (row {row.Id})";
        // SUMMON_CREATURE and MOVE_TO_POS act through the creature the script belongs to; an owner without one cannot run them.
        if (Me is null && row.ActionType is (byte)SmartAction.SummonCreature or (byte)SmartAction.MoveToPos)
            return $"action {row.ActionType} needs a creature owner (row {row.Id})";
        return null;
    }

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

    /// <summary>AzerothCore OnReset: phase 0, timers and run-once state back (unless DONT_RESET), then SMART_EVENT_RESET. A running timed action list stays (SmartScript.cpp:131-155).</summary>
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

    /// <summary>
    /// AzerothCore OnUpdate: the events and stored events, then the timed action list - only its enabled rows count down, and the list is
    /// cleared once none is enabled (SmartScript.cpp:5295-5311).
    /// </summary>
    public void OnUpdate(uint diffMs)
    {
        foreach (SmartHolder e in _events.ToArray()) UpdateTimer(e, diffMs);
        foreach (SmartHolder e in _stored.ToArray()) UpdateTimer(e, diffMs);
        _stored.RemoveAll(e => e.RunOnce && (e.Flags & SmartEventFlags.NotRepeatable) != 0);

        bool needCleanup = true;
        if (_timedList.Count > 0)
        {
            _processingTimedList = true;
            try
            {
                foreach (SmartHolder e in _timedList.ToArray())
                {
                    if (!e.TimedEnabled) continue;
                    UpdateTimer(e, diffMs);
                    needCleanup = false;
                }
            }
            finally
            {
                _processingTimedList = false;
            }
        }

        if (needCleanup) _timedList.Clear();
    }

    /// <summary>AzerothCore UpdateTimer: phase and combat gates, the cast delay while casting, then the timed events run.</summary>
    private void UpdateTimer(SmartHolder e, uint diffMs)
    {
        if (e.Event == SmartEvent.Link) return;
        if (e.Row.EventPhaseMask != 0 && !IsInPhase(e.Row.EventPhaseMask)) return;
        bool engaged = Me?.Combat.IsInCombat ?? false;

        // SmartScript.cpp:5238-5242: UPDATE_IC needs a creature in combat; UPDATE_OOC also runs without a creature (a game object script).
        if (e.Event == SmartEvent.UpdateInCombat && (Me is null || !engaged)) return;
        if (e.Event == SmartEvent.UpdateOutOfCombat && engaged) return;
        if (e.TimerMs >= diffMs && e.TimerMs != 0)
        {
            e.TimerMs -= diffMs;
            return;
        }

        // A cast without INTERRUPT_PREVIOUS waits for the current cast (AzerothCore RaisePriority: tried again next update).
        if (e.Action == SmartAction.Cast && ((SmartCastFlags)e.Row.ActionParam2 & SmartCastFlags.InterruptPrevious) == 0
            && Me is { } caster && (System?.AiServices.Spells?.IsCasting(caster) ?? false))
        {
            e.TimerMs = 0;
            return;
        }

        e.Active = true;
        if (e.Event is SmartEvent.Update or SmartEvent.UpdateInCombat or SmartEvent.UpdateOutOfCombat or SmartEvent.HealthPct)
        {
            ProcessEvent(e, null);

            // SmartScript.cpp:5219-5232: a timed action list row that was processed once (whether or not its condition held) is disabled,
            // and the first row with a greater id is enabled.
            if (e.InTimedList)
            {
                e.TimedEnabled = false;
                SmartHolder? next = _timedList.FirstOrDefault(l => l.Row.Id > e.Row.Id);
                if (next is not null) next.TimedEnabled = true;
            }
        }
        else e.TimerMs = 0;
    }

    // ---- conditions ----

    /// <summary>
    /// The row's ConditionId (cmangos <c>conditions</c>) through the relay-condition path of the creature system: target the event's invoker, source
    /// the script's base object (ScriptMgr.cpp:1769 shape). No condition passes; a missing evaluator, a missing condition or an undecidable one fails.
    /// AzerothCore's smart-event conditions use source type 22 instead (SmartScript.cpp:157-180); see docs/integration/smartai-slice2-20261010.md.
    /// </summary>
    private bool ConditionsHold(SmartHolder e, Unit? invoker)
        => e.Row.ConditionId == 0 || (System?.SmartConditionHolds(e.Row.ConditionId, invoker, _owner.Base) ?? false);

    // ---- events ----

    /// <summary>
    /// AzerothCore ProcessEventsFor: LINK rows are skipped here (they run from the row that links them, unchecked); every other row of the event
    /// type is condition-checked before it is processed (SmartScript.cpp:157-180).
    /// </summary>
    public void ProcessEventsFor(SmartEvent type, Unit? invoker = null, uint var0 = 0, SpellInfo? spell = null)
    {
        if (type == SmartEvent.Link) return;
        foreach (SmartHolder e in _events.ToArray())
        {
            if (e.Event != type || !ConditionsHold(e, invoker)) continue;
            ProcessEvent(e, invoker, var0, spell);
        }

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
            case SmartEvent.AiInit:
            case SmartEvent.JustCreated:
                ProcessAction(e, invoker);
                break;
            case SmartEvent.Update:
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.UpdateOutOfCombat:
                if (Me?.Combat.IsInCombat ?? false) return;
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.UpdateInCombat:
                if (Me is null || !Me.Combat.IsInCombat) return;
                ProcessTimedAction(e, r.EventParam3, r.EventParam4, invoker);
                break;
            case SmartEvent.HealthPct:
            {
                if (Me is null || !Me.Combat.IsInCombat || Me.MaxHealth == 0) return;
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
            case SmartEvent.AreaTriggerOnTrigger:
                // SmartScript.cpp:4743-4749: param1 0 matches any trigger, else the trigger id (var0).
                if (r.EventParam1 == 0 || r.EventParam1 == var0) ProcessAction(e, invoker);
                break;
            case SmartEvent.GossipHello:
                // SmartScript.cpp:4499-4520: filter 0 always, 1 not when var0 (report use) is set, 2 only then. var0 is 0 on 1.12.
                if ((r.EventParam1 == 1 && var0 != 0) || (r.EventParam1 == 2 && var0 == 0)) return;
                ProcessAction(e, invoker);
                break;
        }
    }

    /// <summary>AzerothCore ProcessTimedAction: the action when the row's condition holds, then the repeat timer; a failed check re-arms the timer at 5000 (SmartScript.cpp:4316-4318).</summary>
    private void ProcessTimedAction(SmartHolder e, uint min, uint max, Unit? invoker)
    {
        if (!ConditionsHold(e, invoker))
        {
            RecalcTimer(e, FailedConditionRecheckMs, FailedConditionRecheckMs);
            return;
        }

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
        Creature? me = Me;
        switch (e.Action)
        {
            case SmartAction.Talk:
                if (me is not null)
                {
                    if (system is null) break;
                    Unit? talkTarget = targets.OfType<Unit>().FirstOrDefault() ?? invoker;
                    system.SayText(me, (int)r.ActionParam1, talkTarget);
                }
                else
                {
                    // SmartScript.cpp:221-266 without `me`: the speaker is the first creature target that is not a pet (a pet is never a
                    // speaker or a text target), a player target leaves no speaker, and the text target falls back to the last invoker.
                    Creature? speaker = targets.OfType<Creature>().FirstOrDefault(c => c.Summon is not { Kind: SummonKind.Pet });
                    speaker?.System?.SayText(speaker, (int)r.ActionParam1, LastInvoker);
                }

                break;
            case SmartAction.Cast:
                if (_owner.Go is not null) CastFromObject(r, targets);
                else DoCast(r, targets);
                break;
            case SmartAction.SummonCreature:
                if (system is null || me is null) break;
                Unit? attack = r.ActionParam4 != 0 ? invoker : null;
                if (r.TargetType == (byte)SmartTarget.Position || targets.Count == 0)
                {
                    (float x, float y, float z, float o) = r.TargetType == (byte)SmartTarget.Position
                        ? (r.TargetX, r.TargetY, r.TargetZ, r.TargetO) : (me.X, me.Y, me.Z, me.Orientation);
                    system.SummonAt(me, r.ActionParam1, x, y, z, o, attack, r.ActionParam3);
                }
                else
                {
                    foreach (WorldObject t in targets)
                        system.SummonAt(me, r.ActionParam1, t.X + r.TargetX, t.Y + r.TargetY, t.Z + r.TargetZ, t.Orientation, attack, r.ActionParam3);
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

                // The synthetic row carries no ConditionId: AzerothCore looks its conditions up by the synthetic id, which has none here.
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
                if (me is null) break;
                (float x, float y, float z)? point = r.TargetType == (byte)SmartTarget.Position
                    ? (r.TargetX, r.TargetY, r.TargetZ)
                    : targets.FirstOrDefault() is { } t ? (t.X + r.TargetX, t.Y + r.TargetY, t.Z + r.TargetZ) : null;
                if (point is { } p) me.Motion.MovePoint(r.ActionParam1, p.x, p.y, p.z, run: me.Combat.IsInCombat);
                break;
            }
            case SmartAction.CallTimedActionList:
            case SmartAction.CallRandomTimedActionList:
            case SmartAction.CallRandomRangeTimedActionList:
                CallTimedActionList(r, targets);
                break;
        }

        if (r.Link != 0 && r.Link != r.Id && _events.FirstOrDefault(l => l.Row.Id == r.Link) is { } linked)
        {
            if (linked.Event == SmartEvent.Link) ProcessEvent(linked, invoker);
            else _unsupported.Add($"row {r.Id} links to row {r.Link}, which is not a SMART_EVENT_LINK row");
        }
    }

    private void DoCast(SmartScriptRow r, List<WorldObject> targets)
    {
        if (System is not { } system || Me is not { } me) return;
        var flags = (SmartCastFlags)r.ActionParam2;
        if ((flags & SmartCastFlags.ThreatListNotSingle) != 0 && (!me.Combat.HasThreatList || me.Combat.Threat.Entries.Count <= 1)) return;
        bool triggered = (flags & SmartCastFlags.Triggered) != 0 || r.ActionParam3 != 0;
        foreach (Unit target in targets.OfType<Unit>())
        {
            if ((flags & SmartCastFlags.AuraNotPresent) != 0 && system.HasAura(target, r.ActionParam1)) continue;
            if ((flags & SmartCastFlags.InterruptPrevious) != 0) system.InterruptCast(me);
            system.CastSpell(me, r.ActionParam1, target, triggered);
        }
    }

    /// <summary>
    /// SMART_ACTION_CAST from a game object (SmartScript.cpp:654-680): <c>go->CastSpell(target, spell)</c> per unit target. The cast flags are
    /// checked on the creature branch only, so a game object ignores them.
    /// </summary>
    private void CastFromObject(SmartScriptRow r, List<WorldObject> targets)
    {
        if (_owner.Go is not { } go || _owner.Objects?.Spells is not { } spells) return;
        foreach (Unit target in targets.OfType<Unit>()) spells.Cast(go, r.ActionParam1, target, null);
    }

    // ---- timed action lists ----

    /// <summary>
    /// SMART_ACTION_CALL_TIMED_ACTIONLIST / CALL_RANDOM_TIMED_ACTIONLIST / CALL_RANDOM_RANGE_TIMED_ACTIONLIST (SmartScript.cpp:2136-2160,
    /// 2218-2270): the list id is rolled once, TARGET_NONE does nothing, and each smart creature or game object target runs the list with this
    /// script's last invoker.
    /// </summary>
    private void CallTimedActionList(SmartScriptRow r, List<WorldObject> targets)
    {
        uint id;
        switch ((SmartAction)r.ActionType)
        {
            case SmartAction.CallTimedActionList:
                id = r.ActionParam1;
                break;
            case SmartAction.CallRandomTimedActionList:
            {
                uint[] lists = [.. new[] { r.ActionParam1, r.ActionParam2, r.ActionParam3, r.ActionParam4, r.ActionParam5, r.ActionParam6 }.Where(p => p != 0)];
                if (lists.Length == 0) return;
                id = lists[Rand(0, (uint)lists.Length - 1)];
                break;
            }

            default:
                id = Rand(r.ActionParam1, r.ActionParam2);
                break;
        }

        if (r.TargetType == (byte)SmartTarget.None)
        {
            _unsupported.Add($"action {r.ActionType} of row {r.Id} uses target 0 for its timed action list");
            return;
        }

        foreach (WorldObject target in targets)
        {
            switch (target)
            {
                case Creature { AI: CreatureSmartAI smart }:
                    smart.Script.SetScript9(r, id, LastInvoker);
                    break;
                case GameObject go when go.Map?.FindUpdater<GameObjectMapSystem>() is { } objects && objects.AiFor(go) is SmartGameObjectAi smartObject:
                    smartObject.ScriptFor(objects, go).SetScript9(r, id, LastInvoker);
                    break;
            }
        }
    }

    /// <summary>
    /// AzerothCore SetScript9 (SmartScript.cpp:5595-5622) for the list <paramref name="listId"/> called by <paramref name="caller"/>: refused while a
    /// list is being processed, kept when one runs and the caller's allowOverride (raw param3; for 87 and 88 AzerothCore reads the same union member)
    /// is 0; else the list's rows replace it, only the first enabled, each row's event type replaced by the timer type (raw param2: 0 out of
    /// combat, 1 in combat, above 1 always) and its timer started. SmartAI::SetScript9 stores the invoker first (SmartAI.cpp:1343-1348).
    /// </summary>
    internal void SetScript9(SmartScriptRow caller, uint listId, Unit? invoker)
    {
        if (invoker is not null) LastInvoker = invoker;
        if (_processingTimedList)
        {
            _unsupported.Add($"timed action list {listId} called from a timed action (row {caller.Id} of {caller.EntryOrGuid}) is not allowed");
            return;
        }

        if (caller.ActionParam3 == 0 && _timedList.Count > 0) return;
        IReadOnlyList<SmartScriptRow> rows = _owner.Catalog.TimedActionList(listId);
        if (rows.Count == 0) return;
        byte timerEvent = caller.ActionParam2 switch
        {
            0 => (byte)SmartEvent.UpdateOutOfCombat,
            1 => (byte)SmartEvent.UpdateInCombat,
            _ => (byte)SmartEvent.Update,
        };

        List<SmartHolder> list = [];
        foreach (SmartScriptRow row in rows)
        {
            SmartScriptRow timed = row with { EventType = timerEvent };
            if (Unrunnable(timed) is { } why)
            {
                if (why.Length > 0) _unsupported.Add($"timed action list {listId}: {why}");
                continue;
            }

            list.Add(new SmartHolder(timed) { InTimedList = true });
        }

        _timedList.Clear();
        if (list.Count == 0) return;
        list[0].TimedEnabled = true;
        foreach (SmartHolder holder in list) InitTimer(holder);
        _timedList.AddRange(list);
    }

    // ---- targets ----

    private static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>
    /// AzerothCore SmartScript::GetTargets for the targets the engine resolves. Position (8) yields no object; its action reads target_x/y/z.
    /// The range targets 9/11/17/18 search around the script's base object only; the closest targets 19/21 around the base object, else the
    /// invoker (SmartScript.cpp:3739-3790, 3884-3905, 3926-3975, 4293-4297). Targets that need a creature (victim, threat list, summoner) yield
    /// nothing for a game object or an area trigger.
    /// </summary>
    public List<WorldObject> GetTargets(SmartScriptRow r, Unit? invoker)
    {
        var result = new List<WorldObject>();
        Creature? me = Me;
        IReadOnlyList<ThreatEntry> threat = me is not null && me.Combat.HasThreatList ? me.Combat.Threat.Entries : [];
        bool playerOnly = r.TargetParam2 != 0;
        float maxDist = r.TargetParam1;
        IEnumerable<Unit> Hostile(int skip) => me is null ? [] : threat.Skip(skip).Select(t => t.Target)
            .Where(u => u.IsAlive && (!playerOnly || u is Player) && (maxDist == 0 || Distance(me, u) <= maxDist));
        CreatureMapSystem? system = System;
        WorldObject? baseObject = _owner.Base;
        switch ((SmartTarget)r.TargetType)
        {
            case SmartTarget.Self:
                if (baseObject is not null) result.Add(baseObject);
                break;
            case SmartTarget.Victim:
                if (me?.Combat.Victim is { } victim) result.Add(victim);
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
                bool closest = r.TargetType == (byte)SmartTarget.ClosestCreature;
                WorldObject? origin = closest ? baseObject ?? invoker : baseObject;
                if (system is null || origin is null) break;
                bool range = r.TargetType == (byte)SmartTarget.CreatureRange;
                float min = range ? r.TargetParam2 : 0;
                float max = range ? r.TargetParam3 : r.TargetParam2;
                if (closest && max == 0) max = 100;
                uint alive = r.TargetType switch { (byte)SmartTarget.CreatureRange => r.TargetParam4, (byte)SmartTarget.CreatureDistance => r.TargetParam3, _ => r.TargetParam3 != 0 ? 2u : 1u };
                Creature[] found = [.. system.Creatures.Where(c => !ReferenceEquals(c, origin) && c.IsInWorld
                    && (r.TargetParam1 == 0 || c.Entry == r.TargetParam1)
                    && (alive == 0 || (alive == 1) == c.IsAlive)
                    && Distance(origin, c) >= min && Distance(origin, c) <= max)
                    .OrderBy(c => Distance(origin, c))];
                if (closest) { if (found.Length > 0) result.Add(found[0]); }
                else result.AddRange(found);
                break;
            }
            case SmartTarget.PlayerRange:
            case SmartTarget.PlayerDistance:
            case SmartTarget.ClosestPlayer:
            {
                bool closest = r.TargetType == (byte)SmartTarget.ClosestPlayer;
                WorldObject? origin = closest ? baseObject ?? invoker : baseObject;
                if (origin?.Map is not { } map) break;
                float min = r.TargetType == (byte)SmartTarget.PlayerRange ? r.TargetParam1 : 0;
                float max = r.TargetType == (byte)SmartTarget.PlayerRange ? r.TargetParam2 : r.TargetParam1;
                if (closest && max == 0) max = 100;
                Player[] found = [.. map.Players.Where(p => p.IsAlive && Distance(origin, p) >= min && Distance(origin, p) <= max).OrderBy(p => Distance(origin, p))];
                if (closest) { if (found.Length > 0) result.Add(found[0]); }
                else result.AddRange(found);
                break;
            }
            case SmartTarget.OwnerOrSummoner:
                if (me is null) break;
                if (system?.SummonerOf(me) is { } summoner) result.Add(summoner);
                else if (!me.OwnerGuid.IsEmpty && me.Map?.FindObject(me.OwnerGuid) is Unit owner) result.Add(owner);
                break;
        }

        return result;
    }
}
