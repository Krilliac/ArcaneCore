using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Server rates of the power economy (config section <see cref="SectionName"/>). The defaults are the
/// vmangos defaults, which are the retail values (mangosd.conf.dist.in:2793-2799: every rate is 1).
/// </summary>
public sealed class CombatOptions
{
    public const string SectionName = "Combat";

    /// <summary>vmangos Rate.Rage.Income: multiplies the rage a player gains from damage (Player.cpp:2264).</summary>
    public float RateRageIncome { get; set; } = 1.0f;

    /// <summary>vmangos Rate.Rage.Loss: multiplies the out-of-combat rage decay (Player.cpp:2313). Must not be negative.</summary>
    public float RateRageLoss { get; set; } = 1.0f;

    /// <summary>vmangos Rate.Energy: multiplies the energy regeneration (Player.cpp:2320).</summary>
    public float RateEnergy { get; set; } = 1.0f;

    /// <summary>vmangos Rate.Mana: multiplies the mana regeneration (Player.cpp:2291). Must not be negative.</summary>
    public float RateMana { get; set; } = 1.0f;

    /// <summary>vmangos Rate.Health: multiplies spirit health regeneration and the aura-161 flat bonus. Must not be negative.</summary>
    public float RateHealth { get; set; } = 1.0f;

    /// <summary>
    /// Whether switching between warrior stances keeps the stance-bound buffs the unit cast on itself (Retaliation,
    /// Recklessness, Shield Wall). Default false: vmangos removes them with the old stance (SpellAuras.cpp:5565-5575,
    /// SpellAuraHolder::m_isRemovedOnShapeLost). Patch 1.7.0 is quoted by vmangos as saying they are no longer cancelled
    /// (SpellAuras.cpp:5537-5539), but its code for that sits in a block excluded from the 1.12.1 build, so the code
    /// is followed.
    /// </summary>
    public bool StanceShiftKeepsSelfBuffs { get; set; }

    /// <summary>
    /// Whether a unit casting a non-melee spell loses its melee swing (vmangos Unit::AttackerStateUpdate, Unit.cpp:2240-2241:
    /// <c>if (!extra &amp;&amp; IsNonMeleeSpellCasted(false)) return</c>). Default true, the retail behaviour; the swing timer
    /// still restarts, so the swing is lost, not delayed.
    /// </summary>
    public bool MeleeCastingBlocksSwing { get; set; } = true;

    /// <summary>Path of the client's SpellShapeshiftForm.dbc (build 5875). Empty = only the three warrior stances are known.</summary>
    public string ShapeshiftFormDbcPath { get; set; } = string.Empty;

    /// <summary>
    /// vmangos World::setConfigPos (World.cpp:2959-2967): Rate.Health, Rate.Mana and Rate.Rage.Loss cannot be negative and fall
    /// back to the default 1. Returns the names of the values that were replaced.
    /// </summary>
    public IReadOnlyList<string> Normalize()
    {
        var replaced = new List<string>();
        if (RateMana < 0.0f)
        {
            RateMana = 1.0f;
            replaced.Add("Rate.Mana");
        }

        if (RateHealth < 0.0f)
        {
            RateHealth = 1.0f;
            replaced.Add("Rate.Health");
        }

        if (RateRageLoss < 0.0f)
        {
            RateRageLoss = 1.0f;
            replaced.Add("Rate.Rage.Loss");
        }

        return replaced;
    }
}

/// <summary>
/// Aura queries the power economy needs from the spell system. Combat sits below spells, so the world
/// installs an implementation (<see cref="SpellSystemPowerAuras"/>); without one no unit has any aura.
/// </summary>
public interface IPowerAuraSource
{
    /// <summary>vmangos Unit::HasAura(spellId, effectIndex): the unit has the spell's aura on that effect.</summary>
    bool HasAura(Unit unit, uint spellId, int effectIndex);

    /// <summary>vmangos Unit::HasAuraType.</summary>
    bool HasAuraType(Unit unit, AuraType type);

