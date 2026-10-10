using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>Eye, claw, giant and flesh tentacles spawned by the two C'Thun phases.</summary>
public sealed class CthunTentacleAI(Creature creature) : CreatureAI(creature)
{
    private ObjectGuid _portal;
    private uint _birthMs, _abilityMs, _secondaryMs, _relocateMs;
    private TempleOfAhnQirajInstance? Temple => Me.Map?.FindUpdater<InstanceData>() as TempleOfAhnQirajInstance;
    private bool IsClaw => Me.Entry is 15725 or 15728;
    private bool IsGiant => Me.Entry is 15728 or 15334;

    public override void OnRespawn()
    {
        _birthMs = 3000;
        _abilityMs = Me.Entry == 15725 ? 2000u : 0u;
        _secondaryMs = _relocateMs = 5000;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.Spawning;
        if (Me.Entry != 15802 && System is { } system)
        {
            uint portalEntry = IsGiant ? 15910u : 15904u;
            if (system.SummonAt(Me, portalEntry, Me.X, Me.Y, Me.Z, 0, null, 0) is { } portal)
            {
                _portal = portal.Guid;
                portal.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.Spawning;
            }
        }
        DoCast(Me, 26262, triggered: true);
    }

    public override void OnDeath(Unit? killer) => RemovePortal();
    public override void OnEvade() => RemovePortal();
    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        if (summoned.Guid == _portal) _portal = default;
    }

    private void RemovePortal()
    {
        if (!_portal.IsEmpty && System?.FindCreature(_portal) is { } portal)
            System.ForcedDespawn(portal, 0);
        _portal = default;
    }

    private Player? RandomOutside()
    {
        Player[] candidates = [.. Me.Map?.Players.Where(p => p.IsAlive && !p.IsGameMaster && p.Combat.IsInCombat
            && Temple?.IsInCthunStomach(p) != true) ?? []];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }

    private static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_birthMs != 0)
        {
            if (Due(ref _birthMs, diffMs))
            {
                Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.Spawning);
                if (IsClaw) DoCast(Me, IsGiant ? 26478u : 26139u, triggered: true);
            }
            return;
        }
        switch (Me.Entry)
        {
            case 15726: // small eye: channel Mind Flay
                if (Due(ref _abilityMs, diffMs) && RandomOutside() is { } eyeTarget
                    && DoCast(eyeTarget, 26143) == CreatureCastResult.Ok) _abilityMs = 1500;
                break;
            case 15334: // giant eye: Green Eye Beam
                if (Due(ref _abilityMs, diffMs) && RandomOutside() is { } beamTarget
                    && DoCast(beamTarget, 26134) == CreatureCastResult.Ok) _abilityMs = 2500;
                break;
            case 15725:
            case 15728:
                if (Victim is null && RandomOutside() is { } clawTarget) AttackStart(clawTarget);
                if (Due(ref _abilityMs, diffMs) && Victim is { } victim
                    && DoCast(victim, 26141) == CreatureCastResult.Ok) _abilityMs = 5000;
                if (IsGiant && Due(ref _secondaryMs, diffMs))
                {
                    DoCast(Me, 6524, triggered: true);
                    DoCast(Me, 3391, triggered: true);
                    _secondaryMs = (uint)(System?.RandomInt(6000, 12_000) ?? 9000);
                }
                if (Due(ref _relocateMs, diffMs) && Victim is { } target
                    && !MapCombat.CanReachWithMeleeAutoAttack(Me, target))
                {
                    DoCast(Me, 26234, triggered: true);
                    System?.NearTeleport(Me, target.X + 0.5f, target.Y, target.Z, Me.Orientation);
                    DoCast(Me, 26262, triggered: true);
                    _relocateMs = 5000;
                }
                break;
        }
    }
}

/// <summary>Ground trigger in C'Thun's stomach, used for the delayed upward punt.</summary>
public sealed class CthunPuntAI(Creature creature) : CreatureAI(creature)
{
    private uint _puntMs = 3000;
    public override void OnUpdate(uint diffMs)
    {
        _puntMs = _puntMs > diffMs ? _puntMs - diffMs : 0;
        if (_puntMs != 0) return;
        DoCast(Me, 26224, triggered: true);
        System?.ForcedDespawn(Me, 0);
    }
}
