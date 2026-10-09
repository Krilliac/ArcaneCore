using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload gameobject_template</c> (vmangos <c>HandleReloadGameObjectTemplatesCommand</c>, ServerCommands.cpp:1775-1790,
/// Chat.cpp:845 → <c>ObjectMgr::LoadGameObjectTemplates</c>, ObjectMgr.cpp:8140): the templates are read through
/// <see cref="IGameObjectDataStore"/> off the world thread and put into a copy of the live <see cref="GameObjectContent"/>
/// (spawns, locks and quest relations are not reloaded), which the commit swaps into the object system of every map
/// (<see cref="GameObjectLootFeature.ReplaceContent"/>). Every live object is rebound to the template of its entry, so what is read
/// through it from then on (data fields, loot id, lock id, ...) changes at once; the update fields filled when an object was created
/// (display id, flags, faction) change only when it is created again, as in vmangos, whose objects point at the record the loader overwrites.
/// <para>
/// Retail rules: <c>LoadGameObjectTemplates</c> returns before touching anything when the query is empty (ObjectMgr.cpp:8145-8146),
/// so an empty <c>gameobject_template</c> keeps the loaded templates, whatever <c>HotReload:EmptyTables</c> says; and it inserts or
/// overwrites the entries of its map without ever removing one (ObjectMgr.cpp:8148-8153), so a template whose row has left the table stays
/// loaded until the next restart (reported in the result). The vmangos <c>&lt;entry&gt;</c> argument (one template) is not supported.
/// <c>reload all</c> does not reach it (ServerCommands.cpp:885-905).
/// </para>
/// </summary>
public sealed class GameObjectContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "gameobject_template";

    public bool IncludedInAll => false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        GameObjectLootFeature feature = services.GetRequiredService<GameObjectLootFeature>();
        GameObjectContent fresh;
        using (IServiceScope scope = services.CreateScope())
        {
            IGameObjectDataStore store = scope.ServiceProvider.GetService<IGameObjectDataStore>()
                ?? throw new InvalidOperationException("no game object data store is registered");
            fresh = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        GameObjectTemplate[] rows = [.. fresh.Templates];
        if (rows.Length == 0)
        {
            return new Candidate(feature, null, 0, []);
        }

        // ObjectMgr.cpp:8148-8153 only inserts or overwrites: keep the templates the table no longer lists.
        GameObjectContent live = feature.Content;
        HashSet<uint> listed = [.. rows.Select(t => t.Entry)];
        GameObjectTemplate[] kept = [.. live.Templates.Where(t => !listed.Contains(t.Entry)).OrderBy(t => t.Entry)];
        // The spawns, their alternative entries and their spawn groups are not reloaded (as the spawns of the creature reload).
        var next = new GameObjectContent([.. rows, .. kept], live.Spawns, live.Locks, live.QuestStarters, live.QuestEnders, live.SpawnEntries)
        {
            SpawnGroups = live.SpawnGroups,
            Pools = live.Pools,
        };
        return new Candidate(feature, next, rows.Length, [.. kept.Select(t => t.Entry)]);
    }

    private sealed class Candidate(GameObjectLootFeature feature, GameObjectContent? content, int rows, IReadOnlyList<uint> retained) : ContentCandidate
    {
        public override string Summary => content is null ? "no game object templates" : $"{content.TemplateCount} game object templates ({rows} read from the table)";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            if (rows == 0)
            {
                // ObjectMgr.cpp:8145-8146: an empty result returns before the first template is touched.
                reason = $"gameobject_template is empty, {feature.Content.TemplateCount} game object templates stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            GameObjectContent? previous = null;
            transaction.Step("game object templates", () => previous = feature.ReplaceContent(content!), () => feature.ReplaceContent(previous!));
            if (retained.Count > 0)
            {
                transaction.Note($"{retained.Count} game object template(s) no longer in gameobject_template stay loaded until the next restart, as in vmangos (ObjectMgr.cpp:8148-8153): {string.Join(", ", retained.Take(20))}{(retained.Count > 20 ? ", ..." : string.Empty)}");
            }
        }
    }
}
