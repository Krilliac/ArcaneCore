namespace ArcaneCore.Kernel.WorldData.WorldState;

public enum WarEffortScene : byte { None, CenarionHoldAttack, FinalBattle }

/// <summary>
/// The Silithus war schedule of vmangos HardcodedEvents.cpp WarEffortEvent::Update and its stage table (warEffortStageEvents): each
/// transport day (stages MOVE_1-5, one DAY apart) adds world event 54-58's troops, which stay until the war is over; the gong ring
/// opens the gate and starts the battle; WAR_EFFORT_CH_ATTACK_TIME (4 h) later event 59 starts the Cenarion Hold attack
/// (npc_aqwar_ch_attack); WAR_EFFORT_FINAL_BATTLE_TIME (4 h) after that event 61 is the final battle (npc_aqwar_saurfang); ten
/// hours after the ring (WAR_EFFORT_GONG_DURATION) everything stops. ArcaneCore's ten hours start when the gate has opened
/// (<see cref="WarEffortCatalog.WarStartsAfterSeconds"/> after the ring), so the war start is the saved deadline minus ten hours.
/// Every answer is a function of the saved phase and deadline, so a restart resumes the right scene.
/// </summary>
public static class WarEffortSceneSchedule
{
    public const int AttackAfterWarStartSeconds = 4 * 3_600;
    public const int FinalBattleAfterWarStartSeconds = 8 * 3_600;
    public const int TransportDays = 5;
    public const int FirstWaveAfterSeconds = 60;
    public const int WaveIntervalSeconds = 15 * 60;
    /// <summary>m_waveCount runs 1..12 and stops at 12: eleven waves, the last 2 h 31 min into the four-hour attack.</summary>
    public const int WaveCount = 11;
    public const int MobsPerWave = 10;
    public const int SpeechDelaySeconds = 120;
    public const int SpeechIntervalSeconds = 10;

    public const uint ColossalAnubisath = 15743;
    public const uint QirajiDestroyer = 15744;
    public const uint Saurfang = 14720;
    public static IReadOnlyList<int> WaveTexts { get; } = [11536, 11611, 11612, 11613];
    public static IReadOnlyList<int> SaurfangSpeech { get; } =
        [11620, 11621, 11622, 11623, 11624, 11625, 11626, 11627, 11628, 11629, 11630, 11631, 11646, 11647];
    public const int FinalBattleWorldText = 11619;

    // npc_aqwar_saurfangAI.
    public const uint FactionMightOfKalimdor = 777;
    public const uint SaurfangMount = 10278;
    public const uint MortalStrike = 24573, Cleave = 16044, Charge = 15749, TerrifyingRoar = 14100, SaurfangsRage = 26341;
    /// <summary>BCT_SAURFANG_AGGRO1-9 (vmangos marks some "guessed", some "sniffed").</summary>
    public static IReadOnlyList<int> AggroTexts { get; } = [11527, 11528, 11538, 11540, 11541, 11614, 11615, 11616, 11648];
    public const int RageText = 11563, KilledUnitText = 7237, BattleWonText = 11651;

    // npc_infantrymanAI and its four scripts (creature_template.script_name in the vmangos world database).
    public const uint OrgrimmarInfantry = 15853, TaurenRifleman = 15855, IronforgeInfantry = 15861, Priestess = 15634;
    public const uint Vengeance = 26331;
    public const int FriendlyDiedEmote = 11525, FriendlyDiedSay1 = 11526, FriendlyDiedSay2 = 11529;
    public const uint PriestessMount = 9695;
    public const uint EmoteStateReadyRifle = 214, EmoteStateAtEase = 313, EmoteStateReady1H = 333, EmoteStateReady2H = 375;
    public static (float X, float Y, float Z) IronforgeOrigin => (-6969.21f, 962.33f, 11.88f);
    public static (float X, float Y, float Z) OrgrimmarOrigin => (-6975.30f, 940.14f, 13.14f);
    public static (float X, float Y, float Z) PriestessOrigin => (-6952.21f, 955.01f, 15.83f);
    public static (float X, float Y, float Z) PriestessEnd => (-6968.14f, 926.90f, 11.83f);

    public static (float X, float Y, float Z) WaveSpawn => (-7067.77f, 966.62f, 4.56f);
    public static (float X, float Y, float Z) WaveTarget => (-6959.35f, 940.41f, 14.55f);
    public static (float X, float Y, float Z, float O) SaurfangPost => (-6985.67f, 956.06f, 10.21f, 2.6f);

