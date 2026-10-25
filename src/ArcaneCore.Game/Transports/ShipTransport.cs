using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// A ship or zeppelin (vmangos <c>ShipTransport</c>, a GAMEOBJECT_TYPE_MO_TRANSPORT game object; Transports/Transport.cpp,
/// re-implemented). It runs on a timer along its <see cref="TransportTemplate"/>: the position is a function of the time
/// since it was created, so the client, which gets the path progress in the create block, sails it in step from its own
/// TaxiPathNode data. Passengers are units whose movement block carries this ship's GUID and their offset on it; the
/// ship moves them with it every <see cref="PositionUpdateDelayMs"/>.
/// <para>
/// A ship is not an ordinary map object: vmangos keeps transports out of the grids and the visibility lists and sends
/// them to every player of the map (<c>Map::SendInitTransports</c>), so <see cref="WorldObject.Map"/> stays null and
/// <see cref="CurrentMap"/> names the map it sails on. World thread only.
/// </para>
/// </summary>
public sealed partial class ShipTransport : WorldObject
{
    /// <summary>vmangos <c>ShipTransport::Update</c> <c>positionUpdateDelay</c>: the position is recomputed at most every 50 ms.</summary>
    public const uint PositionUpdateDelayMs = 50;

    /// <summary>Elapsed moving time without waypoint/position advancement before <c>transport/stalled</c> is reported.</summary>
    public const uint StallThresholdMs = 1000;

    /// <summary>GO_ANIMPROGRESS_DEFAULT.</summary>
    private const uint AnimProgressDefault = 100;

    private readonly TransportSystem _system;
    private readonly List<Unit> _passengers = [];
    private readonly uint _creationMs;
    private readonly uint _startProgress;
    private int _current;
    private int _next;
    private uint _pathProgress;
    private int _positionTimer;
    private uint _stalledMovingMs;
    private bool _stallReported;

    internal ShipTransport(TransportSystem system, TransportTemplate template, int startFrame, uint nowMs)
        : base(GuidFor(template.Entry), Game.TypeId.GameObject, Game.TypeMask.Object | Game.TypeMask.GameObject, UpdateFields.GameobjectEnd)
    {
        _system = system;
        Template = template;
        Period = template.PathTime;

        // ShipTransport::Create (Transport.cpp:51-100): placed at the start frame, facing along the path.
        TransportKeyFrame start = template.KeyFrames[startFrame];
        MapId = start.Node.MapId;
        SetPosition(start.Node.X, start.Node.Y, start.Node.Z, start.InitialOrientation);
        _current = startFrame;
        _next = (startFrame + 1) % template.KeyFrames.Count;
        _creationMs = nowMs;
        _pathProgress = start.ArriveTime;
        _startProgress = _pathProgress;
        IsMoving = true;

        GameObjectTemplateFields(template);
    }

    public override ReadOnlySpan<ushort> FieldFlags => UpdateFieldTables.GameObjectVisibility;

    public override ReadOnlySpan<bool> GuidFieldStarts => UpdateFieldTables.GameObjectGuidStarts;

    /// <summary>vmangos ShipTransport constructor (build &gt; 1.8.4): UPDATEFLAG_TRANSPORT | UPDATEFLAG_ALL | UPDATEFLAG_HAS_POSITION.</summary>
    public override ObjectUpdateFlags CreateUpdateFlags => ObjectUpdateFlags.Transport | ObjectUpdateFlags.All | ObjectUpdateFlags.HasPosition;

    public TransportTemplate Template { get; }

    public uint Entry => Template.Entry;

    /// <summary>The round trip in milliseconds (vmangos <c>GetPeriod</c>).</summary>
    public uint Period { get; }

    /// <summary>
    /// vmangos <c>m_pathProgress</c>: milliseconds of path time since the period started counting (the start frame's arrival
    /// plus the time since creation). Sent in the create block; the client takes it modulo its own period.
    /// </summary>
    public uint PathProgress => _pathProgress;

    /// <summary>False while the ship waits at a stop.</summary>
    public bool IsMoving { get; private set; }

    /// <summary>The map the ship sails on now (null between maps and after it was removed).</summary>
    public Map? CurrentMap { get; internal set; }

    public TransportKeyFrame CurrentFrame => Template.KeyFrames[_current];

    public TransportKeyFrame NextFrame => Template.KeyFrames[_next];

    /// <summary>The units riding this ship, in boarding order.</summary>
    public IReadOnlyList<Unit> Passengers => _passengers;

