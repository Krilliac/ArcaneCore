namespace ArcaneCore.Game.Reputation;

public sealed partial class PlayerReputation
{
    /// <summary>
    /// The factions whose standing was set since the last call, in order (the main faction and every spillover target):
    /// vmangos calls Player::ReputationChanged from SetOneFactionReputation for each of them (ReputationMgr.cpp:269).
    /// </summary>
    public IReadOnlyList<uint> TakeChangedFactions()
    {
        uint[] taken = [.. _changed];
        _changed.Clear();
        return taken;
    }
}
