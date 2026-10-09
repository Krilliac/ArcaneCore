using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_razorgore.cpp boss_razorgoreAI:
/// Reset, ReceiveAIEvent, JustPreventedDeath and ExecuteAction. The instance
/// tracks eggs/orb/defenders; combat actions start only after the last egg.
/// vmangos equivalent SpellHitTarget reduces War Stomp threat by 30%.
/// </summary>
public sealed class RazorgoreAI : RaidBossAI
{
    public RazorgoreAI(Creature creature) : base(creature, 0)
    {
        Me.InvincibilityHpThreshold = 1;
        AddAction(10000, 15000, () => Cast(23023), () => RandomDelay(15000, 25000));
        AddAction(15000, 20000, () => Cast(22425), () => RandomDelay(15000, 20000));
        AddAction(30000, () => Cast(24375), () => 30000);
        AddAction(4000, 8000, () => Cast(19632, Victim), () => RandomDelay(4000, 8000));
        Cast(18943, triggered: true);
    }

    private bool PhaseTwo => Instance?.GetData(0) == EncounterState.Special;

    public override void OnRespawn()
    {
        base.OnRespawn();
        Me.InvincibilityHpThreshold = 1;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!PhaseTwo && Me.Health <= 1 && Me.Combat.IsInCombat)
        {
            Instance?.SetData(0, EncounterState.Fail);
            EnterEvadeMode();
            return;
        }
        base.OnUpdate(diffMs);
    }

    public override void OnAggro(Unit target)
    {
        if (!PhaseTwo) base.OnAggro(target);
        System?.RemoveAuras(Me, 23014);
    }

    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        if (eventType == 1 && PhaseTwo)
        {
            System?.RemoveAuras(Me, 23014);
            Cast(23040, triggered: true);
            Me.InvincibilityHpThreshold = 0;
            MeleeEnabled = true;
        }
    }

    public override void OnDeath(Unit? killer)
    {
        Instance?.SetData(0, PhaseTwo ? EncounterState.Done : EncounterState.Fail);
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 24375) Me.Combat.Threat.ModifyThreatPercent(target, -30);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (PhaseTwo) base.UpdateCombat(diffMs);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        Me.InvincibilityHpThreshold = 1;
    }
}
