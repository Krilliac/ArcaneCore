using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos AV_NpcEventAI (scripts/battlegrounds/battleground_alterac.cpp:1488-2474), the AI of the collectors (script npc_AV_blood_collector):
/// <list type="bullet">
/// <item>a quartermaster whose team launched the ground assault summons its troops chief and ten troops of the team's level
/// (<see cref="CheckTroops"/>);</item>
/// <item>a cavalry commander whose team launched the cavalry assault summons eight riders (<see cref="CheckCavalry"/>), and on its escort stops
/// at its rally point, gives its speech and rides off with them in formation;</item>
/// <item>a wing commander whose team launched its global air assault turns into its war rider or gryphon, rises, goes invisible and leaves the
/// war rider in its place (<see cref="CheckAerial"/>);</item>
/// <item>Primalist Thurloga and Arch Druid Renferal, once the offering is complete, ride with their adds to the summoning place, place the
/// altar the players channel to bring the Ice Lord or Ivus, and leave ten minutes (Thurloga) or six seconds (Renferal) after the world boss
/// came; they fight with their own spells;</item>
/// <item>a commander that dies sends its followers on alone along the rest of the path.</item>
/// </list>
/// The escorts walk the entry's escort points (<see cref="EscortAI"/>); without them (classic-db has none for these entries) they do not start.
/// </summary>
public sealed class AvEventAI : EscortAI
{
    // battleground_alterac.cpp:1470-1486
    public const uint SpellChainLightning = 16006;
    public const uint SpellEarthbindTotem = 15786;
    public const uint SpellFlameShock = 15616;
    public const uint SpellLightningBolt = 15234;
    public const uint SpellHealingWave = 12492;
    public const uint SpellEntanglingRoots = 22127;
    public const uint SpellStarfire = 21668;
    public const uint SpellRejuvenation = 15981;
    public const uint SpellVisualTransform = 24085;
    public const uint SpellInvisible = 24699;
    public const uint WarRiderDisplayId = 11012;
    public const uint AerialGryphonDisplayId = 1148;
    public const uint EarthbindTotemEntry = 2630;

    public const int SayLokholarSpawned = 8626;
    public const int SayPrimalistThurloga = 8632;
    public const int SayArchdruidRenferal = 8735;
    public const int SayWolfRiderCommander = 8890;
    public const int SayWarcryHorde = 8891;
    public const int SayRamRiderCommander = 8906;
    public const int SayWarcryAlliance = 8908;

    private readonly AlteracValleyScripts _scripts;
    private readonly Vector4 _spawnPoint;

    private uint _chainLightningTimer;
    private uint _earthbindTotemTimer;
    private uint _flameShockTimer;
    private uint _healingWaveTimer;
    private uint _lightningBoltTimer;
    private uint _entanglingRootsTimer;
    private uint _starfireTimer;
    private uint _rejuvenationTimer;
    private bool _thurlogaBoss;
    private bool _renferalBoss;
    private uint _transformTimer;
    private uint _disappearTimer;
    private bool _transformed;
    private bool _cavalrySpawned;
    private bool _movedToChannelingPoint;
    private bool _startedMovingToChannelingPoint;
    private bool _troopsSpawned;
    private bool _aggro;
    private bool _gobSummoned;
    private bool _isDead;
    private bool _speechDone;
    private uint _eventTimer;
    private uint _point;
    private bool _warRiderSummoned;
    private uint _despawnTimer;

