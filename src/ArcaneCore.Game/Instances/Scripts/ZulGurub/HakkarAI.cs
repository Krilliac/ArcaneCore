using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>
/// vmangos scripts/eastern_kingdoms/stranglethorn_vale/zulgurub/boss_hakkar.cpp:
/// Reset/UpdateAI timers, priest aspects, platform leash and Cause Insanity threat restoration.
/// mangos-classic .../zulgurub/boss_hakkar.cpp Reset/InitiateHakkarPowerStacks supplies Double Attack and
/// one power stack per living priest. The existing SD2 eight-slot save format has no Hakkar slot.
/// </summary>
public sealed class HakkarAI : RaidBossAI
{
    private Unit? _insaneTarget;
    private float _insaneThreat;
    private uint _insanityCheck;

    public HakkarAI(Creature creature) : base(creature, null)
    {
        AddAction(90000, () => Cast(24324), () => 90000);
        AddAction(15000, CorruptedBlood, () => RandomDelay(14000, 16000));
        AddAction(17000, CauseInsanity, () => RandomDelay(20000, 25000));
        AddAction(600000, () => System?.HasAura(Me, 27680) == true || Cast(27680), () => 2000);
        AddAction(4000, () => Aspect(0, 24687, Victim), () => RandomDelay(10000, 14000));
        AddAction(7000, () => Aspect(1, 24688, Victim), () => 8000);
        AddAction(12000, () => Aspect(2, 24686, Victim), () => 10000);
        AddAction(8000, () => Aspect(3, 24689, Me), () => 15000);
        AddAction(18000, () => Aspect(4, 24690, Me), () => RandomDelay(10000, 15000));
    }

    public override void OnAggro(Unit target) => System?.SayText(Me, 10447);

    public override void OnDeath(Unit? killer)
    {
        // SD2 zulgurub.h has only priests/Ohgan/Lor'khan/Zath; raid binds are credited by InstanceManager.
    }

    public override void OnReachedHome() => InitializePower();

    public override void OnRespawn()
    {
        base.OnRespawn();
        InitializePower();
    }

    private void InitializePower()
    {
        if (System is not { } system)
        {
            return;
        }

        if (!system.HasAura(Me, 19818))
        {
            Cast(19818, Me, triggered: true);
        }

        system.RemoveAuras(Me, 24692);
        for (uint priest = 0; priest < 5; priest++)
        {
            if (Instance?.GetData(priest) != EncounterState.Done)
            {
                // Initialization uses triggered casts so all stacks are applied in one world turn.
                Cast(24692, Me, triggered: true);
            }
        }
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _insaneTarget = null;
        _insaneThreat = 0;
        _insanityCheck = 0;
    }

    public override void OnUpdate(uint diffMs)
    {
        _insanityCheck = _insanityCheck > diffMs ? _insanityCheck - diffMs : 0;
        if (_insanityCheck == 0 && _insaneTarget is { } target)
        {
            if (!target.IsAlive || !target.IsInWorld || !ReferenceEquals(target.Map, Me.Map))
            {
                _insaneTarget = null;
            }
            else if (System?.HasAura(target, 24327) != true)
            {
                Me.Combat.Threat.ModifyThreatPercent(target, -100);
                Me.Combat.Threat.AddThreat(target, _insaneThreat);
                _insaneTarget = null;
            }
        }

        base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Me.Z is > 57.28f or < 45.8f)
        {
            EnterEvadeMode();
            return;
        }

        if (System?.AiServices.Spells?.IsCasting(Me) != true)
        {
            base.UpdateCombat(diffMs);
        }
    }

    private bool Aspect(uint priest, uint spell, Unit? target)
        => Instance?.GetData(priest) == EncounterState.Done || Cast(spell, target);

    private bool CorruptedBlood()
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target).Where(t => t.IsAlive && t.IsInWorld && t.Map == Me.Map)];
        return targets.Length > 0 && Cast(24328, targets[System!.RandomInt(0, targets.Length - 1)]);
    }

    private bool CauseInsanity()
    {
        if (Victim is not { } target)
        {
            return false;
        }

        float threat = Me.Combat.Threat.GetThreat(target);
        if (!Cast(24327, target))
        {
            return false;
        }

        _insaneTarget = target;
        _insaneThreat = threat;
        _insanityCheck = 4000;
        return true;
    }
}
