namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Combat</c>: bot fight tactics that are bot policy, not game rules (docs/areas/playbots-combat.md).
/// <see cref="Kite"/> makes a hurt ranged bot (or a warrior or rogue with a ranged weapon) slow the creature on it and back
/// away to casting range. Every key is live: <c>.reload config</c> changes the shared object in place and running bots use the new value at their next decision.
/// </summary>
public sealed class PlayerbotCombatOptions
{
    /// <summary>Kiting (<c>World:Playerbots:Combat:Kite</c>).</summary>
    public PlayerbotKiteOptions Kite { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Combat";
        if (Kite is null) throw new InvalidOperationException($"{section}:Kite is missing.");
        Kite.Validate(section + ":Kite");
    }
}

/// <summary><c>World:Playerbots:Combat:Kite</c>: a hurt bot that can attack from range slows its pursuer and backs away.</summary>
public sealed class PlayerbotKiteOptions
{
    /// <summary>Kite at all (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A bot kites once its health falls to this percentage (10..95).</summary>
    public float HealthPct { get; set; } = 50f;

    /// <summary>The longest single step away from the pursuer, in yards (4..20).</summary>
    public float StepYards { get; set; } = 10f;

    /// <summary>Steps one fight may use before the bot stops kiting and fights on (1..32).</summary>
    public int MaxStepsPerFight { get; set; } = 8;

    internal void Validate(string section)
    {
        if (PlayerbotRiskOptions.Check(HealthPct, 10, 95) is { } health) throw new InvalidOperationException($"{section}: HealthPct {health}");
        if (PlayerbotRiskOptions.Check(StepYards, 4, 20) is { } step) throw new InvalidOperationException($"{section}: StepYards {step}");
        if (MaxStepsPerFight is < 1 or > 32) throw new InvalidOperationException($"{section}: MaxStepsPerFight must be 1..32.");
    }
}