    public AvEventAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
        _spawnPoint = new Vector4(creature.X, creature.Y, creature.Z, creature.Orientation);
    }

    private AlteracValley Match => _scripts.Match;

    private uint Entry => Me.Template.Entry;

    private Random Rng => Match.ScriptRandom;

    /// <summary>Whether the wing commander has left its war rider (m_bWarRiderSummoned).</summary>
    public bool WarRiderSummoned => _warRiderSummoned;

    /// <summary>Whether Thurloga or Renferal placed the summoning altar (m_gobSummoned).</summary>
    public bool InvocationPlaced => _gobSummoned;

    // ------------------------------------------------------------------ the assault checks

    /// <summary>checkTroopsStatus (battleground_alterac.cpp:1544-1639).</summary>
    private void CheckTroops()
    {
        if (System is not { } system)
        {
            return;
        }

        if (Match.PlayerGoStatus(Team.Horde, AssaultGround) && Entry == NpcQuartermasterHorde)
        {
            if (_troopsSpawned)
            {
                return;
            }

            _troopsSpawned = true;
            Match.SetPlayerGoStatus(Team.Horde, AssaultGround, false);
            system.SummonCorpseDespawn(Me, NpcWarmasterGarrick, -470.38f, -51.84f, 41, 5.93f);
            uint troops = Match.ReinforcementLevel(Team.Horde) switch
            {
                TroopsSeasoned => NpcSeasonedReaver,
                TroopsVeteran => NpcVeteranReaver,
                TroopsChampion => NpcChampionReaver,
                _ => NpcFrostwolfReaver,
            };

            float x = -472.2f;
            float y = -48.4f;
            int coeff = 0;
            for (int i = 0; i < 10; i++)
            {
                if (i == 5)
                {
                    x = -474.8f;
                    y = -48.7f;
                    coeff = 0;
                }

                system.SummonCorpseDespawn(Me, troops, x - (0.4f * coeff), y - (1.6f * coeff), 41.3f, 5.93f);
                coeff++;
            }
        }
        else if (Match.PlayerGoStatus(Team.Alliance, AssaultGround) && Entry == NpcQuartermasterAlliance)
        {
            if (_troopsSpawned)
            {
                return;
            }

            _troopsSpawned = true;
            Match.SetPlayerGoStatus(Team.Alliance, AssaultGround, false);
            system.SummonCorpseDespawn(Me, NpcFieldMarshalTeravaine, -243.75f, -431.32f, 20, 2.59f);
            uint troops = Match.ReinforcementLevel(Team.Alliance) switch
            {
                TroopsSeasoned => NpcSeasonedCommando,
                TroopsVeteran => NpcVeteranCommando,
                TroopsChampion => NpcChampionCommando,
                _ => NpcStormpikeCommando,
            };

            float x = -240.9f;
            float y = -431.11f;
            for (int i = 0; i < 10; i++)
            {
                if (i == 5)
                {
                    x = -238.35f;
                    y = -432.42f;
                }

                system.SummonCorpseDespawn(Me, troops, x - (i % 5), y - (i % 5), 20.2f, 2.59f);
            }
        }
    }

    /// <summary>checkCavalryStatus (battleground_alterac.cpp:1641-1708).</summary>
    private void CheckCavalry()
    {
        if (System is not { } system)
        {
            return;
        }

        if (Match.PlayerGoStatus(Team.Horde, AssaultCavalry) && Entry == NpcWolfRiderCommander && !_cavalrySpawned)
        {
            _cavalrySpawned = true;
            Match.SetPlayerGoStatus(Team.Horde, AssaultCavalry, false);
            float x = -1230;
            float y = -611;
            float o = 5.4f;
            for (int i = 0; i < 8; i++)
            {
                if (i == 4)
                {
                    x = -1223;
                    y = -619;
                    o = 2.22f;
                }

                system.SummonCorpseDespawn(Me, NpcWolfRider, x - (4 * (i % 5)), y - (3 * (i % 5)), 54, o);
            }
        }

        if (Match.PlayerGoStatus(Team.Alliance, AssaultCavalry) && Entry == NpcRamRiderCommander && !_cavalrySpawned)
        {
            _cavalrySpawned = true;
            Match.SetPlayerGoStatus(Team.Alliance, AssaultCavalry, false);
            float x = 610;
            float y = -35;
            for (int i = 0; i < 8; i++)
            {
                if (i == 4)
                {
                    x = 607;
                    y = -37;
                }

                system.SummonCorpseDespawn(Me, NpcRamRider, x + (2 * (i % 5)), y - (3 * (i % 5)), 45, 0.6f);
            }
        }
    }

    /// <summary>checkAerialStatus (battleground_alterac.cpp:1710-1792).</summary>
    private void CheckAerial(uint diffMs)
    {
        if (_warRiderSummoned)
        {
            return;
        }

        bool launched = Entry switch
        {
            NpcWingCommanderGuse => Match.PlayerGoStatus(Team.Horde, AssaultAirGlobalSoldier),
            NpcWingCommanderSlidore => Match.PlayerGoStatus(Team.Alliance, AssaultAirGlobalSoldier),
            NpcWingCommanderJeztor => Match.PlayerGoStatus(Team.Horde, AssaultAirGlobalLieutenant),
            NpcWingCommanderVipore => Match.PlayerGoStatus(Team.Alliance, AssaultAirGlobalLieutenant),
            NpcWingCommanderMulverick => Match.PlayerGoStatus(Team.Horde, AssaultAirGlobalCommander),
            NpcWingCommanderIchman => Match.PlayerGoStatus(Team.Alliance, AssaultAirGlobalCommander),
            _ => false,
        };
        if (!launched)
        {
            return;
        }

        if (_transformTimer < diffMs)
        {
            if (!_transformed)
            {
                Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
                _transformed = true;
                Me.DisplayId = Entry is NpcWingCommanderGuse or NpcWingCommanderJeztor or NpcWingCommanderMulverick
                    ? WarRiderDisplayId
                    : AerialGryphonDisplayId;
                AvScript.SetFly(Me, true);
                AvScript.SetWalk(Me, false);
                CombatMovement = false;
                DoCast(Me, SpellVisualTransform);
                Me.Motion.MovePoint(0, Me.X, Me.Y, Me.Z + 30.0f, run: true);
            }
        }
        else
        {
            _transformTimer -= diffMs;
        }

        if (!_transformed)
        {
            return;
        }

        if (_disappearTimer < diffMs)
        {
            DoCast(Me, SpellInvisible, triggered: true); // AddAura(SPELL_AV_INVISIBLE, ADD_AURA_PERMANENT)
            _warRiderSummoned = true;
            uint rider = Entry switch
            {
                NpcWingCommanderMulverick => NpcWarRiderMulverick,
                NpcWingCommanderGuse => NpcWarRiderGuse,
                NpcWingCommanderJeztor => NpcWarRiderJeztor,
                NpcWingCommanderSlidore => NpcGryphonSlidore,
                NpcWingCommanderVipore => NpcGryphonVipore,
                NpcWingCommanderIchman => NpcGryphonIchman,
                _ => 0,
            };
            if (rider != 0)
            {
                System?.SummonCorpseDespawn(Me, rider, Me.X, Me.Y, Me.Z, Me.Orientation);
            }
        }
        else
        {
            _disappearTimer -= diffMs;
        }
    }

    // ------------------------------------------------------------------ the AI

    /// <summary>JustRespawned (battleground_alterac.cpp:1794-1868): back at the spawn point with the adds of the summoners; a cavalry commander's riders go.</summary>
    protected override void JustRespawned()
    {
        _speechDone = false;
        _gobSummoned = false;
        _thurlogaBoss = false;
        _renferalBoss = false;
        _isDead = false;
        _aggro = false;
        _point = 0;

        if (System is { } system)
        {
            system.NearTeleport(Me, _spawnPoint.X, _spawnPoint.Y, _spawnPoint.Z, _spawnPoint.W);
            system.SetHomePosition(Me, _spawnPoint.X, _spawnPoint.Y, _spawnPoint.Z, _spawnPoint.W);
        }

        Stop();
        base.JustRespawned();

        if (Entry == NpcPrimalistThurloga)
        {
            foreach (Creature shaman in AvScript.Near(Me, NpcFrostwolfShaman, 1000f))
            {
                AvScript.Unmount(shaman);
                System?.ForceRespawn(shaman);
            }
        }
        else if (Entry == NpcArchDruidRenferal)
        {
            foreach (Creature druid in AvScript.Near(Me, NpcDruidOfTheGrove, 1000f))
            {
                AvScript.Unmount(druid);
                System?.ForceRespawn(druid);
            }
        }
        else if (Entry == NpcWolfRiderCommander)
        {
            Stop();
            _cavalrySpawned = false;
            foreach (Creature rider in AvScript.Near(Me, NpcWolfRider, 1000f))
            {
                System?.ForcedDespawn(rider, 0);
            }
        }

        if (Entry == NpcRamRiderCommander)
        {
            Stop();
            // vmangos kills the wolf riders here too (AV_NPC_WOLFRIDER, :1859), not the ram riders: kept.
            foreach (Creature rider in AvScript.Near(Me, NpcWolfRider, 1000f))
            {
                System?.ForcedDespawn(rider, 0);
            }
        }
    }

    /// <summary>Reset (battleground_alterac.cpp:1870-1922).</summary>
    protected override void Reset()
    {
        if ((Entry == NpcWingCommanderVipore && AvScript.Distance(Me, -1221.27f, -354.51f, 57.7f) < 5.0f)
            || (Entry == NpcWingCommanderIchman && AvScript.Distance(Me, -1291.28f, -266.65f, 91.66f) < 5.0f))
        {
            Me.StandState = StandState.Sit;
        }

        if (Entry == NpcRamRiderCommander)
        {
            AvScript.Mount(Me, 2786);
        }
        else if (Entry == NpcWolfRiderCommander)
        {
            AvScript.Mount(Me, 1166);
        }
        else if (Entry == NpcPrimalistThurloga && _startedMovingToChannelingPoint && !_movedToChannelingPoint)
        {
            AvScript.Mount(Me, 12242);
        }
        else if (Entry == NpcArchDruidRenferal && _startedMovingToChannelingPoint && !_movedToChannelingPoint)
        {
            AvScript.Mount(Me, 9695);
        }

        if (Entry == NpcPrimalistThurloga)
        {
            _chainLightningTimer = 10000;
            _earthbindTotemTimer = 0;
            _flameShockTimer = 5000;
            _healingWaveTimer = 0;
            _lightningBoltTimer = 7500;
        }
        else if (Entry == NpcArchDruidRenferal)
        {
            _entanglingRootsTimer = 3000;
            _starfireTimer = 0;
            _rejuvenationTimer = 10000;
        }

        _transformTimer = 5000;
        _disappearTimer = 5000;
        _transformed = false;
        _troopsSpawned = false;
        _cavalrySpawned = false;
        _isDead = false;

        if (_aggro)
        {
            SetEscortPaused(false);
            AvScript.SetWalk(Me, false);
            _aggro = false;
        }
    }

    /// <summary>Aggro (battleground_alterac.cpp:1924-1943).</summary>
    protected override void Aggro(Unit target)
    {
        if (Entry is NpcRamRiderCommander or NpcWolfRiderCommander)
        {
            AvScript.Unmount(Me);
        }
        else if (Entry is NpcPrimalistThurloga or NpcArchDruidRenferal && _startedMovingToChannelingPoint && !_movedToChannelingPoint)
        {
            AvScript.Unmount(Me);
        }

        if (Entry == NpcPrimalistThurloga)
        {
            DoCast(Me, SpellEarthbindTotem);
        }

        _aggro = true;
        SetEscortPaused(true);
        _scripts.GroupAggro(Me, target);
    }

    /// <summary>WaypointReached (battleground_alterac.cpp:1945-2116).</summary>
    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 0:
                if (Entry == NpcPrimalistThurloga)
                {
                    AvScript.SetWalk(Me, true);
                }

                break;
            case 2:
                if (Entry == NpcWolfRiderCommander)
                {
                    SetEscortPaused(true);
                    AvScript.Say(Me, SayWolfRiderCommander);
                    _eventTimer = 6000;
                    _point = pointId;
                }

                break;
            case 3:
                if (Entry == NpcArchDruidRenferal)
                {
                    _startedMovingToChannelingPoint = true;
                    AvScript.SetWalk(Me, false);
                    AvScript.Mount(Me, 9695);
                    foreach (Creature druid in AvScript.Near(Me, NpcDruidOfTheGrove, 40f))
                    {
                        AvScript.Mount(druid, 9695);
                    }
                }

                break;
            case 5:
                if (Entry == NpcRamRiderCommander)
                {
                    SetEscortPaused(true);
                    AvScript.Say(Me, SayRamRiderCommander);
                    _eventTimer = 6000;
                    _point = pointId;
                }

                break;
            case 6:
                if (Entry == NpcPrimalistThurloga)
                {
                    _startedMovingToChannelingPoint = true;
                    AvScript.Mount(Me, 12242);
                    AvScript.SetWalk(Me, false);
                    foreach (Creature shaman in AvScript.Near(Me, NpcFrostwolfShaman, 40f))
                    {
                        AvScript.Mount(shaman, 1166);
                    }
                }

                break;
            case 42:
                if (Entry == NpcPrimalistThurloga)
                {
                    ArrivedAtChannelingPoint(NpcFrostwolfShaman);
                }

                break;
            case 43:
                if (Entry == NpcPrimalistThurloga)
                {
                    PlaceInvocation(SayPrimalistThurloga, NpcFrostwolfShaman, GameObjectInvocationHorde, -360.139f, -133.403f, 26.4856f, 4.41568f);
                }

                break;
            case 48:
                if (Entry == NpcArchDruidRenferal)
                {
                    ArrivedAtChannelingPoint(NpcDruidOfTheGrove);
                }

                break;
            case 49:
                if (Entry == NpcArchDruidRenferal)
                {
                    PlaceInvocation(SayArchdruidRenferal, NpcDruidOfTheGrove, GameObjectInvocationAlliance, -199.993f, -343.217f, 6.77662f, 3.68265f);
                }

                break;
            case 66:
                StopAsQuestGiverAt(NpcWingCommanderSlidore);
                break;
            case 74:
                StopAsQuestGiverAt(NpcWingCommanderGuse);
                break;
            case 76:
                StopAsQuestGiverAt(NpcWingCommanderVipore);
                break;
            case 81:
                if (Entry == NpcWolfRiderCommander)
                {
                    StopHere();
                }

                break;
            case 84:
                StopAsQuestGiverAt(NpcWingCommanderJeztor);
                break;
            case 92:
                if (Entry == NpcRamRiderCommander)
                {
                    StopHere();
                }
                else
                {
                    StopAsQuestGiverAt(NpcWingCommanderIchman);
                }

                break;
            case 97:
                StopAsQuestGiverAt(NpcWingCommanderMulverick);
                break;
        }
    }

    private void ArrivedAtChannelingPoint(uint adds)
    {
        _startedMovingToChannelingPoint = true;
        _movedToChannelingPoint = true;
        AvScript.Unmount(Me);
        AvScript.SetWalk(Me, true);
        foreach (Creature add in AvScript.Near(Me, adds, 40f))
        {
            AvScript.Unmount(add);
        }
    }

    /// <summary>Waypoints 43 and 49: the speech, the channel (the adds channel at the summoner), the altar, and the summoner stays.</summary>
    private void PlaceInvocation(int say, uint adds, uint altar, float x, float y, float z, float o)
    {
        AvScript.Say(Me, say);
        DoCast(Me, SpellInvocation);
        foreach (Creature add in AvScript.Near(Me, adds, 30f))
        {
            System?.CastSpell(add, SpellInvocation, Me, triggered: false);
        }

        AvScript.Objects(Me)?.Summon(altar, x, y, z, o);
        StopHere();
        _gobSummoned = true;
    }

    private void StopAsQuestGiverAt(uint entry)
    {
        if (Entry == entry)
        {
            Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
            StopHere();
        }
    }

    /// <summary>SetHomePosition at the current place, then Stop.</summary>
    private void StopHere()
    {
        System?.SetHomePosition(Me, Me.X, Me.Y, Me.Z, 0);
        Stop();
    }

    /// <summary>JustDied (battleground_alterac.cpp:2118-2196): a commander's followers go on alone from where it was.</summary>
    public override void OnDeath(Unit? killer)
    {
        _speechDone = false;
        _aggro = false;
        if (_isDead)
        {
            return;
        }

        uint followers = Entry switch
        {
            NpcRamRiderCommander => NpcRamRider,
            NpcWolfRiderCommander => NpcWolfRider,
            NpcPrimalistThurloga => NpcFrostwolfShaman,
            NpcArchDruidRenferal => NpcDruidOfTheGrove,
            _ => 0,
        };
        foreach (Creature follower in AvScript.Near(Me, followers, 100f))
        {
            if (follower.AI is AvCavalryAI cavalry)
            {
                _scripts.LeaveGroup(follower);
                follower.Motion.Clear();
                cavalry.GoOnAlone(CurrentWaypointIndex);
            }
        }

        _isDead = true;
    }

    /// <summary>UpdateRenferalAI (battleground_alterac.cpp:2198-2222).</summary>
    private void UpdateRenferal(uint diffMs)
    {
        if (_entanglingRootsTimer < diffMs)
        {
            if (DoCast(Victim, SpellEntanglingRoots) == CreatureCastResult.Ok)
            {
                _entanglingRootsTimer = AvScript.URand(Rng, 9000, 13000);
            }
        }
        else
        {
            _entanglingRootsTimer -= diffMs;
        }

        if (_rejuvenationTimer < diffMs)
        {
            DoCast(Me, SpellRejuvenation, triggered: true);
            _rejuvenationTimer = 17000;
        }
        else
        {
            _rejuvenationTimer -= diffMs;
        }

        if (_starfireTimer < diffMs)
        {
            DoCast(Victim, SpellStarfire);
            _starfireTimer = 7000;
        }
        else
        {
            _starfireTimer -= diffMs;
        }
    }

    /// <summary>UpdateThurlogaAI (battleground_alterac.cpp:2224-2276).</summary>
    private void UpdateThurloga(uint diffMs)
    {
        if (_chainLightningTimer < diffMs)
        {
            if (DoCast(Victim, SpellChainLightning) == CreatureCastResult.Ok)
            {
                _chainLightningTimer = AvScript.URand(Rng, 15000, 18000);
            }
        }
        else
        {
            _chainLightningTimer -= diffMs;
        }

        if (_flameShockTimer < diffMs)
        {
            if (DoCast(Victim, SpellFlameShock) == CreatureCastResult.Ok)
            {
                _flameShockTimer = AvScript.URand(Rng, 8000, 9000);
            }
        }
        else
        {
            _flameShockTimer -= diffMs;
        }

        if (_lightningBoltTimer < diffMs)
        {
            if (DoCast(Victim, SpellLightningBolt) == CreatureCastResult.Ok)
            {
                _lightningBoltTimer = AvScript.URand(Rng, 11000, 12000);
            }
        }
        else
        {
            _lightningBoltTimer -= diffMs;
        }

        if (_healingWaveTimer < diffMs)
        {
            if (Me.MaxHealth > 0 && Me.Health * 100f / Me.MaxHealth < 70.0f)
            {
                DoCast(Me, SpellHealingWave);
                _healingWaveTimer = 7000;
            }
        }
        else
        {
            _healingWaveTimer -= diffMs;
        }

        if (_earthbindTotemTimer < diffMs)
        {
            if (AvScript.Near(Me, EarthbindTotemEntry, 20f).All(t => !t.IsAlive))
            {
                DoCast(Me, SpellEarthbindTotem);
                _earthbindTotemTimer = 14000;
            }
        }
        else
        {
            _earthbindTotemTimer -= diffMs;
        }
    }

    /// <summary>UpdateEscortAI (battleground_alterac.cpp:2279-2470).</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        switch (Entry)
        {
            case NpcWingCommanderMulverick or NpcWingCommanderJeztor or NpcWingCommanderGuse
                or NpcWingCommanderIchman or NpcWingCommanderVipore or NpcWingCommanderSlidore:
                CheckAerial(diffMs);
                break;
            case NpcWolfRiderCommander or NpcRamRiderCommander:
                if (!_cavalrySpawned)
                {
                    CheckCavalry();
                }

                break;
            case NpcQuartermasterHorde or NpcQuartermasterAlliance:
                if (!_troopsSpawned)
                {
                    CheckTroops();
                }

                break;
        }

        if (_eventTimer <= diffMs)
        {
            if (_point == 2 && Entry == NpcWolfRiderCommander)
            {
                Rally(NpcWolfRider, 50f, SayWarcryHorde);
            }
            else if (_point == 5 && Entry == NpcRamRiderCommander)
            {
                Rally(NpcRamRider, 20f, SayWarcryAlliance);
            }
        }
        else
        {
            _eventTimer -= diffMs;
        }

        if (Entry == NpcPrimalistThurloga)
        {
            WatchWorldBoss(diffMs, GameObjectInvocationHorde, -360.16f, -130.41f, 27.07f, NpcFrostwolfShaman, ref _thurlogaBoss, 600000, SayLokholarSpawned);
        }
        else if (Entry == NpcArchDruidRenferal)
        {
            WatchWorldBoss(diffMs, GameObjectInvocationAlliance, -199.64f, -342.7f, 7.67f, NpcDruidOfTheGrove, ref _renferalBoss, 6000, 0);
        }

        if (!UpdateVictim() || Victim is null)
        {
            return;
        }

        if (Entry == NpcPrimalistThurloga)
        {
            UpdateThurloga(diffMs);
        }

        if (Entry == NpcArchDruidRenferal)
        {
            UpdateRenferal(diffMs);
        }
    }

    /// <summary>The rally at the commander's speech point: each rider gives its war cry, runs, and joins the commander's formation; the escort goes on.</summary>
    private void Rally(uint riders, float range, int warcry)
    {
        if (!_speechDone)
        {
            _speechDone = true;
            foreach (Creature rider in AvScript.Near(Me, riders, range))
            {
                AvScript.Say(rider, warcry);
                AvScript.SetWalk(rider, false);
                float distance = MathF.Sqrt(((Me.X - rider.X) * (Me.X - rider.X)) + ((Me.Y - rider.Y) * (Me.Y - rider.Y)));
                _scripts.JoinGroup(rider, Me, AvScript.Angle(Me, rider) - Me.Orientation, distance);
            }
        }

        SetEscortPaused(false);
        _eventTimer = 0;
    }

    /// <summary>
    /// The summoner's watch on its altar (battleground_alterac.cpp:2386-2454): once the altar it placed is gone (the world boss came and
    /// deleted it) and the summoner is near the place, the summoner yells (Thurloga) and leaves with its adds after the delay.
    /// </summary>
    private void WatchWorldBoss(uint diffMs, uint altar, float x, float y, float z, uint adds, ref bool bossCame, uint delayMs, int say)
    {
        bool altarStands = AvScript.NearObjects(Me, altar, 250f).Count > 0;
        if (_gobSummoned && !altarStands && AvScript.Distance(Me, x, y, z) < 100f && !bossCame)
        {
            bossCame = true;
            if (say != 0)
            {
                AvScript.Say(Me, say);
            }

            _despawnTimer = delayMs;
        }

        if (_despawnTimer < diffMs)
        {
            if (bossCame)
            {
                foreach (Creature add in AvScript.Near(Me, adds, 100f))
                {
                    AvScript.Unmount(add);
                    System?.ForcedDespawn(add, 0);
                }

                System?.ForcedDespawn(Me, 0);
                bossCame = false;
            }
        }
        else
        {
            _despawnTimer -= diffMs;
        }
    }
}