    /// <summary>
    /// The product of (amount + 100) / 100 over the unit's SPELL_AURA_MOD_POWER_REGEN_PERCENT auras for
    /// <paramref name="power"/> (vmangos Player::Regenerate, Player.cpp:2323-2328); 1 when there is none.
    /// </summary>
    float GetPowerRegenFactor(Unit unit, PowerType power);

    float GetFoodHealthRegen(Unit unit, float tickMs) => 0;

    float GetDrinkPowerRegen(Unit unit, PowerType power, float tickMs) => 0;

    /// <summary>vmangos MOD_MANA_REGEN_INTERRUPT: percentage of spirit mana regen retained after a recent mana spend.</summary>
    float GetManaRegenInterruptPercent(Unit unit) => 0;
    float GetCombatHealthRegenPercent(Unit unit) => 0;

    float GetHealthRegenInCombat(Unit unit) => 0;

    float GetHealthRegenPercentFactor(Unit unit) => 1.0f;

    bool IsPolymorphed(Unit unit) => false;
}

/// <summary>
/// The combat settings and the links from combat down to the spell system for one world. The world daemon creates it
/// before the world thread starts (<see cref="GetOrCreate"/>) and its features fill in the links they own;
/// worlds without one use <see cref="Default"/> (retail rates, no links).
/// </summary>
public sealed class CombatEnvironment
{
    private static readonly ConditionalWeakTable<WorldRuntime, CombatEnvironment> s_registered = new();
    private readonly bool _frozen;
    private IPowerAuraSource? _auras;
    private IMeleeSpellHooks? _meleeSpells;
    private Func<Unit, uint, float, float>? _threatModifier;

    public CombatEnvironment(CombatOptions options, IPowerAuraSource? auras = null, IMeleeSpellHooks? meleeSpells = null,
        Func<Unit, uint, float, float>? threatModifier = null)
        : this(options, auras, meleeSpells, threatModifier, frozen: false)
    {
    }

    private CombatEnvironment(CombatOptions options, IPowerAuraSource? auras, IMeleeSpellHooks? meleeSpells,
        Func<Unit, uint, float, float>? threatModifier, bool frozen)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _auras = auras;
        _meleeSpells = meleeSpells;
        _threatModifier = threatModifier;
        _frozen = frozen;
    }

    /// <summary>Retail rates, no links. Shared and read-only.</summary>
    public static CombatEnvironment Default { get; } = new(new CombatOptions(), null, null, null, frozen: true);

    public CombatOptions Options { get; }

    /// <summary>Aura queries for the power economy (set by the power feature).</summary>
    public IPowerAuraSource? Auras
    {
        get => _auras;
        set
        {
            ThrowIfFrozen();
            _auras = value;
        }
    }

    /// <summary>The melee swing's link to casts (set by the melee spell feature).</summary>
    public IMeleeSpellHooks? MeleeSpells
    {
        get => _meleeSpells;
        set
        {
            ThrowIfFrozen();
            _meleeSpells = value;
        }
    }

    /// <summary>Optional school-scoped threat modifier supplied by the spell feature.</summary>
    public Func<Unit, uint, float, float>? ThreatModifier
    {
        get => _threatModifier;
        set
        {
            ThrowIfFrozen();
            _threatModifier = value;
        }
    }

    public static void Register(WorldRuntime world, CombatEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment._frozen)
        {
            throw new ArgumentException("the default environment cannot be registered", nameof(environment));
        }

        s_registered.AddOrUpdate(world, environment);
    }

    /// <summary>The environment registered for <paramref name="world"/>, else <see cref="Default"/>.</summary>
    public static CombatEnvironment For(WorldRuntime world)
        => s_registered.TryGetValue(world, out CombatEnvironment? environment) ? environment : Default;

    /// <summary>The registered environment of <paramref name="world"/>, creating and registering one from <paramref name="options"/> first.</summary>
    public static CombatEnvironment GetOrCreate(WorldRuntime world, Func<CombatOptions> options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        if (s_registered.TryGetValue(world, out CombatEnvironment? existing))
        {
            return existing;
        }

        var created = new CombatEnvironment(options());
        s_registered.Add(world, created);
        return created;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException("the default combat environment is read-only; register one for the world");
        }
    }

    internal bool HasAura(Unit unit, uint spellId, int effectIndex) => Auras?.HasAura(unit, spellId, effectIndex) ?? false;

    internal bool HasAuraType(Unit unit, AuraType type) => Auras?.HasAuraType(unit, type) ?? false;

    internal float GetPowerRegenFactor(Unit unit, PowerType power) => Auras?.GetPowerRegenFactor(unit, power) ?? 1.0f;

    internal float GetCombatHealthRegenPercent(Unit unit) => Auras?.GetCombatHealthRegenPercent(unit) ?? 0;

    internal float GetHealthRegenInCombat(Unit unit) => Auras?.GetHealthRegenInCombat(unit) ?? 0;

    internal float GetHealthRegenPercentFactor(Unit unit) => Auras?.GetHealthRegenPercentFactor(unit) ?? 1.0f;

    internal bool IsPolymorphed(Unit unit) => Auras?.IsPolymorphed(unit) ?? false;
}

