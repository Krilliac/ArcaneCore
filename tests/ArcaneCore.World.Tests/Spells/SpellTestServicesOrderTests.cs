using ArcaneCore.Data.Content.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <see cref="WorldTestServices.RegisterAll"/> runs the <see cref="IWorldTestServices"/> in FullName order, so the shared
/// <see cref="SpellTestServices"/> default must not replace an <see cref="ISpellContentStore"/> that a fixture in a namespace sorting
/// before 'Spells' (<c>Items.ZFailureItemWorldServices</c>) registered itself.
/// </summary>
public sealed class SpellTestServicesOrderTests
{
    private const uint PeacefulSpell = 99030;

    private static async Task<SpellContent> StoreContentAsync(FailureItemContent? fixture)
    {
        ZFailureItemWorldServices.Current.Value = fixture;
        try
        {
            var services = new ServiceCollection();
            WorldTestServices.RegisterAll(services);
            using ServiceProvider provider = services.BuildServiceProvider();
            return await provider.GetRequiredService<ISpellContentStore>().LoadAsync();
        }
        finally
        {
            ZFailureItemWorldServices.Current.Value = null;
        }
    }

    [Fact]
    public async Task AFixtureStoreRegisteredBeforeSpells_IsNotReplacedByTheSharedDefault()
    {
        SpellContent content = await StoreContentAsync(new FailureItemContent(includePeacefulItem: true));

        Assert.Contains(content.Spells, spell => spell.Id == PeacefulSpell);
    }

    [Fact]
    public async Task WithoutAFixtureStore_TheSharedDefaultIsTheStore()
    {
        SpellContent content = await StoreContentAsync(null);

        Assert.Contains(content.Spells, spell => spell.Id == SpellTestServices.Heal);
        Assert.DoesNotContain(content.Spells, spell => spell.Id == PeacefulSpell);
    }
}
