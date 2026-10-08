namespace ArcaneCore.Game.Entities;

public sealed partial class Player
{
    /// <summary>The race's faction template (vmangos Player::SetFactionForRace), restored when a charm or possession ends (Unit::RestoreFaction).</summary>
    internal uint RaceFactionTemplate => _raceFactionTemplate;
}
