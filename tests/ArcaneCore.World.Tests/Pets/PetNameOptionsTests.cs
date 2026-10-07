using Xunit;
using ArcaneCore.World.Pets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Tests.Spells;

namespace ArcaneCore.World.Tests.Pets;

public sealed class PetNameOptionsTests
{
    [Fact]
    public void DefaultsMatchVmangosAndLengthIsClamped()
    {
        PetNameRules rules = new();
        Assert.Equal("Rex", rules.NormalizeAndValidate("Rex"));
        Assert.Null(rules.NormalizeAndValidate("A"));
        PetNameOptions options = new() { MinPetName = 1, StrictPetNames = 2, RealmZone = 12 };
        Assert.Equal(2, options.EffectiveMinPetName);
        Assert.Equal(12, options.EffectiveMaxPetName);
    }

    [Fact]
    public void StrictRealmAndMinimumArePerRulesInstance()
    {
        PetNameRules cyrillic = new();
        cyrillic.Apply(new PetNameOptions { MinPetName = 3, StrictPetNames = 2, RealmZone = 12 });
        PetNameRules latin = new();
        latin.Apply(new PetNameOptions { MinPetName = 2, StrictPetNames = 1, RealmZone = 1 });
        Assert.Equal("Барс", cyrillic.NormalizeAndValidate("Барс"));
        Assert.Equal("Rex", latin.NormalizeAndValidate("Rex"));
        Assert.Null(cyrillic.NormalizeAndValidate("Re"));
        Assert.Null(cyrillic.NormalizeAndValidate("Rex"));
    }

    [Fact]
    public async Task WorldFeature_BindsConfiguredNamesPerHost_AndPreservesInjectedVeto()
    {
        IConfiguration configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pets:Names:MinPetName"] = "4",
            ["Pets:Names:StrictPetNames"] = "1",
            ["Pets:Names:RealmZone"] = "1",
        }).Build();
        var veto = new PetNameRules { ExternalVeto = name => name != "Forbidden" };
        await using WorldTestHost configuredHost = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IConfiguration>(configured);
            services.AddSingleton(veto);
        });
        await using WorldTestHost defaultsHost = WorldTestHost.Start();

        await configuredHost.OnWorldAsync(() =>
        {
            Func<string, string?> rules = PetControllerRules(configuredHost);
            Assert.Null(rules("Rex"));
            Assert.Equal("Fang", rules("Fang"));
            Assert.Null(rules("Forbidden"));
        });
        await defaultsHost.OnWorldAsync(() => Assert.Equal("Rex", PetControllerRules(defaultsHost)("Rex")));
    }

    private static Func<string, string?> PetControllerRules(WorldTestHost host)
        => host.WorldServices.GetRequiredService<PetsFeature>().Controller.PetNameNormalizer!;

}
