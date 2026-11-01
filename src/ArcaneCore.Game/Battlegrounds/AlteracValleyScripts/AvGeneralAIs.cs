using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// The shared part of the Alterac Valley generals and war masters (vmangos scripts/battlegrounds/battleground_alterac.cpp:71-1148,
/// npc_alterac_bossHelper and the npc_VanndarAI, npc_DrekTharAI, npc_BalindaAI, npc_GalvangarAI and npc_WarMasterAI scripts): the global
/// cooldown the casts share, the one-shot timer pattern, the leash a general holds its room by, the yells at falling health, the rooted
/// stance of a whirlwind and the linked mobs that join a general's fight. Scripts run on the world thread, one instance per creature.
/// <para>
/// The match map is the only place these scripts are registered (<see cref="AlteracValleyScripts"/>), so vmangos' <c>GetMapId() ==
/// MAP_ALTERAC_VALLEY</c> tests are true by construction and are not repeated.
/// </para>
/// </summary>
public abstract class AvGeneralAI : ScriptedAI
{
    /// <summary>The ScriptDev GlobalCD: set by a cast, counted down by the update, held at 1 while a spell is cast.</summary>
    private uint _globalCooldown;

    private readonly HashSet<uint> _linked = [];
    private uint _linkScanMs;
    private bool _rooted;

