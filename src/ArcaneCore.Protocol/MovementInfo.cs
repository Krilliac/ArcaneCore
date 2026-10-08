namespace ArcaneCore.Protocol;

/// <summary>
/// Movement flags of a 1.12.1 <see cref="MovementInfo"/>. Values verified against vmangos
/// MovementInfo.h, cmangos-classic Object.h and mangoszero Unit.h, which agree.
/// <para>
/// Discrepancy: gtker/wow_messages labels 0x200 as ON_TRANSPORT; all three MaNGOS cores
/// use 0x02000000 as the flag that gates the transport block, and so does ArcaneCore.
/// </para>
/// </summary>
[Flags]
public enum MovementFlags : uint
{
    None = 0x00000000,
    Forward = 0x00000001,
    Backward = 0x00000002,
    StrafeLeft = 0x00000004,
    StrafeRight = 0x00000008,
    TurnLeft = 0x00000010,
    TurnRight = 0x00000020,
    PitchUp = 0x00000040,
    PitchDown = 0x00000080,
    WalkMode = 0x00000100,
    Levitating = 0x00000400,
    FixedZ = 0x00000800,
    Root = 0x00001000,
    Jumping = 0x00002000,
    FallingFar = 0x00004000,
    Swimming = 0x00200000,
    SplineEnabled = 0x00400000,
    Flying = 0x01000000,
    OnTransport = 0x02000000,
    SplineElevation = 0x04000000,
    WaterWalking = 0x10000000,
    SafeFall = 0x20000000,
    Hover = 0x40000000,

    /// <summary>Translation/pitch/jump flags (vmangos MOVEFLAG_MASK_MOVING).</summary>
    MaskMoving = Forward | Backward | StrafeLeft | StrafeRight | PitchUp | PitchDown | Jumping
        | FallingFar | SplineElevation,
}

/// <summary>
/// The vanilla movement block carried by every MSG_MOVE_* packet and by living objects'
/// create updates. Layout verified against vmangos/cmangos-classic MovementInfo::Read/Write:
/// flags(u32) time(u32) x y z o(f32) [transport: guid(u64) x y z o] [swimming: pitch(f32)]
/// fallTime(u32) [jumping: zSpeed cos sin xySpeed (f32)] [splineElevation: f32].
/// <para>
/// Reference discrepancy (transport block only): mangoszero reads an extra u32 transport time
/// and skips fallTime on transports; gtker/wow_messages uses a packed GUID plus u32 time.
/// ArcaneCore follows the vmangos/cmangos layout (the reference whose ships it implements,
/// docs/areas/transports.md) until a real client capture on a boat settles it.
/// </para>
/// </summary>
public struct MovementInfo
{
    public MovementFlags Flags;
    public uint Time;
    public float X;
    public float Y;
    public float Z;
    public float Orientation;

    public ulong TransportGuid;
    public float TransportX;
    public float TransportY;
    public float TransportZ;
    public float TransportOrientation;

    public float Pitch;
    public uint FallTime;

    public float JumpZSpeed;
    public float JumpCosAngle;
    public float JumpSinAngle;
    public float JumpXySpeed;

    public float SplineElevation;

    public readonly bool HasFlag(MovementFlags flag) => (Flags & flag) != 0;

    /// <summary>
    /// Remove flag combinations a real client never sends (vmangos MovementInfo::CorrectData, Object.cpp:153-189,
    /// "causing client freezes"): Root together with any moving flag (MASK_MOVING) drops Root; turning left and right,
    /// strafing left and right, pitching up and down and moving forward and backward at once drop both. The
    /// "cannot hover without the aura" rule is commented out in vmangos and is not applied.
    /// </summary>
    public void CorrectData()
    {
        if (HasFlag(MovementFlags.Root) && (Flags & MovementFlags.MaskMoving) != 0)
        {
            Flags &= ~MovementFlags.Root;
        }

        RemoveBoth(MovementFlags.TurnLeft, MovementFlags.TurnRight);
        RemoveBoth(MovementFlags.StrafeLeft, MovementFlags.StrafeRight);
        RemoveBoth(MovementFlags.PitchUp, MovementFlags.PitchDown);
        RemoveBoth(MovementFlags.Forward, MovementFlags.Backward);
    }

