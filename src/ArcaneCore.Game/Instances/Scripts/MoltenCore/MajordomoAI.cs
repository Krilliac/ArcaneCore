using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic boss_majordomo_executus.cpp boss_majordomoAI: ExecuteAction, SummonedCreatureJustDied,
/// JustReachedHome, HandleOutro, StartSummonEvent. Broadcast IDs from vmangos' same script.</summary>
public sealed partial class MajordomoAI(Creature creature, MoltenCoreInstance instance) : RaidCreatureAI(creature, instance, 8)
{
    private readonly HashSet<ObjectGuid> _adds = [];
    private readonly HashSet<ObjectGuid> _dead = [];
    private uint _speechTimer;
    private uint _stage;
    private Creature? _ragnaros;
    public bool Defeated { get; private set; }
    public bool Summoning => _stage >= 10;

    protected override void Reset()
    {
        base.Reset();
        Me.InvincibilityHpThreshold = 1; // ScriptDev CombatAI::SetDeathPrevention(true)
        Defeated = Raid.GetData(8) == EncounterState.Done;
        _stage = 0;
        _dead.Clear();
        if (Defeated) { MakeFriendly(); return; }
        Schedule(15000, 15000, 30000, 30000, () => Cast(Random(0, 1) == 0 ? 20619u : 21075u));
        Schedule(15000, 15000, 25000, 30000, () => Teleport(20534, Victim));
        Schedule(30000, 30000, 25000, 30000, () => Teleport(20618, RandomTarget()));
        Schedule(5000, 5000, 25000, 30000, () => !(System?.HasAura(Me, 20620) ?? false) && Cast(20620, triggered: true));
    }
    private bool Teleport(uint spell, Unit? target)
    {
        if (target is null || !Cast(spell, target)) return false;
        Cast(20538);
        return true;
    }
    public override void OnAggro(Unit target)
    {
        if (Defeated) return;
        base.OnAggro(target);
        Say(7612);
        Cast(21094, triggered: true); Cast(20620, triggered: true);
        foreach (ObjectGuid guid in _adds)
            if (System?.FindCreature(guid) is { IsAlive: true } add)
            {
                add.AI?.AttackStart(target);
            }
    }
    public override void OnKilledUnit(Unit victim) => Say(9425);
    public override bool AttackStart(Unit target) => !Defeated && base.AttackStart(target);
    public override void OnDeath(Unit? killer) { } // death in the introduction is not a new completion
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry is 11663 or 11664) _adds.Add(summoned.Guid);
        if (summoned.Template.Entry == 11502)
        {
            _ragnaros = summoned;
            (summoned.AI as RagnarosAI)?.BeginIntroduction();
            instance.SetLavaPresentation(false);
            summoned.UnitFlags |= UnitFlags.NonAttackable2;
            System?.CastSpell(summoned, 20568, summoned, false);
        }
    }
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (!_adds.Contains(summoned.Guid) || !_dead.Add(summoned.Guid) || Defeated) return;
        // Never complete a partially populated encounter: all eight templates must have spawned.
        if (_dead.Count == 8)
        {
            Defeated = true;
            SetMeleeEnabled(false); CombatMovement = false;
            System?.InterruptCast(Me);
            Me.Map?.Combat.CombatStop(Me);
            Me.Motion.MovePoint(100, Me.Home.X, Me.Home.Y, Me.Home.Z, run: true);
            return;
        }
        if (_dead.Count == 7) { Say(8545); Cast(21090); }
        if (_dead.Count >= 4) Cast(21087);
        Cast(21086);
    }
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type == MovementGeneratorType.Point && pointId == 100 && Defeated)
        {
            MakeFriendly();
            Raid.SetData(8, EncounterState.Done);
            _stage = 1;
            _speechTimer = 0;
        }
    }
    public void MakeFriendly()
    {
        if (System?.AiServices.Spells is ICreatureAuraReset reset) reset.ResetAuras(Me, keepPositive: false);
        Defeated = true;
        SetMeleeEnabled(false); CombatMovement = false;
        Me.FactionTemplate = 1080;
        Me.UnitFlags |= UnitFlags.NonAttackable2;
        Me.SetUInt32(UpdateFields.UnitNpcFlags, 1); // UNIT_NPC_FLAG_GOSSIP
    }
    public override void OnEvade()
    {
        if (Defeated) return;
        foreach (ObjectGuid guid in _adds.ToArray())
            if (System?.FindCreature(guid) is { } add) System.Despawn(add);
        _adds.Clear();
        base.OnEvade();
        instance.SpawnGuards(Me);
    }
    /// <summary>Final validated gossip selection. Idempotent while the introduction or an existing Ragnaros is present.</summary>
    public bool StartSummonEvent(Player player)
    {
        if (!Defeated || _stage != 0 || Raid.GetData(9) is EncounterState.Done or EncounterState.InProgress
            || !ReferenceEquals(player.Map, Me.Map) || !player.IsAlive || DistanceSquared(player, Me) > 100
            || System?.Creatures.Any(c => c.Template.Entry == 11502) == true) return false;
        Me.SetUInt32(UpdateFields.UnitNpcFlags, 0);
        System?.SayText(Me, 7649, player);
        _stage = 10;
        _speechTimer = 5000;
        return true;
    }
    public override void OnUpdate(uint diffMs)
    {
        if (_stage != 0 && Due(ref _speechTimer, diffMs)) AdvanceSpeech();
        if (!Defeated) base.OnUpdate(diffMs);
    }
    private void AdvanceSpeech()
    {
        switch (_stage)
        {
            case 1: Say(7561); _speechTimer = 7500; break;
            case 2: Say(7567); _speechTimer = 8000; break;
            case 3: Say(7568); _speechTimer = 21500; break;
            case 4:
                if (!Cast(19484)) return;
                System?.NearTeleport(Me, 848.933f, -812.875f, -229.601f, 4.046f);
                System?.SetHomePosition(Me, 848.933f, -812.875f, -229.601f, 4.046f);
                _speechTimer = 900;
                break;
            case 5:
                foreach (ObjectGuid guid in _adds)
                    if (System?.FindCreature(guid) is { } add) System.Despawn(add);
                _adds.Clear(); _stage = 0; return;
            case 10: Say(7655); instance.SetLavaPresentation(true); _speechTimer = 1000; break;
            case 11:
                if (!Cast(19774)) return;
                Me.Motion.MovePoint(1, 830.9636f, -814.7055f, -228.9733f, run: true);
                _speechTimer = 11500; break;
            case 12: Say(7657); _speechTimer = 8000; break;
            case 13:
                _ragnaros = System?.SummonAt(Me, 11502, 838.3082f, -831.4665f, -232.1853f, 2.199115f, null, 7200000);
                if (_ragnaros is null) { _stage = 0; MakeFriendly(); return; }
                _speechTimer = 8700; break;
            case 14: if (_ragnaros is { } r1) System?.SayText(r1, 7636); _speechTimer = 11700; break;
            case 15: Say(7661); _speechTimer = 8700; break;
            case 16: if (_ragnaros is { } r2) System?.SayText(r2, 7662); _speechTimer = 16500; break;
            case 17:
                Me.InvincibilityHpThreshold = 0;
                Me.UnitFlags &= ~UnitFlags.NonAttackable2;
                if (_ragnaros is { } r3) System?.CastSpell(r3, 19773, Me, false);
                _stage = 0; return;
        }
        ++_stage;
    }
}