    protected AvGeneralAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        Scripts = scripts;
    }

    protected AlteracValleyScripts Scripts { get; }

    protected Random Rng => Scripts.Match.ScriptRandom;

    /// <summary>The global cooldown left in milliseconds (0: a cast may start).</summary>
    protected uint GlobalCooldown
    {
        get => _globalCooldown;
        set => _globalCooldown = value;
    }

    /// <summary>Whether the script holds the creature rooted for a whirlwind or a standing cast.</summary>
    protected bool IsRooted => _rooted;

    /// <summary>Entries whose creatures within 100 yd join this one's fight (npc_alterac_bossHelper::AddLinkedMob).</summary>
    protected void AddLinkedMob(uint entry) => _linked.Add(entry);

    /// <summary>The global cooldown bookkeeping at the top of every update's fight part (:150-157 and the same block in each script).</summary>
    protected void TickGlobalCooldown(uint diffMs)
    {
        if (_globalCooldown > diffMs)
        {
            _globalCooldown -= diffMs;
        }
        else
        {
            _globalCooldown = IsCastingSpell ? 1u : 0u;
        }
    }

    /// <summary>Whether the creature is casting a non-melee spell (Unit::IsNonMeleeSpellCasted(false)).</summary>
    protected bool IsCastingSpell => System?.AiServices.Spells?.IsCasting(Me) ?? false;

    /// <summary>
    /// The ScriptDev timer: true when the timer is due (strictly less than the elapsed time, nothing taken off it then, as in vmangos where a
    /// due timer waits for a free global cooldown); otherwise the elapsed time comes off it and false is returned.
    /// </summary>
    protected static bool Due(ref uint timer, uint diffMs)
    {
        if (timer < diffMs)
        {
            return true;
        }

        timer -= diffMs;
        return false;
    }

    /// <summary>Unit::AddUnitState / ClearUnitState(UNIT_STATE_ROOT): the creature stands still for a whirlwind or a stationary cast.</summary>
    protected void SetRooted(bool rooted)
    {
        if (_rooted == rooted)
        {
            return;
        }

        _rooted = rooted;
        System?.SetAiImmobilized(Me, rooted, combatOnly: true);
    }

    /// <summary>The root a reset or a leash clears (ClearUnitState(UNIT_STATE_ROOT)).</summary>
    protected void ClearRoot()
    {
        if (_rooted)
        {
            _rooted = false;
            System?.SetAiImmobilized(Me, false, combatOnly: false);
        }
    }

    /// <summary>The distance in the plane from the creature to a point, less its own bounding radius (WorldObject::GetDistance2d(x, y)).</summary>
    protected float Distance2D(float x, float y)
    {
        float dx = Me.X - x;
        float dy = Me.Y - y;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy)) - Me.BoundingRadius;
        return distance > 0 ? distance : 0;
    }

    /// <summary>
    /// The leash (:197-206): the creature that strayed more than <paramref name="radius"/> from the room's centre stops fighting, heals fully and
    /// is rooted no more, then evades (and with it runs Reset). True when it leashed.
    /// </summary>
    protected bool LeashedOutOfRoom(float centerX, float centerY, float radius)
    {
        if (Distance2D(centerX, centerY) <= radius || System is not { } system)
        {
            return false;
        }

        system.StopCombatInPlace(Me); // Unit::CombatStop
        Me.Health = Me.MaxHealth;
        ClearRoot();
        EnterEvadeMode();
        return true;
    }

    /// <summary>
    /// A boss script's MoveInLineOfSight (:143-150): without a victim, a hostile unit closer than <paramref name="range"/> is attacked. The port asks
    /// the engine's aggro rules for the same range (alive, attackable, line of sight, hostile) instead of the bare distance test.
    /// </summary>
    protected bool SightAggro(Unit who, float range)
    {
        if (Victim is not null || System is not { } system || !system.CanAggroOnSight(Me, who, scriptedRange: range))
        {
            return false;
        }

        system.EnterCombatWithTarget(Me, who);
        return true;
    }

    /// <summary>
    /// npc_alterac_bossHelper::AggroLinkedMobsIfNeeded: every living linked creature within 100 yd that is not fighting attacks everyone on this
    /// creature's threat list (the ThreatListCopier). vmangos scans every update; the port scans four times a second (one pass over the
    /// map's creatures instead of one search per linked entry), which the linked mobs cannot tell apart.
    /// </summary>
    protected void AggroLinkedMobsIfNeeded(uint diffMs)
    {
        if (_linked.Count == 0 || System is not { } system)
        {
            return;
        }

        if (_linkScanMs > diffMs)
        {
            _linkScanMs -= diffMs;
            return;
        }

        _linkScanMs = 250;
        if (!Me.Combat.HasThreatList || Me.Combat.Threat.Entries.Count == 0)
        {
            return;
        }

        List<Creature>? idle = null;
        foreach (Creature other in system.Creatures)
        {
            if (!_linked.Contains(other.Template.Entry) || !other.IsAlive || other.Combat.IsInCombat || other.AI is null
                || WorldGeometry.DistanceSq3D(Me, other) > 100f * 100f)
            {
                continue;
            }

            (idle ??= []).Add(other);
        }

        if (idle is null)
        {
            return;
        }

        Unit[] enemies = [.. Me.Combat.Threat.Entries.Select(e => e.Target)];
        foreach (Creature other in idle)
        {
            foreach (Unit enemy in enemies)
            {
                other.AI?.AttackStart(enemy);
            }
        }
    }

    /// <summary>The five health marks at which a general yells one of its six fight lines (:211-239).</summary>
    protected static readonly float[] FightYellMarks = [80f, 60f, 40f, 20f, 5f];

    /// <summary>
    /// The fight yells: each mark not yet passed says one random line of <paramref name="lines"/> the first time health is below it.
    /// <paramref name="done"/> holds one flag per mark.
    /// </summary>
    protected void FightYells(bool[] done, int[] lines)
    {
        for (int i = 0; i < FightYellMarks.Length; i++)
        {
            if (!done[i] && Me.HealthPercent < FightYellMarks[i])
            {
                AvScript.Say(Me, lines[Rng.Next(0, lines.Length)]);
                done[i] = true;
            }
        }
    }

    /// <summary>Creature::GetVictimInRange(min, max): the first unit of the threat list between the two distances.</summary>
    protected Unit? VictimInRange(float min, float max)
    {
        if (!Me.Combat.HasThreatList)
        {
            return null;
        }

        foreach (ThreatEntry entry in Me.Combat.Threat.Entries)
        {
            float distance = WorldGeometry.DistanceMinusRadii(Me, entry.Target);
            if (distance >= min && distance <= max)
            {
                return entry.Target;
            }
        }

        return null;
    }

    /// <summary>The cast of one ability when its timer is due and the global cooldown is free; the timer and cooldown are set when it went off.</summary>
    protected bool CastWhenDue(ref uint timer, uint diffMs, Unit? target, uint spell, uint minMs, uint maxMs, uint globalCooldownMs)
    {
        if (Due(ref timer, diffMs) && _globalCooldown == 0 && DoCast(target, spell) == CreatureCastResult.Ok)
        {
            timer = AvScript.URand(Rng, minMs, maxMs);
            _globalCooldown = globalCooldownMs;
            return true;
        }

        return false;
    }
}

