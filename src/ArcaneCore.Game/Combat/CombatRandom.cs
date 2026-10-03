namespace ArcaneCore.Game.Combat;

/// <summary>Random source for combat rolls; tests substitute a scripted one.</summary>
public interface ICombatRandom
{
    /// <summary>Uniform integer in [min, max] inclusive (vmangos urand).</summary>
    int Next(int minInclusive, int maxInclusive);

    /// <summary>Uniform float in [min, max] (vmangos frand).</summary>
    float NextFloat(float min, float max);
}

/// <summary>The default <see cref="ICombatRandom"/>, backed by <see cref="Random.Shared"/>.</summary>
public sealed class SharedCombatRandom : ICombatRandom
{
    public static readonly SharedCombatRandom Instance = new();

    public int Next(int minInclusive, int maxInclusive) => Random.Shared.Next(minInclusive, maxInclusive + 1);

    public float NextFloat(float min, float max) => min + (float)(Random.Shared.NextDouble() * (max - min));
}
