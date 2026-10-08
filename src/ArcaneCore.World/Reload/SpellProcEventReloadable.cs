using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.World.Spells.Procs;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload spell_proc_event</c> (vmangos <c>HandleReloadSpellProcEventCommand</c>, ServerCommands.cpp:1377-1383, Chat.cpp:900 →
/// <c>SpellMgr::LoadSpellProcEvents</c>): the table is read off the world thread and its rows replace the live ones. vmangos' all_spell reaches it
/// (ServerCommands.cpp:976); here it joins <c>all</c> only when a SQL store is registered, like spell_proc_item_enchant.
/// </summary>
public sealed class SpellProcEventReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_proc_event";

    public bool IncludedInAll => services.GetService<IServiceProviderIsService>()?.IsService(typeof(ISpellProcEventDataStore)) == true;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        SpellProcFeature feature = services.GetRequiredService<SpellProcFeature>();
        using IServiceScope scope = services.CreateScope();
        ISpellProcEventDataStore store = scope.ServiceProvider.GetService<ISpellProcEventDataStore>()
            ?? throw new InvalidOperationException("no spell_proc_event store is registered");
        SpellProcEventContent content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(feature.Table, content, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(SpellProcEventTable table, SpellProcEventContent content, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{content.Count} spell proc event conditions";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = table.Content.Count;
            if (keepEmpty && content.Count == 0 && loaded > 0)
            {
                reason = $"spell_proc_event is empty and HotReload:EmptyTables is KeepLoaded, {loaded} entries stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellProcEventContent? previous = null;
            transaction.Step("spell_proc_event", () =>
            {
                previous = table.Content;
                table.Replace(content);
            }, () => table.Replace(previous!));
        }
    }
}
