using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// What a quest needs beyond the ordinary kill/collect/talk machinery before this server may offer or reward it.
/// One member per behaviour a template can demand; the set is a pure function of the template
/// (<see cref="QuestNeeds.Of"/>). A need that no adapter provides withholds the quest (fail closed, listed at startup):
/// the alternative, rewarding a quest without the behaviour retail attaches to it, would be a silent deviation.
/// </summary>
[Flags]
public enum QuestAdapter
{
    None = 0,

    /// <summary>quest_template.Method 0 (vmangos QUEST_METHOD_AUTOCOMPLETE, QuestDef.h:177-181): turned in without objectives.</summary>
    Autocomplete = 1 << 0,

    /// <summary>QUEST_FLAGS_PARTY_ACCEPT: every eligible party member is offered a confirmation box (QuestHandler.cpp:166-191).</summary>
    PartyAccept = 1 << 1,

    /// <summary>quest_template.SrcSpell: the spell is cast on accept (QuestHandler.cpp:193-194).</summary>
    SrcSpell = 1 << 2,

    /// <summary>ReqSourceId/ReqSourceCount: loot-source counters.</summary>
    ReqSource = 1 << 3,

    /// <summary>Type 41 (QUEST_TYPE_PVP): accepting activates the PvP flag (Player.cpp:12866).</summary>
    PvpType = 1 << 4,

    /// <summary>RepObjectiveFaction: completes when the standing reaches the value; needs the reputation owner.</summary>
    RepObjective = 1 << 5,

    /// <summary>SpecialFlags EXPLORATION_OR_EVENT: only an area trigger, a quest-complete spell or a script credits it.</summary>
    EventCredit = 1 << 6,

    /// <summary>RewMailTemplateId: the reward mail (Player.cpp:13145-13163).</summary>
    Mail = 1 << 7,

    /// <summary>QUEST_FLAGS_AUTO_REWARDED: rewarded on completion and never shown in the log (QuestDef.h:158).</summary>
    AutoRewarded = 1 << 8,
}

/// <summary>Quest settlement modes for <see cref="QuestNpcOptions.RewardMode"/>.</summary>
public enum QuestRewardMode
{
    /// <summary>Retail: every quest whose needs are all provided is rewardable (vmangos has no allowlist).</summary>
    AllSupported = 0,

    /// <summary>Previous behaviour: only <see cref="QuestNpcOptions.OrdinaryRewardQuestIds"/> are rewardable.</summary>
    AllowlistOnly = 1,
}

/// <summary>The classifier: which adapters a quest template needs. Pure; no state, no configuration.</summary>
public static class QuestNeeds
{
    /// <summary>QUEST_TYPE_PVP (vmangos QuestDef.h:137).</summary>
    public const uint PvpType = 41;

    /// <summary>
    /// QuestFlags 0x1 (STAY_ALIVE) and 0x4 (EXPLORATION) are "Not used currently" in vmangos (QuestDef.h:150-152) and
    /// quest types 82, 83, 84 (world event, legendary, escort) only select a client icon, so none of them is a need.
    /// Only type 41 has a server effect (Player.cpp:12867).
    /// </summary>
    public static QuestAdapter Of(Quest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        QuestAdapter needs = QuestAdapter.None;
        var t = quest.Template;
        if (quest.IsAutoComplete)
        {
            needs |= QuestAdapter.Autocomplete;
        }

        if (quest.HasFlag(QuestFlags.PartyAccept))
        {
            needs |= QuestAdapter.PartyAccept;
        }

        if (quest.HasFlag(QuestFlags.AutoRewarded))
        {
            needs |= QuestAdapter.AutoRewarded;
        }

        if (t.SrcSpell != 0)
        {
            needs |= QuestAdapter.SrcSpell;
        }

        if (quest.ReqSourceId.Any(id => id != 0) || quest.ReqSourceCount.Any(count => count != 0))
        {
            needs |= QuestAdapter.ReqSource;
        }

        if (t.Type == PvpType)
        {
            needs |= QuestAdapter.PvpType;
        }

        if (t.RepObjectiveFaction != 0)
        {
            needs |= QuestAdapter.RepObjective;
        }

        if (quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent))
        {
            needs |= QuestAdapter.EventCredit;
        }

        if (t.RewMailTemplateId != 0)
        {
            needs |= QuestAdapter.Mail;
        }

        return needs;
    }
}

/// <summary>
/// A feature that provides one or more <see cref="QuestAdapter"/> behaviours. Discovered by reflection over this
/// assembly (public, concrete, parameterless constructor), ordered by full type name; two modules providing the
/// same adapter fail the first support query (the startup summary), because one of them would silently win.
/// </summary>
public interface IQuestAdapterModule
{
    /// <summary>The adapters this module delivers for <paramref name="services"/> (may depend on its dependencies).</summary>
    QuestAdapter Provides(QuestNpcServices services);

    /// <summary>Event/exploration quests this module credits (their <see cref="QuestAdapter.EventCredit"/> need is met).</summary>
    IEnumerable<uint> EventQuestsCovered(QuestNpcServices services) => [];
}

/// <summary>The startup report of what is withheld and why (counts of active quests per missing adapter).</summary>
public sealed record QuestSupportSummary(int ActiveQuests, int Supported, IReadOnlyDictionary<QuestAdapter, int> WithheldByReason)
{
    public int Withheld => ActiveQuests - Supported;
}