    /// <summary>vmangos <c>Object::_Create(entry, 0, HIGHGUID_MO_TRANSPORT)</c>: the counter is the template entry and no entry is embedded.</summary>
    public static ObjectGuid GuidFor(uint entry) => new(((ulong)HighGuid.MoTransport << 48) | entry);

    // --- passengers (GenericTransport::AddPassenger / RemovePassenger, Transport.cpp:202-257) -----------------------------

    /// <summary>
    /// Board <paramref name="passenger"/> (vmangos <c>GenericTransport::AddPassenger</c>). Its movement block gets the
    /// ONTRANSPORT flag and this ship's GUID; when it changed ships and <paramref name="adjustCoords"/> is set, its offset
    /// is computed from its world position. False when it already rides this ship.
    /// </summary>
    public bool AddPassenger(Unit passenger, bool adjustCoords = true)
    {
        ArgumentNullException.ThrowIfNull(passenger);
        if (_passengers.Contains(passenger))
        {
            return false;
        }

        passenger.Transport?.RemovePassenger(passenger);
        _passengers.Add(passenger);
        passenger.Transport = this;

        MovementInfo movement = passenger.Movement;
        bool changedTransports = movement.TransportGuid != Guid.Value;
        float x = movement.TransportX, y = movement.TransportY, z = movement.TransportZ, o = movement.TransportOrientation;
        if (changedTransports && adjustCoords)
        {
            x = passenger.X;
            y = passenger.Y;
            z = passenger.Z;
            o = passenger.Orientation;
            CalculatePassengerOffset(ref x, ref y, ref z, ref o);
        }

        passenger.SetTransportData(Guid, x, y, z, o);
        _system.OnBoarded(this, passenger);
        return true;
    }

    /// <summary>
    /// Take <paramref name="passenger"/> off (vmangos <c>GenericTransport::RemovePassenger</c>): the link and the transport
    /// GUID and offset are cleared; the ONTRANSPORT flag is left to the caller, as in the reference. False when it was not aboard.
    /// </summary>
    public bool RemovePassenger(Unit passenger)
    {
        ArgumentNullException.ThrowIfNull(passenger);
        if (!_passengers.Remove(passenger))
        {
            return false;
        }

        passenger.Transport = null;
        passenger.ClearTransportData();
        _system.OnLeft(this, passenger);
        return true;
    }

    /// <summary>
    /// vmangos <c>GenericTransport::AddFollowerToTransport</c>: a unit following <paramref name="passenger"/> (a pet) boards with
    /// the passenger's offset and is put at the passenger's position.
    /// </summary>
    public void AddFollower(Unit passenger, Unit follower)
    {
        ArgumentNullException.ThrowIfNull(passenger);
        ArgumentNullException.ThrowIfNull(follower);
        AddPassenger(follower);
        MovementInfo leader = passenger.Movement;
        follower.SetTransportData(Guid, leader.TransportX, leader.TransportY, leader.TransportZ, leader.TransportOrientation);
        follower.RelocateOnTransport(leader.X, leader.Y, leader.Z, leader.Orientation);
    }

    /// <summary>vmangos <c>GenericTransport::RemoveFollowerFromTransport</c>: the follower leaves and is put at the passenger's position.</summary>
    public void RemoveFollower(Unit passenger, Unit follower)
    {
        ArgumentNullException.ThrowIfNull(passenger);
        ArgumentNullException.ThrowIfNull(follower);
        RemovePassenger(follower);
        follower.RemoveMovementFlags(MovementFlags.OnTransport);
        MovementInfo leader = passenger.Movement;
        follower.RelocateOnTransport(leader.X, leader.Y, leader.Z, leader.Orientation);
    }