/// <summary>
/// vmangos npc_VanndarAI (battleground_alterac.cpp:84-262), General Vanndar Stormpike (script npc_Vanndar): he attacks whoever comes within
/// 23 yd, holds his room by a 35 yd leash from its centre (a full reset with the reset yell), brings the war masters (13333 and
/// 14762-14769) into his fight, and casts Avatar, Stormbolt on his victim and Thunderclap; he yells when he is hurt.
/// </summary>
public sealed class AvVanndarAI : AvGeneralAI
{
    public const uint EntryVanndar = 11948;

    public const uint SpellAvatar = 19135;
    public const uint SpellStormbolt = 19136;
    public const uint SpellThunderclap = 15588;

    public const int SayAggro = 10243;
    public const int SayReset = 10373;
    public const int SayRaidWipe = 10374;
    public static readonly int[] FightLines = [8838, 8839, 8840, 8841, 8842, 8843];

    /// <summary>POSITION_VANNDAR_CENTER_X / _Y (:50-51).</summary>
    public const float CenterX = 722.4f;
    public const float CenterY = -11.0f;
    public const float LeashRadius = 35.0f;

    private readonly bool[] _yelled = new bool[5];
    private bool _sayAggro = true;
    private bool _leashed;
    private uint _avatarTimer;
    private uint _stormboltTimer;
    private uint _thunderclapTimer;

    public AvVanndarAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature, scripts)
    {
        AddLinkedMob(13333);
        for (uint entry = 14762; entry <= 14769; entry++)
        {
            AddLinkedMob(entry);
        }

        ResetTimers();
    }

    private void ResetTimers()
    {
        GlobalCooldown = 0;
        _avatarTimer = AvScript.URand(Rng, 12000, 20000);
        _stormboltTimer = 8000;
        _thunderclapTimer = 5000;
        _sayAggro = true;
        _leashed = false;
        Array.Fill(_yelled, false);
    }

    protected override void Reset()
    {
        ClearRoot();
        ResetTimers();
    }

    /// <summary>EnterEvadeMode (:123-131): the reset line after a leash, the raid wipe line after a lost fight.</summary>
    public override void OnEvade()
    {
        AvScript.Say(Me, _leashed ? SayReset : SayRaidWipe);
        _leashed = false;
        Reset();
    }

    public override void OnAggro(Unit target)
    {
        if (_sayAggro)
        {
            AvScript.Say(Me, SayAggro);
            _sayAggro = false;
        }
    }

    public override void MoveInLineOfSight(Unit who) => SightAggro(who, 23f);

    public override void OnUpdate(uint diffMs)
    {
        if (Distance2D(CenterX, CenterY) > LeashRadius && !Me.IsInEvadeMode)
        {
            _leashed = true;
            LeashedOutOfRoom(CenterX, CenterY, LeashRadius);
            return;
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        TickGlobalCooldown(diffMs);
        CastWhenDue(ref _avatarTimer, diffMs, Me, SpellAvatar, 20000, 30000, 1000);
        CastWhenDue(ref _stormboltTimer, diffMs, victim, SpellStormbolt, 8000, 10000, 1000);
        CastWhenDue(ref _thunderclapTimer, diffMs, Me, SpellThunderclap, 5000, 8000, 1000);
        FightYells(_yelled, FightLines);
        AggroLinkedMobsIfNeeded(diffMs);
    }
}

