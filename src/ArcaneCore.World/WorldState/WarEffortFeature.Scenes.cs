using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.Extensions.Logging;
using static ArcaneCore.Kernel.WorldData.WorldState.WarEffortSceneSchedule;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The Silithus war of vmangos world_event_wareffort.cpp: the transport-day troops of events 54-58 with their formation scripts
/// (npc_ironforge_infantry, npc_orgrimmar_infantry, npc_orgrimmar_rifleman, npc_priestess), the Cenarion Hold waves
/// (npc_aqwar_ch_attack, event 59), the final battle's Qiraji (event 61) and Saurfang (npc_aqwar_saurfang): war room, wave post,
/// speech, the ride to the gate and his combat.
/// </summary>
public sealed partial class WarEffortFeature
{
    private int _lastWave = int.MinValue;
    private Creature? _saurfang;
    private bool _finalBattleAnnounced;
    private readonly HashSet<CreatureMapSystem> _sceneAiSystems = [];
    private readonly Dictionary<uint, Creature> _troops = [];
    private readonly Dictionary<uint, GameObject> _troopObjects = [];
    private WarEffortTroop? _summoningTroop;
    private bool _summoningSaurfang;

    /// <summary>Creatures spawned by the wave script, for diagnostics and tests.</summary>
    internal List<Creature> WaveCreatures { get; } = [];

    internal Creature? SceneSaurfang => _saurfang;
    internal IReadOnlyDictionary<uint, Creature> Troops => _troops;
    internal IReadOnlyDictionary<uint, GameObject> TroopObjects => _troopObjects;

    private void RunScenes()
    {
        if (_world?.Maps.FirstOrDefault(m => m.MapId == 1 && m.InstanceId == 0) is not { } kalimdor
            || kalimdor.FindUpdater<CreatureMapSystem>() is not { } silithus)
            return;
        WarEffortSnapshot state = Snapshot;
        long now = UtcNowUnix();
        RegisterSceneAis(silithus);
        SyncResearchers(silithus);

        int days = TransportDaysActive(state, now);
        WarEffortScene scene = SceneAt(state, now);
        bool dbInfantry = days > 0 && AdoptClassicDbWarLayout(silithus);
        SyncTroops(silithus, days, scene, dbInfantry);

        // The Silithus Saurfang (vmangos spawn 113001) stands from transport day 1 until the war is over.
        if (days > 0 && (_saurfang is null || !_saurfang.IsInWorld))
        {
            (float x, float y, float z, float o) = SaurfangWarRoom;
            _summoningSaurfang = true;
            try { _saurfang = silithus.SummonForInstance(Saurfang, x, y, z, o); }
            finally { _summoningSaurfang = false; }
        }
        else if (days == 0 && _saurfang is not null)
        {
            if (_saurfang.IsInWorld && _saurfang.Spawn is null) silithus.Despawn(_saurfang); // a database spawn leaves with its event
            _saurfang = null;
        }

        if (scene == WarEffortScene.None)
        {
            _lastWave = int.MinValue;
            _finalBattleAnnounced = false;
        }
        else if (scene == WarEffortScene.CenarionHoldAttack)
        {
            // After a (re)start, only the current wave is spawned; earlier waves are not replayed.
            int due = LatestWaveDue(state, now);
            if (_lastWave == int.MinValue) _lastWave = Math.Max(due - 1, -1);
            while (_lastWave < due) SpawnWave(silithus, ++_lastWave);
        }
    }

    private void RegisterSceneAis(CreatureMapSystem silithus)
    {
        if (!_sceneAiSystems.Add(silithus)) return;
        RegisterSaurfangAi(silithus, rebuild: false);
        RegisterTroopAis(silithus, rebuild: false);
    }

    /// <summary>A creature of the Cenarion Hold area (the ClassicDB war layout's spawns there take the war scripts too).</summary>
    internal static bool InSilithus(Creature c) => c.Map?.MapId == 1 && c.X < -6000 && c.Y > 300;

    // vmangos GetAI_npc_aqwar_saurfang: the war script only for the Silithus Saurfang; the Orgrimmar database spawn keeps its normal
    // AI (null falls through to the template's AI). The ClassicDB war layout's Silithus Saurfang is adopted rather than doubled.
    private void RegisterSaurfangAi(CreatureMapSystem silithus, bool rebuild)
        => silithus.RegisterEntryAi(Saurfang, c => _summoningSaurfang || (c.Spawn is not null && InSilithus(c))
            ? new SaurfangWarAi(c, this) : null!, rebuildExisting: rebuild);

