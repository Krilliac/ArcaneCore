using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

public sealed partial class PlayerReputation
{
    /// <summary>
    /// ReputationMgr::SetReputation(faction, standing, incremental, noSpillover) (vmangos ReputationMgr.cpp:211-243).
    /// The spillover template is applied first: every target whose current rank is at most the template's rank
    /// receives <c>(int)(standing * rate)</c> through SetOneFactionReputation, without a client update of its own
    /// (its pending send rides on the main faction's). With <paramref name="incremental"/> false the spillover uses
    /// the absolute value times the rate, exactly as vmangos does. Returns whether the main faction changed (it needs
    /// a state); <paramref name="spilled"/> tells whether any spillover target changed, because vmangos applies the
    /// spillover even when the main faction has no state.
    /// </summary>
    public bool ApplyWithSpillover(FactionRecord faction, int standing, bool incremental, bool noSpillover,
        ReputationContent content, out bool spilled)
    {
        ArgumentNullException.ThrowIfNull(faction);
        ArgumentNullException.ThrowIfNull(content);
        spilled = false;
        if (!noSpillover && content.Spillover(faction.Id) is { } template)
        {
            foreach (ReputationSpillover target in template.Targets)
            {
                if (target.Faction == 0 || Factions.Find(target.Faction) is not { } spill)
                {
                    continue;
                }

                if ((int)Rank(spill) <= target.MaxRank)
                {
                    int spilloverRep = (int)(standing * target.Rate);
                    spilled |= Apply(spill, spilloverRep, incremental);
                }
            }
        }

        return Apply(faction, standing, incremental);
    }
}
