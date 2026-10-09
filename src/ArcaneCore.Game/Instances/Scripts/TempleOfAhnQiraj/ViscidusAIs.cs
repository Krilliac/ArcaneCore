using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>Viscidus (15299): frost hits freeze him, physical hits shatter him, and killed globs cost him health.</summary>
public sealed class ViscidusAI : RaidBossAI
{
    private enum Stage { Normal, Frozen, Exploded }

    private const uint Freeze = 25937;
    private const uint Explode = 25938;
    private const uint GlobEntry = 15667;
    private const UnitFlags ExplodedFlags = UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
    private readonly HashSet<ObjectGuid> _globs = [];
    private Stage _stage;
    private uint _frostHits;
    private uint _physicalHits;
    private uint _shockMs;
    private uint _volleyMs;
    private uint _toxinMs;
    private uint _explodeMs;

    public ViscidusAI(Creature creature) : base(creature, TempleOfAhnQirajInstance.Viscidus) => ResetState();

    private void ResetState()
    {
        _stage = Stage.Normal;
        _frostHits = _physicalHits = _explodeMs = 0;
        _shockMs = RandomDelay(7000, 12_000);
        _volleyMs = RandomDelay(10_000, 15_000);
        _toxinMs = RandomDelay(30_000, 40_000);
        _globs.Clear();
        Me.InvincibilityHpThreshold = 1;
        Me.UnitFlags &= ~ExplodedFlags;
        SetMeleeEnabled(true);
        SetCombatMovement(true);
        Cast(25994, Me, triggered: true); // membrane
        Cast(25926, Me, triggered: true); // frost weakness
    }

    public override void OnRespawn() => ResetState();

    public override void OnEvade()
    {
        foreach (ObjectGuid guid in _globs.ToArray())
            if (System?.FindCreature(guid) is { } glob) System.ForcedDespawn(glob, 0);
        ResetState();
    }

