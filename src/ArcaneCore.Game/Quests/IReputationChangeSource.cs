using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// Raised by the reputation owner after a standing changed, on the world thread, so quests with a reputation
/// objective (quest_template.RepObjectiveFaction, vmangos Player::ReputationChanged, Player.cpp:14239-14264) can complete
/// or revert. In production the reputation feature does not go through this seam: it calls
/// <c>QuestNpcServices.ReputationChanged</c> directly (<c>ReputationFeature.OnReputationChanged</c>), so objectives complete
/// and revert as the standing crosses them. The seam remains for a source that wants to raise the event through a subscription
/// (<c>ReputationObjectiveSubscription</c>, used by <c>QuestReputationObjectiveTests</c>); nothing in the server resolves it.
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
