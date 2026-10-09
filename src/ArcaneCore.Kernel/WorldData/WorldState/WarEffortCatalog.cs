namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>mangos-classic WorldState::aqWorldstateMap, aqWorldStateTotalsMap and QuestRewarded_war_effort.
/// The first and repeatable turn-ins share one resource; five resources have separate Alliance/Horde counters.
/// </summary>
public sealed record WarEffortResource(int Id, uint WorldStateField, long Goal, uint FirstQuest, uint RepeatQuest);

public sealed record WarEffortContribution(int ResourceId, uint ItemCount);

public static class WarEffortCatalog
{
    public const uint DaysLeftCondition = 2113;
    public const ushort GatheringEvent = 120;
    public const ushort TransportingEvent = 121;
    public const uint GongQuest = 8743;
    public const uint GongObject = 180717;
    public const uint ColossusOfAshi = 15742;
    public const uint ColossusOfRegal = 15741;
    public const uint ColossusOfZora = 15740;
    public const int ResourceCount = 30;

    public static IReadOnlyList<WarEffortResource> Resources { get; } =
    [
        new(0, 2021, 96000, 8549, 8550), // Peacebloom
        new(1, 2095, 10000, 8611, 8612), // Lean Wolf Steak
        new(2, 2005, 22000, 8542, 8543), // Tin Bar
        new(3, 2079, 250000, 8604, 8605), // Wool Bandage
        new(4, 2050, 19000, 8580, 8581), // Firebloom
        new(5, 2066, 60000, 8588, 8589), // Heavy Leather
        new(6, 2008, 18000, 8545, 8546), // Mithril Bar
        new(7, 2085, 250000, 8607, 8608), // Mageweave Bandage
        new(8, 2073, 60000, 8600, 8601), // Rugged Leather
        new(9, 2105, 10000, 8615, 8616), // Baked Salmon
        new(10, 2060, 180000, 8511, 8512), // Light Leather
        new(11, 2076, 800000, 8517, 8518), // Linen Bandage
        new(12, 2063, 110000, 8513, 8514), // Medium Leather
        new(13, 2047, 33000, 8503, 8504), // Stranglekelp
        new(14, 2092, 14000, 8524, 8525), // Rainbow Fin Albacore
        new(15, 2002, 28000, 8494, 8495), // Iron Bar
        new(16, 2098, 20000, 8526, 8527), // Roast Raptor
        new(17, 2082, 600000, 8520, 8521), // Silk Bandage
        new(18, 2011, 24000, 8499, 8500), // Thorium Bar
        new(19, 2057, 20000, 8509, 8510), // Arthas' Tears
        new(20, 1997, 45000, 8492, 8493), // Alliance Copper Bar
        new(21, 2053, 13000, 8505, 8506), // Alliance Purple Lotus
        new(22, 2069, 40000, 8515, 8516), // Alliance Thick Leather
        new(23, 2101, 8500, 8528, 8529), // Alliance Spotted Yellowtail
        new(24, 2088, 200000, 8522, 8523), // Alliance Runecloth Bandage
        new(25, 2018, 45000, 8532, 8533), // Horde Copper Bar
        new(26, 2054, 13000, 8582, 8583), // Horde Purple Lotus
        new(27, 2070, 40000, 8590, 8591), // Horde Thick Leather
        new(28, 2102, 8500, 8613, 8614), // Horde Spotted Yellowtail
        new(29, 2089, 200000, 8609, 8610), // Horde Runecloth Bandage
    ];

    public static WarEffortResource? ForQuest(uint questId)
        => Resources.FirstOrDefault(r => r.FirstQuest == questId || r.RepeatQuest == questId);

    public static WarEffortResource? ForField(uint field)
        => Resources.FirstOrDefault(r => r.WorldStateField == field);

    public static int? BossIndex(uint creatureEntry) => creatureEntry switch
    {
        ColossusOfAshi => 0,
        ColossusOfRegal => 1,
        ColossusOfZora => 2,
        _ => null,
    };

    public static ushort BossDeathEvent(int bossIndex) => bossIndex switch
    {
        0 => 125,
        1 => 126,
        2 => 127,
        _ => throw new ArgumentOutOfRangeException(nameof(bossIndex)),
    };
}

public enum WarEffortPhase : byte
{
    Disabled,
    Gathering,
    Transporting,
    Gong,
    TenHourWar,
    Done,
}

/// <summary>Immutable snapshot of the global AQ state loaded from character storage.</summary>
public sealed record WarEffortSnapshot(WarEffortPhase Phase, long PhaseEndsAtUnix, IReadOnlyList<long> Counters,
    byte KilledBossMask = 0)
{
    public static WarEffortSnapshot Disabled { get; } = new(WarEffortPhase.Disabled, 0, new long[WarEffortCatalog.ResourceCount]);

    public bool? WorldScriptCondition(uint field, uint state, DateTimeOffset now)
    {
        if (field == WarEffortCatalog.DaysLeftCondition)
        {
            // AhnQirajData::GetDaysRemaining: floor(milliseconds remaining / one day) + 1.
            long remaining = PhaseEndsAtUnix == 0 ? 0 : Math.Max(0, PhaseEndsAtUnix - now.ToUnixTimeSeconds());
            return (ulong)(remaining / 86_400 + 1) == state;
        }

        WarEffortResource? resource = WarEffortCatalog.ForField(field);
        return resource is null ? null : Counters[resource.Id] == resource.Goal;
    }
}

/// <summary>Global AQ state persistence. A reward's contribution is written by the quest reward transaction.</summary>
public interface IWarEffortStateStore
{
    Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default);
    Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default);
}
