using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic boss_ragnaros.cpp boss_ragnarosAI::{Reset,HandlePhaseTransition,ExecuteAction,
/// JustSummoned,SummonedCreatureJustDied,SpellHitTarget,HandleEnterCombat}.</summary>
public sealed class RagnarosAI(Creature creature, MoltenCoreInstance instance) : RaidCreatureAI(creature, instance, 9)
{
    public enum RagnarosPhase { Emerged, Submerging, Submerged, Emerging }
    public RagnarosPhase Phase { get; private set; }
    private uint _phaseTimer;
    private uint _magma;
    private int _rangeCheck;
    private uint _intro;
    private bool _introYelled, _submergedOnce;
    private bool _introPending;
    public override bool AggroesOnSight => !_introPending;
    public override bool AttackStart(Unit target) => !_introPending && base.AttackStart(target);
    internal void BeginIntroduction()
    {
        _introPending = true;
        _introYelled = false;
        Me.UnitFlags |= UnitFlags.NonAttackable2;
    }
    private readonly HashSet<ObjectGuid> _sons = [];
    private readonly HashSet<ObjectGuid> _summons = [];
    public int LivingSons => _sons.Count;
    protected override void Reset()
    {
        base.Reset();
        CombatMovement = false;
        System?.SetAiImmobilized(Me, true, combatOnly: false);
        SetMeleeEnabled(true);
        Phase = RagnarosPhase.Emerged;
        _phaseTimer = 180000;
        _intro = 0; _submergedOnce = false;
        _rangeCheck = -1;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
        foreach (uint spell in new uint[] { 21107, 21859, 20567 }) System?.RemoveAuras(Me, spell);
        foreach (ObjectGuid guid in _summons.ToArray())
            if (System?.FindCreature(guid) is { } summon) System.Despawn(summon);
        _summons.Clear(); _sons.Clear();
        Cast(21387, triggered: true); Cast(20563, triggered: true);
        GroundActions(false);
    }
    private void GroundActions(bool emerged)
    {
        ClearActions();
        Schedule(emerged ? 20000u : 30000u, emerged ? 20000u : 30000u, 25000, 25000, () => { if (!Cast(20566)) return false; Say(9427); return true; });
        Schedule(11000, emerged ? 30000u : 11000u, 11000, 30000, () =>
        {
            Unit? target = RandomTarget(u => u.PowerType == PowerType.Mana);
            if (target is null || !Cast(21154, target)) return false;
            Say(9426); return true;
        });
        Spell(21908, emerged ? 5000u : 20000u, emerged ? 25000u : 20000u, 5000, 25000);
        _magma = emerged ? 3000u : 2000u;
    }
    public override void OnJustSummoned(Creature summoned)
    {
        _summons.Add(summoned.Guid);
        if (summoned.Template.Entry == 12143)
        {
            _sons.Add(summoned.Guid);
            System?.CastSpell(summoned, 19818, summoned, true);
            System?.CastSpell(summoned, 21857, summoned, true);
            if (RandomTarget() is { } target) summoned.AI?.AttackStart(target);
        }
        else if (summoned.Template.Entry == 13148) System?.CastSpell(summoned, 21155, summoned, true);
    }
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (_sons.Remove(summoned.Guid) && _sons.Count == 0 && Phase == RagnarosPhase.Submerged)
            _phaseTimer = Math.Min(_phaseTimer, 1000);
    }
    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 20566) Me.Combat.Threat.ModifyThreatPercent(target, -100);
        if (spell.Id == 19773 && target is Creature c && c.Template.Entry == 12018) _intro = 10000;
    }
    public override void OnKilledUnit(Unit victim) => Say(7626);
    public override void OnUpdate(uint diffMs)
    {
        if (_intro > 0 && Due(ref _intro, diffMs))
        {
            if (!_introYelled) { Say(7685); _introYelled = true; _intro = 3000; }
            else
            {
                Me.UnitFlags &= ~UnitFlags.NonAttackable2;
                _introPending = false;
                if (System?.SelectNearestTarget(Me, 150) is { } target) AttackStart(target);
            }
        }
        if (_introPending) return;
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (Due(ref _phaseTimer, diffMs))
        {
            switch (Phase)
            {
                case RagnarosPhase.Emerged:
                    if (!Cast(21108)) return; // DBC trigger spells supply the eight summon positions; no invented coordinates
                    Cast(20567, triggered: true); Cast(21859, triggered: true);
                    Me.UnitFlags |= UnitFlags.NotSelectable;
                    SetMeleeEnabled(false);
                    Say(_submergedOnce ? 8573 : 8572); _submergedOnce = true;
                    Phase = RagnarosPhase.Submerging; _phaseTimer = 3000; break;
                case RagnarosPhase.Submerging:
                    Cast(21107, triggered: true); Phase = RagnarosPhase.Submerged; _phaseTimer = 90000; break;
                case RagnarosPhase.Submerged:
                    foreach (uint spell in new uint[] { 21107, 21859, 20567 }) System?.RemoveAuras(Me, spell);
                    Phase = RagnarosPhase.Emerging; _phaseTimer = 500; break;
                case RagnarosPhase.Emerging:
                    if (!Cast(20568)) return;
                    Me.UnitFlags &= ~UnitFlags.NotSelectable;
                    SetMeleeEnabled(true); Phase = RagnarosPhase.Emerged; _phaseTimer = 180000;
                    GroundActions(true); break;
            }
            return;
        }
        if (Phase == RagnarosPhase.Emerged)
        {
            TickActions(diffMs);
            if (Due(ref _magma, diffMs))
            {
                _magma = 500;
                if (Victim is { } victim && MapCombat.CanReachWithMeleeAutoAttack(Me, victim)) _rangeCheck = -1;
                else if (++_rangeCheck > 1 && RandomTarget(u => u is Player) is { } target && Cast(20565, target)) _magma = 2500;
            }
        }
    }
}