/// <summary>
/// vmangos npc_DrekTharAI (battleground_alterac.cpp:296-511), Warchief Drek'Thar (script npc_DrekThar): he attacks whoever comes within 22 yd,
/// holds his room by a 33 yd leash, brings his two dogs (12121, 12122) and the war masters (14772-14777) into his fight and respawns the dogs
/// when he resets; he casts Whirlwind (rooted for the two seconds of it), Knockdown and, below 31% health, Frenzy; he yells when he is hurt.
/// </summary>
public sealed class AvDrekTharAI : AvGeneralAI
{
    public const uint EntryDrekThar = 11946;

    public const uint SpellFrenzy = 28747;
    public const uint SpellWhirlwind = 13736;
    public const uint SpellKnockdown = 19128;

    public const int SayAggro = 10245;
    public const int SayReset = 10377;
    public const int SayRaidWipe = 10376;
    public static readonly int[] FightLines = [8844, 8845, 8846, 8847, 8848, 8849];

    /// <summary>POSITION_DKT_CENTER_X / _Y (:292-293).</summary>
    public const float CenterX = -1370.9f;
    public const float CenterY = -219.8f;
    public const float LeashRadius = 33.0f;

    private static readonly uint[] s_dogs = [12121, 12122];

    private readonly bool[] _yelled = new bool[5];
    private bool _sayAggro = true;
    private bool _leashed;
    private uint _whirlwindTimer;
    private uint _inWhirlwindTimer;
    private uint _knockdownTimer;
    private uint _frenzyTimer;

    public AvDrekTharAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature, scripts)
    {
        foreach (uint dog in s_dogs)
        {
            AddLinkedMob(dog);
        }

        for (uint entry = 14772; entry <= 14777; entry++)
        {
            AddLinkedMob(entry);
        }

        ResetTimers();
    }

    private void ResetTimers()
    {
        GlobalCooldown = 0;
        _whirlwindTimer = AvScript.URand(Rng, 8000, 12000);
        _inWhirlwindTimer = 0;
        _knockdownTimer = 18000;
        _frenzyTimer = 1000;
        _sayAggro = true;
        _leashed = false;
        Array.Fill(_yelled, false);
    }

    /// <summary>Reset (:316-344): the timers, the frenzy gone and both dogs back when they died.</summary>
    protected override void Reset()
    {
        ClearRoot();
        ResetTimers();
        if (System is not { } system)
        {
            return;
        }

        system.RemoveAuras(Me, SpellFrenzy);
        foreach (Creature dog in system.Creatures.ToArray())
        {
            if (Array.IndexOf(s_dogs, dog.Template.Entry) >= 0 && !dog.IsAlive && WorldGeometry.DistanceSq3D(Me, dog) <= 100f * 100f)
            {
                system.ForceRespawn(dog);
            }
        }
    }

    /// <summary>EnterEvadeMode (:346-355).</summary>
    public override void OnEvade()
    {
        AvScript.Say(Me, _leashed ? SayReset : SayRaidWipe);
        _leashed = false;
        Reset();
    }

    public override void OnAggro(Unit target)
    {
        if (_sayAggro)
        {
            AvScript.Say(Me, SayAggro);
            _sayAggro = false;
        }
    }

    public override void MoveInLineOfSight(Unit who) => SightAggro(who, 22f);

    public override void OnUpdate(uint diffMs)
    {
        if (Distance2D(CenterX, CenterY) > LeashRadius && !Me.IsInEvadeMode)
        {
            _leashed = true;
            LeashedOutOfRoom(CenterX, CenterY, LeashRadius);
            return;
        }

        _inWhirlwindTimer = _inWhirlwindTimer < diffMs ? 0 : _inWhirlwindTimer - diffMs;
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        TickGlobalCooldown(diffMs);
        SetRooted(_inWhirlwindTimer != 0);

        if (Due(ref _whirlwindTimer, diffMs) && GlobalCooldown == 0 && DoCast(Me, SpellWhirlwind) == CreatureCastResult.Ok)
        {
            _whirlwindTimer = AvScript.URand(Rng, 12000, 16000);
            _inWhirlwindTimer = 2000;
            GlobalCooldown = 2000;
        }

        CastWhenDue(ref _knockdownTimer, diffMs, victim, SpellKnockdown, 16000, 24000, 1000);

        if (Due(ref _frenzyTimer, diffMs) && GlobalCooldown == 0 && Me.HealthPercent <= 30f && DoCast(Me, SpellFrenzy) == CreatureCastResult.Ok)
        {
            _frenzyTimer = 120000;
            GlobalCooldown = 1000;
        }

        FightYells(_yelled, FightLines);
        AggroLinkedMobsIfNeeded(diffMs);
    }
}

