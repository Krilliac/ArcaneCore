using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.WorldData.Threat;
using ArcaneCore.World.Combat;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload spell_threats</c> (vmangos <c>HandleReloadSpellThreatsCommand</c>, ServerCommands.cpp:1419, Chat.cpp:906 → <c>SpellMgr::LoadSpellThreats</c>, Spells/SpellMgr.cpp:834-875):
/// the table is read off the world thread and its rows replace the live ones. vmangos clears the map first, so an empty table leaves no
/// entries (SpellMgr.cpp:836, :840-846) unless <c>HotReload:EmptyTables = KeepLoaded</c>.
/// </summary>
public sealed class SpellThreatReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_threats";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        SpellThreatFeature feature = services.GetRequiredService<SpellThreatFeature>();
        using IServiceScope scope = services.CreateScope();
        ISpellThreatDataStore store = scope.ServiceProvider.GetService<ISpellThreatDataStore>()
            ?? throw new InvalidOperationException("no spell_threat store is registered");
        SpellThreatContent content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Candidate(feature.Table, content, ReloadPolicy.KeepsEmptyTables(services));
    }

    private sealed class Candidate(SpellThreatTable table, SpellThreatContent content, bool keepEmpty) : ContentCandidate
    {
        public override string Summary => $"{content.Count} spell threat entries";

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = table.Content.Count;
            if (keepEmpty && content.Count == 0 && loaded > 0)
            {
                reason = $"spell_threat is empty and HotReload:EmptyTables is KeepLoaded, {loaded} entries stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellThreatContent? previous = null;
            transaction.Step("spell_threat", () =>
            {
                previous = table.Content;
                table.Replace(content);
            }, () => table.Replace(previous!));
        }
    }
}
