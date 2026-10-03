using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// Raised by the reputation owner after a standing changed, on the world thread, so quests with a reputation
/// objective (quest_template.RepObjectiveFaction, vmangos Player::ReputationChanged, Player.cpp:14239-14264) can complete
/// or revert. The quest side only consumes this seam; ReputationService belongs to the reputation lane, which raises it
/// from its change path (<c>ApplyAndNotify</c>) once it registers an implementation in the service container.
/// Until a source is registered the subscription is inert and reputation objectives refresh only on login and accept.
/// </summary>
public interface IReputationChangeSource
{
    /// <summary>The player's standing with the faction (Faction.dbc id) changed.</summary>
    event Action<Player, uint>? ReputationChanged;
}

/// <summary>
/// Connects an <see cref="IReputationChangeSource"/> to <see cref="Npc.QuestNpcServices.ReputationChanged"/>. The services
/// are resolved per event because the world feature replaces its instance once the content is loaded.
/// </summary>
public sealed class ReputationObjectiveSubscription : IDisposable
{
    private readonly IReputationChangeSource _source;
    private readonly Func<Npc.QuestNpcServices> _services;

    public ReputationObjectiveSubscription(IReputationChangeSource source, Func<Npc.QuestNpcServices> services)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(services);
        _source = source;
        _services = services;
        _source.ReputationChanged += OnChanged;
    }

    private void OnChanged(Player player, uint factionId) => _services().ReputationChanged(player, factionId);

    public void Dispose() => _source.ReputationChanged -= OnChanged;
}
