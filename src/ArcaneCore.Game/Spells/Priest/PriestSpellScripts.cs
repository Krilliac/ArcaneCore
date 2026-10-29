using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Priest;

/// <summary>
/// Touch of Weakness, the triggered damage and hex (28598; vmangos scripts/spells/spell_priest.cpp:19-60, <c>PriestTouchOfWeaknessScript::OnEffectExecute</c>):
/// when the Touch of Weakness proc aura (2652, 19261, 19262, 19264, 19265, 19266) triggers 28598 on the attacker, effect 0 also casts the rank's
/// Hex of Weakness debuff (2943, 19249, 19251, 19252, 19253, 19254) on the target, triggered.
/// </summary>
[SpellScript(TouchOfWeakness, ExecuteEffects = [SpellEffectName.SchoolDamage])]
public sealed class TouchOfWeaknessScript : ISpellScript
{
    public const uint TouchOfWeakness = 28598;

    /// <summary>Touch of Weakness aura rank → Hex of Weakness debuff rank (spell_priest.cpp:27-50).</summary>
    public static readonly IReadOnlyDictionary<uint, uint> HexSpells = new Dictionary<uint, uint>
    {
        [2652] = 2943,
        [19261] = 19249,
        [19262] = 19251,
        [19264] = 19252,
        [19265] = 19253,
        [19266] = 19254,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Cast.TriggeringSpell is not { } triggering
            || !HexSpells.TryGetValue(triggering.Id, out uint hexSpell))
        {
            return;
        }

        context.System.CastSpell(context.Caster, hexSpell, SpellCastTargets.ForUnit(context.Target.Guid, context.Cast), triggered: true);
    }
}

/// <summary>
/// Power Word: Shield (17, 592, 600, 3747, 6065, 6066, 10898, 10899, 10900, 10901, 27607; vmangos spell_priest.cpp:62-82, <c>OnHit</c>) and
/// Holy Nova (15237, 15430, 15431, 27799, 27800, 27801; spell_priest.cpp:84-130, <c>OnSuccessfulFinish</c>):
/// <list type="bullet">
/// <item>After Power Word: Shield hits a target (<see cref="SpellMissInfo.None"/>), the caster puts Weakened Soul (6788) on it, triggered.
/// Weakened Soul is <see cref="AuraType.MechanicImmunity"/> to <c>MECHANIC_SHIELD</c> (19), so <see cref="Paladin.PositiveSpellImmunityCheck"/>
/// refuses another Power Word: Shield with <see cref="SpellCastResult.TargetAurastate"/> while it lasts.</item>
/// <item>When a Holy Nova cast finishes (<c>completed == true</c>), the caster casts the rank's Holy Nova heal (23455, 23458, 23459, 27803,
/// 27804, 27805) with the cast's targets, triggered.</item>
/// </list>
/// </summary>
public sealed class PriestCastObserver : ISpellCastObserver
{
    public const uint WeakenedSoul = 6788;

    public static readonly uint[] PowerWordShieldRanks = [17, 592, 600, 3747, 6065, 6066, 10898, 10899, 10900, 10901, 27607];

    /// <summary>Holy Nova damage rank → Holy Nova heal rank (spell_priest.cpp:93-116).</summary>
    public static readonly IReadOnlyDictionary<uint, uint> HolyNovaHeals = new Dictionary<uint, uint>
    {
        [15237] = 23455,
        [15430] = 23458,
        [15431] = 23459,
        [27799] = 27803,
        [27800] = 27804,
        [27801] = 27805,
    };

    private readonly SpellSystem _spells;

    public PriestCastObserver(SpellSystem spells) => _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (outcome.Miss == SpellMissInfo.None && Array.IndexOf(PowerWordShieldRanks, cast.Spell.Id) >= 0
            && _spells.Store.Get(WeakenedSoul) is not null)
        {
            _spells.CastSpell(cast.Caster, WeakenedSoul, SpellCastTargets.ForUnit(outcome.Target.Guid), triggered: true);
        }
    }

    public void OnFinished(SpellCast cast, bool completed)
    {
        if (completed && HolyNovaHeals.TryGetValue(cast.Spell.Id, out uint healSpell)
            && _spells.Store.Get(healSpell) is not null)
        {
            _spells.CastSpell(cast.Caster, healSpell, cast.Targets, triggered: true);
        }
    }
}

/// <summary>Registers the priest cast observer (discovered <see cref="ISpellHandlerModule"/>): Power Word: Shield's Weakened Soul and Holy Nova's heal.</summary>
public sealed class PriestScriptsModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterObserver(new PriestCastObserver(system));
    }
}
