namespace WowWorldMessages.Vanilla;

[Flags]
public enum MovementFlags : uint {
    None = 0,
    Forward = 1,
    Backward = 2,
    StrafeLeft = 4,
    StrafeRight = 8,
    TurnLeft = 16,
    TurnRight = 32,
    PitchUp = 64,
    PitchDown = 128,
    WalkMode = 256,
    OnTransport = 512,
    Levitating = 1024,
    FixedZ = 2048,
    Root = 4096,
    Jumping = 8192,
    Fallingfar = 16384,
    Swimming = 2097152,
    SplineEnabled = 4194304,
    CanFly = 8388608,
    Flying = 16777216,
    Ontransport = 33554432,
    SplineElevation = 67108864,
    Waterwalking = 268435456,
    SafeFall = 536870912,
    Hover = 1073741824,
}

