namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>The four Eastern Plaguelands towers (vmangos OutdoorPvPEP.h:119-127 <c>Towers</c>).</summary>
public enum EpTower
{
    Eastwall = 0,
    Northpass = 1,
    Plaguewood = 2,
    CrownGuard = 3,
}

/// <summary>The tower states shown on the map (vmangos OutdoorPvPEP.h:188-197 <c>TowerStates</c>).</summary>
[Flags]
public enum EpTowerState : uint
{
    None = 0,
    Neutral = 1,
    AllianceContested = 2,
    HordeContested = 4,
    AllianceProgressing = 8,
    HordeProgressing = 16,
    Alliance = 32,
    Horde = 64,
}

/// <summary>One tower's fixed data: its capture point, banners, flares, buffers, world states and announcements.</summary>
public sealed record EpTowerData(
    EpTower Tower,
    OutdoorPvPSpawn CapturePoint,
    OutdoorPvPSpawn Banner1,
    OutdoorPvPSpawn Banner2,
    OutdoorPvPSpawn FlareAlliance,
    OutdoorPvPSpawn FlareHorde,
    OutdoorPvPSpawn BufferAlliance,
    OutdoorPvPSpawn BufferHorde,
    uint TakenAllianceText,
    uint TakenHordeText,
    EpTowerWorldStates States);

/// <summary>A tower's seven map-icon world states (vmangos WorldStates.h:121-155).</summary>
public sealed record EpTowerWorldStates(uint Alliance, uint Horde, uint AllianceProgressing, uint HordeProgressing,
    uint AllianceContested, uint HordeContested, uint Neutral)
{
    public IEnumerable<(uint State, EpTowerState Flag)> All =>
    [
        (Alliance, EpTowerState.Alliance), (Horde, EpTowerState.Horde),
        (AllianceProgressing, EpTowerState.AllianceProgressing), (HordeProgressing, EpTowerState.HordeProgressing),
        (AllianceContested, EpTowerState.AllianceContested), (HordeContested, EpTowerState.HordeContested),
        (Neutral, EpTowerState.Neutral),
    ];
}

/// <summary>
/// Eastern Plaguelands outdoor PvP constants (vmangos src/game/OutdoorPvP/OutdoorPvPEP.h; every position is the sniffed one there,
/// cross-checked against MaNGOS Zero src/game/OutdoorPvP/OutdoorPvPEP.h for the ids, spells and world states).
/// </summary>
public static class EasternPlaguelandsCatalog
{
    public const uint MapId = 0;

    /// <summary>EP_Zone.</summary>
    public const uint ZoneId = 139;

    /// <summary>TFV_area: The Fungal Vale, linked to the Crown Guard graveyard as well.</summary>
    public const uint FungalValeArea = 2258;

    /// <summary>EP_GraveYardId (MaNGOS Zero GRAVEYARD_ID_EASTERN_PLAGUE).</summary>
    public const uint GraveyardId = 927;

    /// <summary>EP_BuffZones: Eastern Plaguelands, Stratholme and Scholomance.</summary>
    public static readonly uint[] BuffZones = [139, 2017, 2057];

    public static readonly uint[] AllianceBuffs = [11413, 11414, 11415, 1386];

    public static readonly uint[] HordeBuffs = [30880, 30683, 30682, 29520];

    public const uint WorldStateTowerCountAlliance = 2327;
    public const uint WorldStateTowerCountHorde = 2328;
    public const uint WorldStateSliderDisplay = 2426;
    public const uint WorldStateSliderPosition = 2427;
    public const uint WorldStateSliderNeutral = 2428;

    public const uint TextAllHorde = 13637;
    public const uint TextAllAlliance = 13638;

    public const uint SoundFlagCapturedAlliance = 8173;
    public const uint SoundFlagCapturedHorde = 8213;
    public const uint SoundVictoryHorde = 8454;
    public const uint SoundVictoryAlliance = 8455;
    public const uint SoundWarningAlliance = 8332;
    public const uint SoundWarningHorde = 8333;