    /// <summary>
    /// vmangos <c>GenericTransport::UpdatePassengerPosition</c>: put a passenger at its offset from the ship's current
    /// position. Skipped for a passenger that is not on the ship's map yet (a player still loading after a map change)
    /// and for a position outside the map.
    /// </summary>
    public void UpdatePassengerPosition(Unit passenger)
    {
        ArgumentNullException.ThrowIfNull(passenger);
        if (CurrentMap is null || !ReferenceEquals(passenger.Map, CurrentMap))
        {
            if (CurrentMap is not null
                && passenger is Player stranded
                && !LocomotionEnvironment.For(CurrentMap).IsBeingTeleported(stranded)
                && !stranded.IsLoggingOut
                && !stranded.Locomotion.SuppressAnomalies
                && TransportAnomalies.TryLatch(stranded.Locomotion, TransportAnomalyLatch.PassengerLeftBehind))
            {
                TransportAnomalies.Report(
                    "passenger-left-behind",
                    stranded,
                    $"Passenger is still attached to transport entry {Entry} on map {CurrentMap.MapId} while player map is {stranded.Map?.MapId.ToString() ?? "null"} without a pending teleport.");
            }
            return;
        }

        MovementInfo movement = passenger.Movement;
        float tx = movement.TransportX, ty = movement.TransportY, tz = movement.TransportZ, to = movement.TransportOrientation;
        if (!TransportSystem.IsOffsetWithinBounds(tx, ty, tz))
        {
            ReportPassengerLeftBehind(passenger, $"Passenger offset ({tx:F2}, {ty:F2}, {tz:F2}) on transport entry {Entry} exceeds max offset {TransportSystem.MaxPassengerOffset:F0}yd on map {CurrentMap.MapId}.");
            return;
        }

        float x = tx, y = ty, z = tz, o = to;
        CalculatePassengerPosition(ref x, ref y, ref z, ref o);
        if (!GridDefines.IsValidMapCoord(x) || !GridDefines.IsValidMapCoord(y) || !GridDefines.IsValidZCoord(z))
        {
            ReportPassengerDesync(passenger, $"Calculated passenger position ({x:F2}, {y:F2}, {z:F2}) from offset ({tx:F2}, {ty:F2}, {tz:F2}) on transport entry {Entry} is outside valid map coordinates on map {CurrentMap.MapId}.");
            return; // vmangos logs "[TRANSPORTS] Object ... has invalid position on transport." and leaves it
        }

        if (passenger is Player validPlayer)
        {
            validPlayer.Locomotion.TransportDesyncReportedInEpisode = false;
            validPlayer.Locomotion.PassengerLeftBehindReportedInEpisode = false;
        }

        passenger.RelocateOnTransport(x, y, z, o);
    }

    private void ReportPassengerLeftBehind(Unit passenger, string detail)
    {
        if (passenger is Player player)
        {
            if (player.Locomotion.SuppressAnomalies || !TransportAnomalies.TryLatch(player.Locomotion, TransportAnomalyLatch.PassengerLeftBehind))
            {
                return;
            }

            TransportAnomalies.Report("passenger-left-behind", player, detail);
        }
        else
        {
            AnomalyLedger.Shared.Report(
                "transport",
                "passenger-left-behind",
                $"creature entry {passenger.Guid.Entry} (guid {passenger.Guid.Counter})",
                detail,
                AnomalySeverity.Warning);
        }
    }

    private void ReportPassengerDesync(Unit passenger, string detail)
    {
        if (passenger is Player player)
        {
            if (player.Locomotion.SuppressAnomalies || !TransportAnomalies.TryLatch(player.Locomotion, TransportAnomalyLatch.TransportDesync))
            {
                return;
            }

            TransportAnomalies.Report("passenger-desync", player, detail);
        }
        else
        {
            AnomalyLedger.Shared.Report(
                "transport",
                "passenger-desync",
                $"creature entry {passenger.Guid.Entry} (guid {passenger.Guid.Counter})",
                detail,
                AnomalySeverity.Warning);
        }
    }

    /// <summary>Transform an offset on this ship into world coordinates (vmangos <c>CalculatePassengerPosition</c>).</summary>
    public void CalculatePassengerPosition(ref float x, ref float y, ref float z, ref float o)
        => CalculatePassengerPosition(ref x, ref y, ref z, ref o, X, Y, Z, Orientation);

    /// <summary>Transform world coordinates into an offset on this ship (vmangos <c>CalculatePassengerOffset</c>).</summary>
    public void CalculatePassengerOffset(ref float x, ref float y, ref float z, ref float o)
        => CalculatePassengerOffset(ref x, ref y, ref z, ref o, X, Y, Z, Orientation);

    /// <summary>vmangos <c>GenericTransport::CalculatePassengerPosition</c> (Transport.cpp:521-531): rotate by the ship's facing, then translate.</summary>
    public static void CalculatePassengerPosition(ref float x, ref float y, ref float z, ref float o, float transX, float transY, float transZ, float transO)
    {
        float inx = x, iny = y, inz = z;
        o = TransportTemplateBuilder.NormalizeOrientation(transO + o);
        x = transX + (inx * MathF.Cos(transO)) - (iny * MathF.Sin(transO));
        y = transY + (iny * MathF.Cos(transO)) + (inx * MathF.Sin(transO));
        z = transZ + inz;
    }