/// <summary>
/// vmangos npc_BalindaAI (battleground_alterac.cpp:536-745), Captain Balinda Stonehearth (script npc_Balinda): the Alliance captain attacks whoever
/// comes within 28 yd, holds her room by a 45 yd leash (no yell for that), stands still while her victim is 5 to 25 yd away in sight, and casts
/// Arcane Explosion (only with three or more enemies within 6 yd), Cone of Cold (within 10 yd), Polymorph (on the second on her threat list),
/// Frostbolt (beyond 15 yd) and Fireball.
/// </summary>
public sealed class AvBalindaAI : AvGeneralAI
{
    public const uint EntryBalinda = 11949;

    public const uint SpellFireball = 20420;
    public const uint SpellFrostbolt = 5530;
    public const uint SpellConeOfCold = 22746;
    public const uint SpellArcaneExplosion = 19712;
    public const uint SpellPolymorph = 15534;

    public const int SayAggro = 10054;
    public const int SayHalfHealth = 10056;
    public const int SayReset = 10375;

    /// <summary>POSITION_BAL_CENTER_X / _Y (:532-533).</summary>
    public const float CenterX = -57.7f;
    public const float CenterY = -286.6f;
    public const float LeashRadius = 45.0f;

    private bool _sayHalf = true;
    private bool _sayAggro = true;
    private uint _fireballTimer;
    private uint _frostboltTimer;
    private uint _coneOfColdTimer;
    private uint _arcaneExplosionTimer;
    private uint _polymorphTimer;

