using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>C'Thun (15727): the Eye phase, body emergence, stomach grabs and alternating carapace/weakness.</summary>
public sealed class CthunBodyAI : RaidBossAI
{
    private enum BodyPhase { Eye, EyeDead, Emerging, Invulnerable, Weakened, Done }
    private static readonly (float X, float Y, float Z)[] FleshSites =
        [(-8571f, 1990f, -98f), (-8525f, 1994f, -98f)];
    private static readonly (float X, float Y, float Z)[] EyeSites =
    [(-8547.27f, 1986.94f, 100.49f), (-8556.05f, 2008.14f, 100.60f),
     (-8577.25f, 2016.94f, 100.32f), (-8598.46f, 2008.18f, 100.32f),
     (-8607.27f, 1986.99f, 100.49f), (-8598.53f, 1965.77f, 100.49f),
     (-8577.34f, 1956.94f, 100.54f), (-8556.12f, 1965.67f, 100.60f)];
    private static readonly HashSet<uint> TentacleEntries = [15726, 15725, 15728, 15334, 15802, 15904, 15910];
    private readonly HashSet<ObjectGuid> _flesh = [];
    private BodyPhase _phase;
    private uint _phaseMs, _clawMs, _eyesMs, _giantClawMs, _giantEyeMs, _grabMs, _grabPortMs;
    private Player? _grabbed;

    private TempleOfAhnQirajInstance? Temple => Instance as TempleOfAhnQirajInstance;

    public CthunBodyAI(Creature creature) : base(creature, TempleOfAhnQirajInstance.CThun) => ResetState();

