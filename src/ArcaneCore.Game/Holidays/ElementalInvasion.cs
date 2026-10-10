using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Holidays;

/// <summary>One element of the invasions: its ClassicDB game event, rift object, invader, boss and zone.</summary>
public readonly record struct InvasionElement(int Index, ushort Event, uint Rift, uint Invader, uint Boss, uint Zone);

/// <summary>
/// The elemental invasions, vmangos src/scripts/world/elemental_invasions.cpp (elemental_invasion_riftAI and npc_invaderAI::JustDied). While an
/// invasion runs, each rift in its zone keeps 3 + stage - 1 invaders (at most 6) of its element alive around it, checked every 70 seconds; the
/// stage goes up after 50 invaders die or an hour passes, until the boss stage (5). The stage and the kill count are vmangos saved variables
/// (VAR_FIRE ... VAR_AIR_KILLS) and are saved whenever they change.
/// ClassicDB runs each invasion as one game event (11 Air, 13 Fire, 38 Water, 39 Earth) that spawns the rifts, a few invaders and the boss
/// itself, and the invaders' spells are its EventAI; so the boss spawn and the invader combat stay ClassicDB's, and only the rifts' staged
/// spawning (which ClassicDB has no script for) is ported. vmangos's separate boss events (72-75, HardcodedEvents.cpp) do not exist in
/// ClassicDB; when an invasion ends, its stage and kills are reset, as vmangos's ElementalInvasion::Disable does.
/// </summary>
public sealed class ElementalInvasionController(Func<ushort, bool> isActiveEvent)
{
    public const int MinRiftSpawn = 3, MaxRiftSpawn = 6, DeadInvaders = 50, StageBoss = 5;
    public const uint UpdateMs = 70_000, IncreaseMs = 3_600_000, InvaderLifeMs = 3_600_000;

    public static readonly InvasionElement[] Elements =
    [
        new(0, 13, 179666, 14460, 14461, 490),  // Fire, Un'Goro Crater
        new(1, 11, 179667, 14455, 14454, 1377), // Air, Silithus
        new(2, 39, 179664, 14462, 14464, 16),   // Earth, Azshara
        new(3, 38, 179665, 14458, 14457, 618),  // Water, Winterspring
    ];

    private readonly int[] _stage = [1, 1, 1, 1];
    private readonly int[] _kills = new int[4];
    private readonly bool[] _wasActive = new bool[4];

    /// <summary>Called with an element's index whenever its saved stage or kills change.</summary>
    public Action<int>? Changed { get; set; }

    public int Stage(int index) => _stage[index];

    public int Kills(int index) => _kills[index];

    public bool IsActive(int index) => isActiveEvent(Elements[index].Event);

    /// <summary>Restore saved values (start-up).</summary>
    public void Restore(int index, int stage, int kills)
    {
        _stage[index] = Math.Clamp(stage, 1, StageBoss + 1);
        _kills[index] = Math.Max(0, kills);
    }

    /// <summary>The invaders one rift keeps at a stage.</summary>
    public static int SpawnCount(int stage) => Math.Min(MinRiftSpawn + stage - 1, MaxRiftSpawn);

    /// <summary>
    /// One rift's 70-second check (elemental_invasion_riftAI::UpdateAI): the stage goes up below the boss stage when 50 invaders died or the
    /// rift's hour ran out; returns whether it went up (the rift's hour restarts then).
    /// </summary>
    public bool TryAdvance(int index, bool hourElapsed)
    {
        if (_stage[index] >= StageBoss || (_kills[index] < DeadInvaders && !hourElapsed)) return false;
        _stage[index]++;
        _kills[index] = 0;
        Changed?.Invoke(index);
        return true;
    }

    /// <summary>npc_invaderAI::JustDied: an invader of a running invasion counts.</summary>
    public void OnCreatureDied(uint entry)
    {
        foreach (InvasionElement e in Elements)
        {
            if (e.Invader != entry || !isActiveEvent(e.Event)) continue;
            _kills[e.Index]++;
            Changed?.Invoke(e.Index);
        }
    }