public sealed partial class QuestNpcServices
{
    private static readonly Lazy<IReadOnlyList<IQuestAdapterModule>> s_adapterModules = new(DiscoverAdapterModules);

    private (QuestAdapter Provided, HashSet<uint> EventCovered)? _support;

    /// <summary>The adapters in place: the reputation owner for reputation objectives plus every discovered module.</summary>
    public QuestAdapter ProvidedAdapters => Support.Provided;

    private (QuestAdapter Provided, HashSet<uint> EventCovered) Support => _support ??= ComputeSupport();

    private (QuestAdapter, HashSet<uint>) ComputeSupport() => MergeProviders(s_adapterModules.Value);

    /// <summary>
    /// The union of what <paramref name="modules"/> provide plus the reputation owner's adapter. Two providers of one
    /// adapter throw: whichever came second would silently win.
    /// </summary>
    public (QuestAdapter Provided, HashSet<uint> EventCovered) MergeProviders(IEnumerable<IQuestAdapterModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        // ReqSource has no behaviour beyond loot visibility (vmangos reads it only in Player::HasQuestForItem,
        // Player.cpp:14298-14316), which QuestNpcServices.ItemNeeds.cs implements; it is provided natively.
        QuestAdapter provided = QuestAdapter.ReqSource | (Deps.Reputation is not null ? QuestAdapter.RepObjective : QuestAdapter.None);
        var owners = new Dictionary<QuestAdapter, string>();
        var covered = new HashSet<uint>();
        foreach (IQuestAdapterModule module in modules)
        {
            QuestAdapter mine = module.Provides(this);
            foreach (QuestAdapter flag in Enum.GetValues<QuestAdapter>().Where(f => f != QuestAdapter.None && (mine & f) != 0))
            {
                if (!owners.TryAdd(flag, module.GetType().FullName!))
                {
                    throw new InvalidOperationException(
                        $"quest adapter {flag} is provided by both {owners[flag]} and {module.GetType().FullName}");
                }
            }

            provided |= mine;
            covered.UnionWith(module.EventQuestsCovered(this));
        }

        return (provided, covered);
    }

    private static IReadOnlyList<IQuestAdapterModule> DiscoverAdapterModules()
        => [.. typeof(QuestNpcServices).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(IQuestAdapterModule).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (IQuestAdapterModule)Activator.CreateInstance(t)!)];

    /// <summary>The adapters this quest still lacks (empty when every need is provided).</summary>
    public QuestAdapter MissingAdapters(Quest quest)
    {
        (QuestAdapter provided, HashSet<uint> covered) = Support;
        if (HasAreaTrigger(quest.Id) || covered.Contains(quest.Id))
        {
            provided |= QuestAdapter.EventCredit;
        }

        return QuestNeeds.Of(quest) & ~provided;
    }

    /// <summary>Every behaviour the quest needs is delivered, so it may be accepted and abandoned.</summary>
    public bool Supported(Quest quest) => MissingAdapters(quest) == QuestAdapter.None;

    /// <summary>Per-reason counts of the active quests this server withholds; the first call also validates the adapter modules.</summary>
    public QuestSupportSummary SupportSummary()
    {
        var byReason = new Dictionary<QuestAdapter, int>();
        int active = 0;
        int supported = 0;
        foreach (Quest quest in Quests.All.Where(q => q.IsActive))
        {
            active++;
            QuestAdapter missing = MissingAdapters(quest);
            if (missing == QuestAdapter.None)
            {
                supported++;
                continue;
            }

            foreach (QuestAdapter flag in Enum.GetValues<QuestAdapter>().Where(f => f != QuestAdapter.None && (missing & f) != 0))
            {
                byReason[flag] = byReason.GetValueOrDefault(flag) + 1;
            }
        }

        return new QuestSupportSummary(active, supported, byReason);
    }

    // Accept, abandon and the area-trigger credit use the support gate: a quest whose behaviour is not implemented is
    // withheld instead of half-working.
    private bool AcceptableQuest(Quest quest) => Supported(quest);

    /// <summary>
    /// Ordinary settlement: Quests:RewardMode AllSupported (default, retail: vmangos Player::CanRewardQuest has no
    /// allowlist) or the old allowlist; then the quest must be active and supported, and every requirement and reward
    /// it carries must have an adapter (XP needs <see cref="IQuestExperience"/>, reward spells need
    /// <see cref="IQuestRewardEffects"/>, reputation gates and rewards need a reputation owner).
    /// </summary>
    private bool SupportedRewardQuest(Quest quest) => (Options.RewardMode == QuestRewardMode.AllSupported
            || Options.OrdinaryRewardQuestIds.Contains(quest.Id))
        && quest.IsActive && AcceptableQuest(quest)
        && ((quest.Template.RequiredMinRepFaction == 0 && quest.Template.RequiredMaxRepFaction == 0) || Deps.Reputation is not null)
        && ((quest.Template.RewXP == 0 && quest.Template.RewMoneyMaxLevel == 0) || Deps.Experience is IQuestExperience)
        && (RewardSpell(quest) == 0 || Deps.RewardEffects?.CanCastRewardSpell(RewardSpell(quest)) == true)
        && (!HasReputationReward(quest) || Deps.ReputationRewards is not null)
        && CoherentObjectives(quest)
        && CoherentRewards(quest.RewItemId, quest.RewItemCount, dense: false)
        && CoherentRewards(quest.RewChoiceItemId, quest.RewChoiceItemCount, dense: true);
}
