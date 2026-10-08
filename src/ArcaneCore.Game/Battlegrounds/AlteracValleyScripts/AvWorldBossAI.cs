using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos av_world_boss_baseai, AV_NpcEventWorldBoss_H_AI and AV_NpcEventWorldBoss_A_AI (scripts/battlegrounds/battleground_alterac.cpp:
/// 3918-4354): Lokholar the Ice Lord (script npc_worldboss_h_av) and Ivus the Forest Lord (npc_worldboss_a_av), the world bosses the
/// players' summoning ritual brings.
/// <list type="bullet">
/// <item>Only one of each lives: a second one disappears at once (events 102 and 103); the event goes when the boss dies.</item>
/// <item>When it comes the living players lose the summoners' channel (11206), the altar (or circle) within 200 yd is deleted and the
/// summoner and its adds stop channelling.</item>
/// <item>It announces itself (the Ice Lord a second line three seconds later), takes the field south of the bridge as its home, and a second
/// later walks its escort path to the enemy base, breaking off to fight and going back to where each fight began; at the base it announces
/// it and stays (Lokholar at point 31, Ivus at point 21); after a fight there it wanders 55 yd around it.</item>
/// <item>It fights with its own spells; the Ice Lord grows with every player it kills (Swell of Souls).</item>
/// </list>
/// </summary>
public sealed class AvWorldBossAI : EscortAI
{
    // battleground_alterac.cpp:3895-3905, 4176-4189
    public const uint SpellBlizzard = 21367;
    public const uint SpellFrostbolt = 21369;
    public const uint SpellFrostNova = 14907;
    public const uint SpellFrostShock = 19133;
    public const uint SpellIceBlast = 15878;
    public const uint SpellIceTomb = 16869;
    public const uint SpellSwellOfSouls = 21307;
    public const uint SpellRoots = 20654;
    public const uint SpellFaerieFire = 21670;
    public const uint SpellMoonfire = 21669;
    public const uint SpellStarfire = 21668;
    public const uint SpellWrath = 21667;

    public const int SayLokholarSpawn1 = 8616;
    public const int SayLokholarSpawn2 = 8617;
    public const int SayLokholarKilledPlayer = 8618;
    public const int SayLokholarReachedBase = 8740;
    public const int SayIvusSpawned = 8736;
    public const int SayIvusPastField = 8737;
    public const int SayIvusReachedBase = 8739;

    private readonly AlteracValleyScripts _scripts;
    private readonly bool _horde;
    private uint _engageTimer = 1000; // "Once invocation is done, World Boss doesn't start waypoint path for 10 minutes." (1 s in vmangos)
    private bool _engaged;
    private bool _invocated;
    private bool _yelling;
    private uint _secondYellTimer;
    private bool _aggro;
    private bool _refused;

    // Lokholar
    private uint _blizzardTimer;
    private uint _frostboltTimer;
    private uint _frostNovaTimer;
    private uint _frostShockTimer;
    private uint _iceBlastTimer;
    private uint _iceTombTimer;

    // Ivus
    private uint _rootsTimer;
    private uint _faerieFireTimer;
    private uint _moonfireTimer;
    private uint _starfireTimer;
    private uint _wrathTimer;

    public AvWorldBossAI(Creature creature, AlteracValleyScripts scripts, bool horde)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
        _horde = horde;
    }

    private byte BossEvent => _horde ? EventBossLokholar : EventBossIvus;

    private Random Rng => _scripts.Match.ScriptRandom;

    /// <summary>Whether this boss was refused because another of its kind lives (it disappears).</summary>
    public bool Refused => _refused;

    /// <summary>The field the boss takes as its home before it sets off (SetHomePosition(-260, -290, 6.7)).</summary>
    public static (float X, float Y, float Z) Field => (-260.0f, -290.0f, 6.7f);

    /// <summary>The script constructors (:3928-3941, 3999-4040, 4205-4244).</summary>
    protected override void JustSpawned()
    {
        if (!_scripts.Match.TryClaimWorldBoss(BossEvent))
        {
            _refused = true;
            System?.ForcedDespawn(Me, 1); // DeleteLater
            return;
        }

        if (System is { } system && Me.Map is { } map)
        {
            foreach (Player player in map.Players)
            {
                if (player.IsAlive)
                {
                    system.RemoveAuras(player, SpellInvocation);
                }
            }

            foreach (GameObjects.GameObject altar in AvScript.NearObjects(Me, _horde ? GameObjectInvocationHorde : GameObjectInvocationAlliance, 200f))
            {
                AvScript.Objects(Me)?.Remove(altar);
            }

            foreach (Creature add in AvScript.Near(Me, _horde ? NpcFrostwolfShaman : NpcDruidOfTheGrove, 200f))
            {
                system.InterruptCast(add);
            }

            foreach (Creature summoner in AvScript.Near(Me, _horde ? NpcPrimalistThurloga : NpcArchDruidRenferal, 200f))
            {
                system.InterruptCast(summoner);
            }
        }
    }

    /// <summary>Reset (:4055-4073, 4259-4276): the spell timers; after a fight the path goes on from where the fight began, walking.</summary>
    protected override void Reset()
    {
        if (_horde)
        {
            _blizzardTimer = 10000;
            _frostboltTimer = 1000;
            _frostNovaTimer = 8500;
            _frostShockTimer = 4000;
            _iceBlastTimer = 9000;
            _iceTombTimer = 5500;
        }
        else
        {
            _rootsTimer = 8500;
            _faerieFireTimer = 5500;
            _moonfireTimer = 7000;
            _starfireTimer = 10000;
            _wrathTimer = 1000;
        }

        if (_aggro)
        {
            SetEscortPaused(false);
            Me.Motion.MovePoint(PointLastPoint, CombatStartPosition.X, CombatStartPosition.Y, CombatStartPosition.Z, run: true);
            AvScript.SetWalk(Me, true);
            _aggro = false;
        }
    }

    protected override void Aggro(Unit target)
    {
        _aggro = true;
        SetEscortPaused(true);
    }

    /// <summary>av_world_boss_baseai::JustDied: the boss's event goes.</summary>
    public override void OnDeath(Unit? killer)
    {
        if (!_refused)
        {
            _scripts.Match.ReleaseWorldBoss(BossEvent);
        }
    }

    /// <summary>KilledUnit (Lokholar, :4089-4098): every player killed brings a line and a stack of Swell of Souls.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (_horde && victim is Player)
        {
            AvScript.Say(Me, SayLokholarKilledPlayer);
            DoCast(Me, SpellSwellOfSouls, triggered: true);
        }
    }

    /// <summary>WaypointReached (:4075-4087, 4282-4291).</summary>
    protected override void WaypointReached(uint pointId)
    {
        if (_horde)
        {
            switch (pointId)
            {
                case 30:
                    AvScript.Say(Me, SayLokholarReachedBase);
                    System?.SetHomePosition(Me, Me.X, Me.Y, Me.Z, 0f);
                    break;
                case 31:
                    Stop();
                    break;
            }
        }
        else if (pointId == 1)
        {
            AvScript.Say(Me, SayIvusPastField);
        }
        else if (pointId == 21)
        {
            AvScript.Say(Me, SayIvusReachedBase);
            Stop();
        }
    }

    /// <summary>After a fight at the base (no escort left) the boss wanders around its home (the random default vmangos set at the start).</summary>
    public override void OnReachedHome()
    {
        if (_invocated && !HasEscortState(EscortState.Escorting))
        {
            System?.SetDefaultRandomMovement(Me, 55f);
        }
    }

    /// <summary>UpdateEscortAI (:4100-4170, 4293-4351).</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_refused)
        {
            return;
        }

        if (_horde)
        {
            if (!_yelling)
            {
                AvScript.Say(Me, SayLokholarSpawn1);
                _secondYellTimer = 3000; // ScriptCommandStart(TALK SAY_LOKHOLAR_SPAWN_2, 3 s)
                _yelling = true;
            }
            else if (_secondYellTimer != 0)
            {
                if (_secondYellTimer <= diffMs)
                {
                    _secondYellTimer = 0;
                    AvScript.Say(Me, SayLokholarSpawn2);
                }
                else
                {
                    _secondYellTimer -= diffMs;
                }
            }
        }

        if (!_invocated)
        {
            if (!_horde)
            {
                AvScript.Say(Me, SayIvusSpawned);
            }

            (float x, float y, float z) = Field;
            System?.SetHomePosition(Me, x, y, z, 0f);
            AvScript.SetWalk(Me, false);
            _invocated = true;
        }

        if (_engageTimer < diffMs && !_engaged)
        {
            Start(run: false);
            _engaged = true;
        }
        else if (!_engaged)
        {
            _engageTimer -= diffMs;
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_horde)
        {
            UpdateLokholar(victim, diffMs);
        }
        else
        {
            UpdateIvus(victim, diffMs);
        }
    }

    private void UpdateLokholar(Unit victim, uint diffMs)
    {
        Timed(ref _blizzardTimer, diffMs, victim, SpellBlizzard, 10000, 12000);
        Timed(ref _frostboltTimer, diffMs, victim, SpellFrostbolt, 4500, 6500);
        Timed(ref _frostNovaTimer, diffMs, victim, SpellFrostNova, 8000, 12000);
        Timed(ref _frostShockTimer, diffMs, victim, SpellFrostShock, 3000, 5000);
        Timed(ref _iceBlastTimer, diffMs, victim, SpellIceBlast, 10000, 12000);
        Timed(ref _iceTombTimer, diffMs, victim, SpellIceTomb, 8000, 12000);
    }

    private void UpdateIvus(Unit victim, uint diffMs)
    {
        Timed(ref _rootsTimer, diffMs, Me, SpellRoots, 6000, 8000);
        Timed(ref _faerieFireTimer, diffMs, victim, SpellFaerieFire, 3000, 7000);
        Timed(ref _moonfireTimer, diffMs, victim, SpellMoonfire, 8000, 12000);
        Timed(ref _starfireTimer, diffMs, victim, SpellStarfire, 3000, 7000);
        Timed(ref _wrathTimer, diffMs, victim, SpellWrath, 1000, 3000);
    }

    /// <summary>The DoCastSpellIfCan timer pattern: the cast when due, the next one urand(min, max) later when it went off.</summary>
    private void Timed(ref uint timer, uint diffMs, Unit target, uint spell, uint min, uint max)
    {
        if (timer < diffMs)
        {
            if (DoCast(target, spell) == CreatureCastResult.Ok)
            {
                timer = AvScript.URand(Rng, min, max);
            }
        }
        else
        {
            timer -= diffMs;
        }
    }
}
