namespace ArcaneCore.Kernel.WorldData.WorldState;

public enum WarEffortScene : byte { None, CenarionHoldAttack, FinalBattle }

/// <summary>
/// The Silithus scenes at the end of the five-day transport (vmangos world_event_wareffort.cpp npc_aqwar_ch_attack and
/// npc_aqwar_saurfang). vmangos starts them with its world-database events 59 (EVENT_WAR_EFFORT_CH_ATTACK) and 61
/// (EVENT_WAR_EFFORT_FINALBATTLE), whose schedule is not in the core repository; ArcaneCore has no such events, so they are
/// placed relative to the saved transport deadline using the script's own comment ("Waves for 3h, 1h break before final
/// battle"): the attack starts five hours before the deadline and the final battle one hour before it. Everything is a
/// function of the saved deadline, so a restart resumes the right scene.
/// </summary>
public static class WarEffortSceneSchedule
{
    public const int AttackStartsBeforeEndSeconds = 5 * 3_600;
    public const int FinalBattleBeforeEndSeconds = 3_600;
    public const int FirstWaveAfterSeconds = 60;
    public const int WaveIntervalSeconds = 15 * 60;
    /// <summary>m_waveCount runs 1..12 and stops at 12: eleven waves.</summary>
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

    public static WarEffortScene SceneAt(WarEffortSnapshot state, long nowUnix)
    {
        if (state.Phase != WarEffortPhase.Transporting || state.PhaseEndsAtUnix <= 0 || nowUnix >= state.PhaseEndsAtUnix)
            return WarEffortScene.None;
        long left = state.PhaseEndsAtUnix - nowUnix;
        if (left <= FinalBattleBeforeEndSeconds) return WarEffortScene.FinalBattle;
        return left <= AttackStartsBeforeEndSeconds ? WarEffortScene.CenarionHoldAttack : WarEffortScene.None;
    }

    /// <summary>The index (0-10) of the latest wave due at <paramref name="nowUnix"/>, or -1 before the first.</summary>
    public static int LatestWaveDue(WarEffortSnapshot state, long nowUnix)
    {
        if (SceneAt(state, nowUnix) != WarEffortScene.CenarionHoldAttack) return -1;
        long since = nowUnix - (state.PhaseEndsAtUnix - AttackStartsBeforeEndSeconds) - FirstWaveAfterSeconds;
        return since < 0 ? -1 : (int)Math.Min(WaveCount - 1, since / WaveIntervalSeconds);
    }

    /// <summary>How many of Saurfang's fourteen lines are due (0-14); the march starts at fourteen.</summary>
    public static int SpeechLinesDue(WarEffortSnapshot state, long nowUnix)
    {
        if (SceneAt(state, nowUnix) != WarEffortScene.FinalBattle) return 0;
        long since = nowUnix - (state.PhaseEndsAtUnix - FinalBattleBeforeEndSeconds) - SpeechDelaySeconds;
        return since < 0 ? 0 : (int)Math.Min(SaurfangSpeech.Count, since / SpeechIntervalSeconds + 1);
    }
}
