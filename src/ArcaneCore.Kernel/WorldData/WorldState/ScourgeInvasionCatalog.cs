namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>One mangos-classic CityAttack point (Pallid Horror / Patchwork Terror summon positions in a capital).</summary>
public sealed record ScourgeCityAttack(int Index, uint ZoneId, uint MapId, IReadOnlyList<ScourgeInvasionPosition> Spawns);

public readonly record struct ScourgeInvasionPosition(float X, float Y, float Z, float O);

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

    public const uint MouthOfKelThuzad = 16995;
    public const uint PallidHorror = 16394;
    public const uint PatchworkTerror = 16382;
    public const int CityAttackTimerMinSeconds = 45 * 60;
    public const int CityAttackTimerMaxSeconds = 60 * 60;
    public const uint UndercityZone = 1497;
    public const uint StormwindZone = 1519;

    /// <summary>ScourgeInvasionData::m_attackPoints (Undercity royal/trade quarter, Stormwind keep twice as in the reference).</summary>
    public static IReadOnlyList<ScourgeCityAttack> Cities { get; } =
    [
        new(0, UndercityZone, 0, [new(1595.87f, 440.539f, -46.3349f, 2.28207f), new(1659.2f, 265.988f, -62.1788f, 3.64283f)]),
        new(1, StormwindZone, 0, [new(-8578.15f, 886.382f, 87.3148f, 0.586275f), new(-8578.15f, 886.382f, 87.3148f, 0.586275f)]),
    ];

    /// <summary>ScourgeInvasionData::InvasionZone::mouth, where the Mouth of Kel'Thuzad stands while a zone is attacked.</summary>
    public static IReadOnlyDictionary<uint, ScourgeInvasionPosition> MouthPositions { get; } = new Dictionary<uint, ScourgeInvasionPosition>
    {
        [16] = new(3273.75f, -4276.98f, 125.509f, 5.44543f),
        [4] = new(-11429.3f, -3327.82f, 7.73628f, 1.0821f),
        [46] = new(-8229.53f, -1118.11f, 144.012f, 6.17846f),
        [139] = new(2014.55f, -4934.52f, 73.9846f, 0.0698132f),
        [440] = new(-8352.68f, -3972.68f, 10.0753f, 2.14675f),
        [618] = new(7736.56f, -4033.75f, 696.327f, 5.51524f),
    };

    public static ScourgeCityAttack? ForCity(uint zoneId) => Cities.FirstOrDefault(c => c.ZoneId == zoneId);

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

/// <summary>A capital's saved next city-attack time; 0 means due (the reference's empty TimePoint).</summary>
public sealed record ScourgeCityAttackProgress(uint ZoneId, long NextAttackUnix);

public sealed record ScourgeInvasionSnapshot(ScourgeInvasionState State, int BattlesWon, uint LastAttackZone,
    IReadOnlyList<ScourgeInvasionZoneProgress> Zones)
{
    public IReadOnlySet<uint> DestroyedSpawnGuids { get; init; } = new HashSet<uint>();
    public IReadOnlyList<ScourgeCityAttackProgress> Cities { get; init; } =
        ScourgeInvasionCatalog.Cities.Select(c => new ScourgeCityAttackProgress(c.ZoneId, 0)).ToArray();

    public long NextCityAttack(uint zoneId) => Cities.FirstOrDefault(c => c.ZoneId == zoneId)?.NextAttackUnix ?? 0;
    public bool IsCityAttackDue(uint zoneId, long nowUnix)
        => State == ScourgeInvasionState.Enabled && ScourgeInvasionCatalog.ForCity(zoneId) is not null
            && NextCityAttack(zoneId) <= nowUnix;
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

    /// <summary>
    /// mangos-classic StartNewCityAttackIfTime: claims a due capital attack and saves its next 45-60 minute time. False when the
    /// invasion is off or the attack is not due, so two callers cannot both summon.
    /// </summary>
    Task<bool> ClaimCityAttackAsync(uint zoneId, long nowUnix, int nextAttackSeconds, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