    public override void OnDeath(Unit? killer)
    {
        foreach (ObjectGuid guid in _globs.ToArray())
            if (System?.FindCreature(guid) is { } glob) System.ForcedDespawn(glob, 0);
        _globs.Clear();
        base.OnDeath(killer);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (who is Player && Victim is null && System is { } system && system.CanAggroOnSight(Me, who, scriptedRange: 95f))
            system.EnterCombatWithTarget(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (ReferenceEquals(caster, Me) || !Me.IsAlive) return;
        if (_stage == Stage.Normal && spell.School == SpellSchool.Frost)
        {
            _frostHits++;
            if (_frostHits == 200)
            {
                _stage = Stage.Frozen;
                _physicalHits = 0;
                System?.SayText(Me, 11695);
                Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26036);
                Cast(Freeze, Me, triggered: true);
            }
            else if (_frostHits == 150)
            {
                System?.SayText(Me, 11345);
                Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26034);
                Cast(26036, Me, triggered: true);
            }
            else if (_frostHits == 100)
            {
                System?.SayText(Me, 11343);
                Cast(26034, Me, triggered: true);
            }
        }
        else if (_stage == Stage.Frozen && spell.School == SpellSchool.Normal && spell.Id != 5019
                 && spell.Effects.Any(e => e.Effect is SpellEffectName.SchoolDamage or SpellEffectName.WeaponDamage
                     or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg
                     or SpellEffectName.WeaponPercentDamage)) // Shoot needs its wand item's damage school.
            RegisterPhysicalHit();
    }

    public override void OnMeleeHitReceived(MeleeDamageInfo hit)
    {
        if (_stage == Stage.Frozen) RegisterPhysicalHit();
    }

    private void RegisterPhysicalHit()
    {
        _physicalHits++;
        if (_physicalHits == 50) System?.SayText(Me, 11346);
        else if (_physicalHits == 100) System?.SayText(Me, 11347);
        if (_physicalHits != 150) return;
        _stage = Stage.Exploded; // freeze removal must not restore Normal before the explosion
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, Freeze);
        Cast(Explode, Me, triggered: true);
        if ((ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 5)
        {
            Me.InvincibilityHpThreshold = 0;
            Me.Map?.Combat.Kill(Me, Me);
            return;
        }

        uint count = Math.Min(20u, Me.MaxHealth == 0 ? 0u : (uint)((ulong)Me.Health * 20 / Me.MaxHealth));
        for (uint i = 0; i < count; i++) Cast(25865 + i, Me, triggered: true);
        _explodeMs = 2500;
        SetMeleeEnabled(false);
        SetCombatMovement(false);
    }

    /// <summary>Called by the freeze aura script when it expires or is removed before shattering.</summary>
    public void OnFreezeRemoved()
    {
        if (_stage != Stage.Frozen) return;
        _stage = Stage.Normal;
        _frostHits = _physicalHits = 0;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry != GlobEntry) return;
        _globs.Add(summoned.Guid);
        summoned.Motion.MovePoint(1, -7993.956f, 926.309f, -52.699f, run: true);
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (summoned.Entry != GlobEntry || !_globs.Remove(summoned.Guid)) return;
        uint loss = Math.Max(1u, Me.MaxHealth / 20);
        Me.Health = Math.Max(1u, Me.Health > loss ? Me.Health - loss : 1u);
        Cast(27934, Me, triggered: true);
        FinishGlobPhaseIfEmpty();
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        if (summoned.Entry == GlobEntry && _globs.Remove(summoned.Guid)) FinishGlobPhaseIfEmpty();
    }

    public void GlobRejoined(Creature glob)
    {
        if (_stage != Stage.Exploded || !_globs.Remove(glob.Guid)) return;
        Cast(25897, Me, triggered: true);
        System?.ForcedDespawn(glob, 650);
        FinishGlobPhaseIfEmpty();
    }

    private void FinishGlobPhaseIfEmpty()
    {
        if (_stage != Stage.Exploded || _globs.Count != 0) return;
        _stage = Stage.Normal;
        _frostHits = _physicalHits = _explodeMs = 0;
        Me.UnitFlags &= ~ExplodedFlags;
        SetMeleeEnabled(true);
        SetCombatMovement(true);
        ResetThreat();
    }

    private static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (_stage == Stage.Exploded && _explodeMs != 0 && Due(ref _explodeMs, diffMs))
            Me.UnitFlags |= ExplodedFlags;
        if (_stage != Stage.Normal) return;

        if (Due(ref _shockMs, diffMs) && Cast(25993, Victim)) _shockMs = RandomDelay(7000, 12_000);
        if (Due(ref _volleyMs, diffMs) && Cast(25991, Me)) _volleyMs = RandomDelay(10_000, 15_000);
        if (Due(ref _toxinMs, diffMs) && RandomTarget() is { } target)
        {
            System?.SummonAt(Me, 15922, target.X, target.Y, target.Z, target.Orientation, null, 180_000);
            _toxinMs = RandomDelay(30_000, 40_000);
        }
    }
}

/// <summary>A glob returns to Viscidus's center; killing it before arrival removes five percent of his health.</summary>
public sealed class ViscidusGlobAI(Creature creature) : CreatureAI(creature)
{
    private uint _speedMs = 4000;
    public override void OnUpdate(uint diffMs)
    {
        if (_speedMs == 0 || _speedMs > diffMs) { if (_speedMs != 0) _speedMs -= diffMs; return; }
        _speedMs = 0;
        DoCast(Me, 26633, triggered: true);
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type == MovementGeneratorType.Point && pointId == 1 && System?.SummonerOf(Me) is { AI: ViscidusAI boss } summoner)
        {
            DoCast(summoner, 25896, triggered: true);
            boss.GlobRejoined(Me);
        }
    }
}

/// <summary>Toxin cloud trigger waits for the visual lead-in before applying its periodic poison.</summary>
public sealed class ViscidusToxinTriggerAI(Creature creature) : CreatureAI(creature)
{
    private uint _delayMs = 3000;
    public override void OnUpdate(uint diffMs)
    {
        if (_delayMs == 0 || _delayMs > diffMs) { if (_delayMs != 0) _delayMs -= diffMs; return; }
        _delayMs = 0;
        Me.FactionTemplate = 14;
        Me.UnitFlags |= UnitFlags.NonAttackable2;
        DoCast(Me, 25989, triggered: true);
        DoCast(Me, 26575, triggered: true);
    }
}

/// <summary>Freeze expiry restores frost progress unless the physical-hit shatter already started.</summary>
public sealed class ViscidusSpellModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id == 25937 && holder.Target is Creature { AI: ViscidusAI boss })
                boss.OnFreezeRemoved();
        };
    }
}
