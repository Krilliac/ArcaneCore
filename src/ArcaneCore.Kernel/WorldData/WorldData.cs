namespace ArcaneCore.Kernel.WorldData;

/// <summary>Starting location for a race/class (the DB-driven equivalent of playercreateinfo).</summary>
public sealed record StartPosition(uint MapId, uint ZoneId, float X, float Y, float Z, float Orientation);

/// <summary>Per-race appearance/faction data (the DB-driven equivalent of ChrRaces).</summary>
public sealed record RaceInfo(uint DisplayId, uint FactionTemplate);

/// <summary>Per-class base stats (the DB-driven equivalent of the relevant DBC rows).</summary>
public sealed record ClassInfo(uint BaseHealth, uint BaseMana, byte PowerType);

/// <summary>
/// Read access to the DB-driven "DBC" world data. Per the project's design, all of this is
/// loaded from database tables (seeded with sane defaults, fully tunable) — no client
/// extraction required.
/// </summary>
public interface IWorldDataStore
{
    /// <summary>Start position for a race/class, or null if the combination is not allowed.</summary>
    Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default);

    Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default);

    Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default);

    /// <summary>Whether a race/class pairing is valid (a start position exists for it).</summary>
    Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default);
}
