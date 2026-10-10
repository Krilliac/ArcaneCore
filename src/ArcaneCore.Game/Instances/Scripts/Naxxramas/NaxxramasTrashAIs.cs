using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// mangos-classic eastern_kingdoms/naxxramas/naxxramas.cpp npc_stoneskin_gargoyleAI (16168). Reset: Acid Volley 4 s, Stealth Detection
/// (18950) if missing, and an idle (non-patrolling) gargoyle takes Stoneform (29154), becoming uninteractible and immune to players, also
/// on reaching home. A stone gargoyle wakes only for a player within 17 yd in line of sight. Aggro clears those flags; only idle gargoyles,
/// or ones whose spawn is below z 297.6, cast Acid Volley (29325, then every 8 s). Below 30% without Stoneskin it casts Stoneskin (28995)
/// with "%s emits a strange noise." (broadcast_text 10755). ClassicDB gives 16168 both AIName EventAI and this ScriptName; the script
/// replaces the EventAI, as in cmangos. Limits: the reference's leash box (evade beyond x 2963 / y -3476 / z 297.6) and its feign-death
/// check for the wake-up are not modelled.
/// </summary>
public sealed class StoneskinGargoyleAI : RaidBossAI
{
    public const uint Entry = 16168;
    public const uint Stoneform = 29154, StealthDetection = 18950, Stoneskin = 28995, AcidVolley = 29325;
    public const float WakeRange = 17f, ResetZ = 297.6f;

    private uint _volleyMs = 4000;

    public StoneskinGargoyleAI(Creature creature) : base(creature, null) { }

    public bool CanCastVolley { get; private set; }
    public bool IsStone => (Me.UnitFlags & UnitFlags.NotSelectable) != 0;

    protected override void ResetActions()
    {
        base.ResetActions();
        _volleyMs = 4000;
        CanCastVolley = false;
        TryStoneForm();
        if (!(System?.HasAura(Me, StealthDetection) ?? false)) Cast(StealthDetection, Me, triggered: true);
    }

    private void TryStoneForm()
    {
        if (Me.MovementType != CreatureMovementType.Idle) return;
        if (!(System?.HasAura(Me, Stoneform) ?? false) && !Cast(Stoneform, Me, triggered: true)) return;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        TryStoneForm();
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (!IsStone)
        {
            base.MoveInLineOfSight(who);
            return;
        }

        if (who is not Player { IsAlive: true } || Me.Combat.IsInCombat || System is not { } system) return;
        float dx = Me.X - who.X, dy = Me.Y - who.Y, dz = Me.Z - who.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > WakeRange * WakeRange || !system.IsInLineOfSight(Me, who)) return;
        Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
        AttackStart(who);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
        if (Me.MovementType == CreatureMovementType.Idle || Me.Home.Z < ResetZ) CanCastVolley = true;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (HealthBelowPct(30) && !(System?.HasAura(Me, Stoneskin) ?? false) && Cast(Stoneskin, Me))
            System?.SayText(Me, 10755);
        if (!CanCastVolley) return;
        if (_volleyMs < diffMs)
        {
            if (Cast(AcidVolley, Me)) _volleyMs = 8000;
        }
        else _volleyMs -= diffMs;
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_heigan.cpp npc_diseased_maggotAI (Diseased Maggot 16056, Rotting Maggot 16057): every 3 s in combat, a
/// maggot within 45 yd of the World Trigger (15384) in the middle of Heigan's room evades.
/// </summary>
public sealed class DiseasedMaggotAI : RaidBossAI
{
    public const uint Diseased = 16056, Rotting = 16057, WorldTrigger = 15384;
    public const uint CheckMs = 3000;
    public const float TriggerRange = 45f;

    private uint _checkMs = CheckMs;

    public DiseasedMaggotAI(Creature creature) : base(creature, null) { }

