using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload quest_template</c> (vmangos <c>HandleReloadQuestTemplateCommand</c>, ServerCommands.cpp:1145-1156,
/// Chat.cpp:884 → <c>ObjectMgr::LoadQuests</c>, ObjectMgr.cpp:5523), reached by <c>reload all</c> through
/// <c>all_quest</c> (ServerCommands.cpp:935-944, which also reloads the quest relations).
/// <para>
/// The quest templates and the four relation tables (<c>creature_questrelation</c>,
/// <c>creature_involvedrelation</c>, <c>gameobject_questrelation</c>, <c>gameobject_involvedrelation</c>) are one
/// <see cref="QuestContent"/> and one immutable <see cref="QuestStore"/> here, whose chains and exclusive groups are
/// derived from the whole set, so the one reload reads them together (a structural difference, see docs/areas/hot-reload.md).
/// The store is built off the world thread and swapped into the live <see cref="ArcaneCore.Game.Npc.QuestNpcServices"/>, so every
/// online player's journal, every quest giver and every query sees the new templates at its next lookup by id; nothing
/// that holds a quest id has to be re-wired (journals keep ids, not templates).
/// </para>
/// <para>
/// Retail rules encoded: the relation tables are cleared and re-read (ObjectMgr.cpp:9174); an empty
/// <c>quest_template</c> result returns before anything is touched (ObjectMgr.cpp:5568-5577), so the loaded quests stay,
/// whatever <c>HotReload:EmptyTables</c> says; and a template whose row has left the table is not erased, because
/// <c>LoadQuests</c> only inserts or overwrites entries of its map (ObjectMgr.cpp:5590-5594) and never removes one, so such
/// a quest stays loaded until the next restart (reported in the result).
/// </para>
/// </summary>
public sealed class QuestContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "quest_template";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        QuestNpcFeature feature = services.GetRequiredService<QuestNpcFeature>();
        QuestContent fresh;
        using (IServiceScope scope = services.CreateScope())
        {
            IQuestContentStore store = scope.ServiceProvider.GetService<IQuestContentStore>()
                ?? throw new InvalidOperationException("no quest content store is registered");
            fresh = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        uint[] duplicates = [.. fresh.Templates.GroupBy(t => t.Entry).Where(g => g.Count() > 1).Select(g => g.Key).Order()];
        if (fresh.Templates.Count == 0 || duplicates.Length > 0)
        {
            return new QuestCandidate(feature, null, fresh.Templates.Count, [], duplicates);
        }

        // vmangos never erases a loaded template (ObjectMgr.cpp:5590): keep the ones the table no longer lists.
        QuestStore current = feature.Services.Quests;
        HashSet<uint> listed = [.. fresh.Templates.Select(t => t.Entry)];
        QuestTemplate[] kept = [.. current.Templates.Where(t => !listed.Contains(t.Entry)).OrderBy(t => t.Entry)];
        var merged = fresh with { Templates = [.. fresh.Templates, .. kept] };
        return new QuestCandidate(feature, new QuestStore(merged), fresh.Templates.Count, [.. kept.Select(t => t.Entry)], duplicates);
    }

    private sealed class QuestCandidate(QuestNpcFeature feature, QuestStore? store, int rows, IReadOnlyList<uint> retained, IReadOnlyList<uint> duplicates) : ContentCandidate
    {
        public override string Summary => store is null ? "no quest templates" : $"{store.Count} quest templates ({rows} read from the table)";

        public override IReadOnlyList<string> Validate()
            => duplicates.Count == 0 ? [] : [$"quest_template lists entries more than once: {string.Join(", ", duplicates)}"];

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            if (rows == 0)
            {
                // ObjectMgr.cpp:5568-5577: an empty result returns before the first template is touched.
                reason = $"quest_template is empty, {feature.Services.Quests.Count} quest templates stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            QuestStore? previous = null;
            transaction.Step("quest store", () => previous = feature.Services.ReplaceQuests(store!), () => feature.Services.ReplaceQuests(previous!));
            if (retained.Count > 0)
            {
                transaction.Note($"{retained.Count} quest(s) no longer in quest_template stay loaded until the next restart, as in vmangos (ObjectMgr.cpp:5590): {string.Join(", ", retained.Take(20))}{(retained.Count > 20 ? ", ..." : string.Empty)}");
            }
        }
    }
}
