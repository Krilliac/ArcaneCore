using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Elevators and trams (GAMEOBJECT_TYPE_TRANSPORT, 11; vmangos ElevatorTransport, Transports/Transport.cpp:383-430): the object is created as a
/// transport (<see cref="GameObject.CreateUpdateFlags"/>, HIGHGUID_TRANSPORT, level, state and flags) and its create block carries the
/// milliseconds into its TransportAnimation.dbc cycle, from which the client animates it; the progress is the time since the object was
/// created modulo the cycle length, as vmangos computes it every update. Without the DBC (or a row for the entry) the progress stays 0, as in
/// vmangos without animation info. The server keeps the object at its spawn (stationary) position: vmangos moves its copy along the nodes
/// for the passengers it carries, and nothing boards an elevator here (a player on one keeps the position its client reports).
/// <para>
/// An elevator whose animation carries it farther from its spawn than the visibility range (<see cref="Map.VisibilityRange"/>; in the 5875
/// data only the six Deeprun Tram cars, 2,480 yards) is a map-wide object, as vmangos keeps every elevator (ObjectMgr.cpp:2667-2668 leaves
/// type 11 spawns out of the grid; Map::LoadElevatorTransports creates them with the map, Map.cpp:568-600; SendInitTransports sends them at
/// map entry, Map.cpp:1720-1733): it is created with the map's object system, whatever grid is loaded, keeps its grid loaded (an active
/// object), and every player on the map has it (<see cref="Map.AddMapWideObject"/>). Without that a passenger lost the car under it about
/// a hundred yards out of the platform (out of range of the car's spawn cell), and the cars of the other platform were never created for a
/// player waiting at this one. vmangos does not pool or event-gate elevators either. Elevators that stay within range of their spawn (the
/// Thunder Bluff and Undercity lifts) remain grid objects: their riders never leave their range.
/// </para>
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>Minimum stationary floor stop duration (ms) for a vertical elevator car before reporting <c>elevator-car/stop-too-short</c>.</summary>
    public const uint MinElevatorStopMs = 1000;

    private const float StationaryNodeToleranceYards = 0.05f;
    private TransportAnimationCatalog _elevatorAnimations = TransportAnimationCatalog.Empty;
    private readonly List<GameObject> _mapWideElevators = [];
    private readonly HashSet<ulong> _reportedNoAnimationSpawns = [];
    private readonly HashSet<ulong> _reportedShortStopSpawns = [];
    private readonly HashSet<ulong> _reportedNonFinitePositionSpawns = [];

    /// <summary>
    /// The elevator and tram animations (TransportAnimation.dbc), shared by every map; empty without the DBC. Setting it creates the map-wide
    /// elevators of this map (see the class remarks); world thread, outside the map's update.
    /// </summary>
    public TransportAnimationCatalog ElevatorAnimations
    {
        get => _elevatorAnimations;
        set
        {
            _elevatorAnimations = value ?? TransportAnimationCatalog.Empty;
            _reportedNoAnimationSpawns.Clear();
            _reportedShortStopSpawns.Clear();
            InstallMapWideElevators();
        }
    }

    /// <summary>The elevators every player of the map has (created with the map; see the class remarks).</summary>
    public IReadOnlyList<GameObject> MapWideElevators => _mapWideElevators;

    /// <summary>
    /// How far, horizontally, the animation of <paramref name="entry"/> carries it from its spawn at most (the largest node offset; the
    /// rotation does not change a length). 0 without an animation.
    /// </summary>
    public float ElevatorReach(uint entry)
    {
        float reach = 0;
        foreach (TransportAnimationNode node in _elevatorAnimations.Nodes(entry))
        {
            reach = MathF.Max(reach, MathF.Sqrt((node.X * node.X) + (node.Y * node.Y)));
        }

        return reach;
    }

    private void InstallMapWideElevators()
    {
        if (_elevatorAnimations.Count == 0)
        {
            return;
        }

        foreach ((GridCoord coord, List<GameObjectSpawn> spawns) in _spawnsByGrid)
        {
            foreach (GameObjectSpawn spawn in spawns.ToArray())
            {
                if (_content.FindTemplate(spawn.Entry) is not { } template || (GameObjectType)template.Type != GameObjectType.Transport)
                {
                    continue;
                }

                if ((spawn.SpawnFlags & 0x02) == 0)
                {
                    ValidateElevatorSpawn(spawn.Entry, spawn.Guid);
                }

                if (ElevatorReach(spawn.Entry) <= Map.VisibilityRange)
                {
                    continue;
                }

                spawns.Remove(spawn); // a grid load never creates it again
                if ((spawn.SpawnFlags & 0x02) != 0)
                {
                    continue; // SPAWN_FLAG_DISABLED: GameObject::LoadFromDB refuses it (GameObject.cpp:969)
                }

                // A grid loaded before the animations were known already created it: it stays, and leaves the grid's list.
                GameObject? go = _grids.TryGetValue(coord, out List<GameObject>? list)
                    ? list.FirstOrDefault(g => g.Spawn is { } s && s.Guid == spawn.Guid) : null;
                if (go is not null)
                {
                    list!.Remove(go);
                    if (go.IsInWorld && ReferenceEquals(go.Map, Map))
                    {
                        Map.SetActive(go, true);
                    }
                }
                else
                {
                    _spawnEntries[spawn.Guid] = template.Entry;
                    go = new GameObject(spawn.Guid, template, spawn) { CreatedAtMs = _clockMs };
                    go.RolledRespawnSeconds = RollRespawnSeconds(spawn);
                    go.System = this;
                    _objects[go.Guid] = go;
                    go.ClearChangedFields();
                    Map.AddObject(go, active: true);
                }

                _mapWideElevators.Add(go);
                if (go.IsInWorld && ReferenceEquals(go.Map, Map))
                {
                    Map.AddMapWideObject(go);
                }
            }
        }

        UpdateElevators();
    }

    private void UpdateElevators()
    {
        foreach (GameObject go in _objects.Values)
        {
            if (go.Type != GameObjectType.Transport)
            {
                continue;
            }

            ValidateElevatorObject(go);
            if (ElevatorAnimations.TotalTime(go.Entry) is var total and not 0)
            {
                go.PathProgress = (uint)((_clockMs - go.CreatedAtMs) % total);
                _ = ElevatorPositionAt(go, go.PathProgress);
            }
        }
    }

    /// <summary>
    /// Where the animation puts an elevator now (vmangos ElevatorTransport::Update, Transport.cpp:403-427): the node offset at its progress, turned
    /// by the object's rotation the way the reference does it (the row vector times the rotation matrix of GAMEOBJECT_ROTATION), its Y negated
    /// ("magical sign flip but it works - vanilla/tbc only"), plus the stationary position. Null without an animation for the entry.
    /// </summary>
    public (float X, float Y, float Z)? ElevatorPosition(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return ElevatorPositionAt(go, go.PathProgress);
    }

    /// <summary>The cycle length of <paramref name="go"/>'s animation in ms (0 without one).</summary>
    public uint ElevatorCycle(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.Transport)
        {
            return 0;
        }

        ValidateElevatorObject(go);
        return ElevatorAnimations.TotalTime(go.Entry);
    }

    /// <summary>
    /// The progress of <paramref name="go"/>'s animation <paramref name="aheadMs"/> from now (the clock of <see cref="UpdateElevators"/>: the
    /// time since the object was created, modulo the cycle), or null without an animation.
    /// </summary>
    public uint? ElevatorProgressAhead(GameObject go, uint aheadMs)
    {
        uint total = ElevatorCycle(go);
        return total == 0 ? null : (uint)((_clockMs - go.CreatedAtMs + aheadMs) % total);
    }

    /// <summary>Where the animation puts <paramref name="go"/> at <paramref name="pathProgress"/> (see <see cref="ElevatorPosition(GameObject)"/>).</summary>
    public (float X, float Y, float Z)? ElevatorPositionAt(GameObject go, uint pathProgress)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.Transport)
        {
            return null;
        }

        ValidateElevatorObject(go);
        if (ElevatorAnimations.Offset(go.Entry, pathProgress) is not { } offset)
        {
            return null;
        }

        uint spawnId = go.Spawn?.Guid ?? go.Guid.Counter;
        ulong episodeKey = ((ulong)go.Entry << 32) | spawnId;

        float qx = go.GetFloat(UpdateFields.GameobjectRotation), qy = go.GetFloat(UpdateFields.GameobjectRotation + 1);
        float qz = go.GetFloat(UpdateFields.GameobjectRotation + 2), qw = go.GetFloat(UpdateFields.GameobjectRotation + 3);
        if (!float.IsFinite(go.X) || !float.IsFinite(go.Y) || !float.IsFinite(go.Z)
            || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(offset.Z)
            || !float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz) || !float.IsFinite(qw))
        {
            if (_reportedNonFinitePositionSpawns.Add(episodeKey))
            {
                AnomalyLedger.Shared.Report(
                    "elevator-car",
                    "position-non-finite",
                    $"gameobject entry {go.Entry} spawn {spawnId}",
                    $"non-finite elevator input on map {Map.MapId} at progress {pathProgress} (pos=({go.X},{go.Y},{go.Z}), offset=({offset.X},{offset.Y},{offset.Z}))",
                    AnomalySeverity.Error);
            }

            return null;
        }

        float length = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
        if (length > 0 && float.IsFinite(length))
        {
            // Matrix3(Quat) unitizes a copy first.
            (qx, qy, qz, qw) = (qx / length, qy / length, qz / length, qw / length);
        }

        // G3D Matrix3(Quat) (dep/g3dlite Matrix3.cpp): the usual rotation matrix of a unit quaternion.
        float xx = 2 * qx * qx, yy = 2 * qy * qy, zz = 2 * qz * qz;
        float xy = 2 * qx * qy, xz = 2 * qx * qz, yz = 2 * qy * qz;
        float wx = 2 * qw * qx, wy = 2 * qw * qy, wz = 2 * qw * qz;
        // Locals, not an array: this runs for every car sample of a bot's tram planning and every rider's anticheat claim.
        float m00 = 1 - (yy + zz), m01 = xy - wz, m02 = xz + wy;
        float m10 = xy + wz, m11 = 1 - (xx + zz), m12 = yz - wx;
        float m20 = xz - wy, m21 = yz + wx, m22 = 1 - (xx + yy);

        // Vector3 * Matrix3 (G3D: the row vector on the left).
        float rx = (offset.X * m00) + (offset.Y * m10) + (offset.Z * m20);
        float ry = (offset.X * m01) + (offset.Y * m11) + (offset.Z * m21);
        float rz = (offset.X * m02) + (offset.Y * m12) + (offset.Z * m22);
        float x = go.X + rx;
        float y = go.Y - ry;
        float z = go.Z + rz;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            if (_reportedNonFinitePositionSpawns.Add(episodeKey))
            {
                AnomalyLedger.Shared.Report(
                    "elevator-car",
                    "position-non-finite",
                    $"gameobject entry {go.Entry} spawn {spawnId}",
                    $"computed non-finite elevator position ({x},{y},{z}) on map {Map.MapId} at progress {pathProgress}",
                    AnomalySeverity.Error);
            }

            return null;
        }

        _reportedNonFinitePositionSpawns.Remove(episodeKey);
        return (x, y, z);
    }

    private void ValidateElevatorObject(GameObject go)
        => ValidateElevatorSpawn(go.Entry, go.Spawn?.Guid ?? go.Guid.Counter);

    private void ValidateElevatorSpawn(uint entry, uint spawnId)
    {
        ulong key = ((ulong)entry << 32) | spawnId;
        uint totalTime = _elevatorAnimations.TotalTime(entry);
        if (totalTime == 0)
        {
            if (_reportedNoAnimationSpawns.Add(key))
            {
                AnomalyLedger.Shared.Report(
                    "elevator-car",
                    "no-animation",
                    $"gameobject entry {entry} spawn {spawnId}",
                    $"transport gameobject entry {entry} spawn {spawnId} on map {Map.MapId} has no TransportAnimation keyframes",
                    AnomalySeverity.Warning);
            }

            return;
        }

        IReadOnlyList<TransportAnimationNode> nodes = _elevatorAnimations.Nodes(entry);
        if (nodes.Count < 2)
        {
            return;
        }

        uint shortestStopMs = uint.MaxValue;
        int stopCount = 0;
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            TransportAnimationNode a = nodes[i];
            TransportAnimationNode b = nodes[i + 1];
            if (b.TimeSeg <= a.TimeSeg)
            {
                continue;
            }

            if (MathF.Abs(b.X - a.X) <= StationaryNodeToleranceYards
                && MathF.Abs(b.Y - a.Y) <= StationaryNodeToleranceYards
                && MathF.Abs(b.Z - a.Z) <= StationaryNodeToleranceYards)
            {
                uint duration = b.TimeSeg - a.TimeSeg;
                if (duration < shortestStopMs)
                {
                    shortestStopMs = duration;
                }

                stopCount++;
            }
        }

        bool tooShort = (stopCount > 0 && shortestStopMs < MinElevatorStopMs)
            || totalTime < (2 * MinElevatorStopMs)
            || (stopCount == 0 && nodes.Count >= 4);
        if (tooShort && _reportedShortStopSpawns.Add(key))
        {
            uint reportedMs = stopCount > 0 ? shortestStopMs : 0;
            AnomalyLedger.Shared.Report(
                "elevator-car",
                "stop-too-short",
                $"gameobject entry {entry} spawn {spawnId}",
                $"elevator entry {entry} spawn {spawnId} on map {Map.MapId} has floor stop duration {reportedMs} ms (< {MinElevatorStopMs} ms, cycle {totalTime} ms)",
                AnomalySeverity.Warning);
        }
    }
}