    private void RemoveBoth(MovementFlags a, MovementFlags b)
    {
        if (HasFlag(a) && HasFlag(b))
        {
            Flags &= ~(a | b);
        }
    }
    /// <summary>Parse a movement block; throws <see cref="ArgumentOutOfRangeException"/> (the <see cref="MalformedPacket"/> outcome) if truncated.</summary>
    public static MovementInfo Read(ref PacketReader reader)
    {
        if (!TryRead(ref reader, out MovementInfo info))
        {
            MalformedPacket.Throw("movement block past the end of the payload");
        }

        return info;
    }

    /// <summary>
    /// Parse a movement block without throwing: false (and the cursor left at the block's start) when
    /// the payload is too short for the fields its own flags announce. Allocation-free.
    /// </summary>
    public static bool TryRead(ref PacketReader reader, out MovementInfo info)
    {
        info = default;
        int start = reader.Position;
        PacketReader cursor = reader;
        if (!cursor.TryReadUInt32(out uint flags)
            || !cursor.TryReadUInt32(out info.Time)
            || !cursor.TryReadSingle(out info.X)
            || !cursor.TryReadSingle(out info.Y)
            || !cursor.TryReadSingle(out info.Z)
            || !cursor.TryReadSingle(out info.Orientation))
        {
            return Fail(ref info);
        }

        info.Flags = (MovementFlags)flags;

        if (info.HasFlag(MovementFlags.OnTransport)
            && (!cursor.TryReadUInt64(out info.TransportGuid)
                || !cursor.TryReadSingle(out info.TransportX)
                || !cursor.TryReadSingle(out info.TransportY)
                || !cursor.TryReadSingle(out info.TransportZ)
                || !cursor.TryReadSingle(out info.TransportOrientation)))
        {
            return Fail(ref info);
        }

        if (info.HasFlag(MovementFlags.Swimming) && !cursor.TryReadSingle(out info.Pitch))
        {
            return Fail(ref info);
        }

        if (!cursor.TryReadUInt32(out info.FallTime))
        {
            return Fail(ref info);
        }

        if (info.HasFlag(MovementFlags.Jumping)
            && (!cursor.TryReadSingle(out info.JumpZSpeed)
                || !cursor.TryReadSingle(out info.JumpCosAngle)
                || !cursor.TryReadSingle(out info.JumpSinAngle)
                || !cursor.TryReadSingle(out info.JumpXySpeed)))
        {
            return Fail(ref info);
        }

        if (info.HasFlag(MovementFlags.SplineElevation) && !cursor.TryReadSingle(out info.SplineElevation))
        {
            return Fail(ref info);
        }

        System.Diagnostics.Debug.Assert(cursor.Position >= start + 28, "a movement block is at least 28 bytes");
        reader = cursor;
        return true;

        static bool Fail(ref MovementInfo info)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Serialize in the same layout <see cref="Read"/> parses.</summary>
    public readonly void Write(PacketWriter writer)
    {
        writer.WriteUInt32((uint)Flags);
        writer.WriteUInt32(Time);
        writer.WriteSingle(X);
        writer.WriteSingle(Y);
        writer.WriteSingle(Z);
        writer.WriteSingle(Orientation);

        if (HasFlag(MovementFlags.OnTransport))
        {
            writer.WriteUInt64(TransportGuid);
            writer.WriteSingle(TransportX);
            writer.WriteSingle(TransportY);
            writer.WriteSingle(TransportZ);
            writer.WriteSingle(TransportOrientation);
        }

        if (HasFlag(MovementFlags.Swimming))
        {
            writer.WriteSingle(Pitch);
        }

        writer.WriteUInt32(FallTime);

        if (HasFlag(MovementFlags.Jumping))
        {
            writer.WriteSingle(JumpZSpeed);
            writer.WriteSingle(JumpCosAngle);
            writer.WriteSingle(JumpSinAngle);
            writer.WriteSingle(JumpXySpeed);
        }

        if (HasFlag(MovementFlags.SplineElevation))
        {
            writer.WriteSingle(SplineElevation);
        }
    }
}