    private void ResetState()
    {
        _phase = BodyPhase.Eye;
        _phaseMs = 0;
        _clawMs = 5000;
        _eyesMs = 45_000;
        _giantClawMs = _giantEyeMs = _grabMs = _grabPortMs = 0;
        _grabbed = null;
        _flesh.Clear();
        Me.Health = Me.MaxHealth;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.Spawning;
        Me.InvincibilityHpThreshold = Me.MaxHealth;
        SetMeleeEnabled(false);
        SetCombatMovement(false);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26156);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26235);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26232);
    }

    public override void OnRespawn()
    {
        ResetState();
        if (Temple?.CthunEye is { IsAlive: false } eye) System?.ForceRespawn(eye);
    }

    private Player? RandomOutsidePlayer()
    {
        Player[] candidates = [.. Me.Map?.Players.Where(p => p.IsAlive && !p.IsGameMaster && p.Combat.IsInCombat
            && Temple?.IsInCthunStomach(p) != true) ?? []];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }

    private void StartEncounter(Player player)
    {
        if (_phase != BodyPhase.Eye || _phaseMs != 0 || Temple?.CthunEye?.AI is not CthunEyeAI eye) return;
        _phaseMs = uint.MaxValue; // zone combat calls AttackStart back into this AI
        Temple.SetData(TempleOfAhnQirajInstance.CThun, EncounterState.InProgress);
        Me.Combat.Threat.AddThreat(player, 1);
        System?.SetInCombatWithZone(Me);
        eye.Pull(player);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (_phase == BodyPhase.Eye && _phaseMs == 0 && who is Player { IsAlive: true, IsGameMaster: false } player
            && MathF.Abs(player.Z - 100f) < 10f
            && (player.X - Me.X) * (player.X - Me.X) + (player.Y - Me.Y) * (player.Y - Me.Y) < 95f * 95f
            && Me.Map?.Collision.IsWithinLineOfSight(player, Me) == true)
            StartEncounter(player);
    }

    public override bool AttackStart(Unit target)
    {
        if (target is Player player && _phase == BodyPhase.Eye) StartEncounter(player);
        return _phase != BodyPhase.Eye && base.AttackStart(target);
    }

    public void EyeDied()
    {
        if (_phase != BodyPhase.Eye || _phaseMs == 0) return;
        _phase = BodyPhase.EyeDead;
        _phaseMs = 4000;
    }

    private static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }

    private Creature? SummonTentacle(uint entry, float x, float y, float z, Unit? target = null)
        => System?.SummonCorpseTimedDespawn(Me, entry, x, y, z, 0, target, 1500);

    private void SpawnClaw(uint entry)
    {
        if (RandomOutsidePlayer() is not { } target) return;
        Creature? claw = SummonTentacle(entry, target.X + 0.5f, target.Y, target.Z, target);
        if (claw is not null) System?.SetInCombatWithZone(claw);
    }

    private void SpawnEyeRing()
    {
        foreach (var site in EyeSites)
            if (SummonTentacle(15726, site.X, site.Y, site.Z) is { } eye)
                System?.SetInCombatWithZone(eye);
    }

    private void SpawnFlesh()
    {
        _flesh.Clear();
        foreach (var site in FleshSites)
            if (SummonTentacle(15802, site.X, site.Y, site.Z) is { } flesh)
                _flesh.Add(flesh.Guid);
    }

    private void BeginTransition()
    {
        _phase = BodyPhase.Emerging;
        _phaseMs = 8000;
        _giantClawMs = 8000;
        _giantEyeMs = 38_000;
        _eyesMs = 38_000;
        _grabMs = 14_750;
        Cast(26232, Me, triggered: true);
        Cast(26156, Me, triggered: true);
        SpawnFlesh();
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
    }

    private void Weaken()
    {
        if (_phase != BodyPhase.Invulnerable) return;
        _phase = BodyPhase.Weakened;
        _phaseMs = 45_000;
        Me.InvincibilityHpThreshold = 0;
        if (Me.Map?.Combat.SpellMitigation is { } spells)
        {
            spells.RemoveAuras(Me, 26156);
            // The imported script-target spell reports CastOk but drops its aura in this core;
            // apply the same spell's holder directly so the vulnerable phase is real.
            spells.AddAura(Me, 26235, caster: Me);
        }
        System?.SayText(Me, 11476);
        if (_grabbed is { } grabbed) Me.Map?.Combat.SpellMitigation?.RemoveAuras(grabbed, 26332);
    }

    private void RestoreCarapace()
    {
        _phase = BodyPhase.Invulnerable;
        Me.InvincibilityHpThreshold = Me.MaxHealth;
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26235);
        Cast(26156, Me, triggered: true);
        _giantClawMs = 8000;
        _giantEyeMs = _eyesMs = 38_000;
        _grabMs = 14_750;
        SpawnFlesh();
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (summoned.Entry != 15802 || !_flesh.Remove(summoned.Guid)) return;
        if (_flesh.Count == 0) Weaken();
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        if (summoned.Entry == 15802 && _flesh.Remove(summoned.Guid) && _flesh.Count == 0)
            Weaken();
    }

    private void UpdateTentacles(uint diffMs, bool phaseTwo)
    {
        if (!phaseTwo && Due(ref _clawMs, diffMs))
        {
            SpawnClaw(15725);
            _clawMs = 5000;
        }
        if (Due(ref _eyesMs, diffMs))
        {
            SpawnEyeRing();
            _eyesMs = phaseTwo ? 30_000u : 45_000u;
        }
        if (!phaseTwo) return;
        if (Due(ref _giantClawMs, diffMs))
        {
            SpawnClaw(15728);
            _giantClawMs = 60_000;
        }
        if (Due(ref _giantEyeMs, diffMs))
        {
            SpawnClaw(15334);
            _giantEyeMs = 60_000;
        }
    }

    private void UpdateStomachGrab(uint diffMs)
    {
        if (_grabbed is { } player)
        {
            if (Due(ref _grabPortMs, diffMs))
            {
                if (player.IsAlive) Temple?.SendToCthunStomach(player);
                Me.Map?.Combat.SpellMitigation?.RemoveAuras(player, 26332);
                _grabbed = null;
            }
        }
        if (Due(ref _grabMs, diffMs))
        {
            if (RandomOutsidePlayer() is { } target && Me.Map?.Combat.SpellMitigation is { } spells)
            {
                spells.CastSpell(target, 26332, SpellCastTargets.ForSelf(), triggered: true);
                _grabbed = target;
                _grabPortMs = 3250;
            }
            _grabMs = 10_000;
        }
    }

    private void CleanupTentacles()
    {
        if (System is not { } system) return;
        foreach (Creature tentacle in system.Creatures.Where(c => TentacleEntries.Contains(c.Entry)
            && (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) < 350f * 350f).ToArray())
            system.ForcedDespawn(tentacle, 0);
        _flesh.Clear();
    }

    public override void OnEvade()
    {
        CleanupTentacles();
        Temple?.KillPlayersInCthunStomach();
        ResetState();
        if (Temple?.CthunEye is { IsAlive: false } eye) System?.ForceRespawn(eye);
        else Temple?.CthunEye?.AI?.OnEvade();
    }

    public override void OnReachedHome() => Temple?.SetData(TempleOfAhnQirajInstance.CThun, EncounterState.Fail);

    public override void OnDeath(Unit? killer)
    {
        _phase = BodyPhase.Done;
        CleanupTentacles();
        base.OnDeath(killer);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive || _phase == BodyPhase.Done) return;
        if (_phase == BodyPhase.Eye && _phaseMs == 0)
        {
            foreach (Player player in Me.Map?.Players ?? []) MoveInLineOfSight(player);
            return;
        }
        if (_phase is BodyPhase.Eye or BodyPhase.EyeDead)
        {
            if (_phase == BodyPhase.EyeDead && Due(ref _phaseMs, diffMs)) BeginTransition();
            else if (_phase == BodyPhase.Eye) UpdateTentacles(diffMs, phaseTwo: false);
            return;
        }
        if (_phase == BodyPhase.Emerging)
        {
            UpdateTentacles(diffMs, phaseTwo: true);
            UpdateStomachGrab(diffMs);
            if (Due(ref _phaseMs, diffMs))
            {
                Me.UnitFlags &= ~UnitFlags.Spawning;
                System?.SetInCombatWithZone(Me);
                _phase = BodyPhase.Invulnerable;
            }
            return;
        }
        if (_phase == BodyPhase.Weakened)
        {
            if (Due(ref _phaseMs, diffMs)) RestoreCarapace();
            return;
        }
        if (_flesh.Count == 0) { Weaken(); return; }
        UpdateTentacles(diffMs, phaseTwo: true);
        UpdateStomachGrab(diffMs);
        if (RandomOutsidePlayer() is null && Temple?.KillPlayersInCthunStomach() == true)
            EnterEvadeMode();
    }
}
