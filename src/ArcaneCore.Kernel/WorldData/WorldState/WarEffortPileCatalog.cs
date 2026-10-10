namespace ArcaneCore.Kernel.WorldData.WorldState;

public enum WarEffortTeam : byte { Alliance, Horde }

/// <summary>mangos-classic AQResourceGroup.</summary>
public enum WarEffortPileGroup : byte { Skinning, Bandages, Bars, Cooking, Herbs }

/// <summary>One resource pile of classic-db Updates/4498_backport_errors.sql (guid, entry, position). Tier 0 is the
/// "Initial" pile shown with the gathering event; tiers 1-5 grow with the faction's group total.</summary>
public sealed record WarEffortPile(uint Guid, uint Entry, uint MapId, WarEffortTeam Team, WarEffortPileGroup Group, int Tier,
    float X, float Y, float Z, float Orientation);

/// <summary>
/// The Ironforge and Orgrimmar resource piles and the capital-city counters of the AQ war effort. The spawn rows are
/// classic-db Updates/4498 (155000-155054 Alliance, 155500-155554 Horde), which the z2815 importer does not load, so they
/// are summoned at runtime from saved state. Tier rule: vmangos world_event_wareffort.cpp HandleWarEffortGameObject.
/// </summary>
public static class WarEffortPileCatalog
{
    public static IReadOnlyList<WarEffortPile> Piles { get; } =
    [
        new(155000, 180681, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 0, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155001, 180598, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 0, -4971.55f, -1148.57f, 501.650f, 2.29000f),
        new(155002, 180680, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 0, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155003, 180679, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 0, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155010, 180692, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 1, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155011, 180674, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 1, -4968.33f, -1152.89f, 501.930f, 2.27000f),
        new(155012, 180780, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 1, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155013, 180800, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 1, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155014, 180801, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Herbs, 1, -4935.58f, -1284.82f, 501.671f, 2.25147f),
        new(155020, 180693, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 2, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155021, 180675, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 2, -4969.21f, -1143.84f, 509.250f, 2.20000f),
        new(155022, 180781, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 2, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155023, 180806, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 2, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155024, 180802, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Herbs, 2, -4935.58f, -1284.82f, 501.671f, 2.25147f),
        new(155030, 180694, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 3, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155031, 180676, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 3, -4983.00f, -1136.22f, 501.670f, 2.30000f),
        new(155032, 180782, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 3, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155033, 180807, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 3, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155034, 180803, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Herbs, 3, -4935.58f, -1284.82f, 501.671f, 2.25147f),
        new(155040, 180695, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 4, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155041, 180677, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 4, -4975.60f, -1147.33f, 509.250f, 2.27000f),
        new(155042, 180783, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 4, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155043, 180808, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 4, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155044, 180804, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Herbs, 4, -4935.58f, -1284.82f, 501.671f, 2.25147f),
        new(155050, 180696, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Skinning, 5, -4958.52f, -1179.33f, 501.660f, 2.26893f),
        new(155051, 180678, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bandages, 5, -4974.11f, -1148.40f, 510.850f, 2.27000f),
        new(155052, 180784, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Bars, 5, -4913.85f, -1226.00f, 501.651f, 2.25147f),
        new(155053, 180809, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Cooking, 5, -4937.29f, -1282.74f, 501.672f, 2.26893f),
        new(155054, 180805, 0, WarEffortTeam.Alliance, WarEffortPileGroup.Herbs, 5, -4935.58f, -1284.82f, 501.671f, 2.25147f),
        new(155500, 180812, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 0, 1590.82f, -4155.33f, 36.2926f, 3.70010f),
        new(155501, 180818, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 0, 1637.11f, -4147.21f, 36.0414f, 3.73501f),
        new(155502, 180826, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 0, 1579.35f, -4109.25f, 34.5417f, 3.75246f),
        new(155503, 180832, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 0, 1619.83f, -4092.43f, 34.5107f, 3.70010f),
        new(155504, 180838, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 0, 1683.11f, -4134.35f, 39.5419f, 3.71755f),
        new(155510, 180813, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 1, 1590.88f, -4155.33f, 36.2980f, 3.68265f),
        new(155511, 180819, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 1, 1637.10f, -4147.25f, 36.0531f, 3.73501f),
        new(155512, 180827, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 1, 1579.33f, -4109.25f, 34.5487f, 3.71755f),
        new(155513, 180833, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 1, 1619.80f, -4092.53f, 34.4888f, 3.70010f),
        new(155514, 180839, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 1, 1683.10f, -4134.31f, 39.5390f, 3.73501f),
        new(155520, 180814, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 2, 1590.88f, -4155.33f, 36.2980f, 3.68265f),
        new(155521, 180820, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 2, 1637.10f, -4147.25f, 36.0531f, 3.73501f),
        new(155522, 180828, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 2, 1579.33f, -4109.25f, 34.5487f, 3.71755f),
        new(155523, 180834, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 2, 1619.80f, -4092.53f, 34.4888f, 3.70010f),
        new(155524, 180840, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 2, 1683.10f, -4134.31f, 39.5390f, 3.73501f),
        new(155530, 180815, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 3, 1590.88f, -4155.33f, 36.2980f, 3.68265f),
        new(155531, 180821, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 3, 1637.10f, -4147.25f, 36.0531f, 3.73501f),
        new(155532, 180829, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 3, 1579.33f, -4109.25f, 34.5487f, 3.71755f),
        new(155533, 180835, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 3, 1619.80f, -4092.53f, 34.4888f, 3.70010f),
        new(155534, 180841, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 3, 1683.10f, -4134.31f, 39.5390f, 3.73501f),
        new(155540, 180816, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 4, 1590.88f, -4155.33f, 36.2980f, 3.68265f),
        new(155541, 180822, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 4, 1637.10f, -4147.25f, 36.0531f, 3.73501f),
        new(155542, 180830, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 4, 1579.33f, -4109.25f, 34.5487f, 3.71755f),
        new(155543, 180836, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 4, 1619.80f, -4092.53f, 34.4888f, 3.70010f),
        new(155544, 180842, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 4, 1683.10f, -4134.31f, 39.5390f, 3.73501f),
        new(155550, 180817, 1, WarEffortTeam.Horde, WarEffortPileGroup.Skinning, 5, 1590.88f, -4155.33f, 36.2980f, 3.68265f),
        new(155551, 180823, 1, WarEffortTeam.Horde, WarEffortPileGroup.Herbs, 5, 1637.10f, -4147.25f, 36.0531f, 3.73501f),
        new(155552, 180831, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bandages, 5, 1579.33f, -4109.25f, 34.5487f, 3.71755f),
        new(155553, 180837, 1, WarEffortTeam.Horde, WarEffortPileGroup.Cooking, 5, 1619.80f, -4092.53f, 34.4888f, 3.70010f),
        new(155554, 180843, 1, WarEffortTeam.Horde, WarEffortPileGroup.Bars, 5, 1683.10f, -4134.31f, 39.5390f, 3.73501f),
    ];

