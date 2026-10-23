namespace ArcaneCore.Game.Reputation;

/// <summary>World rates (vmangos Rate.Reputation.Gain, Rate.Reputation.LowLevel.Kill).</summary>
public sealed class ReputationRates
{
    public float Gain { get; set; } = 1f;

    public float LowLevelKill { get; set; } = 0.2f;
}
