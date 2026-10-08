using System.Numerics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// One key frame of a ship's path (vmangos <c>KeyFrame</c>, TransportMgr.h:46-83): a TaxiPathNode with the distances and
/// times the motion model needs. Times are milliseconds into the period; distances are yards along the spline.
/// </summary>
public sealed class TransportKeyFrame
{
    internal TransportKeyFrame(TaxiPathNodeRecord node) => Node = node;

    /// <summary>The TaxiPathNode.dbc row this frame stands for.</summary>
    public TaxiPathNodeRecord Node { get; }

    /// <summary>Segment index of this frame in <see cref="Spline"/> (the segment that leaves it).</summary>
    public int Index { get; internal set; }

    /// <summary>Facing at the frame, from the path's tangent (vmangos <c>InitialOrientation</c>).</summary>
    public float InitialOrientation { get; internal set; }

    public float DistSinceStop { get; internal set; } = -1f;

    public float DistUntilStop { get; internal set; } = -1f;

    public float DistFromPrev { get; internal set; } = -1f;

    public float TimeFrom { get; internal set; }

    public float TimeTo { get; internal set; }

    /// <summary>The ship jumps from this frame to the next (map change, or the wrap from the last frame to the first).</summary>
    public bool Teleport { get; internal set; }

    /// <summary>The client drops a far-sailing ship by itself; it is destroyed and created again here (vmangos ferries 293 and 303).</summary>
    public bool Update { get; internal set; }

    public uint ArriveTime { get; internal set; }

    public uint DepartureTime { get; internal set; }

    /// <summary>The spline of the stretch this frame belongs to (frames between two teleports share one).</summary>
    public TransportSpline? Spline { get; internal set; }

    public float NextDistFromPrev { get; internal set; }

    public uint NextArriveTime { get; internal set; }

    /// <summary>vmangos <c>IsStopFrame</c>: TaxiPathNode action flag 2 (the ship waits <see cref="TaxiPathNodeRecord.Delay"/> seconds).</summary>
    public bool IsStopFrame => Node.Flags == 2;

    public Vector3 Position => new(Node.X, Node.Y, Node.Z);
}

/// <summary>
/// A ship or zeppelin route (vmangos <c>TransportTemplate</c>): the key frames, the maps it sails on and its period.
/// Built once from <c>gameobject_template</c> (type 15) and TaxiPathNode.dbc by <see cref="TransportTemplateBuilder"/>;
/// read-only afterwards except for <see cref="Spawned"/> (world thread).
/// </summary>
public sealed class TransportTemplate
{
    internal TransportTemplate(GameObjectTemplate gameObject, uint pathId)
    {
        GameObject = gameObject;
        PathId = pathId;
    }

    public GameObjectTemplate GameObject { get; }

    public uint Entry => GameObject.Entry;

    public uint PathId { get; }

    /// <summary>Yards per second at full speed (gameobject_template data1).</summary>
    public float MoveSpeed => GameObject.GetData(1);

    /// <summary>Yards per second squared (gameobject_template data2).</summary>
    public float AccelRate => GameObject.GetData(2);

    public IReadOnlyList<TransportKeyFrame> KeyFrames => Frames;

    internal List<TransportKeyFrame> Frames { get; } = [];

    public IReadOnlySet<uint> MapsUsed => Maps;

    internal SortedSet<uint> Maps { get; } = [];

    /// <summary>The route lies on one instanceable map, so every instance of it gets its own ship.</summary>
    public bool InInstance { get; internal set; }

    /// <summary>The full round trip in milliseconds (the computed value, or the <c>transports.period</c> override).</summary>
    public uint PathTime { get; internal set; }

    public float AccelTime { get; internal set; }

    public float AccelDist { get; internal set; }

    /// <summary>The single continent ship of this route exists (vmangos <c>TransportTemplate::spawned</c>).</summary>
    public bool Spawned { get; internal set; }
}

/// <summary>Why a <c>gameobject_template</c> type 15 row produced no route.</summary>
public enum TransportTemplateError
{
    None,
    NotAShip,
    NoPath,
    InvalidSpeed,
    BadPath,
}

