namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>mangos-classic WorldState's six invasion zones and the matching ClassicDB world-script fields/events.</summary>
public sealed record ScourgeInvasionZone(int Index, uint ZoneId, uint MapId, uint WorldStateField,
    ushort EventId, int Necropolises);

public static class ScourgeInvasionCatalog
{
    public const ushort MainEvent = 17;
    public const uint NecropolisHealth = 16421;
    public const uint ZapNecropolis = 28386;
    public const uint CampDeathCommunique = 28351;

    public static IReadOnlyList<ScourgeInvasionZone> Zones { get; } =
    [
        new(0, 16, 1, 2260, 92, 2), // Azshara
        new(1, 4, 0, 2261, 93, 2), // Blasted Lands
        new(2, 46, 0, 2262, 95, 2), // Burning Steppes
        new(3, 139, 0, 2264, 94, 2), // Eastern Plaguelands
        new(4, 440, 1, 2263, 91, 3), // Tanaris
        new(5, 618, 1, 2259, 90, 3), // Winterspring
    ];

    public static ScourgeInvasionZone? ForZone(uint zoneId) => Zones.FirstOrDefault(z => z.ZoneId == zoneId);
    public static ScourgeInvasionZone? ForField(uint field) => Zones.FirstOrDefault(z => z.WorldStateField == field);
    public static ScourgeInvasionZone? ForEvent(ushort eventId) => Zones.FirstOrDefault(z => z.EventId == eventId);
}

public enum ScourgeInvasionState : byte
{
    Disabled,
    Enabled,
}

public sealed record ScourgeInvasionZoneProgress(uint ZoneId, int Remaining, long NextAttackUnix);

public sealed record ScourgeInvasionSnapshot(ScourgeInvasionState State, int BattlesWon, uint LastAttackZone,
    IReadOnlyList<ScourgeInvasionZoneProgress> Zones)
{
    public static ScourgeInvasionSnapshot Disabled { get; } = new(ScourgeInvasionState.Disabled, 0, 0,
        ScourgeInvasionCatalog.Zones.Select(z => new ScourgeInvasionZoneProgress(z.ZoneId, 0, 0)).ToArray());

    public int Remaining(uint zoneId) => Zones.FirstOrDefault(z => z.ZoneId == zoneId)?.Remaining ?? 0;

    public bool? WorldScriptCondition(uint field)
        => ScourgeInvasionCatalog.ForField(field) is { } zone ? Remaining(zone.ZoneId) > 0 : null;
}

public interface IScourgeInvasionStateStore
{
    Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds,
        CancellationToken cancellationToken = default);
    Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default);
}
