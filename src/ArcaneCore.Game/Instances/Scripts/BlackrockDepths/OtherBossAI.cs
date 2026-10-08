using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic blackrock_depths/boss_general_angerforge.cpp: boss_general_angerforgeAI.</summary>
public sealed class GeneralAngerforgeAI(Creature creature) : CreatureAI(creature)
{
    private uint _sunderMs, _alarmMs;
    public override bool AggroesOnSight => true;

    public override void OnRespawn()
    {
        _sunderMs = (uint)(System?.RandomInt(5000, 10_000) ?? 5000);
        _alarmMs = 0;
    }

    public override void OnAggro(Unit target)
    {
        if (System?.HasAura(Me, 15088) != true) DoCast(Me, 15088, triggered: true);
        if (System?.HasAura(Me, 15097) != true) DoCast(Me, 15097, triggered: true);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (Victim is { } victim) summoned.AI?.AttackStart(victim);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim) return;
        if (_sunderMs < diffMs)
        {
            if (DoCast(victim, 15572) == CreatureCastResult.Ok)
                _sunderMs = (uint)(System?.RandomInt(5000, 15_000) ?? 5000);
        }
        else _sunderMs -= diffMs;
        if (Me.Health * 100UL < Me.MaxHealth * 30UL)
        {
            if (_alarmMs < diffMs)
            {
                System?.SayText(Me, -1230035);
                for (int i = 0; i < 8; i++) SummonAdd(8901);
                for (int i = 0; i < 2; i++) SummonAdd(8894);
                _alarmMs = 180_000;
            }
            else _alarmMs -= diffMs;
        }
    }

    private void SummonAdd(uint entry)
    {
        float x = 717.343f + (System?.RandomInt(-100, 100) ?? 0) / 100f;
        float y = 22.116f + (System?.RandomInt(-100, 100) ?? 0) / 100f;
        System?.SummonAt(Me, entry, x, y, -45.4321f, 3.1415f, null, 30_000);
    }
}

/// <summary>mangos-classic blackrock_depths/boss_high_interrogator_gerstahn.cpp: boss_high_interrogator_gerstahnAI.</summary>
public sealed class HighInterrogatorGerstahnAI(Creature creature) : CreatureAI(creature)
{
    private uint _painMs, _burnMs, _screamMs, _shieldMs;
    public override bool AggroesOnSight => true;

    public override void OnRespawn() { _painMs = 4000; _burnMs = 14_000; _screamMs = 32_000; _shieldMs = 8000; }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim) return;
        Tick(ref _painMs, diffMs, 7000, () => DoCast(RandomTarget() ?? victim, 14032));
        Tick(ref _burnMs, diffMs, 10_000, () =>
        {
            Unit? mana = RandomTarget(onlyMana: true);
            return mana is null ? CreatureCastResult.Failed : DoCast(mana, 14033);
        });
        Tick(ref _screamMs, diffMs, 30_000, () => DoCast(Me, 13704));
        Tick(ref _shieldMs, diffMs, 25_000, () => DoCast(Me, 12040));
    }

    private Unit? RandomTarget(bool onlyMana = false)
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
