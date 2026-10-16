using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_lightning (mangos-classic kalimdor/azshara.cpp:368-397, registered :518-521): the Theatric Lightning (183356) of Azshara plays its custom
/// animation (SMSG_GAMEOBJECT_CUSTOM_ANIM, animation 0) every two to five whole seconds for as long as it is spawned. The timer starts at a
/// random 2-5 s when the object is first updated (the source's constructor) and is held, not counted down, while the object is not spawned.
/// One instance serves every object of the entry: the timer is kept by the object. World thread.
/// </summary>
internal sealed class LightningAi(Random random) : IGameObjectAi
{
    public const uint MinSeconds = 2, MaxSeconds = 5;

    private readonly ConditionalWeakTable<GameObject, Timer> _timers = new();

    private sealed class Timer
    {
        public uint RemainingMs;
    }

    /// <summary>The time left before <paramref name="go"/>'s next flash, or null before its first update.</summary>
    public uint? RemainingMs(GameObject go) => _timers.TryGetValue(go, out Timer? timer) ? timer.RemainingMs : null;

    private uint NextMs() => (uint)random.Next((int)MinSeconds, (int)MaxSeconds + 1) * 1000;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        Timer timer = _timers.GetValue(go, _ => new Timer { RemainingMs = NextMs() });
        if (!go.IsSpawned)
        {
            return;
        }

        if (timer.RemainingMs <= diffMs)
        {
            objects.SendCustomAnim(go, 0);
            timer.RemainingMs = NextMs();
        }
        else
        {
            timer.RemainingMs -= diffMs;
        }
    }
}

/// <summary>
/// go_covert_ops (mangos-classic kalimdor/stonetalon_mountains.cpp:104-213, registered :236-239): the Venture Co. Wagon Trap (19590, Stonetalon
/// Mountains) calls the Venture Co. workers when it has been sprung. As soon as the trap's loot state becomes just-deactivated it collects every
/// Deforester (3991), Logger (3989), Digger (3999), Overlord (4004) and Operator (3988) within 100 yd. Two seconds later each one that is alive and
/// not in combat stops, runs to the point 10 yd from the wagon on the side facing it (GetContactPoint) and waits there; 45 s after that each one
/// that is alive and not in combat walks again and evades, which sends it home. The phase belongs to the object and starts over when the object
/// respawns; a worker found by an earlier springing is kept (the source never empties its list; a worker is listed once here, which changes
/// nothing as both commands are idempotent). Timers fire on the update after they run out, as <c>timer &lt; diff</c> does.
/// <para>
/// The source reacts to <c>OnLootStateChange</c>; there is no such hook here, so the object's update (which runs before the despawn the same
/// state causes) sees the state once per spawn. The contact point's height is the ground under it, without GetNearPoint's collision search.
/// One instance serves every object of the entry: the state is kept by the object. World thread.
/// </para>
/// </summary>
internal sealed class CovertOpsAi : IGameObjectAi
{
    public const uint CallDelayMs = 2000, ReturnDelayMs = 45_000;
    public const float SearchRange = 100f, ContactDistance = 10f;
    public const uint PointWagon = 1;

    /// <summary>NPC_VENTURE_DEFORESTER, LOGGER, DIGGER, OVERLORD, OPERATOR (:111-115, ventureCoList :123).</summary>
    public static readonly uint[] VentureCoWorkers = [3991, 3989, 3999, 4004, 3988];

    /// <summary>PHASE_INACTIVE, PHASE_CALLING, PHASE_HOMING (:117-120).</summary>
    public enum Phase
    {
        Inactive,
        Calling,
        Homing,
    }

    private sealed class State
    {
        public Phase Phase;
        public uint CallMs = CallDelayMs, ReturnMs = ReturnDelayMs;
        public bool SawDeactivation;
        public readonly List<Creature> Workers = [];
    }

    private readonly ConditionalWeakTable<GameObject, State> _states = new();

    /// <summary>The phase of <paramref name="go"/>'s event.</summary>
    public Phase PhaseOf(GameObject go) => _states.TryGetValue(go, out State? state) ? state.Phase : Phase.Inactive;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    /// <summary>JustSpawned → Reset (:160-163, Reset :137-142): the object's event starts over.</summary>
    public void OnRespawn(GameObjectMapSystem objects, GameObject go)
    {
        State state = _states.GetValue(go, _ => new State());
        state.Phase = Phase.Inactive;
        state.CallMs = CallDelayMs;
        state.ReturnMs = ReturnDelayMs;
        state.SawDeactivation = false;
    }

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        State state = _states.GetValue(go, _ => new State());
        CreatureMapSystem? creatures = objects.Map.FindUpdater<CreatureMapSystem>();
        if (go.IsSpawned && go.LootState == GameObjectLootState.JustDeactivated)
        {
            if (!state.SawDeactivation)
            {
                state.SawDeactivation = true;
                state.Phase = Phase.Calling; // OnLootStateChange (:144-158)
                if (creatures is not null)
                {
                    foreach (uint entry in VentureCoWorkers)
                    {
                        foreach (Creature worker in creatures.CreaturesOfEntryInRange(go, entry, SearchRange))
                        {
                            if (!state.Workers.Contains(worker))
                            {
                                state.Workers.Add(worker);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            state.SawDeactivation = false;
        }

        switch (state.Phase)
        {
            case Phase.Calling:
                if (state.CallMs < diffMs)
                {
                    if (creatures is not null)
                    {
                        foreach (Creature worker in state.Workers.Where(w => w.IsAlive && !w.Combat.IsInCombat))
                        {
                            CallToWagon(creatures, go, worker);
                        }
                    }

                    state.CallMs = 0;
                    state.Phase = Phase.Homing;
                }
                else
                {
                    state.CallMs -= diffMs;
                }

                break;
            case Phase.Homing:
                if (state.ReturnMs < diffMs)
                {
                    if (creatures is not null)
                    {
                        foreach (Creature worker in state.Workers.Where(w => w.IsAlive && !w.Combat.IsInCombat))
                        {
                            creatures.SetScriptRun(worker, false); // SetWalk(true)
                            worker.AI?.EnterEvadeMode();
                        }
                    }

                    state.ReturnMs = 0;
                    state.Phase = Phase.Inactive;
                }
                else
                {
                    state.ReturnMs -= diffMs;
                }

                break;
        }
    }

    /// <summary>
    /// GetContactPoint(worker, x, y, z, 10) (Object.h:915-919): the point 10 yd (plus both bounding radii) from the wagon towards the worker; then
    /// SetWalk(false), MoveIdle and MovePoint(1, ...).
    /// </summary>
    private static void CallToWagon(CreatureMapSystem creatures, GameObject go, Creature worker)
    {
        float angle = MathF.Atan2(worker.Y - go.Y, worker.X - go.X);
        float distance = ContactDistance + go.BoundingRadius + worker.BoundingRadius;
        float x = go.X + (distance * MathF.Cos(angle));
        float y = go.Y + (distance * MathF.Sin(angle));
        float z = creatures.GroundZ(x, y, go.Z) ?? go.Z;
        creatures.SetScriptRun(worker, true); // SetWalk(false)
        creatures.MoveIdle(worker);
        worker.Motion.MovePoint(PointWagon, x, y, z, run: true);
    }
}