    /// <summary>World tick: an invasion that ended starts again from stage 1 next time.</summary>
    public void Update()
    {
        foreach (InvasionElement e in Elements)
        {
            bool active = isActiveEvent(e.Event);
            if (_wasActive[e.Index] && !active && (_stage[e.Index] != 1 || _kills[e.Index] != 0))
            {
                _stage[e.Index] = 1;
                _kills[e.Index] = 0;
                Changed?.Invoke(e.Index);
            }

            _wasActive[e.Index] = active;
        }
    }
}

/// <summary>go_elemental_invasion_rift_fire / _water / _earth / _air (elemental_invasions.cpp:44-128).</summary>
public sealed class ElementalRiftAi(ElementalInvasionController invasion) : IGameObjectAi
{
    private sealed class RiftState
    {
        public long TimerMs = 500;
        public long IncreaseMs = ElementalInvasionController.IncreaseMs;
        public readonly ObjectGuid[] Invaders = new ObjectGuid[ElementalInvasionController.MaxRiftSpawn];
    }

    private readonly Dictionary<ObjectGuid, RiftState> _states = [];

    internal Random Random { get; set; } = Random.Shared;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (Array.FindIndex(ElementalInvasionController.Elements, e => e.Rift == go.Entry) is var index && index < 0) return;
        if (!_states.TryGetValue(go.Guid, out RiftState? state)) _states[go.Guid] = state = new RiftState();

        bool hourElapsed = state.IncreaseMs < diffMs;
        if (!hourElapsed) state.IncreaseMs -= diffMs;
        if (state.TimerMs >= diffMs)
        {
            state.TimerMs -= diffMs;
            return;
        }

        state.TimerMs = ElementalInvasionController.UpdateMs;
        InvasionElement element = ElementalInvasionController.Elements[index];
        if (!invasion.IsActive(index)) return;
        if (objects.Map.GetZoneAndAreaId(go.X, go.Y, go.Z).ZoneId != element.Zone) return;
        if (invasion.TryAdvance(index, hourElapsed)) state.IncreaseMs = ElementalInvasionController.IncreaseMs;

        if (objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        int count = ElementalInvasionController.SpawnCount(invasion.Stage(index));
        for (int i = 0; i < count; i++)
        {
            if (!state.Invaders[i].IsEmpty && creatures.FindCreature(state.Invaders[i]) is { IsAlive: true }) continue;
            state.Invaders[i] = Spawn(objects.Map, creatures, go, element.Invader)?.Guid ?? default;
        }
    }

    // DoSpawn: summoned on the rift for an hour (or until dead), then sent to wander 30 yards around a point 15-65 yards from the rift.
    private Creature? Spawn(Map map, CreatureMapSystem creatures, GameObject go, uint entry)
    {
        if (creatures.SummonInstanceCreatureTimedOocOrDead(entry, go.X, go.Y, go.Z, 0f, ElementalInvasionController.InvaderLifeMs) is not { } invader)
            return null;
        float x = go.X, y = go.Y, z = go.Z;
        for (int i = 0; i < 20; i++)
        {
            float angle = (float)(Random.NextDouble() * 2 * Math.PI), dist = (float)(Random.NextDouble() * 65);
            x = go.X + (dist * MathF.Cos(angle));
            y = go.Y + (dist * MathF.Sin(angle));
            float floor = map.GetHeight(x, y, go.Z + 10f);
            z = floor > TerrainTile.InvalidHeight ? floor : go.Z;
            if (dist > 15f) break;
        }

        float o = (float)(Random.NextDouble() * 2 * Math.PI);
        creatures.SetHomePosition(invader, x, y, z, o);
        invader.Motion.MoveRandom(new RandomMovementGenerator(30f, new CreatureHome(x, y, z, o), run: false));
        return invader;
    }
}