/// <summary>
/// vmangos <c>TransportMgr::GenerateWaypoints</c> and <c>LoadTransportTemplates</c> (TransportMgr.cpp:48-354), re-implemented
/// (behaviour only). The arithmetic is single precision like the reference so the times match the client's own path model.
/// Rows the reference would trip an assertion on (a path too short to form a spline, zero speed or acceleration) are
/// refused instead of crashing.
/// </summary>
public static class TransportTemplateBuilder
{
    /// <summary>GAMEOBJECT_TYPE_MO_TRANSPORT.</summary>
    public const uint MoTransportType = 15;

    /// <summary>
    /// Build the route of one ship. <paramref name="isInstanceable"/> answers vmangos <c>MapEntry::Instanceable</c> for a map
    /// id. <paramref name="periodOverride"/> is the <c>transports.period</c> of the newest build at or below 5875 (0 or null:
    /// keep the computed period).
    /// </summary>
    public static TransportTemplate? Build(
        GameObjectTemplate gameObject, TaxiPathNodeCatalog pathNodes, Func<uint, bool> isInstanceable,
        uint? periodOverride, out TransportTemplateError error)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        ArgumentNullException.ThrowIfNull(pathNodes);
        ArgumentNullException.ThrowIfNull(isInstanceable);
        if (gameObject.Type != MoTransportType)
        {
            error = TransportTemplateError.NotAShip;
            return null;
        }

        uint pathId = gameObject.GetData(0);
        IReadOnlyList<TaxiPathNodeRecord> path = pathNodes.Nodes(pathId);
        if (path.Count == 0)
        {
            error = TransportTemplateError.NoPath;
            return null;
        }

        float speed = gameObject.GetData(1);
        float accel = gameObject.GetData(2);
        if (speed <= 0 || accel <= 0)
        {
            error = TransportTemplateError.InvalidSpeed;
            return null;
        }

        var template = new TransportTemplate(gameObject, pathId);
        if (!GenerateWaypoints(template, path, speed, accel, isInstanceable))
        {
            error = TransportTemplateError.BadPath;
            return null;
        }

        // TransportMgr::LoadTransportTemplates: the database period replaces the computed one ("our algorithm is not
        // perfect"); only the last frame's departure follows it.
        if (periodOverride is > 0 and { } period)
        {
            template.PathTime = period;
            template.Frames[^1].DepartureTime = period;
        }