    /// <summary>saurfangGatePath: Cenarion Hold to the Scarab Wall.</summary>
    public static IReadOnlyList<(float X, float Y, float Z, float O)> GatePath { get; } =
    [
        (-7002.48f, 967.38f, 6.70f, 3.15f), (-7205.49f, 967.08f, 0.95f, 2.9f), (-7265.48f, 995.34f, 2.55f, 3.16f),
        (-7418.73f, 1000.99f, 0.91f, 2.91f), (-7661.01f, 1052.23f, 4.82f, 2.32f), (-7759.05f, 1164.64f, 0.02f, 2.22f),
        (-7810.24f, 1275.49f, -11.08f, 2.72f), (-7909.35f, 1319.05f, -7.79f, 2.32f), (-7952.77f, 1377.95f, 2.94f, 1.38f),
        (-7933.09f, 1490.65f, -6.62f, 2.68f), (-8014.01f, 1532.97f, 2.81f, 3.10f), (-8079.99f, 1523.19f, 2.61f, 3.15f),
    ];

    /// <summary>The Silithus Saurfang's post in the Cenarion Hold war room (vmangos spawn 113001, event 54).</summary>
    public static (float X, float Y, float Z, float O) SaurfangWarRoom => (-6774.63f, 814.611f, 55.7475f, 3.07395f);

    /// <summary>
    /// How many transport-day events (54 = day 1 ... 58 = day 5) are active: day n starts n-1 days into the transport; all five stay
    /// through the gong wait and the war (stages 7-11), none after the war (stage 12) or before the transport.
    /// </summary>
    public static int TransportDaysActive(WarEffortSnapshot state, long nowUnix)
    {
        switch (state.Phase)
        {
            case WarEffortPhase.Transporting when state.PhaseEndsAtUnix > 0:
                long since = nowUnix - (state.PhaseEndsAtUnix - (long)TransportDays * 86_400);
                return (int)Math.Clamp(since / 86_400 + 1, 1, TransportDays);
            case WarEffortPhase.Gong:
                return TransportDays;
            case WarEffortPhase.TenHourWar:
                return state.PhaseEndsAtUnix > 0 && nowUnix >= state.PhaseEndsAtUnix ? 0 : TransportDays;
            default:
                return 0;
        }
    }

    /// <summary>The war's start: the gate is open and the ten hours run (VAR_WE_GONG_TIME).</summary>
    public static long WarStartUnix(WarEffortSnapshot state) => state.PhaseEndsAtUnix - WarEffortCatalog.TenHourWarSeconds;

    public static WarEffortScene SceneAt(WarEffortSnapshot state, long nowUnix)
    {
        if (state.Phase != WarEffortPhase.TenHourWar || state.PhaseEndsAtUnix <= 0 || nowUnix >= state.PhaseEndsAtUnix)
            return WarEffortScene.None;
        long since = nowUnix - WarStartUnix(state);
        if (since >= FinalBattleAfterWarStartSeconds) return WarEffortScene.FinalBattle;
        return since >= AttackAfterWarStartSeconds ? WarEffortScene.CenarionHoldAttack : WarEffortScene.None;
    }

    /// <summary>The index (0-10) of the latest wave due at <paramref name="nowUnix"/>, or -1 before the first.</summary>
    public static int LatestWaveDue(WarEffortSnapshot state, long nowUnix)
    {
        if (SceneAt(state, nowUnix) != WarEffortScene.CenarionHoldAttack) return -1;
        long since = nowUnix - WarStartUnix(state) - AttackAfterWarStartSeconds - FirstWaveAfterSeconds;
        return since < 0 ? -1 : (int)Math.Min(WaveCount - 1, since / WaveIntervalSeconds);
    }

    /// <summary>How many of Saurfang's fourteen lines are due (0-14); the march starts at fourteen.</summary>
    public static int SpeechLinesDue(WarEffortSnapshot state, long nowUnix)
    {
        if (SceneAt(state, nowUnix) != WarEffortScene.FinalBattle) return 0;
        long since = nowUnix - WarStartUnix(state) - FinalBattleAfterWarStartSeconds - SpeechDelaySeconds;
        return since < 0 ? 0 : (int)Math.Min(SaurfangSpeech.Count, since / SpeechIntervalSeconds + 1);
    }
}
