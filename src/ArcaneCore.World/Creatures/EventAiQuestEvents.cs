using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// The world's <see cref="IEventAiQuestEvents"/>: EventAI quest credit through the quest service of <see cref="QuestNpcFeature"/> (resolved at
/// the first call, because the creature feature attaches first). Group credit follows vmangos RewardPlayerAndGroupAtEventExplored /
/// RewardPlayerAndGroupAtEventCredit (Player.cpp): every group member on the source's map within the group reward distance, as the kill
/// rewards do (<see cref="KillRewards.Recipients"/>); a ghost gets no quest credit.
/// </summary>
public sealed class EventAiQuestEvents(IServiceProvider services) : IEventAiQuestEvents
{
    private readonly Lazy<Func<Player, RewardGroup?>> _groups = new(() => RewardGroups.Resolver(services));

    private float RewardDistance => services.GetService<ProgressionFeature>()?.Progression.Options.GroupXpDistance ?? new ProgressionOptions().GroupXpDistance;

    public void EventHappened(Player player, uint questId, Creature source, bool rewardGroup)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(source);
        if (services.GetService<QuestNpcFeature>()?.Services is not { } quests)
        {
            return;
        }

        IReadOnlyList<Player> recipients = rewardGroup ? KillRewards.Recipients(player, source, _groups.Value(player), RewardDistance) : [player];
        foreach (Player member in recipients)
        {
            if (KillRewards.CanReceiveQuestCredit(member))
            {
                quests.AreaExploredOrEventHappens(member, questId);
            }
        }
    }

    public void KillCredit(Player player, uint creatureEntry, Creature source)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(source);
        if (services.GetService<QuestNpcFeature>()?.Services is not { } quests)
        {
            return;
        }

        RewardGroup? group = _groups.Value(player);
        foreach (Player member in KillRewards.Recipients(player, source, group, RewardDistance))
        {
            if (KillRewards.CanReceiveQuestCredit(member))
            {
                quests.KilledMonsterCredit(member, creatureEntry, source.Guid, group?.IsRaid ?? false);
            }
        }
    }
}