    public AvBalindaAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature, scripts) => ResetTimers();

    private void ResetTimers()
    {
        GlobalCooldown = 0;
        _fireballTimer = 0;
        _frostboltTimer = 0;
        _coneOfColdTimer = 1500;
        _arcaneExplosionTimer = 2000;
        _polymorphTimer = 1750;
        _sayHalf = true;
        _sayAggro = true;
    }

    protected override void Reset()
    {
        ClearRoot();
        ResetTimers();
    }

    /// <summary>The reset line of vmangos' Reset() once it ran before: every evade says it (a respawn does not; vmangos' JustRespawned does).</summary>
    public override void OnEvade()
    {
        AvScript.Say(Me, SayReset);
        Reset();
    }

    public override void OnAggro(Unit target)
    {
        if (_sayAggro)
        {
            AvScript.Say(Me, SayAggro);
            _sayAggro = false;
        }
    }

    public override void MoveInLineOfSight(Unit who) => SightAggro(who, 28f);

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (LeashedOutOfRoom(CenterX, CenterY, LeashRadius))
        {
            return;
        }

        if (_sayHalf && Me.HealthPercent < 50f)
        {
            AvScript.Say(Me, SayHalfHealth);
            _sayHalf = false;
        }

        TickGlobalCooldown(diffMs);

        float victimDistance = WorldGeometry.DistanceMinusRadii(Me, victim);
        bool standing = victimDistance <= 25f && victimDistance >= 5f && (System?.IsInLineOfSight(Me, victim) ?? true);
        SetRooted(standing);

        // Arcane Explosion: vmangos rolls urand(0, 1000) > 1000 * (3 / (n + 1)) with integer division, which is true only for three or more
        // enemies within 6 yd (and not on a roll of 0): kept as it is.
        if (Due(ref _arcaneExplosionTimer, diffMs) && GlobalCooldown == 0)
        {
            int near = Me.Combat.Threat.Entries.Count(e => WorldGeometry.DistanceMinusRadii(Me, e.Target) <= 6f);
            if (Rng.Next(0, 1001) > 1000 * (3 / (near + 1)))
            {
                if (DoCast(victim, SpellArcaneExplosion) == CreatureCastResult.Ok)
                {
                    _arcaneExplosionTimer = AvScript.URand(Rng, 6000, 9000);
                    GlobalCooldown = 1000;
                }
            }
            else
            {
                _arcaneExplosionTimer = 1000;
            }
        }

        if (Due(ref _coneOfColdTimer, diffMs) && GlobalCooldown == 0)
        {
            if (victimDistance < 10f)
            {
                if (DoCast(victim, SpellConeOfCold) == CreatureCastResult.Ok)
                {
                    _coneOfColdTimer = AvScript.URand(Rng, 12000, 16000);
                    GlobalCooldown = 1000;
                }
            }
            else
            {
                _coneOfColdTimer = 2000;
            }
        }

        if (Due(ref _polymorphTimer, diffMs) && GlobalCooldown == 0)
        {
            // SelectAttackingTarget(ATTACKING_TARGET_TOPAGGRO, 1): the second on the threat list.
            IReadOnlyList<ThreatEntry> threat = Me.Combat.Threat.Entries;
            if (threat.Count > 1 && threat[1].Target.IsAlive)
            {
                if (DoCast(threat[1].Target, SpellPolymorph) == CreatureCastResult.Ok)
                {
                    _polymorphTimer = AvScript.URand(Rng, 16000, 20000);
                    GlobalCooldown = 1000;
                }
            }
            else
            {
                _polymorphTimer = 2000;
            }
        }

        if (Due(ref _frostboltTimer, diffMs) && GlobalCooldown == 0)
        {
            if (victimDistance > 15f)
            {
                if (DoCast(victim, SpellFrostbolt) == CreatureCastResult.Ok)
                {
                    _frostboltTimer = AvScript.URand(Rng, 8000, 12000);
                    GlobalCooldown = 1000;
                }
            }
            else
            {
                _coneOfColdTimer = 2000; // vmangos resets the cone of cold timer here, not the frostbolt's (:668): kept
            }
        }

        CastWhenDue(ref _fireballTimer, diffMs, victim, SpellFireball, 2500, 3000, 1000);
    }
}

/// <summary>
/// vmangos npc_GalvangarAI (battleground_alterac.cpp:768-956), Captain Galvangar (script npc_Galvangar): the Horde captain attacks whoever comes
/// within 28 yd, holds his room by a 45 yd leash (no yell for that), and casts Whirlwind (rooted for the two seconds of it, only with three or
/// more enemies within 6 yd), Frightening Shout, Cleave and Mortal Strike; he yells at half health.
/// </summary>
public sealed class AvGalvangarAI : AvGeneralAI
{
    public const uint EntryGalvangar = 11947;

    public const uint SpellWhirlwind = 13736;
    public const uint SpellMortalStrike = 16856;
    public const uint SpellCleave = 15284;
    public const uint SpellFrighteningShout = 19134;

