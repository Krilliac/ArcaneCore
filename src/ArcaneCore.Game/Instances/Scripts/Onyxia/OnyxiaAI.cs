using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.Onyxia;

/// <summary>mangos-classic kalimdor/onyxias_lair/boss_onyxia.cpp boss_onyxiaAI: ExecuteAction, MovementInform,
/// PhaseTransition, HandlePhaseTransition, SummonWhelps, SpellHit. Movement completion, not elapsed travel time, advances the liftoff
/// and landing phases. In flight nothing waits for a breath or a move to finish: as in the reference, the fireball and movement actions and
/// the 40% landing only wait while a spell is being cast (CombatAI's IsNonMeleeSpellCasted gate), so a lost breath or a replaced move
/// cannot hold Onyxia in the air.</summary>
public sealed class OnyxiaAI(Creature creature, OnyxiaInstance instance) : RaidCreatureAI(creature, instance, 0)
{
    public enum OnyxiaPhase { Ground, ToLiftoff, LiftingOff, FlyingNorth, Flight, Landing, LandingDelay, Final }
    public OnyxiaPhase Phase { get; private set; }
    public int FlightPoint { get; private set; }
    public readonly record struct FlightLocation(uint Breath, float X, float Y, float Z);
    public static IReadOnlyList<FlightLocation> FlightPath { get; } = Array.AsReadOnly<FlightLocation>([
        new(17086,24.16332f,-216.0808f,-58.98009f),new(18617,10.2191f,-247.912f,-60.896f),
        new(18576,-15.00505f,-244.4841f,-60.40087f),new(18564,-63.5156f,-240.096f,-60.477f),
        new(18351,-66.3589f,-215.928f,-64.23904f),new(18596,-58.2509f,-189.020f,-60.790f),
        new(18609,-16.70134f,-181.4501f,-61.98513f),new(18584,12.26687f,-181.1084f,-60.23914f)]);
    private uint _transition, _movement, _whelps;
    private int _waveCount, _waveSize;
    private bool _firstWave;
    private bool _lured;
    private readonly HashSet<ObjectGuid> _summons = [];
    protected override void Reset()
    {
        base.Reset();
        Phase = OnyxiaPhase.Ground; FlightPoint = 0;
        Me.StandState = StandState.Sleep;
        _transition = _movement = _whelps = 0;
        _waveCount = 0; _waveSize = 20; _firstWave = true;
        _lured = false;
        SetMeleeEnabled(true); CombatMovement = true;
        Me.RemoveMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
        System?.RemoveAuras(Me, 18430); System?.RemoveAuras(Me, 19951);
        foreach (ObjectGuid guid in _summons.ToArray())
            if (System?.FindCreature(guid) is { } summon) System.Despawn(summon);
        _summons.Clear();
        GroundActions(false);
    }
    private Unit? PlayerTarget() => RandomTarget(u => u is Player);
    private void GroundActions(bool final)
    {
        ClearActions();
        Spell(18435, 10000, 20000, 10000, 20000, () => Victim);
        Spell(19983, 2000, 5000, 5000, 10000, () => Victim);
        Spell(15847, 15000, 20000, 15000, 20000);
        if (!final) Spell(18500, 10000, 20000, 15000, 30000);
        Spell(19633, final ? 10000u : 20000u, final ? 20000u : 30000u, 25000, 40000, () => Victim);
        if (final) Spell(18431, 0, 0, 15000, 45000);
        Schedule(3000, 3000, 3000, 3000, () =>
        {
            Creature? trigger = System?.Creatures.FirstOrDefault(c => c.Template.Entry == 12758);
            if (trigger is not null && DistanceSquared(Me, trigger) > 8100)
            {
                if (!_lured) Say(8570);
                _lured = true;
                Cast(21131);
            }
            else _lured = false;
            return true;
        });
    }
    public override void OnAggro(Unit target) { base.OnAggro(target); Me.StandState = StandState.Stand; Say(8286); }
    public override void OnKilledUnit(Unit victim) => Say(8287);
    public override void OnEvade()
    {
        base.OnEvade();
        // Here SPECIAL means liftoff, not a completed/doused rune.
        Raid.SetData(0, EncounterState.Fail);
    }
    public override bool AttackStart(Unit target) => Phase is OnyxiaPhase.Ground or OnyxiaPhase.Final && base.AttackStart(target);
    public override void OnJustSummoned(Creature summoned)
    {
        _summons.Add(summoned.Guid);
        if (summoned.Template.Entry == 11262) ++_waveCount;
    }
    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point) return;
        if (pointId == 9 && Phase == OnyxiaPhase.ToLiftoff)
        {
            Me.AddMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
            Me.Map?.BroadcastToObservers(Me, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(254, Me.Guid));
            Phase = OnyxiaPhase.LiftingOff; _transition = 3500;
        }
        else if (pointId == 10 && Phase == OnyxiaPhase.FlyingNorth)
        {
            Phase = OnyxiaPhase.Flight; _movement = 25000;
            Cast(18430, triggered: true); Cast(19951, triggered: true);
            ClearActions(); Spell(18392, 0, 0, 3000, 5000, PlayerTarget);
        }
        else if (pointId == 11 && Phase == OnyxiaPhase.Landing)
        {
            Me.RemoveMovementFlags(MovementFlags.Flying | MovementFlags.Hover);
            Me.Map?.BroadcastToObservers(Me, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(293, Me.Guid));
            System?.RemoveAuras(Me, 19951);
            Phase = OnyxiaPhase.LandingDelay; _transition = 2000;
        }
        else if (pointId < 8 && Phase == OnyxiaPhase.Flight) Cast(18430, triggered: true);
    }
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (Phase != OnyxiaPhase.Flight || !FlightPath.Any(p => p.Breath == spell.Id)) return;
        MoveTo(FlightPoint, (uint)FlightPoint);
        Cast(22191, triggered: true);
    }
    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 18392) Me.Combat.Threat.ModifyThreatPercent(target, -100);
    }
    private void MoveTo(int point, uint id)
    {
        var p = FlightPath[point];
        Me.Motion.MovePoint(id, p.X, p.Y, p.Z, run: true);
    }
    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (Phase == OnyxiaPhase.Ground && Below(65))
        {
            Say(8288); Phase = OnyxiaPhase.ToLiftoff;
            CombatMovement = false; SetMeleeEnabled(false);
            System?.InterruptCast(Me); System?.MoveIdle(Me);
            Me.Motion.MovePoint(9, FlightPath[4].X, FlightPath[4].Y, -84.25523f, run: true);
            return;
        }
        // ONYXIA_PHASE_3_TRANSITION: at or below 40% as soon as no spell is being cast; the landing move replaces any flight move.
        if (Phase == OnyxiaPhase.Flight && Below(40) && !IsCasting)
        {
            Say(8290); Phase = OnyxiaPhase.Landing;
            System?.RemoveAuras(Me, 18430);
            Me.Motion.MovePoint(11, -1.060547f, -229.9293f, -86.14094f, run: true);
            return;
        }
        if (Phase == OnyxiaPhase.LiftingOff && Due(ref _transition, diffMs))
        {
            Phase = OnyxiaPhase.FlyingNorth; Raid.SetData(0, EncounterState.Special);
            _whelps = 3000; MoveTo(0, 10); return;
        }
        if (Phase == OnyxiaPhase.LandingDelay && Due(ref _transition, diffMs))
        {
            Phase = OnyxiaPhase.Final; CombatMovement = true; SetMeleeEnabled(true);
            if (Victim is { } victim) Me.Motion.MoveChase(victim);
            GroundActions(true); return;
        }
        if (Phase is OnyxiaPhase.FlyingNorth or OnyxiaPhase.Flight)
        {
            if (Due(ref _whelps, diffMs)) SummonWave();
        }
        if (Phase == OnyxiaPhase.Flight)
        {
            // ONYXIA_MOVEMENT: retried each update while a spell is being cast, otherwise not gated on the previous move.
            if (Due(ref _movement, diffMs) && !IsCasting)
            {
                uint choice = Random(0, 2);
                if (choice == 0)
                {
                    // SpellHit may fire synchronously: publish the destination before casting.
                    uint spell = FlightPath[FlightPoint].Breath;
                    int old = FlightPoint; FlightPoint = (FlightPoint + 4) % 8;
                    Say(7213);
                    if (!Cast(spell)) { FlightPoint = old; return; }
                    _movement = 25000;
                }
                else
                {
                    FlightPoint = (FlightPoint + (choice == 1 ? 7 : 1)) % 8;
                    System?.RemoveAuras(Me, 18430); MoveTo(FlightPoint, (uint)FlightPoint);
                    _movement = Random(15000, 25000);
                }
                return;
            }
        }
        if (Phase is OnyxiaPhase.Ground or OnyxiaPhase.Final or OnyxiaPhase.Flight) TickActions(diffMs);
    }
    private void SummonWave()
    {
        if (_waveCount >= _waveSize)
        {
            _whelps = (uint)(90000 - _waveSize * (_firstWave ? 1750 : 3000));
            _firstWave = false; _waveSize = (int)Random(4, 10); _waveCount = 0; return;
        }
        System?.SummonAt(Me, 11262, -30.127f, -254.463f, -89.440f, 0, Victim, 3000);
        System?.SummonAt(Me, 11262, -30.817f, -177.106f, -89.258f, 0, Victim, 3000);
        _whelps = _firstWave ? Random(1000, 2500) : Random(2000, 4000);
    }
}
