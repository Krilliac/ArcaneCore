namespace ArcaneCore.World.Playerbots;

/// <summary>The brain's view of its auction house visits for tests and inspection.</summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>The bot's auction house visits (tests, inspection).</summary>
    internal PlayerbotEconomy Economy => _economy;

    /// <summary>The one-line economy report of the inspect command (<see cref="PlayerbotEconomy.Report"/>).</summary>
    internal string EconomyReport => _economy.Report(_session.World.NowMs);
}
