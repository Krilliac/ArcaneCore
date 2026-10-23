namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>Silithus outdoor PvP constants (vmangos src/game/OutdoorPvP/OutdoorPvPSI.h, OutdoorPvPSI.cpp; MaNGOS Zero OutdoorPvPSI.h).</summary>
public static class SilithusCatalog
{
    public const uint MapId = 1;

    /// <summary>OutdoorPvPSIBuffZones: Silithus, Ahn'Qiraj (Temple) and Ruins of Ahn'Qiraj.</summary>
    public static readonly uint[] BuffZones = [1377, 3428, 3429];

    public const uint SilithusZone = 1377;

    public const uint DefaultMaxResources = 200;

    public const uint AreaTriggerAlliance = 4162;
    public const uint AreaTriggerHorde = 4168;

    public const uint TurnInCreditAlliance = 17090;
    public const uint TurnInCreditHorde = 18199;

    public const uint SpellSilithystFlag = 29519;
    public const uint SpellTracesOfSilithyst = 29534;
    public const uint SpellCenarionFavor = 30754;
    public const uint SpellFlagDrop = 29533;
    public const uint SpellHonorPoints199 = 31420;
    public const uint SpellSilithystCapReward = 31247;

    public const uint WorldStateGatheredAlliance = 2313;
    public const uint WorldStateGatheredHorde = 2314;
    public const uint WorldStateSilithystMax = 2317;

    public const uint AnnouncerHorde = 17079;
    public const uint AnnouncerAlliance = 17080;

    /// <summary>BCT_SILITHYST_ALLIANCE_25/50/75/100.</summary>
    public static readonly uint[] YellsAlliance = [13479, 13480, 13481, 13470];

    /// <summary>BCT_SILITHYST_HORDE_25/50/75/100.</summary>
    public static readonly uint[] YellsHorde = [13476, 13477, 13478, 13469];

    /// <summary>mangos_string 10050 / 10049 (LANG_OPVP_SI_CAPTURE_A / _H), English.</summary>
    public const string CaptureTextAlliance = "The Alliance has collected 200 silithyst!";

    public const string CaptureTextHorde = "The Horde has collected 200 silithyst!";

    /// <summary>SI_DUST_BAG.</summary>
    public const uint DustBagEntry = 181962;

    /// <summary>One dust bag per this many silithyst delivered (vmangos SpawnDustBags: <c>resource / 15</c>).</summary>
    public const uint SilithystPerDustBag = 15;

    /// <summary>The dust bag piles at the camps: vmangos migration 20241228161610_world.sql, spawn_flags 2 (script-spawned).</summary>
    public static readonly OutdoorPvPSpawn[] DustBagsAlliance =
    [
        new(DustBagEntry, MapId, -7144.21f, 1402.09f, 4.55081f, 6.10865f),
        new(DustBagEntry, MapId, -7142.41f, 1402.99f, 4.79462f, 6.03884f),
        new(DustBagEntry, MapId, -7140.45f, 1401.68f, 4.85929f, 5.98648f),
        new(DustBagEntry, MapId, -7142.03f, 1403.94f, 4.91669f, 1.02974f),
        new(DustBagEntry, MapId, -7143.07f, 1403.95f, 4.82184f, 1.81514f),
        new(DustBagEntry, MapId, -7145.65f, 1401.86f, 4.39894f, 2.23402f),
        new(DustBagEntry, MapId, -7144.95f, 1402.46f, 4.51687f, 1.36136f),
        new(DustBagEntry, MapId, -7144.9f, 1401.41f, 4.42734f, 4.4855f),
        new(DustBagEntry, MapId, -7147.84f, 1405.12f, 4.44306f, 0.506145f),
        new(DustBagEntry, MapId, -7148.8f, 1405.12f, 4.35205f, 1.98967f),
        new(DustBagEntry, MapId, -7148.12f, 1403.96f, 4.36007f, 5.55015f),
    ];

    public static readonly OutdoorPvPSpawn[] DustBagsHorde =
    [
        new(DustBagEntry, MapId, -7596.26f, 756.602f, -16.7828f, 2.51327f),
        new(DustBagEntry, MapId, -7595.53f, 756.927f, -16.7378f, 1.15192f),
        new(DustBagEntry, MapId, -7595.12f, 756.091f, -16.7162f, 5.28835f),
        new(DustBagEntry, MapId, -7596.3f, 754.496f, -16.8031f, 0.59341f),
        new(DustBagEntry, MapId, -7598.29f, 756.553f, -16.8921f, 0.383971f),
        new(DustBagEntry, MapId, -7594.49f, 753.237f, -16.7275f, 2.26893f),
        new(DustBagEntry, MapId, -7598.62f, 755.709f, -16.9598f, 1.98967f),
        new(DustBagEntry, MapId, -7597.09f, 754.64f, -16.8684f, 4.04917f),
        new(DustBagEntry, MapId, -7596.59f, 755.269f, -16.8084f, 1.20428f),
        new(DustBagEntry, MapId, -7594.29f, 752.393f, -16.7654f, 1.46608f),
        new(DustBagEntry, MapId, -7599.11f, 757.084f, -16.9406f, 4.69494f),
        new(DustBagEntry, MapId, -7598.36f, 757.403f, -16.8868f, 2.18166f),
    ];

    /// <summary>A flag dropped within this many yards of the own team's turn-in trigger is kept (vmangos HandleDropFlag).</summary>
    public const float TurnInKeepRadius = 5.0f;
}
