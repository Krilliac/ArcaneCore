using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>
/// Subscribes the quest service to <see cref="IReputationChangeSource"/> when the reputation owner registers one, so a
/// reputation objective completes or reverts the moment the standing crosses it (vmangos Player::ReputationChanged).
/// Without a registered source (the reputation lane has not raised the event yet) nothing is subscribed, one line says so,
/// and reputation objectives are evaluated only when the quest is accepted or the character logs in.
/// </summary>
public sealed class QuestReputationBinding(IServiceProvider services, ILogger<QuestReputationBinding> logger) : IWorldFeature
{
    private ReputationObjectiveSubscription? _subscription;

    public void Attach(WorldRuntime world)
    {
        IReputationChangeSource? source = services.GetService<IReputationChangeSource>();
        if (source is null)
        {
            logger.LogInformation("No reputation change source is registered: reputation-objective quests refresh only on accept and login");
            return;
        }

        QuestNpcFeature quests = services.GetRequiredService<QuestNpcFeature>();
        _subscription = new ReputationObjectiveSubscription(source, () => quests.Services);
    }

    public Task StopAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