    public const uint ArtKitNeutral = 21;
    public const uint ArtKitAlliance = 2;
    public const uint ArtKitHorde = 1;
    public const uint AnimationNeutral = 2;
    public const uint AnimationAlliance = 1;
    public const uint AnimationHorde = 0;

    /// <summary>SPELL_TOWER_CAPTURE_TEST_DND: the tower buffer's pulse that credits the players around it.</summary>
    public const uint SpellTowerCaptureTest = 30882;

    public const uint SpellSpiritParticles = 17327;
    public const uint SpellSpiritParticlesRedBig = 31309;
    public const uint SpellSpiritParticlesSuperBig = 31954;
    public const uint SpellSpiritParticlesRedSuperBig = 31951;

    public const uint FactionFlightMasterAlliance = 774;
    public const uint FactionFlightMasterHorde = 775;

    /// <summary>
    /// The capture-point template numbers used when the 1810xx/1820xx templates are not loaded with type 29 data. The slider
    /// geometry is the one vmangos draws in ZoneScript.cpp:378-384 (max 1200, grey band 240 = 20%); the radius and minimum time are
    /// this base's fallback, not sourced.
    /// </summary>
    public static readonly CapturePointTemplate FallbackTemplate =
        new(60f, WorldStateSliderDisplay, WorldStateSliderPosition, WorldStateSliderNeutral, 20, 60, 1200);

    public const uint TowerBannerEntry = 182106;

    private static OutdoorPvPSpawn S(uint entry, float x, float y, float z, float o) => new(entry, MapId, x, y, z, o);

    public static readonly EpTowerData Eastwall = new(EpTower.Eastwall,
        S(182097, 2574.51f, -4794.89f, 144.704f, -1.45003f),
        S(TowerBannerEntry, 2539.61f, -4801.55f, 115.766f, 2.00713f),
        S(TowerBannerEntry, 2569.6f, -4772.93f, 115.399f, 2.72271f),
        S(181852, 2563.26f, -4795.15f, 145.852f, 1.81514f),
        S(181853, 2565.27f, -4797.59f, 147.846f, 3.05433f),
        S(17794, 2574.12f, -4795.33f, 145.871f, 5.11381f),
        S(17795, 2574.0f, -4794.79f, 145.881f, 1.95477f),
        13631, 13636,
        new EpTowerWorldStates(2354, 2356, 2357, 2358, 2359, 2360, 2361));

    public static readonly EpTowerData Northpass = new(EpTower.Northpass,
        S(181899, 3181.08f, -4379.36f, 174.123f, -2.03472f),
        S(TowerBannerEntry, 3148.17f, -4365.51f, 145.029f, 1.53589f),
        S(TowerBannerEntry, 3188.76f, -4358.5f, 144.555f, 1.97222f),
        S(181852, 3171.86f, -4377.2f, 174.898f, 0.174532f),
        S(181853, 3169.76f, -4375.13f, 175.458f, 2.70526f),
        S(17794, 3180.54f, -4379.31f, 175.275f, 3.57792f),
        S(17795, 3180.48f, -4379.07f, 174.995f, 2.74017f),
        13630, 13635,
        new EpTowerWorldStates(2372, 2373, 2364, 2365, 2362, 2363, 2352));

    public static readonly EpTowerData Plaguewood = new(EpTower.Plaguewood,
        S(182098, 2962.71f, -3042.31f, 154.789f, 2.08426f),
        S(TowerBannerEntry, 2975.5f, -3060.36f, 125.108f, 5.23599f),
        S(TowerBannerEntry, 2992.63f, -3022.95f, 125.593f, 3.03684f),
        S(181852, 2971.41f, -3038.36f, 157.492f, 5.35816f),
        S(181853, 2973.17f, -3037.19f, 156.443f, 1.97222f),
        S(17794, 2962.6f, -3041.96f, 155.835f, 3.00197f),
        S(17795, 2963.02f, -3041.9f, 155.965f, 4.27606f),
        13629, 13634,
        new EpTowerWorldStates(2370, 2371, 2368, 2369, 2366, 2367, 2353));