    /// <summary>mangos-classic GetResourceInfo, indexed by WarEffortResource.Id.</summary>
    public static (WarEffortPileGroup Group, WarEffortTeam Team) GroupOf(int resourceId) => resourceId switch
    {
        0 or 4 or 26 => (WarEffortPileGroup.Herbs, WarEffortTeam.Horde),
        1 or 9 or 28 => (WarEffortPileGroup.Cooking, WarEffortTeam.Horde),
        2 or 6 or 25 => (WarEffortPileGroup.Bars, WarEffortTeam.Horde),
        3 or 7 or 29 => (WarEffortPileGroup.Bandages, WarEffortTeam.Horde),
        5 or 8 or 27 => (WarEffortPileGroup.Skinning, WarEffortTeam.Horde),
        10 or 12 or 22 => (WarEffortPileGroup.Skinning, WarEffortTeam.Alliance),
        11 or 17 or 24 => (WarEffortPileGroup.Bandages, WarEffortTeam.Alliance),
        13 or 19 or 21 => (WarEffortPileGroup.Herbs, WarEffortTeam.Alliance),
        14 or 16 or 23 => (WarEffortPileGroup.Cooking, WarEffortTeam.Alliance),
        15 or 18 or 20 => (WarEffortPileGroup.Bars, WarEffortTeam.Alliance),
        _ => throw new ArgumentOutOfRangeException(nameof(resourceId)),
    };