        error = TransportTemplateError.None;
        return template;
    }

    private static bool GenerateWaypoints(
        TransportTemplate template, IReadOnlyList<TaxiPathNodeRecord> path, float speed, float accel, Func<uint, bool> isInstanceable)
    {
        // Three nodes is the least the reference loop (nodes 1 .. n-2) can turn into a key frame.
        if (path.Count < 3)
        {
            return false;
        }

        List<TransportKeyFrame> keyFrames = template.Frames;
        var splinePath = new List<Vector3>();

        // The orientation spline runs through every node plus extrapolated points at both ends, so the tangent exists at
        // every node (TransportMgr.cpp:116-125).
        var all = new List<Vector3>(path.Count + 3);
        foreach (TaxiPathNodeRecord node in path)
        {
            all.Add(new Vector3(node.X, node.Y, node.Z));
        }

        all.Insert(0, TransportSpline.Lerp(all[0], all[1], -0.2f));
        all.Add(TransportSpline.Lerp(all[^1], all[^2], -0.2f));
        all.Add(TransportSpline.Lerp(all[^1], all[^2], -1.0f));
        TransportSpline orientation = TransportSpline.Raw([.. all]);

        // The first and the last node are never key frames (the reference loop runs 1 .. n-2). A node with action flag 1
        // or before a map change ends a stretch: the frame before it teleports, and the node after it is skipped.
        bool mapChange = false;
        for (int i = 1; i < path.Count - 1; i++)
        {
            if (mapChange)
            {
                mapChange = false;
                continue;
            }

            TaxiPathNodeRecord node = path[i];
            if ((node.Flags & 1) != 0 || node.MapId != path[i + 1].MapId)
            {
                if (keyFrames.Count == 0)
                {
                    return false; // the reference marks keyFrames.back() of an empty list
                }

                keyFrames[^1].Teleport = true;
                mapChange = true;
                continue;
            }

            Vector3 tangent = orientation.Derivative(i + 1, 0f);
            var frame = new TransportKeyFrame(node)
            {
                InitialOrientation = NormalizeOrientation((float)(MathF.Atan2(tangent.Y, tangent.X) + Math.PI)),
            };
            keyFrames.Add(frame);
            splinePath.Add(frame.Position);
            template.Maps.Add(node.MapId);
        }

        if (keyFrames.Count == 0)
        {
            return false;
        }

        if (template.Maps.Count > 1)
        {
            // Continent ships only: the reference asserts no map of a multi-map route is instanceable.
            if (template.Maps.Any(isInstanceable))
            {
                return false;
            }

            template.InInstance = false;
        }
        else
        {
            template.InInstance = isInstanceable(template.Maps.Min);
        }

        // From the last frame back to the first is always a teleport, even on a closed route.
        keyFrames[^1].Teleport = true;

        float accelDist = 0.5f * speed * speed / accel;
        template.AccelTime = speed / accel;
        template.AccelDist = accelDist;

        int firstStop = -1;
        int lastStop = -1;

        // The first frame is reached by teleportation.
        keyFrames[0].DistFromPrev = 0;
        keyFrames[0].Index = 1;
        if (keyFrames[0].IsStopFrame)
        {
            firstStop = 0;
            lastStop = 0;
        }

        // Every stretch between teleports gets its own spline (TransportMgr.cpp:186-226).
        int start = 0;
        for (int i = 1; i < keyFrames.Count; i++)
        {
            if (keyFrames[i - 1].Teleport || i + 1 == keyFrames.Count)
            {
                int extra = !keyFrames[i - 1].Teleport ? 1 : 0;
                int controls = i - start + extra;
                if (controls < 2)
                {
                    return false; // a one-frame stretch has no segment (the reference reads past its controls)
                }

                TransportSpline spline = TransportSpline.CatmullRom(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(splinePath).Slice(start, controls));
                for (int j = start; j < i + extra; j++)
                {
                    keyFrames[j].Index = j - start + 1;
                    keyFrames[j].DistFromPrev = (float)spline.Length(j - start, j + 1 - start);
                    if (j > 0)
                    {
                        keyFrames[j - 1].NextDistFromPrev = keyFrames[j].DistFromPrev;
                    }

                    keyFrames[j].Spline = spline;
                }

                if (keyFrames[i - 1].Teleport)
                {
                    keyFrames[i].Index = i - start + 1;
                    keyFrames[i].DistFromPrev = 0.0f;
                    keyFrames[i - 1].NextDistFromPrev = 0.0f;
                    keyFrames[i].Spline = spline;
                }

                start = i;
            }

            if (keyFrames[i].IsStopFrame)
            {
                if (firstStop == -1)
                {
                    firstStop = i;
                }

                lastStop = i;
            }
        }

        keyFrames[^1].NextDistFromPrev = keyFrames[0].DistFromPrev;

        if (firstStop == -1 || lastStop == -1)
        {
            firstStop = lastStop = 0;
        }

        int count = keyFrames.Count;

        // At a stop distSinceStop is 0 and distUntilStop runs to the next stop (two stops in a row exist).
        float tmpDist = 0.0f;
        for (int i = 0; i < count; i++)
        {
            int j = (i + lastStop) % count;
            if (keyFrames[j].IsStopFrame || j == lastStop)
            {
                tmpDist = 0.0f;
            }
            else
            {
                tmpDist += keyFrames[j].DistFromPrev;
            }

            keyFrames[j].DistSinceStop = tmpDist;
        }

        tmpDist = 0.0f;
        for (int i = count - 1; i >= 0; i--)
        {
            int j = (i + firstStop) % count;
            tmpDist += keyFrames[(j + 1) % count].DistFromPrev;
            keyFrames[j].DistUntilStop = tmpDist;
            if (keyFrames[j].IsStopFrame || j == firstStop)
            {
                tmpDist = 0.0f;
            }
        }

        foreach (TransportKeyFrame frame in keyFrames)
        {
            float totalDist = frame.DistSinceStop + frame.DistUntilStop;
            if (totalDist < 2 * accelDist)
            {
                // Too short to reach full speed.
                if (frame.DistSinceStop < frame.DistUntilStop)
                {
                    float segmentTime = 2.0f * MathF.Sqrt((frame.DistUntilStop + frame.DistSinceStop) / accel);
                    frame.TimeTo = segmentTime - MathF.Sqrt(2 * frame.DistSinceStop / accel);
                }
                else
                {
                    frame.TimeTo = MathF.Sqrt(2 * frame.DistUntilStop / accel);
                }
            }
            else if (frame.DistSinceStop < accelDist)
            {
                // Still accelerating, will reach full speed.
                float segmentTime = ((frame.DistUntilStop + frame.DistSinceStop) / speed) + (speed / accel);
                frame.TimeTo = segmentTime - MathF.Sqrt(2 * frame.DistSinceStop / accel);
            }
            else if (frame.DistUntilStop < accelDist)
            {
                // Already braking after full speed.
                frame.TimeTo = MathF.Sqrt(2 * frame.DistUntilStop / accel);
            }
            else
            {
                // Cruising.
                frame.TimeTo = (frame.DistUntilStop / speed) + (0.5f * speed / accel);
            }
        }

        float stretchTime = 0.0f;
        for (int i = 0; i < count; i++)
        {
            int j = (i + lastStop) % count;
            if (keyFrames[j].IsStopFrame || j == lastStop)
            {
                stretchTime = keyFrames[j].TimeTo;
            }

            keyFrames[j].TimeFrom = stretchTime - keyFrames[j].TimeTo;
        }

        // Path times (TransportMgr.cpp:315-341).
        keyFrames[0].ArriveTime = 0;
        float curPathTime = 0.0f;
        if (keyFrames[0].IsStopFrame)
        {
            curPathTime = keyFrames[0].Node.Delay;
            keyFrames[0].DepartureTime = (uint)(curPathTime * 1000u);
        }

        for (int i = 1; i < count; i++)
        {
            curPathTime += keyFrames[i - 1].TimeTo;
            if (keyFrames[i].IsStopFrame)
            {
                keyFrames[i].ArriveTime = (uint)(curPathTime * 1000u);
                keyFrames[i - 1].NextArriveTime = keyFrames[i].ArriveTime;
                curPathTime += keyFrames[i].Node.Delay;
                keyFrames[i].DepartureTime = (uint)(curPathTime * 1000u);
            }
            else
            {
                curPathTime -= keyFrames[i].TimeTo;
                keyFrames[i].ArriveTime = (uint)(curPathTime * 1000u);
                keyFrames[i - 1].NextArriveTime = keyFrames[i].ArriveTime;
                keyFrames[i].DepartureTime = keyFrames[i].ArriveTime;
            }
        }

        keyFrames[^1].NextArriveTime = keyFrames[^1].DepartureTime;

        // The client destroys a ship by itself after a while: the Feathermoon (303) and Teldrassil (293) ferries are refreshed
        // mid course.
        if (template.PathId is 303 or 293 && count > 12)
        {
            keyFrames[12].Update = true;
        }

        template.PathTime = keyFrames[^1].DepartureTime;
        return true;
    }

    /// <summary>vmangos <c>Geometry::NormalizeOrientation</c> (Geometry.h:96-108): fmod into [0, 2 pi), negatives emulated.</summary>
    internal static float NormalizeOrientation(float o)
    {
        const float TwoPi = 2.0f * MathF.PI;
        if (o < 0)
        {
            float mod = o * -1;
            mod %= TwoPi;
            return -mod + TwoPi;
        }

        return o % TwoPi;
    }
}
