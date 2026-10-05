using ArcaneCore.Data.Content.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Skills;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reload;

public sealed class ItemEnchantmentContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_item_enchantment";

    public bool IncludedInAll => !string.IsNullOrWhiteSpace(services.GetService<IConfiguration>()?["World:SpellItemEnchantmentDbcPath"]);

    public Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        string? path = services.GetService<IConfiguration>()?["World:SpellItemEnchantmentDbcPath"];
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("SpellItemEnchantment.dbc path is not configured");
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("configured SpellItemEnchantment.dbc was not found", path);
        IReadOnlyList<ItemEnchantmentDefinition> definitions = ItemEnchantmentDbcReader.Load(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ContentCandidate>(new Candidate(services, definitions));
    }

    private sealed class Candidate(IServiceProvider services, IReadOnlyList<ItemEnchantmentDefinition> definitions) : ContentCandidate
    {
        public override string Summary => $"{definitions.Count} enchant definitions";
        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellFeature feature = services.GetRequiredService<SpellFeature>();
            ItemEnchantmentCatalogProvider provider = feature.EnchantmentCatalogProvider
                ?? throw new InvalidOperationException("enchantment catalog provider is not attached");
            ItemEnchantmentCatalogProvider.Snapshot previous = provider.Capture();
            transaction.Step("item enchantment catalog", () => provider.ReplaceDefinitions(definitions, Ranks(services)),
                () => { provider.Restore(previous); feature.System.ItemEnchantments = provider.Current; });
            feature.System.ItemEnchantments = provider.Current;
        }
    }

    private static SpellRankChains? Ranks(IServiceProvider services)
        => services.GetService<SkillCatalog>()?.Ranks
            ?? services.GetService<SkillsFeature>()?.Catalog.Ranks;
}

public sealed class ItemEnchantProcContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_proc_item_enchant";
    public bool IncludedInAll => services.GetService<IServiceProviderIsService>()?.IsService(typeof(IItemEnchantProcStore)) == true;

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IItemEnchantProcStore store = scope.ServiceProvider.GetRequiredService<IItemEnchantProcStore>();
        IReadOnlyList<ItemEnchantProc> rows = (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        return new Candidate(services, rows);
    }

    private sealed class Candidate(IServiceProvider services, IReadOnlyList<ItemEnchantProc> procs) : ContentCandidate
    {
        public override string Summary => $"{procs.Count} enchant PPM overrides";
        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellFeature feature = services.GetRequiredService<SpellFeature>();
            ItemEnchantmentCatalogProvider provider = feature.EnchantmentCatalogProvider
                ?? throw new InvalidOperationException("enchantment catalog provider is not attached");
            ItemEnchantmentCatalogProvider.Snapshot previous = provider.Capture();
            transaction.Step("item enchantment PPM catalog", () => provider.ReplaceProcs(procs, Ranks(services)),
                () => { provider.Restore(previous); feature.System.ItemEnchantments = provider.Current; });
            feature.System.ItemEnchantments = provider.Current;
        }
    }

    private static SpellRankChains? Ranks(IServiceProvider services)
        => services.GetService<SkillCatalog>()?.Ranks
            ?? services.GetService<SkillsFeature>()?.Catalog.Ranks;
}

public sealed class SpellEnchantChargesContentReloadable(IServiceProvider services) : IContentReloadable
{
    public string Name => "spell_enchant_charges";
    public bool IncludedInAll => services.GetService<IServiceProviderIsService>()?.IsService(typeof(ISpellEnchantChargesStore)) == true;
    public IReadOnlyCollection<string> CommitAfter => ["spell_template"];

    public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        ISpellEnchantChargesStore store = scope.ServiceProvider.GetRequiredService<ISpellEnchantChargesStore>();
        IReadOnlyList<SpellEnchantCharges> rows = (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        return new Candidate(services, rows);
    }

    private sealed class Candidate(IServiceProvider services, IReadOnlyList<SpellEnchantCharges> rows) : ContentCandidate
    {
        public override string Summary => $"{rows.Count} enchant charge rows";
        public override IReadOnlyList<string> Validate()
            => [.. rows.GroupBy(r => r.SpellId).Where(g => g.Count() > 1).Select(g => $"spell_enchant_charges has duplicate spell {g.Key}")];

        public override void Commit(WorldRuntime world, ReloadTransaction transaction)
        {
            SpellFeature feature = services.GetRequiredService<SpellFeature>();
            ISpellEnchantChargesCatalog previous = feature.System.SpellEnchantCharges;
            SpellEnchantCharges[] valid = [.. rows.Where(r => feature.System.Store.Get(r.SpellId) is not null)];
            int ignored = rows.Count - valid.Length;
            if (ignored != 0) transaction.Note($"ignored {ignored} unknown spell_enchant_charges rows");
            transaction.Step("spell enchant charges", () => feature.System.SpellEnchantCharges = new SpellEnchantChargesCatalog(valid),
                () => feature.System.SpellEnchantCharges = previous);
        }
    }
}
