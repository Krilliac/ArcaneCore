using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Quests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reputation;

public sealed partial class ReputationFeature
{
    private const int MaxContentWarnings = 40;

    /// <summary>
    /// One startup line saying whether reputation is ACTIVE or INACTIVE and why (a world without Faction.dbc silently ran without
    /// reputation before), then the quest columns that name factions missing from Faction.dbc (ObjectMgr.cpp:5702-5750, 6026-6039).
    /// The combat reactions report their own state (<see cref="ReputationCombatFeature"/>).
    /// </summary>
    private void ReportStatus(ReputationService service)
    {
        if (service.Factions.Count == 0)
        {
            _logger.LogWarning(
                "Reputation INACTIVE: no Faction.dbc is loaded (set Reputation:FactionDbcPath). The reputation pane stays empty, kill and quest reputation is not recorded, " +
                "and item, vendor and quest rank gates, spillover and combat reactions stay off");
            return;
        }

        _logger.LogInformation(
            "Reputation ACTIVE: {Factions} factions, spillover {Spillover}, item rank gating on, forced peace compares the {Standing}",
            service.Factions.Count, service.SpilloverEnabled ? "on" : "off (Reputation:SpilloverEnabled)",
            service.PeaceForcedUsesEffectiveStanding ? "effective rank (Reputation:PeaceForcedUsesEffectiveStanding)" : "relative standing (retail)");

        using IServiceScope scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<IQuestContentStore>() is not { } questStore)
        {
            return;
        }

        IReadOnlyList<string> warnings = ReputationContentValidator.ValidateQuests(questStore.LoadAsync().GetAwaiter().GetResult().Templates, service.Factions);
        foreach (string warning in warnings.Take(MaxContentWarnings))
        {
            _logger.LogWarning("Reputation content: {Warning}", warning);
        }

        if (warnings.Count > MaxContentWarnings)
        {
            _logger.LogWarning("Reputation content: {Omitted} more quest reputation problems not listed", warnings.Count - MaxContentWarnings);
        }
    }
}
