namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// The part of a character that changes while it is in the world, captured on the world
/// thread and persisted asynchronously (logout, disconnect, autosave, shutdown).
/// </summary>
public sealed record CharacterState(
    int Id,
    uint MapId,
    uint ZoneId,
    float X,
    float Y,
    float Z,
    float Orientation,
    byte Level,
    uint PlayedTime);