    /// <summary>vmangos <c>GenericTransport::CalculatePassengerOffset</c> (Transport.cpp:533-547): the inverse of <see cref="CalculatePassengerPosition(ref float, ref float, ref float, ref float, float, float, float, float)"/>.</summary>
    public static void CalculatePassengerOffset(ref float x, ref float y, ref float z, ref float o, float transX, float transY, float transZ, float transO)
    {
        o = TransportTemplateBuilder.NormalizeOrientation(o - transO);
        float dx = x - transX;
        float dy = y - transY;
        z -= transZ;
        float sinO = MathF.Sin(transO);
        float cosO = MathF.Cos(transO);
        x = (dx * cosO) + (dy * sinO);
        y = (dy * cosO) - (dx * sinO);
    }

    // --- motion (ShipTransport::Update, Transport.cpp:316-381) -------------------------------------------------------------

    /// <summary>
    /// Advance the ship to <paramref name="nowMs"/> (world clock). Stops, departures and map changes follow the key frames;
    /// the position (and every passenger's) is recomputed at most every <see cref="PositionUpdateDelayMs"/>.
    /// </summary>
    internal void Update(uint nowMs)
    {
        IReadOnlyList<TransportKeyFrame> frames = Template.KeyFrames;
        if (frames.Count <= 1 || Period == 0)
        {
            ReportStalled($"Transport entry {Entry} cannot advance: KeyFrames.Count={frames.Count}, Period={Period} on map {MapId}.");
            return;
        }

        uint currentMsTime = unchecked(nowMs - _creationMs + _startProgress);
        // Same-tick re-entry after a map change (vmangos: the path progress already equals this timestamp).
        // The step is unsigned: NowMs is a uint32 and wraps after 49.7 days, and a numerically smaller
        // timestamp is that wrap, not time running backwards. Comparing the raw timestamps froze the ship
        // there — and everyone aboard it — for the rest of the uptime.
        uint elapsed = unchecked(currentMsTime - _pathProgress);
        if (elapsed == 0)
        {
            return;
        }

        uint diff = elapsed;
        _pathProgress = currentMsTime; // vmangos: unless a stop is pending, which nothing sets

        if (!ArcaneCore.Kernel.Diagnostics.Invariant.Check(Period > 0, $"ShipTransport {Guid} (Entry {Entry}) has Period == 0."))
        {
            ReportStalled($"Transport entry {Entry} has Period == 0 on map {MapId}.");
            return;
        }

        uint pathProgress = _pathProgress % Period;
        float prevX = X, prevY = Y, prevZ = Z;
        int prevFrame = _current;
        bool exhaustedGuard = true;

        // Bounded where the reference loops until a frame matches: inconsistent frame times cannot hang the world thread.
        for (int guard = 0; guard <= frames.Count; guard++)
        {
            TransportKeyFrame current = frames[_current];
            if (pathProgress >= current.ArriveTime && pathProgress < current.DepartureTime)
            {
                IsMoving = false;
                exhaustedGuard = false;
                break; // waiting at a stop
            }

            IsMoving = true;
            if (pathProgress >= current.DepartureTime && pathProgress < current.NextArriveTime)
            {
                exhaustedGuard = false;
                break; // between this frame and the next
            }

            MoveToNextWayPoint();
            current = frames[_current];
            if (current.Node.MapId != MapId || current.Teleport)
            {
                TransportKeyFrame next = frames[_next];
                _system.TeleportTransport(this, next.Node.MapId, next.Node.X, next.Node.Y, next.Node.Z, next.InitialOrientation);
                _stalledMovingMs = 0;
                _stallReported = false;
                return;
            }

            if (current.Update)
            {
                _system.Refresh(this);
            }
        }

        if (exhaustedGuard)
        {
            ReportStalled($"Transport entry {Entry} exhausted keyframe lookup guard ({frames.Count} frames) at pathProgress={pathProgress}/{Period} on map {MapId}.");
        }

        _positionTimer -= (int)Math.Min(diff, int.MaxValue);
        if (_positionTimer <= 0)
        {
            _positionTimer = (int)PositionUpdateDelayMs;
            if (IsMoving && pathProgress != 0)
            {
                TransportKeyFrame current = frames[_current];
                float t = CalculateSegmentPos(pathProgress * 0.001f);
                if (current.Spline is { } spline && current.Index >= spline.First && current.Index < spline.Last && float.IsFinite(t))
                {
                    Vector3 position = spline.Evaluate(current.Index, t);
                    Vector3 direction = spline.Derivative(current.Index, t);
                    UpdatePosition(position.X, position.Y, position.Z, (float)(MathF.Atan2(direction.Y, direction.X) + Math.PI));
                }
            }
        }

        if (!IsMoving)
        {
            _stalledMovingMs = 0;
            _stallReported = false;
        }
        else
        {
            float movedSq = ((X - prevX) * (X - prevX)) + ((Y - prevY) * (Y - prevY)) + ((Z - prevZ) * (Z - prevZ));
            if (_current != prevFrame || (float.IsFinite(movedSq) && movedSq > 1e-4f))
            {
                _stalledMovingMs = 0;
                _stallReported = false;
            }
            else
            {
                _stalledMovingMs = unchecked(_stalledMovingMs + diff);
                if (_stalledMovingMs >= StallThresholdMs)
                {
                    ReportStalled($"Transport entry {Entry} is marked IsMoving at frame {_current} (pathProgress={pathProgress}/{Period}) on map {MapId} at ({X:F2}, {Y:F2}, {Z:F2}) but position has not advanced for {_stalledMovingMs}ms.");
                }
            }
        }
    }

