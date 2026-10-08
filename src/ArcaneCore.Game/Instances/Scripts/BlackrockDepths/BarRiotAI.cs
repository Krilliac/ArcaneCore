using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic blackrock_depths/blackrock_depths.cpp boss_plugger_spazzringAI (Reset, Aggro, SpellHit, UpdateAI, WarnThief, AttackThief).</summary>
public sealed class PluggerAI(Creature creature) : CreatureAI(creature)
{
    private uint _oocSayMs = 10_000, _armorMs = 1000, _banishMs, _immolateMs, _boltMs, _curseMs, _pickpocketMs;
    public override bool AggroesOnSight => true;

    public override void OnRespawn()
    {
        _oocSayMs = 10_000; _armorMs = 1000;
        _banishMs = _immolateMs = _boltMs = _curseMs = _pickpocketMs = 0;
    }

    public override void OnAggro(Unit target)
    {
        _banishMs = (uint)(System?.RandomInt(8000, 12_000) ?? 8000);
        _immolateMs = (uint)(System?.RandomInt(18_000, 20_000) ?? 18_000);
        _boltMs = 1000; _curseMs = 17_000;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (caster is Player && spell.Id == 921) _pickpocketMs = 5000;
    }

    public void WarnThief(Player player)
        => System?.SayText(Me, -1230054 - (System?.RandomInt(0, 2) ?? 0), player);

    public void AttackThief(Player player)
    {
        System?.SayText(Me, -1230057 - (System?.RandomInt(0, 1) ?? 0), player);
        Me.FactionTemplate = 54;
        AttackStart(player);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (UpdateVictim() && Victim is { } victim)
        {
            // Banish a random attacker; Curse of Tongues a random mana user (SelectAttackingTarget RANDOM / SELECT_FLAG_POWER_MANA).
            Tick(ref _banishMs, diffMs, (uint)(System?.RandomInt(26_000, 28_000) ?? 26_000), () => DoCast(RandomAttacker() ?? victim, 8994));
            Tick(ref _immolateMs, diffMs, 25_000, () => DoCast(victim, 12742));
            Tick(ref _boltMs, diffMs, (uint)(System?.RandomInt(3600, 6300) ?? 3600), () => DoCast(victim, 12739));
            Tick(ref _curseMs, diffMs, (uint)(System?.RandomInt(19_000, 31_000) ?? 19_000),
                () => RandomAttacker(onlyMana: true) is { } caster ? DoCast(caster, 13338) : CreatureCastResult.Failed);
        }
        else
        {
            if (_oocSayMs < diffMs)
            {
                System?.SayText(Me, -1230050 - (System?.RandomInt(0, 3) ?? 0));
                _oocSayMs = (uint)(System?.RandomInt(10_000, 20_000) ?? 10_000);
            }
            else _oocSayMs -= diffMs;
            if (_pickpocketMs > 0)
            {
                if (_pickpocketMs < diffMs)
                {
                    System?.SayText(Me, -1230059);
                    Me.FactionTemplate = 54;
                    _pickpocketMs = 0;
                }
                else _pickpocketMs -= diffMs;
            }
            Tick(ref _armorMs, diffMs, 30 * 60_000, () => DoCast(Me, 13787));
        }
    }

    private Unit? RandomAttacker(bool onlyMana = false)
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(u => u.IsAlive && (!onlyMana || u.PowerType == PowerType.Mana))];
        return targets.Length == 0 ? null : targets[System?.RandomInt(0, targets.Length - 1) ?? 0];
    }

    private static void Tick(ref uint remaining, uint diffMs, uint resetMs, Func<CreatureCastResult> cast)
    {
        if (remaining < diffMs)
        {
            if (cast() == CreatureCastResult.Ok) remaining = resetMs;
        }
        else remaining -= diffMs;
    }
}

/// <summary>mangos-classic blackrock_depths/blackrock_depths.cpp npc_phalanxAI (Reset, WaypointReached, Aggro, UpdateEscortAI).</summary>
public sealed class PhalanxAI(Creature creature, BlackrockDepthsInstance instance) : EscortAI(creature)
{
    private uint _thunderMs, _blowMs, _volleyMs, _patrolMs;

    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting | EscortState.Paused))
        {
            SetCurrentWaypoint(1);
            SetEscortPaused(false);
        }
        _thunderMs = _blowMs = _volleyMs = _patrolMs = 0;
    }

    protected override void Aggro(Unit target) { _thunderMs = 12_000; _blowMs = 15_000; _volleyMs = 1; }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId == 1) System?.SayText(Me, -1230040);
        if (pointId != 2) return;
        SetEscortPaused(true);
        if (instance.GetData(BlackrockDepthsInstance.TypePlugger) is EncounterState.Done or EncounterState.InProgress)
        {
            Me.FactionTemplate = 122;
            if (instance.GetData(BlackrockDepthsInstance.TypePlugger) == EncounterState.InProgress)
            {
                _patrolMs = 10_000;
                instance.SetData(BlackrockDepthsInstance.TypePlugger, EncounterState.Done);
            }
        }
        else Me.FactionTemplate = 54;
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_patrolMs > 0)
        {
            if (_patrolMs < diffMs)
            {
                if (instance.GetData(BlackrockDepthsInstance.TypeBar) != EncounterState.Done)
                    instance.SetData(BlackrockDepthsInstance.TypeBar, EncounterState.InProgress);
                _patrolMs = 0;
            }
            else _patrolMs -= diffMs;
        }
        if (!UpdateVictim() || Victim is not { } victim) return;
        Tick(ref _thunderMs, diffMs, 10_000, () => DoCast(victim, 15588));
        Tick(ref _blowMs, diffMs, 10_000, () => DoCast(victim, 14099));
        if (Me.Health * 100UL < Me.MaxHealth * 51UL)
            Tick(ref _volleyMs, diffMs, 15_000, () => DoCast(Me, 15285));
    }

    private static void Tick(ref uint remaining, uint diffMs, uint resetMs, Func<CreatureCastResult> cast)
    {
        if (remaining < diffMs)
        {
            if (cast() == CreatureCastResult.Ok) remaining = resetMs;
        }
        else remaining -= diffMs;
    }
}
