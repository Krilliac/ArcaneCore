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
    public void AggroOptions_DefaultToVmangosValues_AndBindFromTheSection()
    {
        var defaults = new CreatureOptions();
        Assert.Equal(
            (AggroScanMode.Relocation, 1000u, 40f, 5000u, true, false),
            (defaults.AggroScanMode, defaults.AiRelocationNotifyDelayMs, defaults.MaxCreatureAttackRadius, defaults.RespawnPacifyMs, defaults.SendAiReaction, defaults.AggroUsesBoundingRadius));

        // vmangos World.cpp:564 ThreatRadius 50; the leash check cadence and the 12 s extension (Objects/Creature.cpp:976, 2813).
        Assert.Equal((50f, 3000u, 12u), (defaults.ThreatRadius, defaults.LeashCheckIntervalMs, defaults.LeashExtensionSeconds));

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Creatures:AggroScanMode"] = "Poll",
            ["Creatures:RespawnPacifyMs"] = "0",
            ["Creatures:SendAiReaction"] = "false",
            ["Creatures:AggroUsesBoundingRadius"] = "true",
        }).Build();
        var bound = new CreatureOptions();
        configuration.GetSection(CreatureOptions.SectionName).Bind(bound);

        Assert.Equal((AggroScanMode.Poll, 0u, false, true), (bound.AggroScanMode, bound.RespawnPacifyMs, bound.SendAiReaction, bound.AggroUsesBoundingRadius));
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

    [Fact]
    public void MovementOptions_DefaultToRetail_AndBindFromTheSection()
    {
        Assert.Equal((MonsterMoveOffsetBase.Destination, 15u, false, false), (new CreatureOptions().Movement.MonsterMoveOffsetBase, new CreatureOptions().Movement.RunDuringWanderChancePercent, new CreatureOptions().Movement.EvadeRestoresFullHealth, new CreatureOptions().Movement.HonorWaypointRunColumn));

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Creatures:Movement:MonsterMoveOffsetBase"] = "Midpoint",
            ["Creatures:Movement:RunDuringWanderChancePercent"] = "40",
            ["Creatures:Movement:EvadeRestoresFullHealth"] = "true",
            ["Creatures:Movement:HonorWaypointRunColumn"] = "true",
        }).Build();
        var bound = new CreatureOptions();
        configuration.GetSection(CreatureOptions.SectionName).Bind(bound);

        Assert.Equal((MonsterMoveOffsetBase.Midpoint, 40u, true, true), (bound.Movement.MonsterMoveOffsetBase, bound.Movement.RunDuringWanderChancePercent, bound.Movement.EvadeRestoresFullHealth, bound.Movement.HonorWaypointRunColumn));
    }

    [Fact]
    public void RespawnOptions_DefaultToRetail_AndBindFromTheSection()
    {
        var defaults = new CreatureOptions();
        Assert.Equal((true, false, true, true, true), (defaults.Respawn.DrawDelayAtLoad, defaults.Respawn.HonorTemplateCorpseDecay, defaults.Respawn.AlternateEntries, defaults.Respawn.Persist, defaults.Respawn.SaveImmediately));

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Creatures:Respawn:DrawDelayAtLoad"] = "false",
            ["Creatures:Respawn:HonorTemplateCorpseDecay"] = "true",
            ["Creatures:Respawn:AlternateEntries"] = "false",
            ["Creatures:Respawn:Persist"] = "false",
            ["Creatures:Respawn:SaveImmediately"] = "false",
        }).Build();
        var bound = new CreatureOptions();
        configuration.GetSection(CreatureOptions.SectionName).Bind(bound);

        Assert.Equal((false, true, false, false, false), (bound.Respawn.DrawDelayAtLoad, bound.Respawn.HonorTemplateCorpseDecay, bound.Respawn.AlternateEntries, bound.Respawn.Persist, bound.Respawn.SaveImmediately));
    }
}
