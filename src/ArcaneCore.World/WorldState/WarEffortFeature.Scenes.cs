using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging;
using static ArcaneCore.Kernel.WorldData.WorldState.WarEffortSceneSchedule;

namespace ArcaneCore.World.WorldState;

/// <summary>vmangos npc_aqwar_ch_attack (the Qiraji waves on Cenarion Hold) and npc_aqwar_saurfang (speech, then the ride to the gate).</summary>
public sealed partial class WarEffortFeature
{
    private int _lastWave = int.MinValue;
    private Creature? _saurfang;
    private bool _finalBattleAnnounced;
    private readonly HashSet<CreatureMapSystem> _saurfangSystems = [];

    /// <summary>Creatures spawned by the wave script, for diagnostics and tests.</summary>
    internal List<Creature> WaveCreatures { get; } = [];

    internal Creature? SceneSaurfang => _saurfang;

    private void RunScenes()
    {
        if (_world is null) return;
        WarEffortSnapshot state = Snapshot;
        long now = UtcNowUnix();
        if (_world.Maps.FirstOrDefault(m => m.MapId == 1 && m.InstanceId == 0)?.FindUpdater<CreatureMapSystem>() is not { } silithus)
            return;

        int due = LatestWaveDue(state, now);
        // After a (re)start, only the current wave is spawned; earlier waves are not replayed.
        WarEffortScene scene = SceneAt(state, now);
        if (scene == WarEffortScene.None)
        {
            _lastWave = int.MinValue;
            _finalBattleAnnounced = false;
        }
        else if (scene == WarEffortScene.CenarionHoldAttack)
        {
            if (_lastWave == int.MinValue) _lastWave = Math.Max(due - 1, -1);
            while (_lastWave < due) SpawnWave(silithus, ++_lastWave);
        }

        if (scene == WarEffortScene.FinalBattle && (_saurfang is null || !_saurfang.IsInWorld))
        {
            (float x, float y, float z, float o) = SaurfangPost;
            if (_saurfangSystems.Add(silithus))
            {
                // vmangos GetAI_npc_aqwar_saurfang: the war script only for the Silithus Saurfang (zone 1377); the Orgrimmar
                // database spawn keeps its normal AI (null falls through to the template's AI).
                silithus.RegisterEntryAi(Saurfang, creature => creature.Spawn is null && creature.X < -6000 && creature.Y > 800
                    ? new SaurfangSceneAi(creature, this, SpeechLinesDue(Snapshot, UtcNowUnix()))
                    : null!, rebuildExisting: false);
            }

            // A Saurfang summoned mid-speech (restart) starts at the current line, as vmangos's JustRespawned resumes the ride.
            _saurfang = silithus.SummonForInstance(Saurfang, x, y, z, o);
        }
    }

    private void SpawnWave(CreatureMapSystem silithus, int wave)
    {
        (float sx, float sy, float sz) = WaveSpawn;
        (float tx, float ty, float tz) = WaveTarget;
        Creature? speaker = null;
        for (int i = 0; i < MobsPerWave; i++)
        {
            // 80% Colossal Anubisath, 20% Qiraji Destroyer (urand(0, 5) ? anubisath : destroyer).
            uint entry = silithus.RandomInt(0, 5) != 0 ? ColossalAnubisath : QirajiDestroyer;
            float x = sx + silithus.RandomInt(-15, 15), y = sy + silithus.RandomInt(-15, 15);
            if (silithus.SummonForInstance(entry, x, y, sz, 6) is not { } mob) continue;
            silithus.SetHomePosition(mob, tx, ty, tz, 3.0f);
            mob.Motion.MovePoint(1, tx, ty, tz, run: true);
            WaveCreatures.Add(mob);
            speaker ??= mob;
        }

        if (speaker is not null)
            silithus.SayText(speaker, WaveTexts[silithus.RandomInt(0, WaveTexts.Count - 1)]);
        logger.LogInformation("AQ war effort: Cenarion Hold wave {Wave} of {Count}", wave + 1, WaveCount);
    }

    internal void OnSaurfangSpeechDone()
    {
        if (_finalBattleAnnounced) return;
        _finalBattleAnnounced = true;
        BroadcastToWorld(FinalBattleWorldText, "[broadcast_text 11619]", null);
    }

    internal sealed class SaurfangSceneAi(Creature creature, WarEffortFeature feature, int spoken) : CreatureAI(creature)
    {
        private int _spoken = spoken;
        private int _nextPoint = -1;
        private bool _moving;

        public int Spoken => _spoken;
        public int NextPoint => _nextPoint;
        public bool Moving => _moving;

        public override void OnUpdate(uint diffMs)
        {
            base.OnUpdate(diffMs);
            int due = SpeechLinesDue(feature.Snapshot, feature.UtcNowUnix());
            while (_spoken < due) System?.SayText(Me, SaurfangSpeech[_spoken++]);
            if (_spoken >= SaurfangSpeech.Count && _nextPoint < 0)
            {
                feature.OnSaurfangSpeechDone();
                _nextPoint = 0;
            }

            if (_nextPoint >= 0 && !_moving && _nextPoint < GatePath.Count && !Me.Combat.IsInCombat)
            {
                (float x, float y, float z, float o) = GatePath[_nextPoint];
                System?.SetHomePosition(Me, x, y, z, o);
                Me.Motion.MovePoint((uint)_nextPoint, x, y, z, run: true);
                _moving = true;
            }
        }

        public override void OnMovementInform(MovementGeneratorType type, uint pointId)
        {
            if (type != MovementGeneratorType.Point || pointId != (uint)_nextPoint) return;
            _moving = false;
            _nextPoint++;
        }

        public override void OnReachedHome() => _moving = false;
    }
}