    // The troop scripts drive this feature's summons and the ClassicDB war layout's Cenarion Hold infantry.
    private void RegisterTroopAis(CreatureMapSystem silithus, bool rebuild)
    {
        foreach (uint entry in new[] { OrgrimmarInfantry, TaurenRifleman, IronforgeInfantry, Priestess })
            silithus.RegisterEntryAi(entry, c => _summoningTroop is { } t ? new TroopAi(c, this, t)
                : c.Spawn is not null && InSilithus(c) && c.Entry is OrgrimmarInfantry or IronforgeInfantry
                    ? new TroopAi(c, this, new WarEffortTroop(0, c.Entry, (byte)ClassicDbWarEvent, c.X, c.Y, c.Z, c.Orientation))
                    : null!, rebuildExisting: rebuild);
    }

    /// <summary>
    /// ClassicDB event 123 (the ten-hour war) brings its own Silithus Saurfang and 55 Cenarion Hold infantry. While they are there the
    /// scene uses them: their Saurfang becomes the scripted one (the summoned one leaves) and the vmangos Ironforge and Orgrimmar
    /// infantry of days 1-5 step aside, so nothing is doubled.
    /// </summary>
    private bool AdoptClassicDbWarLayout(CreatureMapSystem silithus)
    {
        Creature? dbSaurfang = silithus.Creatures.FirstOrDefault(c => c.Entry == Saurfang && c.Spawn is not null && c.IsInWorld && InSilithus(c));
        if (dbSaurfang is not null)
        {
            if (dbSaurfang.AI is not SaurfangWarAi) RegisterSaurfangAi(silithus, rebuild: true);
            if (_saurfang is not null && !ReferenceEquals(_saurfang, dbSaurfang) && _saurfang.IsInWorld) silithus.Despawn(_saurfang);
            _saurfang = dbSaurfang;
        }

        bool dbInfantry = false;
        bool rebuild = false;
        foreach (Creature c in silithus.Creatures)
        {
            if (c.Spawn is null || c.Entry is not (OrgrimmarInfantry or IronforgeInfantry) || !InSilithus(c)) continue;
            dbInfantry = true;
            rebuild |= c.AI is not TroopAi;
        }

        if (rebuild) RegisterTroopAis(silithus, rebuild: true);
        return dbInfantry;
    }

