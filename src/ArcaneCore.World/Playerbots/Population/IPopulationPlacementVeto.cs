namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The seam for a feature that decides where a bot lives (the bot-home lane's home area). The population reads every registered
/// implementation (<c>services.GetServices&lt;IPopulationPlacementVeto&gt;()</c>) before it relocates an out-levelled bot, and never
/// relocates a bot one of them owns. There is no implementation in this lane; the home-area feature (or the integrator) registers one.
/// The population still parks and restarts such a bot, and still places a bot it creates.
/// <para>Thread affinity: called on the world thread.</para>
/// </summary>
public interface IPopulationPlacementVeto
{
    /// <summary>Whether this feature owns the placement of the bot, so the population must not move it.</summary>
    bool OwnsPlacement(Guid botId);
}
