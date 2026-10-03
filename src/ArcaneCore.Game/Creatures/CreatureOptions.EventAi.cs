namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>EventAI tuning (<c>Creatures:EventAi</c>).</summary>
    public EventAiOptions EventAi { get; } = new();
}
