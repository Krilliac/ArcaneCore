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

    /// <summary>
    /// vmangos World::setConfigPos (World.cpp:2959-2967): Rate.Mana and Rate.Rage.Loss cannot be negative and fall
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
}

/// <summary>The options and aura source of one world, registered by the world daemon before the world thread starts.</summary>
public sealed class PowerEnvironment(CombatOptions options, IPowerAuraSource? auras)
{
    private static readonly ConditionalWeakTable<WorldRuntime, PowerEnvironment> s_registered = new();

    /// <summary>Retail rates, no aura source.</summary>
    public static PowerEnvironment Default { get; } = new(new CombatOptions(), null);

    public CombatOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public IPowerAuraSource? Auras { get; } = auras;

    public static void Register(WorldRuntime world, PowerEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(environment);
        s_registered.AddOrUpdate(world, environment);
    }

    public static PowerEnvironment For(WorldRuntime world)
        => s_registered.TryGetValue(world, out PowerEnvironment? environment) ? environment : Default;

    internal bool HasAura(Unit unit, uint spellId, int effectIndex) => Auras?.HasAura(unit, spellId, effectIndex) ?? false;

    internal bool HasAuraType(Unit unit, AuraType type) => Auras?.HasAuraType(unit, type) ?? false;

    internal float GetPowerRegenFactor(Unit unit, PowerType power) => Auras?.GetPowerRegenFactor(unit, power) ?? 1.0f;
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
}
