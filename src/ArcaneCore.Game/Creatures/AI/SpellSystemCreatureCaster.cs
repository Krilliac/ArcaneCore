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
public sealed class SpellSystemCreatureCaster : ICreatureSpellCaster, ICreatureAuraReset
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

    public void RemoveAuras(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Spells.RemoveAuras(unit, spellId);
    }

    public CreatureCastResult AddAura(Unit unit, uint spellId, bool permanent)
    {
        ArgumentNullException.ThrowIfNull(unit);
        SpellSystem spells = Spells;
        if (spells.Store.Get(spellId) is null)
        {
            return CreatureCastResult.UnknownSpell;
        }

        return spells.AddAura(unit, spellId, permanent) ? CreatureCastResult.Ok : CreatureCastResult.Failed;
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

    public CreatureCastResult CastByUnit(Unit caster, uint spellId, Unit? target, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        SpellSystem spells = Spells;
        if (spells.Store.Get(spellId) is null)
        {
            return CreatureCastResult.UnknownSpell;
        }

        SpellCastTargets targets = target is null || ReferenceEquals(target, caster)
            ? SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target.Guid);
        return spells.CastSpell(caster, spellId, targets, triggered) == SpellCastResult.CastOk
            ? CreatureCastResult.Ok
            : CreatureCastResult.Failed;
    }

    public CreatureCastResult CastAtDestination(Creature caster, uint spellId, float x, float y, float z, bool triggered)
    {
        ArgumentNullException.ThrowIfNull(caster);
        SpellSystem spells = Spells;
        if (spells.Store.Get(spellId) is null) return CreatureCastResult.UnknownSpell;
        if (!triggered && IsCasting(caster)) return CreatureCastResult.AlreadyCasting;
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (x, y, z) };
        return spells.CastSpell(caster, spellId, targets, triggered) == SpellCastResult.CastOk
            ? CreatureCastResult.Ok : CreatureCastResult.Failed;
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

    /// <summary>
    /// vmangos Creature::RemoveAurasAtReset (Objects/Creature.cpp:3611-3630): KEEP_POSITIVE_AURAS_ON_EVADE removes only the negative auras;
    /// otherwise every aura goes except a non-permanent positive one whose caster is a player. Either way an aura the evade never removes
    /// stays (<see cref="IsRemovedOnEvade"/>, cMaNGOS Unit::RemoveAllAurasOnEvade).
    /// </summary>
    public void ResetAuras(Creature creature, bool keepPositive)
    {
        ArgumentNullException.ThrowIfNull(creature);
        SpellSystem spells = Spells;
        foreach (SpellAuraHolder holder in spells.GetAuras(creature).ToArray())
        {
            if (holder.IsRemoved || !IsRemovedOnEvade(holder))
            {
                continue;
            }

            bool keep = keepPositive
                ? holder.IsPositive
                : holder.CasterGuid.IsPlayer && !holder.IsPermanent && holder.IsPositive;
            if (!keep)
            {
                spells.RemoveAurasByCaster(creature, holder.Spell.Id, holder.CasterGuid);
            }
        }
    }

    /// <inheritdoc/>
    public void RemoveAllAuras(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        SpellSystem spells = Spells;
        foreach (SpellAuraHolder holder in spells.GetAuras(creature).ToArray())
        {
            if (!holder.IsRemoved)
            {
                spells.RemoveAurasByCaster(creature, holder.Spell.Id, holder.CasterGuid);
            }
        }
    }

    /// <summary>
    /// cMaNGOS IsSpellRemovedOnEvade (mangos-classic Spells/SpellMgr.h:469-600): charm and possess auras stay, and so do the creature
    /// passives and cosmetic auras on its hard-coded list (Thrash, the poison procs, Ghost Visual, Disease Cloud...). These are the
    /// creature's own spawn auras: nothing would put them back after an evade. vmangos marks the same spells with
    /// SPELL_CUSTOM_NOT_REMOVED_ON_EVADE in its spell_template, which ArcaneCore does not load. Not delivered: SPELL_ATTR_SS_IGNORE_EVADE
    /// (a cMaNGOS server-side attribute with no DBC bit) and SPELL_ATTR_EX_AURA_STAYS_AFTER_COMBAT (cMaNGOS marks bit 25 "possibly different
    /// in vanilla").
    /// </summary>
    internal static bool IsRemovedOnEvade(SpellAuraHolder holder)
    {
        foreach (SpellAura? aura in holder.Auras)
        {
            if (aura is { Type: AuraType.ModCharm or AuraType.ModPossess })
            {
                return false;
            }
        }

        return !KeptOnEvade.Contains(holder.Spell.Id);
    }

    /// <summary>The spell ids of cMaNGOS IsSpellRemovedOnEvade's switch (mangos-classic Spells/SpellMgr.h:487-597).</summary>
    internal static readonly HashSet<uint> KeptOnEvade =
    [
        588, 3235, 3284, 3417, 3418, 3509, 3512, 3582, 3616, 3637, 4148, 5111, 5301, 5680, 6488, 6498, 6718, 6752, 6820, 6821, 6822, 6823,
        6923, 6947, 6961, 7056, 7090, 7095, 7165, 7276, 7999, 8247, 8279, 8393, 8599, 8601, 8876, 8909, 8990, 9205, 9460, 9464, 9617, 9769,
        9941, 10022, 10072, 10074, 10095, 11838, 11919, 11959, 11964, 11966, 11984, 12099, 12187, 12246, 12529, 12539, 12544, 12546, 12550,
        12556, 12627, 12787, 12898, 13260, 13299, 13616, 13767, 13787, 14111, 14133, 14178, 15088, 15097, 15506, 15876, 16140, 16345, 16563,
        16577, 16592, 17327, 17467, 18148, 18268, 18847, 18943, 18968, 19030, 18950, 19194, 19195, 19396, 19483, 19514, 19626, 19640, 19817,
        19818, 20514, 21061, 21857, 21862, 22128, 22578, 22650, 22735, 22781, 22788, 22856, 23255, 24313, 25039, 25592, 26341, 27578, 27793,
        27987, 28002, 28126, 28156, 28362, 28370, 29526, 30074,
    ];

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
