using ArcaneCore.Game.Locomotion;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Locomotion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Locomotion;

/// <summary>The daemon features that wire the locomotion rules (docs/areas/locomotion.md).</summary>
public sealed class LocomotionWiringTests
{
    [Fact]
    public async Task TheFeature_BindsTheLocomotionSection_AndKeepsRetailDefaultsOtherwise()
    {
        await using var host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Locomotion:RateDamageFall"] = "0.5",
                ["Locomotion:PendingAckResponseTimeMs"] = "1500",
                ["Locomotion:GhostRunSpeedWorld"] = "50",   // clamped to 10 like setConfigMinMax
                ["Locomotion:EnvironmentalDamageMin"] = "700",
                ["Locomotion:SlimeDamage"] = "true",
            })
            .Build()));

        LocomotionOptions options = LocomotionEnvironment.For(host.World).Options;

        Assert.Equal(0.5f, options.RateDamageFall);
        Assert.Equal(1500u, options.PendingAckResponseTimeMs);
        Assert.Equal(10.0f, options.GhostRunSpeedWorld);
        Assert.Equal((700u, 700u), (options.EnvironmentalDamageMin, options.EnvironmentalDamageMax)); // the maximum follows the minimum
        Assert.True(options.SlimeDamage);
        Assert.Equal(1.0f, options.GhostRunSpeedBattleground);
        Assert.Equal(60u, options.MirrorTimerBreathMaxSec);
    }

    [Fact]
    public async Task WithoutConfiguration_EverythingIsRetail()
    {
        await using var host = WorldTestHost.Start();

        LocomotionOptions options = LocomotionEnvironment.For(host.World).Options;

        Assert.Equal((4000u, 1.0f, 1.0f, 60u, 60u, 1u, 605u, 610u, false), (options.PendingAckResponseTimeMs, options.RateDamageFall, options.GhostRunSpeedWorld, options.MirrorTimerFatigueMaxSec, options.MirrorTimerBreathMaxSec, options.MirrorTimerEnvironmentalMaxSec, options.EnvironmentalDamageMin, options.EnvironmentalDamageMax, options.SlimeDamage));
    }

    [Fact]
    public async Task TheSpellBridgeFeature_RegistersTheSpellSystemForTheLiquidRules()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SWIM", "Swimmer");
        ArcaneCore.Game.Entities.Player player = await host.PlayerAsync("Swimmer");

        IEnvironmentSpellBridge? bridge = await host.OnWorldAsync(() => LocomotionEnvironment.SpellBridgeFor(player.Map));

        Assert.IsType<EnvironmentSpellBridgeFeature>(bridge);
        Assert.Contains(host.WorldServices.GetServices<ArcaneCore.World.Features.IWorldFeature>(), f => f is EnvironmentSpellBridgeFeature);
        Assert.NotNull(host.WorldServices.GetRequiredService<SpellFeature>());
    }
}
