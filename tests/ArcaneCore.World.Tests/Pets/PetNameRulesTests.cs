using ArcaneCore.World.Pets;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

public sealed class PetNameRulesTests
{
    // Build-5875 CheckPetName accepts one alphabet with 2..12 letters and
    // preserves the submitted name; it does not normalize player-name casing.
    [Theory]
    [InlineData("rEx", true)]
    [InlineData("Rex", true)]
    [InlineData("A", false)]
    [InlineData("Thirteenchars", false)]
    [InlineData("Rex2", false)]
    [InlineData("Rex Name", false)]
    [InlineData("R\u0435x", false)]
    public void PetValidation_PreservesValidCasingAndRejectsInvalidNames(string name, bool allowed)
        => Assert.Equal(allowed ? name : null, new PetNameRules().NormalizeAndValidate(name));

    [Fact]
    public void ExternalCatalogVeto_IsConfinedToTheConfiguredWorldPolicy()
    {
        var restricted = new PetNameRules { ExternalVeto = name => name != "Rex" };
        Assert.Null(restricted.NormalizeAndValidate("Rex"));
        Assert.Equal("Rex", new PetNameRules().NormalizeAndValidate("Rex"));
    }

    [Fact]
    public void StrictBasicLatinPolicy_RejectsAnOtherwiseValidCyrillicName()
    {
        const string name = "\u0420\u0435\u043a\u0441";
        Assert.Equal(name, new PetNameRules().NormalizeAndValidate(name));
        Assert.Null(new PetNameRules { StrictMask = 1 }.NormalizeAndValidate(name));
    }
}
