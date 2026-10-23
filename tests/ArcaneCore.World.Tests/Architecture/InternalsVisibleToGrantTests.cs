using System.Reflection;
using System.Runtime.CompilerServices;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// Pins the exact <c>InternalsVisibleTo</c> grants on the assemblies this project can see, however they are declared (assembly
/// attribute in source or csproj item): moving a grant must not add, drop or misspell one.
/// </summary>
public sealed class InternalsVisibleToGrantTests
{
    private static string[] Grants(Assembly assembly) =>
        assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void World_grants_exactly_the_expected_assemblies()
    {
        Assert.Equal(
            ["ArcaneCore.Benchmarks", "ArcaneCore.Gateway.Tests", "ArcaneCore.Playerbots", "ArcaneCore.World.Tests"],
            Grants(typeof(EconomyFeature).Assembly));
    }

    [Fact]
    public void Playerbots_grants_exactly_the_expected_assemblies()
    {
        Assert.Equal(
            ["ArcaneCore.Benchmarks", "ArcaneCore.ChatModelProbe", "ArcaneCore.World.Tests"],
            Grants(typeof(GgufFile).Assembly));
    }

    [Fact]
    public void WorldContracts_grants_exactly_the_expected_assemblies()
    {
        Assert.Equal(
            ["ArcaneCore.Benchmarks", "ArcaneCore.Playerbots", "ArcaneCore.World", "ArcaneCore.World.Tests"],
            Grants(typeof(IManagedBotSessionPolicy).Assembly));
    }
}