    public const int SayAggro = 10055;
    public const int SayHalfHealth = 10057;
    public const int SayReset = 10378;

    /// <summary>POSITION_GAL_CENTER_X / _Y (:764-765).</summary>
    public const float CenterX = -545.2f;
    public const float CenterY = -165.3f;
    public const float LeashRadius = 45.0f;

    private bool _sayHalf = true;
    private bool _sayAggro = true;
    private uint _whirlwindTimer;
    private uint _inWhirlwindTimer;
    private uint _mortalStrikeTimer;
    private uint _cleaveTimer;
    private uint _frighteningShoutTimer;

    public AvGalvangarAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature, scripts) => ResetTimers();

    private void ResetTimers()
    {
        GlobalCooldown = 0;
        _whirlwindTimer = AvScript.URand(Rng, 8000, 12000);
        _inWhirlwindTimer = 0;
        _mortalStrikeTimer = 7000;
        _cleaveTimer = 4000;
        _frighteningShoutTimer = _whirlwindTimer + AvScript.URand(Rng, 1000, 5000);
        _sayHalf = true;
        _sayAggro = true;
    }

    protected override void Reset()
    {
        ClearRoot();
        ResetTimers();
    }

    /// <summary>The reset line of vmangos' Reset() once it ran before: every evade says it.</summary>
    public override void OnEvade()
    {
        AvScript.Say(Me, SayReset);
        Reset();
    }

    public override void OnAggro(Unit target)
    {
        if (_sayAggro)
        {
            AvScript.Say(Me, SayAggro);
            _sayAggro = false;
        }
    }

    public override void MoveInLineOfSight(Unit who) => SightAggro(who, 28f);

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (LeashedOutOfRoom(CenterX, CenterY, LeashRadius))
        {
            return;
        }

        _inWhirlwindTimer = _inWhirlwindTimer < diffMs ? 0 : _inWhirlwindTimer - diffMs;
        if (_sayHalf && Me.HealthPercent < 50f)
        {
            AvScript.Say(Me, SayHalfHealth);
            _sayHalf = false;
        }

        TickGlobalCooldown(diffMs);
        SetRooted(_inWhirlwindTimer != 0);

        // The same integer-division roll as Balinda's Arcane Explosion: whirlwind only with three or more enemies within 6 yd.
        if (Due(ref _whirlwindTimer, diffMs) && GlobalCooldown == 0)
        {
            int near = Me.Combat.Threat.Entries.Count(e => WorldGeometry.DistanceMinusRadii(Me, e.Target) <= 6f);
            if (Rng.Next(0, 1001) > 1000 * (3 / (near + 1)))
            {
                if (DoCast(victim, SpellWhirlwind) == CreatureCastResult.Ok)
                {
                    _whirlwindTimer = AvScript.URand(Rng, 10000, 16000);
                    _inWhirlwindTimer = 2000;
                    GlobalCooldown = 2000;
                }
            }
            else
            {
                _whirlwindTimer = 1000;
            }
        }

        CastWhenDue(ref _frighteningShoutTimer, diffMs, victim, SpellFrighteningShout, 24000, 32000, 1000);
        CastWhenDue(ref _cleaveTimer, diffMs, victim, SpellCleave, 7000, 9000, 1000);
        CastWhenDue(ref _mortalStrikeTimer, diffMs, victim, SpellMortalStrike, 9000, 13000, 1000);
    }
}

/// <summary>
/// vmangos npc_WarMasterAI (battleground_alterac.cpp:973-1148, script npc_WarMaster), the marshals and warmasters that stand with the generals:
/// one that comes within 22 yd of a hostile unit charges it and fights; it casts Whirlwind (rooted for four seconds, a second one two seconds
/// after the first), Enrage below 31% health, Charge (on a victim 8 to 25 yd away), Cleave and Demoralizing Shout. One that died is deleted
/// the next time it updates, which is after it respawned: a linked objective that was tagged and defended in between would bring it back.
/// </summary>
public sealed class AvWarMasterAI : AvGeneralAI
{
    public const uint SpellCharge = 22911;
    public const uint SpellCleave = 20684;
    public const uint SpellDemoralizingShout = 23511;
    public const uint SpellEnrage = 8599;
    public const uint SpellWhirlwind = 13736;

