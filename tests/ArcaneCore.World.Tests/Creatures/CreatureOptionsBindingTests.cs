using ArcaneCore.Game.Creatures;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>The <c>Creatures:EventAi</c> configuration section binds into <see cref="CreatureOptions.EventAi"/> (retail defaults when absent).</summary>
public sealed class CreatureOptionsBindingTests
{
    [Fact]
    public void EventAiOptions_DefaultToCmangosValues()
    {
        var options = new CreatureOptions();

        Assert.Equal(500u, options.EventAi.UpdateIntervalMs); // cmangos EVENT_UPDATE_TIME, CreatureEventAI.h:32
        Assert.False(options.EventAi.DebugOnlyEvents);
        Assert.True(options.EventAi.ReportUnsupported);
    }

    [Fact]
    public void TheEventAiSection_BindsIntoTheNestedOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Creatures:EventAi:UpdateIntervalMs"] = "250",
            ["Creatures:EventAi:DebugOnlyEvents"] = "true",
            ["Creatures:EventAi:ReportUnsupported"] = "false",
            ["Creatures:AggroRate"] = "2",
        }).Build();
        var options = new CreatureOptions();

        configuration.GetSection(CreatureOptions.SectionName).Bind(options);

        Assert.Equal((250u, true, false), (options.EventAi.UpdateIntervalMs, options.EventAi.DebugOnlyEvents, options.EventAi.ReportUnsupported));
        Assert.Equal(2f, options.AggroRate);
    }
}