    private void ReportStalled(string detail)
    {
        if (_stallReported)
        {
            return;
        }

        _stallReported = true;
        AnomalyLedger.Shared.Report(
            "transport",
            "stalled",
            $"transport entry {Entry}",
            detail,
            AnomalySeverity.Warning);
    }

    /// <summary>vmangos <c>GenericTransport::UpdatePosition</c>: move the ship, then every passenger with it.</summary>
    internal void UpdatePosition(float x, float y, float z, float o)
    {
        SetPosition(x, y, z, o);
        if (_passengers.Count == 0)
        {
            return;
        }

        foreach (Unit passenger in _passengers.ToArray())
        {
            UpdatePassengerPosition(passenger);
        }
    }

    /// <summary>Put the ship at a frame after a map change (the end of vmangos <c>TeleportTransport</c>).</summary>
    internal void Relocate(uint mapId, float x, float y, float z, float o)
    {
        MapId = mapId;
        SetPosition(x, y, z, o);
    }

    private void MoveToNextWayPoint()
    {
        _current = _next;
        _next = (_next + 1) % Template.KeyFrames.Count;
    }

    // vmangos ShipTransport::CalculateSegmentPos (Transport.cpp:383-413): the fraction of the current segment covered at
    // path time `now` (seconds), from the nearer stop with constant acceleration and braking.
    private float CalculateSegmentPos(float now)
    {
        TransportKeyFrame frame = Template.KeyFrames[_current];
        float speed = Template.MoveSpeed;
        float accel = Template.AccelRate;
        float timeSinceStop = frame.TimeFrom + (now - ((1.0f / 1000f) * frame.DepartureTime));
        float timeUntilStop = frame.TimeTo - (now - ((1.0f / 1000f) * frame.DepartureTime));
        float accelTime = Template.AccelTime;
        float accelDist = Template.AccelDist;
        float segmentPos, dist;
        if (timeSinceStop < timeUntilStop)
        {
            dist = timeSinceStop < accelTime
                ? 0.5f * accel * timeSinceStop * timeSinceStop
                : accelDist + ((timeSinceStop - accelTime) * speed);
            segmentPos = dist - frame.DistSinceStop;
        }
        else
        {
            dist = timeUntilStop < accelTime
                ? 0.5f * accel * timeUntilStop * timeUntilStop
                : accelDist + ((timeUntilStop - accelTime) * speed);
            segmentPos = frame.DistUntilStop - dist;
        }

        return frame.NextDistFromPrev > 0f && float.IsFinite(frame.NextDistFromPrev)
            ? segmentPos / frame.NextDistFromPrev
            : 0f;
    }

    // ShipTransport::Create: the GAMEOBJECT_* fields from the template; position and rotation fields stay 0.
    private void GameObjectTemplateFields(TransportTemplate template)
    {
        Kernel.WorldData.GameObjects.GameObjectTemplate info = template.GameObject;
        SetFloat(UpdateFields.ObjectFieldScaleX, info.Size > 0 ? info.Size : 1.0f);
        SetUInt32(UpdateFields.ObjectFieldEntry, info.Entry);
        SetUInt32(UpdateFields.GameobjectFaction, info.Faction);
        SetUInt32(UpdateFields.GameobjectFlags, info.Flags);
        SetUInt32(UpdateFields.GameobjectDisplayid, info.DisplayId);
        SetUInt32(UpdateFields.GameobjectState, (uint)GameObjectState.Ready);
        SetUInt32(UpdateFields.GameobjectTypeId, info.Type);
        SetUInt32(UpdateFields.GameobjectAnimprogress, AnimProgressDefault);
    }
}
