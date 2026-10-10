using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>The Eye of C'Thun: 45-second green-beam windows separated by the 3/38/1-second Dark Glare sequence.</summary>
public sealed class CthunEyeAI(Creature creature) : CreatureAI(creature)
{
    private enum EyePhase { Green, GlareCast, Glare, Cooling }
    private EyePhase _phase;
    private bool _pulled;
    private uint _phaseMs, _beamMs, _pulseMs;
    private int _rotationDirection;

    private TempleOfAhnQirajInstance? Temple => Me.Map?.FindUpdater<InstanceData>() as TempleOfAhnQirajInstance;

    public override void OnRespawn()
    {
        _phase = EyePhase.Green;
        _pulled = false;
        _phaseMs = 45_000;
        _beamMs = 3000;
        _pulseMs = 1000;
        _rotationDirection = 0;
        Me.FactionTemplate = Me.Template.Faction;
        Me.UnitFlags &= ~UnitFlags.Spawning;
        Me.Orientation = 3.44f;
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26009);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26136);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 16245);
    }

    public void Pull(Unit puller)
    {
        if (_pulled || !Me.IsAlive) return;
        _pulled = true;
        Me.FactionTemplate = 14;
        System?.SetInCombatWithZone(Me);
        DoCast(puller, 26134);
    }

    public override bool AttackStart(Unit target) => false; // the body controls the encounter pull

    public override void OnDeath(Unit? killer)
    {
        _pulled = false;
        if (Temple?.CthunBody?.AI is CthunBodyAI body) body.EyeDied();
    }

    public override void OnEvade() => OnRespawn();

    private Player? RandomRaidPlayer()
    {
        Player[] candidates = [.. Me.Map?.Players.Where(p => p.IsAlive && !p.IsGameMaster && p.Combat.IsInCombat
            && Temple?.IsInCthunStomach(p) != true) ?? []];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_pulled || !Me.IsAlive) return;
        switch (_phase)
        {
            case EyePhase.Green:
                if (_phaseMs <= diffMs)
                {
                    _phase = EyePhase.GlareCast;
                    _phaseMs = 3000;
                    _rotationDirection = System?.RandomInt(0, 1) == 0 ? -1 : 1;
                    DoCast(Me, 26137, triggered: true);
                    DoCast(Me, 16245, triggered: true);
                    DoCast(Me, _rotationDirection > 0 ? 26009u : 26136u, triggered: true);
                }
                else
                {
                    _phaseMs -= diffMs;
                    if (_beamMs <= diffMs && RandomRaidPlayer() is { } target
                        && DoCast(target, 26134) == CreatureCastResult.Ok) _beamMs = 3000;
                    else _beamMs = _beamMs > diffMs ? _beamMs - diffMs : 0;
                }
                break;
            case EyePhase.GlareCast:
                _phaseMs = _phaseMs > diffMs ? _phaseMs - diffMs : 0;
                if (_phaseMs == 0) { _phase = EyePhase.Glare; _phaseMs = 38_000; }
                break;
            case EyePhase.Glare:
            {
                uint active = Math.Min(diffMs, _phaseMs);
                _phaseMs -= active;
                uint remaining = active;
                while (remaining >= _pulseMs)
                {
                    remaining -= _pulseMs;
                    _pulseMs = 1000;
                    Me.Orientation = Creature.NormalizeOrientation(Me.Orientation + _rotationDirection * MathF.PI / 40);
                    DoCast(Me, 26029, triggered: true);
                }
                _pulseMs -= remaining;
                if (_phaseMs == 0)
                {
                    Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26009);
                    Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26136);
                    Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 16245);
                    _phase = EyePhase.Cooling;
                    _phaseMs = 1000;
                }
                break;
            }
            case EyePhase.Cooling:
                _phaseMs = _phaseMs > diffMs ? _phaseMs - diffMs : 0;
                if (_phaseMs == 0) { _phase = EyePhase.Green; _phaseMs = 45_000; _beamMs = 0; }
                break;
        }
    }
}
