using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>The pure parts of the packet heuristics: options, opcode tables, the per-session windows and the report throttle.</summary>
public sealed class PacketHeuristicsUnitTests
{
    [Fact]
    public void TheDefaults_AreOff_ExemptBotsAndStaff_AndValid()
    {
        var options = new PacketHeuristicsOptions();

        Assert.False(options.Enabled);
        Assert.True(options.ExemptBots);
        Assert.True(options.ExemptStaff);
        Assert.Empty(options.Validate());
        Assert.Equal(10, options.CadenceLimits.CastPerSecond);
        Assert.Equal(10, options.CadenceLimits.UsePerSecond);
        Assert.Equal(50, options.CadenceLimits.QueryPerSecond);
        Assert.Equal(5, options.CadenceLimits.EmotePerSecond);
        Assert.Equal(5000, options.CadenceLimits.PingMinIntervalMs);
        Assert.Equal(5, options.ReplayCount);
        Assert.Equal(1000, options.ReplayWindowMs);
        Assert.Equal(10, options.ReportIntervalSeconds);
    }

    [Theory]
    [InlineData("ReportIntervalSeconds", 0)]
    [InlineData("ReplayCount", 1)]
    [InlineData("ReplayWindowMs", 10)]
    [InlineData("WrongStateGraceMs", -1)]
    [InlineData("CadenceLimits.CastPerSecond", 5000)]
    [InlineData("CadenceLimits.PingMinIntervalMs", -5)]
    public void ABadValue_IsRefused_AndNamesItsKey(string key, int value)
    {
        var options = new PacketHeuristicsOptions();
        switch (key)
        {
            case "ReportIntervalSeconds": options.ReportIntervalSeconds = value; break;
            case "ReplayCount": options.ReplayCount = value; break;
            case "ReplayWindowMs": options.ReplayWindowMs = value; break;
            case "WrongStateGraceMs": options.WrongStateGraceMs = value; break;
            case "CadenceLimits.CastPerSecond": options.CadenceLimits.CastPerSecond = value; break;
            default: options.CadenceLimits.PingMinIntervalMs = value; break;
        }

        string problem = Assert.Single(options.Validate());
        Assert.Contains(key.Split('.')[^1], problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CMSG_NOT_AN_OPCODE")]
    [InlineData("**")]
    [InlineData("*")]
    [InlineData("CMSG_*_*")]
    [InlineData(" CMSG_PING")]
    [InlineData("MSG_NOTHING_*")]
    public void AReplayExclusionThatIsNotAnOpcodePattern_IsRefused(string pattern)
    {
        var options = new PacketHeuristicsOptions { ReplayExclude = [pattern] };

        Assert.Single(options.Validate());
    }

    [Fact]
    public void TheDefaultReplayExclusions_CoverWhatRepeatsLegitimately_AndNotWhatShouldNot()
    {
        var settings = new PacketHeuristicsSettings(new PacketHeuristicsOptions());

        foreach (WorldOpcode excluded in new[]
        {
            WorldOpcode.CmsgPing, WorldOpcode.MsgMoveHeartbeat, WorldOpcode.MsgMoveStartForward, WorldOpcode.CmsgMoveTimeSkipped,
            WorldOpcode.CmsgSetActiveMover, WorldOpcode.CmsgCreatureQuery, WorldOpcode.CmsgNameQuery, WorldOpcode.CmsgQueryTime,
            WorldOpcode.CmsgSetSelection, WorldOpcode.CmsgAttackswing, WorldOpcode.CmsgAttackstop, WorldOpcode.CmsgCancelTrade,
            WorldOpcode.CmsgStandstatechange, WorldOpcode.CmsgLootRelease, WorldOpcode.CmsgAreatrigger, WorldOpcode.CmsgCharEnum,
            WorldOpcode.MsgQueryNextMailTime,
        })
        {
            Assert.False(settings.ReplayChecked[(int)excluded], WorldOpcodeNames.GetName(excluded) + " legitimately repeats");
        }

        foreach (WorldOpcode watched in new[]
        {
            WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgUseItem, WorldOpcode.CmsgEmote, WorldOpcode.CmsgMessagechat,
            WorldOpcode.CmsgAutostoreLootItem, WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgGameobjUse, WorldOpcode.CmsgLoot,
        })
        {
            Assert.True(settings.ReplayChecked[(int)watched], WorldOpcodeNames.GetName(watched) + " should not repeat identically");
        }
    }

    [Fact]
    public void TheOpcodeClasses_AreTheOnesTheDesignNames()
    {
        Assert.Equal(CadenceClass.Cast, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgCastSpell));
        Assert.Equal(CadenceClass.Use, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgUseItem));
        Assert.Equal(CadenceClass.Use, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgGameobjUse));
        Assert.Equal(CadenceClass.Use, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgLoot));
        Assert.Equal(CadenceClass.Query, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgCreatureQuery));
        Assert.Equal(CadenceClass.Query, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgItemQuerySingle));
        Assert.Equal(CadenceClass.Emote, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgEmote));
        Assert.Equal(CadenceClass.Emote, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgTextEmote));
        Assert.Equal(CadenceClass.Ping, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgPing));
        Assert.Equal(CadenceClass.None, PacketOpcodeClasses.ClassOf(WorldOpcode.MsgMoveHeartbeat));
        Assert.Equal(CadenceClass.None, PacketOpcodeClasses.ClassOf(WorldOpcode.CmsgAutostoreLootItem)); // an auto-loot burst is one packet per slot
        Assert.Equal(CadenceClass.None, PacketOpcodeClasses.ClassOf((WorldOpcode)5000));
    }

    [Fact]
    public void TheIncapacitatedActionLists_KeepWhatAGhostStillMayDo()
    {
        PacketOpcodeClasses.Incapacity Of(WorldOpcode opcode) => PacketOpcodeClasses.ActionMask(opcode);

        Assert.NotEqual(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgUseItem) & PacketOpcodeClasses.Incapacity.Dead);
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgSpiritHealerActivate)); // a ghost talks to a spirit healer
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgReclaimCorpse));
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgRepopRequest));
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgSwapInvItem)); // the dead may rearrange their bags
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgCastSpell) & PacketOpcodeClasses.Incapacity.Dead); // Reincarnation
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgGossipHello) & PacketOpcodeClasses.Incapacity.Dead); // how a ghost opens a spirit healer
        Assert.NotEqual(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgCastSpell) & PacketOpcodeClasses.Incapacity.Stunned);
        Assert.NotEqual(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.CmsgGossipHello) & PacketOpcodeClasses.Incapacity.Taxi);
        Assert.Equal(PacketOpcodeClasses.Incapacity.None, Of(WorldOpcode.MsgMoveHeartbeat));
    }

    // --- the windows ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCadenceWindow_FlagsTheCeilingPlusOneInASlidingSecond_AndSlides()
    {
        var state = new PacketHeuristicsSession();

        // Five per second allowed: the sixth inside a second of the first is over.
        for (int i = 0; i < 5; i++)
        {
            Assert.False(state.NoteCadence(CadenceClass.Emote, 1000 + (i * 100), 5, out _));
        }

        Assert.True(state.NoteCadence(CadenceClass.Emote, 1500, 5, out int inWindow));
        Assert.Equal(6, inWindow);

        // Sliding, not a fixed bucket: the next one at 2000 is exactly a second after the packet at 1000, which has left the window,
        // but the ring now holds 1100..1500 plus the packet at 1500, so 2001 is the first that sees only packets older than a second.
        Assert.True(state.NoteCadence(CadenceClass.Emote, 1900, 5, out _));
        Assert.False(state.NoteCadence(CadenceClass.Emote, 4000, 5, out _));
    }

    [Fact]
    public void TheCadenceWindows_AreIndependentPerClass_AndFollowACeilingChange()
    {
        var state = new PacketHeuristicsSession();
        for (int i = 0; i < 3; i++)
        {
            Assert.False(state.NoteCadence(CadenceClass.Cast, 10 + i, 3, out _));
        }

        Assert.False(state.NoteCadence(CadenceClass.Query, 20, 3, out _)); // another class: its own window
        Assert.True(state.NoteCadence(CadenceClass.Cast, 30, 3, out _));
        Assert.False(state.NoteCadence(CadenceClass.Cast, 31, 100, out _)); // a new ceiling starts a new window
    }

    [Fact]
    public void ThePingCheck_NeverFlagsTheFirstPing_AndFlagsAnEarlyOne()
    {
        var state = new PacketHeuristicsSession();

        Assert.False(state.NotePing(100, 5000, out _));
        Assert.True(state.NotePing(600, 5000, out long gap));
        Assert.Equal(500, gap);
        Assert.False(state.NotePing(6000, 5000, out _)); // 5400 ms later
    }

    [Fact]
    public void TheReplayCheck_CountsIdenticalPayloadsInTheWindow_AndOnlyThoseAndOnlyThatOpcode()
    {
        var state = new PacketHeuristicsSession();
        byte[] same = [1, 2, 3, 4];
        for (int i = 1; i < 5; i++)
        {
            Assert.False(state.NoteReplay(WorldOpcode.CmsgBuyItem, same, 100 + i, 5, 1000, out int count));
            Assert.Equal(i, count);
        }

        Assert.False(state.NoteReplay(WorldOpcode.CmsgSellItem, same, 105, 5, 1000, out _)); // another opcode with the same bytes
        Assert.False(state.NoteReplay(WorldOpcode.CmsgBuyItem, [1, 2, 3, 5], 106, 5, 1000, out _)); // one byte different
        Assert.True(state.NoteReplay(WorldOpcode.CmsgBuyItem, same, 107, 5, 1000, out int fifth));
        Assert.Equal(5, fifth);

        // Outside the window the count starts again.
        Assert.False(state.NoteReplay(WorldOpcode.CmsgBuyItem, same, 5000, 5, 1000, out int restarted));
        Assert.Equal(1, restarted);
    }

    [Fact]
    public void TheReplayCheck_TracksEightRecentPayloads_AndStaysBoundedBeyondThat()
    {
        var state = new PacketHeuristicsSession();
        for (byte i = 0; i < 40; i++)
        {
            state.NoteReplay(WorldOpcode.CmsgBuyItem, [i], 100, 5, 1000, out _);
        }

        // 40 distinct payloads cycled through 8 slots; a repeated recent one is still found.
        Assert.False(state.NoteReplay(WorldOpcode.CmsgBuyItem, [39], 101, 5, 1000, out int count));
        Assert.Equal(2, count);
    }

    [Fact]
    public void TheThrottle_ReportsOnceAnInterval_PerCheckAndOpcode_AndCountsWhatItHolds()
    {
        var state = new PacketHeuristicsSession();

        Assert.True(state.ShouldReport(PacketCheck.Shape, WorldOpcode.CmsgPing, 1000, 10_000, 0, out _));
        for (int i = 0; i < 99; i++)
        {
            Assert.False(state.ShouldReport(PacketCheck.Shape, WorldOpcode.CmsgPing, 1001 + i, 10_000, 0, out _));
        }

        Assert.True(state.ShouldReport(PacketCheck.Replay, WorldOpcode.CmsgPing, 1200, 10_000, 0, out _)); // another check
        Assert.True(state.ShouldReport(PacketCheck.Shape, WorldOpcode.CmsgEmote, 1200, 10_000, 0, out _)); // another opcode
        Assert.True(state.ShouldReport(PacketCheck.Shape, WorldOpcode.CmsgPing, 11_000, 10_000, 0, out int held));
        Assert.Equal(99, held);
        Assert.True(state.ShouldReport(PacketCheck.Shape, WorldOpcode.CmsgPing, 11_001, 10_000, 1, out _)); // a reset (new epoch) reports at once
    }

    [Fact]
    public void TheThrottle_SharesOneBucketForEveryOpcodeOutsideTheTable()
    {
        var state = new PacketHeuristicsSession();

        Assert.True(state.ShouldReport(PacketCheck.UnknownOpcode, (WorldOpcode)2000, 1, 10_000, 0, out _));
        Assert.False(state.ShouldReport(PacketCheck.UnknownOpcode, (WorldOpcode)40000, 2, 10_000, 0, out _));
        Assert.False(state.ShouldReport(PacketCheck.UnknownOpcode, (WorldOpcode)65535, 3, 10_000, 0, out _));
    }

    // --- the startup check ---------------------------------------------------------------------------------------------------

    private static IReadOnlyList<ConfigIssue> Check(params (string Key, string Value)[] values)
        => [.. new PacketHeuristicsConfigChecks().Check(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build())];

    [Fact]
    public void TheShippedDefaults_PassTheStartupCheck() => Assert.Empty(Check());

    [Theory]
    [InlineData("World:PacketHeuristics:ReplayCount", "lots")]
    [InlineData("World:PacketHeuristics:ReplayCount", "1")]
    [InlineData("World:PacketHeuristics:Enabled", "maybe")]
    [InlineData("World:PacketHeuristics:CadenceLimits:QueryPerSecond", "-3")]
    [InlineData("World:PacketHeuristics:ReportIntervalSeconds", "0")]
    public void ABadValue_IsAStartupError(string key, string value)
        => Assert.Contains(Check((key, value)), issue => issue.Severity == ConfigSeverity.Error);

    [Fact]
    public void AReplayExclusionNamingNoOpcode_IsAStartupError()
        => Assert.Contains(Check(("World:PacketHeuristics:ReplayExclude:0", "CMSG_NOT_REAL")), issue => issue.Severity == ConfigSeverity.Error);

    [Fact]
    public void TheOptionsBind_FromTheSection()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:PacketHeuristics:Enabled"] = "true",
            ["World:PacketHeuristics:ExemptBots"] = "false",
            ["World:PacketHeuristics:CadenceLimits:EmotePerSecond"] = "2",
            ["World:PacketHeuristics:ReplayExclude:0"] = "CMSG_PING",
        }).Build();
        var options = new PacketHeuristicsOptions();

        configuration.GetSection(PacketHeuristicsOptions.SectionName).Bind(options);

        Assert.True(options.Enabled);
        Assert.False(options.ExemptBots);
        Assert.Equal(2, options.CadenceLimits.EmotePerSecond);
        Assert.Equal("CMSG_PING", Assert.Single(options.ReplayExclude).ToString());
    }
}