    /// <summary>
    /// game_event_creature / _gameobject for events 54-58 (the first <paramref name="days"/>) and 61 (final battle). A troop that died
    /// comes back once its corpse is gone (vmangos respawns them; the spawns carry a 25 s respawn). With the ClassicDB war layout's
    /// infantry present (<paramref name="dbInfantry"/>) the vmangos Ironforge and Orgrimmar infantry are left out.
    /// </summary>
    private void SyncTroops(CreatureMapSystem silithus, int days, WarEffortScene scene, bool dbInfantry)
    {
        bool Wanted(WarEffortTroop t) => t.Event == WarEffortTroopCatalog.FinalBattleEvent
            ? scene == WarEffortScene.FinalBattle
            : t.Event - WarEffortTroopCatalog.FirstDayEvent < days;
        bool WantedCreature(WarEffortTroop t) => Wanted(t) && !(dbInfantry && t.Entry is OrgrimmarInfantry or IronforgeInfantry);

        foreach (WarEffortTroop troop in WarEffortTroopCatalog.Creatures)
        {
            bool exists = _troops.TryGetValue(troop.Guid, out Creature? c) && c.IsInWorld;
            if (WantedCreature(troop) && !exists)
            {
                _summoningTroop = troop;
                try
                {
                    if (silithus.SummonForInstance(troop.Entry, troop.X, troop.Y, troop.Z, troop.Orientation) is { } spawned)
                        _troops[troop.Guid] = spawned;
                }
                finally { _summoningTroop = null; }
            }
            else if (!WantedCreature(troop) && c is not null)
            {
                if (c.IsInWorld) silithus.Despawn(c);
                _troops.Remove(troop.Guid);
            }
        }

        if (_world is null) return;
        foreach (Map map in _world.Maps.Where(m => m.MapId is 0 or 1 && m.InstanceId == 0).ToArray())
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) continue;
            foreach (WarEffortTroop go in WarEffortTroopCatalog.GameObjects)
            {
                if (go.MapId != map.MapId) continue;
                bool exists = _troopObjects.TryGetValue(go.Guid, out GameObject? o) && o.IsSpawned;
                if (Wanted(go) && !exists)
                {
                    if (objects.Summon(go.Entry, go.X, go.Y, go.Z, go.Orientation) is { } spawned) _troopObjects[go.Guid] = spawned;
                }
                else if (!Wanted(go) && o is not null)
                {
                    objects.Remove(o);
                    _troopObjects.Remove(go.Guid);
                }
            }
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
        // The last wave tells Saurfang to cheer when it is beaten (m_lastWave).
        if (wave == WaveCount - 1 && _saurfang?.AI is SaurfangWarAi ai) ai.LastWave = true;
        logger.LogInformation("AQ war effort: Cenarion Hold wave {Wave} of {Count}", wave + 1, WaveCount);
    }

    internal void OnSaurfangSpeechDone()
    {
        if (_finalBattleAnnounced) return;
        _finalBattleAnnounced = true;
        BroadcastToWorld(FinalBattleWorldText, "[broadcast_text 11619]", null);
    }

    internal static void Mount(Creature creature, uint displayId) => creature.SetUInt32(UpdateFields.UnitFieldMountdisplayid, displayId);
    internal static uint MountOf(Creature creature) => creature.GetUInt32(UpdateFields.UnitFieldMountdisplayid);

    /// <summary>
    /// vmangos npc_infantrymanAI and its four scripts. When the Cenarion Hold attack starts each soldier turns its spot a quarter
    /// turn about its unit's origin (the priestesses line up between two points) and waits there in its ready stance; from the final
    /// battle on it follows Saurfang at that offset (the priestesses mounted). A soldier dying near Saurfang in combat gives him
    /// Vengeance with an emote and a line, once per aura.
    /// </summary>
    internal sealed class TroopAi : CreatureAI
    {
        private readonly WarEffortFeature _feature;
        private readonly (float X, float Y, float Z, float O) _spawn;
        private bool _movedIntoPosition, _following;

        public TroopAi(Creature creature, WarEffortFeature feature, WarEffortTroop troop) : base(creature)
        {
            _feature = feature;
            _spawn = (troop.X, troop.Y, troop.Z, troop.Orientation);
            PriestessIndex = troop.Entry == Priestess ? PriestessIndexOf(troop.Guid) : 0;
        }

        public override bool AggroesOnSight => true; // MoveInLineOfSight: attack any hostile in sight
        public bool Following => _following;
        public bool MovedIntoPosition => _movedIntoPosition;
        public int PriestessIndex { get; }

        public override void OnUpdate(uint diffMs)
        {
            WarEffortScene scene = SceneAt(_feature.Snapshot, _feature.UtcNowUnix());
            if (!_movedIntoPosition && scene != WarEffortScene.None)
            {
                MoveToWaveBattlePosition();
                _movedIntoPosition = true;
            }

            if (!_following && scene == WarEffortScene.FinalBattle && _feature.SceneSaurfang is { IsInWorld: true })
            {
                _following = true;
                if (!Me.Combat.IsInCombat) FollowSaurfang();
            }

            if (Me.Combat.IsInCombat) UpdateVictim();
        }

        public override void OnAggro(Unit target)
        {
            if (Me.Entry == Priestess) Mount(Me, 0);
        }

        public override void OnReachedHome()
        {
            if (_following) FollowSaurfang();
            else Me.SetUInt32(UpdateFields.UnitNpcEmotestate, ReadyEmote(Me.Entry)); // JustReachedHome: ready for that wave
        }

        public override void OnDeath(Unit? killer)
        {
            if (_feature.SceneSaurfang is not { IsAlive: true } saurfang || System is not { } system) return;
            if (Distance(Me, saurfang.X, saurfang.Y) > 30 || !saurfang.Combat.IsInCombat || system.HasAura(saurfang, Vengeance)) return;
            system.SayText(saurfang, FriendlyDiedEmote, Me);
            system.SayText(saurfang, system.RandomInt(0, 1) == 0 ? FriendlyDiedSay1 : FriendlyDiedSay2, Me);
            system.CastSpell(saurfang, Vengeance, saurfang, triggered: false);
        }

        /// <summary>MoveToWaveBattlePosition / CalculateRotatedPositionAboutLeader (the priestess override lines them up).</summary>
        internal (float X, float Y, float Z, float O) WaveBattlePosition()
        {
            if (Me.Entry == Priestess)
            {
                float t = PriestessIndex / 10f;
                return (PriestessOrigin.X + (PriestessEnd.X - PriestessOrigin.X) * t, PriestessOrigin.Y + (PriestessEnd.Y - PriestessOrigin.Y) * t,
                    PriestessOrigin.Z + (PriestessEnd.Z - PriestessOrigin.Z) * t, 2.62f);
            }

            bool clockwise = Me.Entry == IronforgeInfantry;
            (float ox, float oy, float oz) = clockwise ? IronforgeOrigin : OrgrimmarOrigin;
            float rx = _spawn.X - ox, ry = _spawn.Y - oy;
            // The source keeps the spawn's z and asks the map for the height; ArcaneCore keeps the spawn's z.
            return clockwise
                ? (ox + ry, oy - rx, _spawn.Z, _spawn.O - MathF.PI / 2)
                : (ox - ry, oy + rx, _spawn.Z, _spawn.O + MathF.PI / 2);
        }

        private void MoveToWaveBattlePosition()
        {
            (float x, float y, float z, float o) = WaveBattlePosition();
            System?.SetHomePosition(Me, x, y, z, o);
            if (Me.IsAlive && !Me.Combat.IsInCombat) Me.Motion.MoveTargetedHome(Me.Home);
        }

        /// <summary>FollowSaurfang: behind him at the offset of the battle spot from his wave post.</summary>
        private void FollowSaurfang()
        {
            if (_feature.SceneSaurfang is not { IsInWorld: true } saurfang) return;
            (float x, float y, _, _) = WaveBattlePosition();
            float dx = x - SaurfangPost.X, dy = y - SaurfangPost.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= 0) return;
            float angle = MathF.Asin(dx / dist) + MathF.PI;
            if (Me.Entry == Priestess) Mount(Me, PriestessMount);
            Me.Motion.MoveFollow(saurfang, dist, angle);
        }

        private static int PriestessIndexOf(uint guid)
        {
            int i = 0;
            foreach (WarEffortTroop t in WarEffortTroopCatalog.Creatures)
            {
                if (t.Entry != Priestess) continue;
                i++;
                if (t.Guid == guid) return i;
            }

            return i;
        }

        private static uint ReadyEmote(uint entry) => entry switch
        {
            IronforgeInfantry => EmoteStateReady1H,
            OrgrimmarInfantry => EmoteStateReady2H,
            TaurenRifleman => EmoteStateReadyRifle,
            _ => EmoteStateAtEase,
        };
    }

    internal static float Distance(Unit a, float x, float y)
    {
        float dx = a.X - x, dy = a.Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// vmangos npc_aqwar_saurfangAI. Faction Might of Kalimdor. In the war room until the Cenarion Hold attack, then at his wave post;
    /// in the final battle he mounts and is immune while he speaks, then broadcasts and rides the gate path, stopping to fight and
    /// carrying on from the last point once home. Combat: Saurfang's Rage and an aggro line on entering combat, Mortal Strike, Cleave,
    /// Charge and Terrifying Roar on their timers, a 10% kill line, and a victory line when the last wave is beaten.
    /// </summary>
    internal sealed class SaurfangWarAi : CreatureAI
    {
        private readonly WarEffortFeature _feature;
        private int _spoken;
        private int _nextPoint = -1;
        private bool _moving, _paused, _atPost, _finalBattle, _ridingToGate;
        private uint _mortalStrikeMs, _cleaveMs, _chargeMs, _roarMs;

        public SaurfangWarAi(Creature creature, WarEffortFeature feature) : base(creature)
        {
            _feature = feature;
            _spoken = SpeechLinesDue(feature.Snapshot, feature.UtcNowUnix());
            Me.FactionTemplate = FactionMightOfKalimdor;
            ResetTimers();
        }

        public int Spoken => _spoken;
        public int NextPoint => _nextPoint;
        public bool Moving => _moving;
        public bool RidingToGate => _ridingToGate;
        public bool LastWave { get; set; }
        internal (uint MortalStrike, uint Cleave, uint Charge, uint Roar) Timers => (_mortalStrikeMs, _cleaveMs, _chargeMs, _roarMs);

        private uint Roll(int minSeconds, int maxSeconds) => (uint)((System?.RandomInt(minSeconds, maxSeconds) ?? minSeconds) * 1000);

        /// <summary>Reset: the opening timers.</summary>
        private void ResetTimers()
        {
            _mortalStrikeMs = Roll(1, 15);
            _chargeMs = Roll(0, 4);
            _cleaveMs = Roll(3, 9);
            _roarMs = Roll(4, 12);
        }

        public override void OnUpdate(uint diffMs)
        {
            if (!Me.IsAlive) return;
            WarEffortScene scene = SceneAt(_feature.Snapshot, _feature.UtcNowUnix());
            if (!_atPost && scene != WarEffortScene.None)
            {
                // MoveToWaveBattlePosition (also the restart recovery of the final battle).
                (float x, float y, float z, _) = SaurfangPost;
                System?.SetHomePosition(Me, x, y, z, 2.6f);
                if (!Me.Combat.IsInCombat) Me.Motion.MoveTargetedHome(Me.Home);
                _atPost = true;
            }

            if (!_finalBattle && scene == WarEffortScene.FinalBattle)
            {
                Me.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
                Mount(Me, SaurfangMount);
                _finalBattle = true;
            }

            if (_finalBattle)
            {
                int due = SpeechLinesDue(_feature.Snapshot, _feature.UtcNowUnix());
                while (_spoken < due) System?.SayText(Me, SaurfangSpeech[_spoken++]);
                if (_spoken >= SaurfangSpeech.Count && _nextPoint < 0)
                {
                    _feature.OnSaurfangSpeechDone();
                    Me.UnitFlags &= ~(UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc);
                    _ridingToGate = true;
                    _nextPoint = 0;
                }

                if (_ridingToGate && !_moving && !_paused && _nextPoint < GatePath.Count && !Me.Combat.IsInCombat)
                {
                    (float x, float y, float z, float o) = GatePath[_nextPoint];
                    System?.SetHomePosition(Me, x, y, z, o);
                    Me.Motion.MovePoint((uint)_nextPoint, x, y, z, run: true);
                    _moving = true;
                }
            }

            if (!Me.Combat.IsInCombat || !UpdateVictim() || Victim is not { } victim) return;
            Tick(ref _mortalStrikeMs, diffMs, victim, MortalStrike, 11, 20);
            Tick(ref _cleaveMs, diffMs, victim, Cleave, 9, 21);
            Tick(ref _chargeMs, diffMs, victim, Charge, 4, 12);
            Tick(ref _roarMs, diffMs, victim, TerrifyingRoar, 30, 40);
        }

        /// <summary>The source's timer blocks: cast when due and re-arm only on a successful cast (else retry next update).</summary>
        private void Tick(ref uint timer, uint diffMs, Unit victim, uint spell, int minSeconds, int maxSeconds)
        {
            if (timer < diffMs || timer == 0)
            {
                if (DoCast(victim, spell) == CreatureCastResult.Ok) timer = Roll(minSeconds, maxSeconds);
            }
            else
                timer -= diffMs;
        }

        public override void OnAggro(Unit target)
        {
            Mount(Me, 0);
            _paused = true;
            System?.SayText(Me, AggroTexts[System.RandomInt(0, AggroTexts.Count - 1)], target);
            if (DoCast(Me, SaurfangsRage) == CreatureCastResult.Ok) System?.SayText(Me, RageText);
        }

        public override void OnKilledUnit(Unit victim)
        {
            if (System is { } system && system.RandomInt(0, 99) < 10) system.SayText(Me, KilledUnitText);
        }

        public override bool OnEnterEvadeMode()
        {
            if (LastWave)
            {
                System?.SayText(Me, BattleWonText);
                LastWave = false;
            }

            ResetTimers();
            return false;
        }

        public override void OnReachedHome()
        {
            // JustReachedHome resumes the ride; Reset remounts him while he is on his way.
            _paused = false;
            _moving = false;
            if (_ridingToGate) Mount(Me, SaurfangMount);
        }

        public override void OnMovementInform(MovementGeneratorType type, uint pointId)
        {
            if (type != MovementGeneratorType.Point || !_ridingToGate || pointId != (uint)_nextPoint) return;
            _moving = false;
            _nextPoint++;
            if (_nextPoint == GatePath.Count)
            {
                _ridingToGate = false;
                Mount(Me, 0);
                (float x, float y, float z, _) = GatePath[^1];
                System?.SetHomePosition(Me, x, y, z, 2.6f);
            }
        }
    }
}
