using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Reload;

/// <summary>
/// <c>.reload spell_template</c> (vmangos <c>HandleReloadSpellTemplateCommand</c>,
/// ServerCommands.cpp:1409 → <c>SpellMgr::LoadSpells</c>, SpellMgr.cpp:3702): the spell tables are read
/// and resolved into a new <see cref="SpellStore"/> off the world thread, then swapped into the
/// running <see cref="SpellSystem"/>.
/// <list type="bullet">
/// <item>An empty <c>spell_template</c> keeps what is loaded: vmangos returns before touching the
/// table when the select comes back empty, before the first <c>LoadSpell</c> (SpellMgr.cpp:3709-3717, 3724-3732).</item>
/// <item>Casts and auras in flight hold the <see cref="SpellInfo"/> they started with (the records
/// are immutable); vmangos mutates its table in place, so a running aura there sees the new numbers
/// on its next tick. That difference is not visible to a client.</item>
/// <item>vmangos also reloads <c>spell_mod</c> in the same command (ServerCommands.cpp:1414-1415); ArcaneCore
/// has no such table.</item>
/// </list>
/// </summary>
public sealed class SpellContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_template";

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SpellContentReloadable).FullName!) ?? NullLogger.Instance;
        using IServiceScope scope = services.CreateScope();
        ISpellContentStore store = scope.ServiceProvider.GetService<ISpellContentStore>()
            ?? throw new InvalidOperationException("no spell content store is registered");

        SpellContent content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        // The store is keyed by spell id, so two rows for one spell cannot be built (vmangos' build-aware select
        // returns one row per entry, SpellMgr.cpp:3722): report them instead of failing inside the factory.
        IReadOnlyList<string> duplicates = DuplicateSpellIds(content);
        SpellStore built = duplicates.Count == 0 ? SpellStoreFactory.Build(content, logger) : SpellStore.Empty;
        return new SpellCandidate(services, built, duplicates);
    }

    private static IReadOnlyList<string> DuplicateSpellIds(SpellContent content)
        => [.. content.Spells.GroupBy(s => s.Id).Where(g => g.Count() > 1).OrderBy(g => g.Key)
            .Select(g => $"spell_template has {g.Count()} rows for spell {g.Key}")];

    private sealed class SpellCandidate(IServiceProvider services, SpellStore store, IReadOnlyList<string> problems) : ContentCandidate
    {
        public override string Summary => $"{store.Count} spells";

        public override IReadOnlyList<string> Validate() => problems;

        public override bool TryKeepCurrent(WorldRuntime world, out string reason)
        {
            int loaded = services.GetRequiredService<SpellFeature>().System.Store.Count;
            if (store.Count == 0 && loaded > 0)
            {
                reason = $"spell_template is empty (vmangos SpellMgr.cpp:3724-3732 returns early too), {loaded} spells stay loaded";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellSystem system = services.GetRequiredService<SpellFeature>().System;
            SpellStore previous = system.Store;
            transaction.Step("spell store", () => system.Store = store, () => system.Store = previous);
        }
    }
}