    protected override void ResetActions()
    {
        base.ResetActions();
        _checkMs = CheckMs;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (_checkMs > diffMs)
        {
            _checkMs -= diffMs;
            return;
        }

        _checkMs = CheckMs;
        if (System?.CreaturesOfEntryInRange(Me, WorldTrigger, TriggerRange).Count > 0) EnterEvadeMode();
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_kelthuzad.cpp npc_icecrown_guardianAI (16441). Reset puts Guardian Passive (29897) on if missing; evading
/// drops Blood Tap (28470) and reaching home despawns it. A kill casts Blood Tap at the victim; every 2 s it remembers its victim and
/// casts Blood Tap when the victim changed.
/// </summary>
public sealed class IcecrownGuardianAI : RaidBossAI
{
    public const uint Entry = 16441, GuardianPassive = 29897, BloodTap = 28470;
    public const uint CheckMs = 2000;

    private ObjectGuid _victim;
    private uint _checkMs = CheckMs;

    public IcecrownGuardianAI(Creature creature) : base(creature, null) { }

    protected override void ResetActions()
    {
        base.ResetActions();
        _victim = default;
        _checkMs = CheckMs;
        if (!(System?.HasAura(Me, GuardianPassive) ?? false)) Cast(GuardianPassive, Me);
    }

    public override void OnEvade()
    {
        System?.RemoveAuras(Me, BloodTap);
        base.OnEvade();
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        System?.ForcedDespawn(Me, 0);
    }

    public override void OnKilledUnit(Unit victim) => Cast(BloodTap, Victim);

    protected override void UpdateCombat(uint diffMs)
    {
        if (_checkMs >= diffMs)
        {
            _checkMs -= diffMs;
            return;
        }

        _checkMs = CheckMs;
        if (Victim is not { } victim) return;
        if (_victim.IsEmpty) _victim = victim.Guid;
        if (_victim != victim.Guid)
        {
            Cast(BloodTap, victim);
            _victim = victim.Guid;
        }
    }
}

/// <summary>
/// mangos-classic naxxramas.cpp DoHandleAreaTrigger (AREATRIGGER_FAERLINA_INTRO 4115) and the aIntroDialogue / JustDidDialogueStep
/// follower steps: while Faerlina is NOT_STARTED and the intro has not run, she says SAY_FAERLINA_INTRO (broadcast_text 12852); 10 s later
/// her out-of-combat Cultists (15980) and Acolytes (15981) stand, 3 s later they channel Dark Channeling (21157), 30 s later they stop and
/// kneel. The intro is marked done at the stand step, as in the reference.
/// </summary>
public sealed partial class NaxxramasInstance
{
    public const uint AreaTriggerFaerlinaIntro = 4115, Cultist = 15980, Acolyte = 15981, DarkChanneling = 21157;

    private readonly List<ObjectGuid> _faerlinaFollowers = [];
    private int _faerlinaStep = -1;
    private uint _faerlinaMs;

    public bool IsFaerlinaIntroDone { get; private set; }
    public int FaerlinaIntroStep => _faerlinaStep;

    private void OnFaerlinaFollowerCreated(Creature creature)
    {
        if (creature.Entry is Cultist or Acolyte) _faerlinaFollowers.Add(creature.Guid);
    }

    private bool HandleFaerlinaIntroTrigger(uint triggerId)
    {
        if (triggerId != AreaTriggerFaerlinaIntro) return false;
        if (GetData(Faerlina) != EncounterState.NotStarted || IsFaerlinaIntroDone || _faerlinaStep >= 0) return true;
        if (GetSingleCreatureFromStorage(15953) is { } faerlina) faerlina.System?.SayText(faerlina, 12852);
        _faerlinaStep = 1;
        _faerlinaMs = 10000;
        return true;
    }

    private void UpdateFaerlinaIntro(uint diffMs)
    {
        if (_faerlinaStep < 0) return;
        if (_faerlinaMs > diffMs)
        {
            _faerlinaMs -= diffMs;
            return;
        }

        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        foreach (ObjectGuid guid in _faerlinaFollowers)
        {
            if (system?.FindCreature(guid) is not { IsAlive: true } follower || follower.Combat.IsInCombat) continue;
            switch (_faerlinaStep)
            {
                case 1: follower.StandState = StandState.Stand; break;
                case 2: system.CastSpell(follower, DarkChanneling, follower, triggered: true); break;
                case 3:
                    system.RemoveAuras(follower, DarkChanneling);
                    follower.StandState = StandState.Kneel;
                    break;
            }
        }

        if (_faerlinaStep == 1) IsFaerlinaIntroDone = true;
        (_faerlinaStep, _faerlinaMs) = _faerlinaStep switch { 1 => (2, 3000u), 2 => (3, 30000u), _ => (-1, 0u) };
    }
}