/// <summary>The <see cref="IPowerAuraSource"/> backed by the world's <see cref="SpellSystem"/>.</summary>
public sealed class SpellSystemPowerAuras(SpellSystem spells) : IPowerAuraSource
{
    private readonly SpellSystem _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    public bool HasAura(Unit unit, uint spellId, int effectIndex)
    {
        ArgumentNullException.ThrowIfNull(unit);
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            if (!holder.IsRemoved && holder.Spell.Id == spellId && holder.Auras.Any(a => a is not null && a.EffectIndex == effectIndex))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasAuraType(Unit unit, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _spells.GetAuras(unit).Any(h => !h.IsRemoved && h.HasAura(type));
    }

    public float GetPowerRegenFactor(Unit unit, PowerType power)
    {
        ArgumentNullException.ThrowIfNull(unit);
        float factor = 1.0f;
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.ModPowerRegenPercent } && aura.MiscValue == (int)power)
                {
                    factor *= (aura.Amount + 100) / 100.0f;
                }
            }
        }

        return factor;
    }

    public float GetFoodHealthRegen(Unit unit, float tickMs)
    {
        float total = 0;
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            if (holder.IsRemoved) continue;
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.ModRegen })
                    total += aura.Amount * (tickMs / (aura.Amplitude > 0 ? aura.Amplitude : 5000.0f));
            }
        }
        return total;
    }

    public float GetDrinkPowerRegen(Unit unit, PowerType power, float tickMs)
    {
        float total = 0;
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            if (holder.IsRemoved) continue;
            foreach (SpellAura? aura in holder.Auras)
            {
                if (power == PowerType.Mana && aura is { Type: AuraType.ModPowerRegen, MiscValue: (int)PowerType.Mana })
                    total += aura.Amount * (tickMs / 5000.0f);
            }
        }
        return total;
    }

    public float GetManaRegenInterruptPercent(Unit unit)
        => Math.Min(100.0f, _spells.GetTotalAuraModifier(unit, AuraType.ModManaRegenInterrupt));

    public float GetCombatHealthRegenPercent(Unit unit)
        => _spells.GetTotalAuraModifier(unit, AuraType.ModRegenDuringCombat);

    public float GetHealthRegenInCombat(Unit unit)
        => _spells.GetTotalAuraModifier(unit, AuraType.ModHealthRegenInCombat);

    public float GetHealthRegenPercentFactor(Unit unit)
    {
        float factor = 1.0f;
        foreach (SpellAuraHolder holder in _spells.GetAuras(unit))
        {
            if (holder.IsRemoved) continue;
            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.ModHealthRegenPercent })
                    factor *= (aura.Amount + 100) / 100.0f;
            }
        }

        return factor;
    }

    public bool IsPolymorphed(Unit unit) => _spells.IsPolymorphed(unit);
}
