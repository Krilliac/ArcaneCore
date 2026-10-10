namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>mangos-classic WorldState's six invasion zones and the matching ClassicDB world-script fields/events.</summary>
public sealed record ScourgeInvasionZone(int Index, uint ZoneId, uint MapId, uint WorldStateField,
    ushort EventId, int Necropolises)
{
    public uint NecropolisCountField => (uint)(2279 + Index);
}

public static class ScourgeInvasionCatalog
{
    public const ushort MainEvent = 17;
    public const uint BattlesWonField = 2219;
    public const uint NecropolisHealth = 16421;
    public const uint ZapNecropolis = 28386;
    public const uint CampDeathCommunique = 28351;
    public const uint NecroticShard = 16136;
    public const uint DamagedNecroticShard = 16172;
    public const uint NecropolisRelay = 16386;
    public const uint NecropolisProxy = 16398;
    public const uint SummonCircle = 181136;
    public const uint MinionFinder = 16356;
    public const uint GhostGhoulSpawner = 16306;
    public const uint GhostSkeletonSpawner = 16336;
    public const uint GhoulSkeletonSpawner = 16338;
    public static IReadOnlySet<uint> CampMinions { get; } = new HashSet<uint>
    {
        16141, 16299, 16298, // common ghoul, skeleton, ghost
        14697, 16380, 16379, // rare horror, witch, spirit
    };
    public static IReadOnlySet<uint> CampDoodads { get; } = new HashSet<uint>
    {
        181173, 181174, // undead fire and its aura
        181191, 181192, 181193, 181194, // skull piles
    };

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
    public IReadOnlySet<uint> DestroyedSpawnGuids { get; init; } = new HashSet<uint>();
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