    private bool _killed;
    private uint _chargeTimer;
    private uint _cleaveTimer;
    private uint _shoutTimer;
    private uint _whirlwindTimer;
    private uint _inWhirlwindTimer;
    private uint _enrageTimer;

    public AvWarMasterAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature, scripts) => ResetTimers();

    /// <summary>
    /// The marshals' and warmasters' entries: 14762 to 14777 without a gap, the Dun Baldar North and South warmasters (14770,
    /// 14771) included (vmangos world DB sets ScriptName npc_WarMaster on every entry of the range).
    /// </summary>
    public static IEnumerable<uint> Entries
    {
        get
        {
            for (uint entry = 14762; entry <= 14777; entry++)
            {
                yield return entry;
            }
        }
    }

    /// <summary>Whether this creature died and goes at its next update.</summary>
    public bool Killed => _killed;

    private void ResetTimers()
    {
        GlobalCooldown = 0;
        _chargeTimer = 0;
        _cleaveTimer = 8000;
        _shoutTimer = 4000;
        _whirlwindTimer = 12000;
        _enrageTimer = 0;
        _inWhirlwindTimer = 0;
    }

    protected override void Reset()
    {
        ClearRoot();
        ResetTimers();
    }

    public override void OnDeath(Unit? killer) => _killed = true;

    public override void MoveInLineOfSight(Unit who)
    {
        if (SightAggro(who, 22f))
        {
            if (DoCast(who, SpellCharge) == CreatureCastResult.Ok)
            {
                _chargeTimer = 12000;
            }
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_killed)
        {
            System?.Despawn(Me); // Creature::DeleteLater
            return;
        }

        _inWhirlwindTimer = _inWhirlwindTimer < diffMs ? 0 : _inWhirlwindTimer - diffMs;
        if (!UpdateVictim() || Victim is null)
        {
            return;
        }

        TickGlobalCooldown(diffMs);
        SetRooted(_inWhirlwindTimer != 0);

        if (Due(ref _whirlwindTimer, diffMs) && GlobalCooldown == 0 && DoCast(Me, SpellWhirlwind) == CreatureCastResult.Ok)
        {
            if (_inWhirlwindTimer < diffMs)
            {
                _inWhirlwindTimer = 4000;
                _whirlwindTimer = 2000;
            }
            else
            {
                _whirlwindTimer = AvScript.URand(Rng, 11000, 13000);
            }

            GlobalCooldown = 2000;
        }

        if (Due(ref _enrageTimer, diffMs) && GlobalCooldown == 0 && Me.HealthPercent <= 30f && DoCast(Me, SpellEnrage) == CreatureCastResult.Ok)
        {
            _enrageTimer = 120000;
            GlobalCooldown = 1000;
        }

        if (Due(ref _chargeTimer, diffMs) && GlobalCooldown == 0)
        {
            if (VictimInRange(8f, 25f) is { } target)
            {
                if (DoCast(target, SpellCharge) == CreatureCastResult.Ok)
                {
                    _chargeTimer = AvScript.URand(Rng, 12000, 18000);
                    GlobalCooldown = 250;
                }
            }
            else
            {
                _chargeTimer = AvScript.URand(Rng, 5000, 10000);
            }
        }

        CastWhenDue(ref _cleaveTimer, diffMs, Me, SpellCleave, 8000, 10000, 1000);
        CastWhenDue(ref _shoutTimer, diffMs, Me, SpellDemoralizingShout, 14000, 20000, 1000);
    }
}
