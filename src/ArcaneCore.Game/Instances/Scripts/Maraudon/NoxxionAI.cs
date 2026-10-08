using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>ScriptDev2 boss_noxxionAI (mangos-classic kalimdor/maraudon/boss_noxxion.cpp:36-93).</summary>
public sealed class NoxxionAI(Creature creature) : CreatureAI(creature)
{
    private uint _volleyMs = 7000;
    private uint _uppercutMs = 16000;
    private uint _summonMs = 19000;
    private bool _spawnAuraActive;

    public override bool AggroesOnSight => true;

    public override void OnRespawn()
    {
        _volleyMs = 7000;
        _uppercutMs = 16000;
        _summonMs = 19000;
        _spawnAuraActive = false;
    }

    /// <summary>SummonNoxxionsSpawns::OnApply's remove branch (boss_noxxion.cpp): the aura goes with his death, and so does the
    /// uninteractible flag - the polled aura state is not looked at again once he is dead, so it is cleared here.</summary>
    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        if (_spawnAuraActive)
        {
            _spawnAuraActive = false;
            Me.UnitFlags &= ~UnitFlags.NotSelectable;
        }
    }

    public override void OnJustSummoned(Creature summoned)
    {
        // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0): any living unit on the threat list.
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive)];
        if (targets.Length > 0)
        {
            summoned.AI?.AttackStart(targets[System?.RandomInt(0, targets.Length - 1) ?? 0]);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        // boss_noxxion.cpp SummonNoxxionsSpawns::OnApply: the aura summons entry 13456 through spell 21707,
        // and makes Noxxion uninteractible until it falls. Watch the actual aura state, not a guessed duration.
        bool spawnAura = System?.HasAura(Me, 21708) == true;
        if (spawnAura != _spawnAuraActive)
        {
            _spawnAuraActive = spawnAura;
            if (spawnAura)
            {
                DoCast(null, 21707, triggered: true);
                Me.UnitFlags |= UnitFlags.NotSelectable;
            }
            else Me.UnitFlags &= ~UnitFlags.NotSelectable;
        }
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        Tick(ref _volleyMs, diffMs, 9000, () => DoCast(Me, 21687));
        Tick(ref _uppercutMs, diffMs, 12000, () => DoCast(victim, 22916));
        Tick(ref _summonMs, diffMs, 40000, () => DoCast(Me, 21708));
    }

    private static void Tick(ref uint remaining, uint diffMs, uint resetMs, Func<CreatureCastResult> cast)
    {
        if (remaining < diffMs)
        {
            if (cast() == CreatureCastResult.Ok)
            {
                remaining = resetMs;
            }
        }
        else
        {
            remaining -= diffMs;
        }
    }
}
