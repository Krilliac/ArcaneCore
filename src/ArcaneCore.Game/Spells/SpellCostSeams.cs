namespace ArcaneCore.Game.Spells;

/// <summary>
/// Takes a non-power cost of a completed cast (crafting lane seam). <see cref="SpellSystem"/> calls every registered taker once per
/// completed cast right after the power is spent and before the ranged ammunition and the effects, which is the vmangos order
/// <c>TakePower, TakeReagents, TakeAmmo</c> then <c>HandleEffects</c> (Spells/Spell.cpp:3716-3718: "remove reagents before
/// HandleEffects so a created item can use the freed slot"). It is not called when a check fails. Callbacks run on the world thread.
/// </summary>
public interface ISpellCostTaker
{
    /// <summary>Consume this taker's cost of <paramref name="cast"/> (the checks already proved it can be paid).</summary>
    void TakeCost(SpellCast cast);
}

public sealed partial class SpellSystem
{
    private ISpellCostTaker[] _costTakers = [];

    /// <summary>The registered cost takers in registration order.</summary>
    public IReadOnlyList<ISpellCostTaker> CostTakers => _costTakers;

    /// <summary>Add a cost taker (copy-on-write, startup only). A duplicate registration throws.</summary>
    public void RegisterCostTaker(ISpellCostTaker taker)
    {
        ArgumentNullException.ThrowIfNull(taker);
        if (_costTakers.Contains(taker))
        {
            throw new InvalidOperationException("this cost taker is already registered");
        }

        _costTakers = [.. _costTakers, taker];
    }

    private void TakeCosts(SpellCast cast)
    {
        foreach (ISpellCostTaker taker in _costTakers)
        {
            taker.TakeCost(cast);
        }
    }
}
