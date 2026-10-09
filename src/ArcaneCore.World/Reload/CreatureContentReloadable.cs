using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload creature_template</c> (vmangos <c>HandleReloadCreatureTemplatesCommand</c>,
/// ServerCommands.cpp:1758-1773, Chat.cpp:830 → <c>ObjectMgr::LoadCreatureTemplates</c>, ObjectMgr.cpp:1190):
/// the creature content is read off the world thread, and its definitions (templates, model infos,
/// addons, waypoints, EventAI) replace the live ones inside the content object everybody already
/// holds (<see cref="CreatureContent.SwapDefinitions"/>). A creature reads
/// <see cref="Game.Creatures.Creature.Template"/> through that object, so a running creature sees the new
/// template at once and takes its unit fields from it at the next respawn; queries and grids loaded
/// later use it too.
/// <list type="bullet">
/// <item>An empty template table keeps what is loaded: vmangos returns before touching the map
/// (ObjectMgr.cpp:1192-1196).</item>
/// <item>The spawn table (<c>creature</c>) is not reloaded: it is what the world was started with.
/// vmangos reloads it with a different command (<c>LoadCreatures(true)</c>, ObjectMgr.cpp:2319), not
/// carried over yet. A spawn whose template disappeared stays in the world with its last known
/// template; the reload reports how many.</item>
/// <item>A world that started with no creature data at all (the shared empty content) takes the whole
/// content, spawns included, through <see cref="CreatureWorldFeature.Install"/>.</item>
/// <item>The optional <c>&lt;entry&gt;</c> argument of the vmangos command (one template) is not supported;
/// the whole table is reloaded.</item>
/// </list>
/// </summary>
public sealed class CreatureContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "creature_template";

    /// <summary>vmangos reload all (ServerCommands.cpp:885-905) reaches no creature_template: all_npc reloads gossip, trainer, vendor and POI only (:925-933).</summary>
    public bool IncludedInAll => false;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        CreatureWorldFeature feature = services.GetRequiredService<CreatureWorldFeature>();
        using IServiceScope scope = services.CreateScope();
        ICreatureDataStore store = scope.ServiceProvider.GetService<ICreatureDataStore>()
            ?? throw new InvalidOperationException("no creature data store is registered");
        CreatureContent fresh = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new CreatureCandidate(feature, fresh);
    }

    private sealed class CreatureCandidate(CreatureWorldFeature feature, CreatureContent fresh) : ContentCandidate
    {
        public override string Summary => $"{fresh.TemplateCount} creature templates";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = feature.Content.TemplateCount;
            if (fresh.TemplateCount == 0 && loaded > 0)
            {
                reason = $"creature_template is empty (vmangos ObjectMgr.cpp:1192-1196 returns early too), {loaded} templates stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            CreatureContent current = feature.Content;
            if (ReferenceEquals(current, CreatureContent.Empty))
            {
                transaction.Step("creature content", () => feature.Install(fresh), () => feature.Install(CreatureContent.Empty));
                return;
            }

            (int orphaned, string entries) = OrphanedSpawns(current);
            CreatureDefinitions? previous = null;
            transaction.Step("creature definitions", () => previous = current.SwapDefinitions(fresh), () => current.RestoreDefinitions(previous!));
            if (orphaned > 0)
            {
                transaction.Note($"{orphaned} spawn(s) use creature_template entries that are no longer defined ({entries}); they keep their last known template.");
            }
        }

        // A spawn with creature_spawn_entry rows is orphaned only when none of its entries has a template any more; an entry-0 member of a
        // spawn group only when none of the group's spawn_group_entry rows has one.
        private bool IsOrphaned(CreatureContent current, CreatureSpawn spawn)
            => current.GetSpawnEntries(spawn.Guid) is { Count: > 0 } entries
                ? entries.All(entry => fresh.FindTemplate(entry) is null)
                : spawn.Entry == 0 && current.SpawnGroups.GroupOf(SpawnGroupType.Creature, spawn.Guid) is { RandomEntries.Count: > 0 } group
                    ? group.RandomEntries.All(entry => fresh.FindTemplate(entry.Entry) is null)
                    : fresh.FindTemplate(spawn.Entry) is null;

        private (int Spawns, string Entries) OrphanedSpawns(CreatureContent current)
        {
            var entries = new SortedSet<uint>();
            int count = 0;
            foreach (uint mapId in current.MapsWithSpawns)
            {
                foreach (CreatureSpawn spawn in current.GetSpawns(mapId).Where(spawn => IsOrphaned(current, spawn)))
                {
                    count++;
                    entries.Add(spawn.Entry);
                }
            }

            string shown = string.Join(", ", entries.Take(10));
            return (count, entries.Count > 10 ? shown + ", ..." : shown);
        }
    }
}
