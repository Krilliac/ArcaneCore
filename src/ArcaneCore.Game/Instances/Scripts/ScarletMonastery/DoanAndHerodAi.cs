using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.ScarletMonastery;

/// <summary>ScriptDev2 boss_arcanist_doanAI (mangos-classic scarlet_monastery/boss_arcanist_doan.cpp:
/// Reset, Aggro, UpdateAI).</summary>
public sealed class DoanAi(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 6487;
    private uint _polymorphMs, _silenceMs, _explosionMs, _detonationMs;
    private bool _shielded;

    /// <summary>Whether Arcane Bubble was cast this fight (bShielded).</summary>
    public bool HasShielded => _shielded;

    protected override void Reset()
    {
        _polymorphMs = 15_000;
        _silenceMs = 7500;
        _explosionMs = (uint)Random.Shared.Next(1000, 3001);
        _detonationMs = 0;
        _shielded = false;
    }
    public override void OnAggro(Unit target) => System?.SayText(Me, -1189019);

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        if (_detonationMs > 0)
        {
            if (_detonationMs > diffMs) _detonationMs -= diffMs;
            else if (DoCast(Me, 9435) == CreatureCastResult.Ok)
            {
                System?.SayText(Me, -1189020);
                _detonationMs = 0;
            }
        }
        if (System?.HasAura(Me, 9438) == true) return;
        if (!_shielded && Me.MaxHealth > 0 && Me.Health * 2 <= Me.MaxHealth && DoCast(Me, 9438) == CreatureCastResult.Ok)
        {
            _shielded = true;
            _detonationMs = 1000;
        }

        if (_polymorphMs >= diffMs) _polymorphMs -= diffMs;
        else
        {
            // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 1): a random threat-list entry below the top one.
            if (RandomThreatTargetBelowTop() is { } other && DoCast(other, 13323) == CreatureCastResult.Ok) _polymorphMs = 20_000;
        }
        if (_silenceMs >= diffMs) _silenceMs -= diffMs;
        else if (DoCast(Me, 8988) == CreatureCastResult.Ok) _silenceMs = (uint)Random.Shared.Next(15000, 22001);
        if (_explosionMs >= diffMs) _explosionMs -= diffMs;
        else if (DoCast(Me, 9433) == CreatureCastResult.Ok) _explosionMs = (uint)Random.Shared.Next(2500, 8501);
    }

    private Unit? RandomThreatTargetBelowTop()
    {
        if (!Me.Combat.HasThreatList) return null;
        IReadOnlyList<ThreatEntry> threat = Me.Combat.Threat.Entries; // highest threat first
        return threat.Count > 1 ? threat[Random.Shared.Next(1, threat.Count)].Target : null;
    }
}

/// <summary>ScriptDev2 boss_herodAI (mangos-classic scarlet_monastery/boss_herod.cpp:
/// Reset, Aggro, KilledUnit, JustDied, JustSummoned, UpdateAI).</summary>
public sealed class HerodAi(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 3975, Trainee = 6575;
    private uint _cleaveMs, _whirlwindMs;
    private bool _enraged, _traineeSaid;

    /// <summary>Whether Frenzy was cast this fight (m_bEnrage).</summary>
    public bool IsEnraged => _enraged;

    protected override void Reset()
    {
        _cleaveMs = 7500;
        _whirlwindMs = 14_500;
        _enraged = _traineeSaid = false;
    }
    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, -1189000);
        DoCast(Me, 8260);
    }
    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1189003);
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry == Trainee && !_traineeSaid)
        {
            _traineeSaid = true;
            System?.SayText(summoned, -1189035);
        }
    }
    public override void OnDeath(Unit? killer)
    {
        if (System is not { } system || system.Content.FindTemplate(Trainee) is not { } template) return;
        system.RegisterEntryAi(Trainee, c => new ScarletTraineeAi(c), rebuildExisting: false);
        for (int i = 0; i < 20; i++)
        {
            Creature trainee = system.SpawnTemporary(template, 1939.18f, -431.58f, 17.09f, 6.22f, Me);
            system.ForcedDespawn(trainee, 600_000);
        }
    }
    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        if (!_enraged && Me.MaxHealth > 0 && Me.Health * 10 <= Me.MaxHealth * 3 && DoCast(Me, 8269) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, -1000003);
            System?.SayText(Me, -1189002);
            _enraged = true;
        }
        if (_cleaveMs >= diffMs) _cleaveMs -= diffMs;
        else { DoCast(Victim, 15496); _cleaveMs = (uint)Random.Shared.Next(7500, 17501); }
        if (_whirlwindMs >= diffMs) _whirlwindMs -= diffMs;
        else if (DoCast(Victim, 8989) == CreatureCastResult.Ok)
        {
            System?.SayText(Me, -1189001);
            _whirlwindMs = (uint)Random.Shared.Next(15000, 25001);
        }
    }
}

/// <summary>ScriptDev2 mob_scarlet_traineeAI (mangos-classic scarlet_monastery/boss_herod.cpp: UpdateEscortAI).</summary>
public sealed class ScarletTraineeAi(Creature creature) : EscortAI(creature)
{
    private uint _startMs = (uint)Random.Shared.Next(1000, 6001);
    protected override void WaypointReached(uint pointId) { }
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_startMs > 0)
        {
            if (_startMs > diffMs) _startMs -= diffMs;
            else { Start(run: true); _startMs = 0; }
        }
        UpdateVictim();
    }
}
