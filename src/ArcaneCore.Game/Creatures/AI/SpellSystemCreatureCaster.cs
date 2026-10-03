using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// <see cref="ICreatureSpellCaster"/> over the world <see cref="SpellSystem"/>. Creature casts
/// use <see cref="SpellSystem.CastSpell"/> with the creature object as caster, so every aura
/// they apply captures that exact creature through the aura caster ownership contract
/// (docs/integration/aura-caster-ownership.md). <see cref="OnCreatureRemoved"/> calls
/// <see cref="SpellSystem.RemoveUnit"/>, which revokes the ownership token: a creature that
/// respawns or is re-added never regains attribution for auras from its previous life.
/// </summary>
public sealed class SpellSystemCreatureCaster : ICreatureSpellCaster
{
    private readonly Func<SpellSystem> _spells;
    private SpellSystem? _subscribed;

    /// <summary>Use <paramref name="spells"/> (resolved lazily: the spell feature may attach after the creature feature).</summary>
    public SpellSystemCreatureCaster(Func<SpellSystem> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        _spells = spells;
    }

    public SpellSystemCreatureCaster(SpellSystem spells)
        : this(() => spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        Subscribe();
    }

    private event Action<Unit, Unit, SpellInfo>? Hit;

    public event Action<Unit, Unit, SpellInfo>? SpellHit
    {
        add
        {
            Hit += value;
            Subscribe();
        }

        remove => Hit -= value;
    }

    private SpellSystem Spells
    {
        get
        {
            Subscribe();
            return _subscribed!;
        }
    }

    public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        SpellSystem spells = Spells;
        if (spells.Store.Get(spellId) is null)
        {
            return CreatureCastResult.UnknownSpell;
        }

        if (!triggered && IsCasting(caster))
        {
            return CreatureCastResult.AlreadyCasting;
        }

        SpellCastTargets targets = target is null || ReferenceEquals(target, caster)
            ? SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target.Guid);
        return spells.CastSpell(caster, spellId, targets, triggered) == SpellCastResult.CastOk
            ? CreatureCastResult.Ok
            : CreatureCastResult.Failed;
    }

    public bool IsCasting(Creature caster)
        => Spells.GetState(caster.Guid) is { Unit: var unit, CurrentCast: { State: SpellCastState.Preparing or SpellCastState.Casting } }
            && ReferenceEquals(unit, caster);

    public bool HasAura(Unit unit, uint spellId) => Spells.HasAura(unit, spellId);

    public void Interrupt(Creature caster)
    {
        SpellSystem spells = Spells;
        spells.CancelCast(caster, 0);
        spells.CancelChannel(caster);
    }

    public void OnCreatureRemoved(Creature creature) => Spells.RemoveUnit(creature);

    private void Subscribe()
    {
        if (_subscribed is not null)
        {
            return;
        }

        SpellSystem spells = _spells();
        _subscribed = spells;
        spells.SpellHit += (caster, target, spell) => Hit?.Invoke(caster, target, spell);
    }
}