    /// <summary>
    /// The highest pile tier shown for a group. Gathering: vmangos counts down from the summed objective in fifths, so tier
    /// k needs current &gt;= objective - (5 - k) * (objective / 5). Transporting: one tier fewer per elapsed day (the DAY1-5
    /// transition events; here the reference days-remaining value, clamped to 1-5). Later phases keep tier 1 (vmangos's
    /// default case); disabled shows nothing.
    /// </summary>
    public static int TierOf(WarEffortSnapshot state, WarEffortPileGroup group, WarEffortTeam team, DateTimeOffset now)
    {
        switch (state.Phase)
        {
            case WarEffortPhase.Gathering:
            {
                long objective = 0, current = 0;
                foreach (WarEffortResource resource in WarEffortCatalog.Resources)
                {
                    if (GroupOf(resource.Id) != (group, team)) continue;
                    objective += resource.Goal;
                    current += state.Counters[resource.Id];
                }

                long step = objective / 5;
                for (int tier = 5; tier >= 1; tier--)
                    if (current >= objective - (5 - tier) * step) return tier;
                return 0;
            }
            case WarEffortPhase.Transporting:
            {
                long remaining = state.PhaseEndsAtUnix == 0 ? 0 : Math.Max(0, state.PhaseEndsAtUnix - now.ToUnixTimeSeconds());
                return (int)Math.Clamp(remaining / 86_400 + 1, 1, 5);
            }
            case WarEffortPhase.Gong or WarEffortPhase.TenHourWar or WarEffortPhase.Done:
                return 1;
            default:
                return 0;
        }
    }

    /// <summary>The piles that should exist: the tier-0 piles while gathering (event 120), every tier up to the group's.</summary>
    public static IEnumerable<WarEffortPile> Visible(WarEffortSnapshot state, DateTimeOffset now)
        => Piles.Where(p => p.Tier == 0
            ? state.Phase == WarEffortPhase.Gathering
            : p.Tier <= TierOf(state, p.Group, p.Team, now));

    // ---- capital-city counters (mangos-classic WorldState::FillInitialWorldStates and AddWarEffortProgress)

    /// <summary>Stormwind, Darnassus, Ironforge, Orgrimmar, Thunder Bluff, Undercity.</summary>
    public static IReadOnlyList<uint> CapitalZones { get; } = [1519, 1657, 1537, 1637, 1638, 1497];

    /// <summary>The *_TOTAL world state of each resource id; the five shared resources use one total for both factions.</summary>
    private static readonly uint[] TotalFields =
    [
        2020, 2096, 2006, 2080, 2051, 2067, 2009, 2086, 2074, 2106,
        2061, 2077, 2064, 2048, 2093, 2003, 2099, 2083, 2012, 2058,
        1998, 2055, 2071, 2103, 2090,
    ];

    public static uint TotalField(int resourceId) => TotalFields[resourceId >= TotalFields.Length ? resourceId - 5 : resourceId];

    /// <summary>The counter world states of a capital zone entry: totals then counts while gathering, days left while transporting.</summary>
    public static IReadOnlyList<(uint Field, uint Value)> CapitalStates(WarEffortSnapshot state, DateTimeOffset now)
    {
        List<(uint, uint)> states = [];
        if (state.Phase == WarEffortPhase.Gathering)
        {
            for (int i = 0; i < TotalFields.Length; i++)
                states.Add((TotalFields[i], (uint)WarEffortCatalog.Resources[i].Goal));
            foreach (WarEffortResource resource in WarEffortCatalog.Resources)
                states.Add((resource.WorldStateField, (uint)Math.Min(uint.MaxValue, state.Counters[resource.Id])));
        }
        else if (state.Phase == WarEffortPhase.Transporting)
        {
            long remaining = state.PhaseEndsAtUnix == 0 ? 0 : Math.Max(0, state.PhaseEndsAtUnix - now.ToUnixTimeSeconds());
            states.Add((WarEffortCatalog.DaysLeftCondition, (uint)(remaining / 86_400 + 1)));
        }

        return states;
    }
}