    public static readonly EpTowerData CrownGuard = new(EpTower.CrownGuard,
        S(182096, 1860.85f, -3731.23f, 196.716f, -2.53214f),
        S(TowerBannerEntry, 1838.42f, -3703.56f, 167.713f, 0.890117f),
        S(TowerBannerEntry, 1877.6f, -3716.76f, 167.188f, 1.74533f),
        S(181852, 1855.66f, -3725.0f, 197.044f, 1.53589f),
        S(181853, 1853.12f, -3722.62f, 197.406f, 0.628317f),
        S(17794, 1860.59f, -3730.8f, 197.854f, 2.54818f),
        S(17795, 1860.48f, -3731.34f, 197.778f, 2.42601f),
        13632, 13633,
        new EpTowerWorldStates(2378, 2379, 2376, 2377, 2374, 2375, 2355));

    /// <summary>The towers in the vmangos <c>SetupZoneScript</c> order (EWT, PWT, CGT, NPT).</summary>
    public static readonly EpTowerData[] Towers = [Eastwall, Plaguewood, CrownGuard, Northpass];

    /// <summary>EP_EWT_Summons_A: Lordaeron Commander and four Soldiers at Eastwall.</summary>
    public static readonly OutdoorPvPSpawn[] EastwallSquadAlliance =
    [
        S(17635, 2532.85f, -4764.92f, 103.617f, 2.35619f),
        S(17647, 2533.33f, -4769.31f, 104.396f, 2.37365f),
        S(17647, 2537.34f, -4773.92f, 105.941f, 2.21657f),
        S(17647, 2537.77f, -4765.94f, 104.432f, 2.3911f),
        S(17647, 2542.57f, -4770.22f, 106.145f, 2.42601f),
    ];

    /// <summary>EP_EWT_Summons_H: Lordaeron Veteran and four Fighters.</summary>
    public static readonly OutdoorPvPSpawn[] EastwallSquadHorde =
    [
        S(17995, 2532.85f, -4764.92f, 103.617f, 2.35619f),
        S(17996, 2533.33f, -4769.31f, 104.396f, 2.37365f),
        S(17996, 2537.34f, -4773.92f, 105.941f, 2.21657f),
        S(17996, 2537.77f, -4765.94f, 104.432f, 2.3911f),
        S(17996, 2542.57f, -4770.22f, 106.145f, 2.42601f),
    ];

    /// <summary>EP_NPT_LordaeronShrine: [curing shrine, banner aura] per team.</summary>
    public static readonly OutdoorPvPSpawn[] NorthpassShrineAlliance =
        [S(181682, 3167.72f, -4355.91f, 138.785f, 1.69297f), S(180100, 3167.72f, -4355.91f, 138.785f, 1.69297f)];

    public static readonly OutdoorPvPSpawn[] NorthpassShrineHorde =
        [S(181955, 3167.5f, -4356.25f, 138.821f, 1.69297f), S(180101, 3167.5f, -4356.25f, 138.821f, 1.69297f)];

    /// <summary>EP_CGT_BannerAuraGraveYard: the large banner aura at the Crown Guard graveyard.</summary>
    public static readonly OutdoorPvPSpawn CrownGuardBannerAuraAlliance = S(180421, 1985.47f, -3653.88f, 120.172f, 1.46608f);

    public static readonly OutdoorPvPSpawn CrownGuardBannerAuraHorde = S(180422, 1985.47f, -3653.88f, 120.172f, 1.46608f);

    /// <summary>EP_PWT_FlightMaster: William Kielar.</summary>
    public static readonly OutdoorPvPSpawn PlaguewoodFlightMaster = S(17209, 2987.5f, -3049.11f, 120.126f, 5.75959f);

    /// <summary>EP_CGT_SpiritOfVictory.</summary>
    public static readonly OutdoorPvPSpawn CrownGuardSpiritOfVictory = S(18039, 1856.58f, -3714.72f, 194.637f, 0.762214f);
}
