using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// <c>World:Control:PacketTools</c> and the pure parts of the packet tools (docs/areas/map-feed.md, "Packet tools"): the defaults, the
/// environment gate, the startup checks, the registration, the opcode rules, the request parser and the tap's filter and bounds.
/// </summary>
public sealed class PacketToolsOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ConfigIssue[] Check(params (string Key, string Value)[] values) => [.. new PacketToolsConfigChecks().Check(Config(values))];

    [Fact]
    public void TheDefaults_AreOff_InEveryEnvironment()
    {
        var options = new WorldPacketToolsOptions();

        Assert.False(options.Enabled);
        Assert.False(options.AllowOutsideDevelopment);
        Assert.Equal("World:Control:PacketTools", WorldPacketToolsOptions.SectionName);
        Assert.False(options.PermitsEnvironment("Development", liveServer: false));
        Assert.False(options.PermitsEnvironment("Production", liveServer: false));
    }

    [Theory]
    [InlineData(true, false, "Development", true)]
    [InlineData(true, false, "development", true)]
    [InlineData(true, false, "Production", false)]
    [InlineData(true, false, "Staging", false)]
    [InlineData(true, false, "", false)]
    [InlineData(true, true, "Production", true)]
    [InlineData(false, true, "Production", false)]
    [InlineData(false, true, "Development", false)]
    public void TheEnvironmentGate_NeedsDevelopment_OrTheSecondOptIn(bool enabled, bool allowOutside, string environment, bool permitted)
        => Assert.Equal(permitted, new WorldPacketToolsOptions { Enabled = enabled, AllowOutsideDevelopment = allowOutside }.PermitsEnvironment(environment, liveServer: false));

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    [InlineData("Development", true)]
    public void TheLiveServer_IsRefused_WhateverTheOptionsSay(string environment, bool allowOutside)
    {
        var options = new WorldPacketToolsOptions { Enabled = true, AllowOutsideDevelopment = allowOutside };

        Assert.True(options.PermitsEnvironment(environment, liveServer: false));
        Assert.False(options.PermitsEnvironment(environment, liveServer: true));
    }

    [Fact]
    public void TheLiveLauncher_SetsTheLiveServerMarker_TheWorldReads()
    {
        string script = File.ReadAllText(Path.Combine(RepositorySource.RequireRoot(), "tools", "ops", "live", "launch-daemon.ps1"));
        Assert.Contains($"$env:{WorldPacketToolsOptions.LiveServerVariable} = '1'", script, StringComparison.Ordinal);
        Assert.Equal("ARCANECORE_LIVE_SERVER", WorldPacketToolsOptions.LiveServerVariable);
    }

    [Fact]
    public void TheConfigCheck_WarnsAboutAllowOutsideDevelopment_AndAboutEnabledOnTheLiveServer()
    {
        ConfigIssue outside = Assert.Single(Check(("World:Control:PacketTools:AllowOutsideDevelopment", "true")));
        Assert.Equal(ConfigSeverity.Warning, outside.Severity);
        Assert.Equal("World:Control:PacketTools:AllowOutsideDevelopment", outside.Key);

        Assert.Empty(Check(("World:Control:PacketTools:AllowOutsideDevelopment", "false")));

        ConfigIssue live = Assert.Single(Check(("World:Control:PacketTools:Enabled", "true"), ("World:Control:MapFeed:Enabled", "true"), ("ARCANECORE_LIVE_SERVER", "1")));
        Assert.Equal(ConfigSeverity.Warning, live.Severity);
        Assert.Equal("World:Control:PacketTools:Enabled", live.Key);
        Assert.Contains("live server", live.Problem, StringComparison.Ordinal);

        // The marker alone, with the tools off, is the normal live configuration: nothing to say.
        Assert.Empty(Check(("ARCANECORE_LIVE_SERVER", "1")));
        Assert.Empty(Check(("World:Control:PacketTools:Enabled", "true"), ("World:Control:MapFeed:Enabled", "true"), ("ARCANECORE_LIVE_SERVER", "0")));
    }

    [Fact]
    public void TheConfigCheck_FlagsNonBooleans_AndToolsWithoutTheFeed()
    {
        Assert.Empty(Check());
        Assert.Empty(Check(("World:Control:PacketTools:Enabled", "true"), ("World:Control:MapFeed:Enabled", "true")));

        ConfigIssue bad = Assert.Single(Check(("World:Control:PacketTools:Enabled", "yes"), ("World:Control:MapFeed:Enabled", "true")));
        Assert.Equal(ConfigSeverity.Error, bad.Severity);
        Assert.Equal("World:Control:PacketTools:Enabled", bad.Key);

        ConfigIssue badSecond = Assert.Single(Check(("World:Control:PacketTools:AllowOutsideDevelopment", "1")));
        Assert.Equal(ConfigSeverity.Error, badSecond.Severity);

        ConfigIssue noFeed = Assert.Single(Check(("World:Control:PacketTools:Enabled", "true")));
        Assert.Equal(ConfigSeverity.Warning, noFeed.Severity);
        Assert.Contains("map feed", noFeed.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigCheck_IsOneTheDaemonRuns()
        => Assert.Contains(typeof(PacketToolsConfigChecks), ConfigCheckDiscovery.ConcreteTypes(ArcaneCore.World.Ops.Cli.OpsCli.CheckAssemblies));

    [Fact]
    public void ProductionRegistration_BindsTheSection_AndLeavesTheToolsOff()
    {
        var services = new ServiceCollection();
        services.AddWorldMapFeed(Config());
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            Assert.False(provider.GetRequiredService<IOptions<WorldPacketToolsOptions>>().Value.Enabled);
        }

        var configured = new ServiceCollection();
        configured.AddWorldMapFeed(Config(("World:Control:PacketTools:Enabled", "true"), ("World:Control:PacketTools:AllowOutsideDevelopment", "true")));
        using ServiceProvider bound = configured.BuildServiceProvider();
        WorldPacketToolsOptions options = bound.GetRequiredService<IOptions<WorldPacketToolsOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.True(options.AllowOutsideDevelopment);
    }

    [Fact]
    public void Opcodes_ParseByNameInAnyCase_ByDecimal_AndByHex_AndKnowTheirDirection()
    {
        Assert.True(PacketToolOpcodes.TryParse("CMSG_QUERY_TIME", out ushort byName));
        Assert.Equal((ushort)WorldOpcode.CmsgQueryTime, byName);
        Assert.True(PacketToolOpcodes.TryParse("cmsg_query_time", out ushort lower));
        Assert.Equal(byName, lower);
        Assert.True(PacketToolOpcodes.TryParse("462", out ushort decimalValue));
        Assert.Equal(462, decimalValue);
        Assert.True(PacketToolOpcodes.TryParse("0x1CE", out ushort hexValue));
        Assert.Equal(462, hexValue);
        Assert.False(PacketToolOpcodes.TryParse("CMSG_NOT_A_THING", out _));
        Assert.False(PacketToolOpcodes.TryParse("70000", out _));

        Assert.True(PacketToolOpcodes.IsKnown((uint)WorldOpcode.CmsgQueryTime));
        Assert.False(PacketToolOpcodes.IsKnown(0x7FFF));
        Assert.True(PacketToolOpcodes.IsClientToServer((uint)WorldOpcode.CmsgQueryTime));
        Assert.False(PacketToolOpcodes.IsServerToClient((uint)WorldOpcode.CmsgQueryTime));
        Assert.True(PacketToolOpcodes.IsServerToClient((uint)WorldOpcode.SmsgNotification));
        Assert.True(PacketToolOpcodes.IsClientToServer((uint)WorldOpcode.MsgMoveTeleportAck));
        Assert.True(PacketToolOpcodes.IsServerToClient((uint)WorldOpcode.MsgMoveTeleportAck));
        Assert.Equal(10236, PacketToolOpcodes.MaxInjectPayload);
    }

    [Fact]
    public void TheRefusedLists_HoldEveryAuthenticationAndLifecycleOpcode_InTheirOwnDirection()
    {
        Assert.Equal(13, PacketToolOpcodes.RefusedInject.Count);
        Assert.Equal(18, PacketToolOpcodes.RefusedSend.Count);
        Assert.All(PacketToolOpcodes.RefusedInject, o => Assert.True(PacketToolOpcodes.IsClientToServer((uint)o), o.ToString()));
        Assert.All(PacketToolOpcodes.RefusedSend, o => Assert.True(PacketToolOpcodes.IsServerToClient((uint)o), o.ToString()));
        Assert.Contains(WorldOpcode.CmsgAuthSession, (IReadOnlySet<WorldOpcode>)PacketToolOpcodes.RefusedInject);
        Assert.Contains(WorldOpcode.CmsgPlayerLogin, (IReadOnlySet<WorldOpcode>)PacketToolOpcodes.RefusedInject);
        Assert.Contains(WorldOpcode.SmsgAuthResponse, (IReadOnlySet<WorldOpcode>)PacketToolOpcodes.RefusedSend);
        Assert.Contains(WorldOpcode.SmsgNewWorld, (IReadOnlySet<WorldOpcode>)PacketToolOpcodes.RefusedSend);

        Assert.Equal(MapFeedProtocol.LifecycleOpcode, PacketTools.CheckInject((uint)WorldOpcode.CmsgPlayerLogout, 0)?.Code);
        Assert.Equal(MapFeedProtocol.WrongDirection, PacketTools.CheckInject((uint)WorldOpcode.SmsgNotification, 0)?.Code);
        Assert.Equal(MapFeedProtocol.UnknownOpcode, PacketTools.CheckInject(0x7FFF, 0)?.Code);
        Assert.Equal(MapFeedProtocol.Oversize, PacketTools.CheckInject((uint)WorldOpcode.CmsgQueryTime, PacketToolOpcodes.MaxInjectPayload + 1)?.Code);
        Assert.Null(PacketTools.CheckInject((uint)WorldOpcode.CmsgQueryTime, PacketToolOpcodes.MaxInjectPayload));
        Assert.Equal(MapFeedProtocol.LifecycleOpcode, PacketTools.CheckSend((uint)WorldOpcode.SmsgLogoutComplete, 0)?.Code);
        Assert.Equal(MapFeedProtocol.WrongDirection, PacketTools.CheckSend((uint)WorldOpcode.CmsgQueryTime, 0)?.Code);
        Assert.Equal(MapFeedProtocol.Oversize, PacketTools.CheckSend((uint)WorldOpcode.SmsgNotification, PacketToolOpcodes.MaxSendPayload + 1)?.Code);
        Assert.Null(PacketTools.CheckSend((uint)WorldOpcode.SmsgNotification, PacketToolOpcodes.MaxSendPayload));
    }

    private static (bool Ok, FeedRequest Request, FeedError Error) Parse(string line)
    {
        bool ok = MapFeedProtocol.TryParse(System.Text.Encoding.UTF8.GetBytes(line), out FeedRequest request, out FeedError error);
        return (ok, request, error);
    }

    [Fact]
    public void TheParser_ReadsEveryToolOp_AndKeepsTheIdOfARefusedOne()
    {
        (bool ok, FeedRequest request, _) = Parse("""{"op":"inject","id":5,"target":" Feedalice ","opcode":"CMSG_QUERY_TIME","payload":"0a0B"}""");
        Assert.True(ok);
        Assert.Equal(FeedOp.Inject, request.Op);
        Assert.Equal("Feedalice", request.TargetName);
        Assert.Equal((uint)WorldOpcode.CmsgQueryTime, request.Opcode);
        Assert.Equal(new byte[] { 0x0A, 0x0B }, request.Payload);

        (ok, request, _) = Parse("""{"op":"send","id":6,"guid":"0x00000000000000AB","opcode":459}""");
        Assert.True(ok);
        Assert.Equal(0xABUL, request.TargetGuid);
        Assert.Empty(request.Payload!);

        (ok, request, _) = Parse("""{"op":"tap","id":7,"target":"x"}""");
        Assert.True(ok);
        Assert.Equal(TapDirections.Both, request.Directions);
        Assert.Null(request.TapOpcodes);
        Assert.Equal(MapFeedProtocol.DefaultTapPackets, request.MaxPackets);
        Assert.Equal(MapFeedProtocol.DefaultTapSeconds, request.Seconds);
        Assert.Equal(MapFeedProtocol.DefaultTapBytes, request.MaxBytes);

        (ok, request, _) = Parse("""{"op":"tap","id":8,"target":"x","dir":"out","opcodes":[459,"SMSG_NOTIFICATION","cmsg_ping"],"maxPackets":3,"seconds":9,"bytes":0}""");
        Assert.True(ok);
        Assert.Equal(TapDirections.Out, request.Directions);
        Assert.Equal(new ushort[] { 459, (ushort)WorldOpcode.CmsgPing }, request.TapOpcodes);
        Assert.Equal((3, 9, 0), (request.MaxPackets, request.Seconds, request.MaxBytes));

        (ok, request, _) = Parse("""{"op":"console","id":9,"line":"reload status"}""");
        Assert.True(ok);
        Assert.Equal("reload status", request.Line);

        (ok, request, _) = Parse("""{"op":"untap","id":10}""");
        Assert.True(ok);
        Assert.Equal(FeedOp.Untap, request.Op);
    }

    [Theory]
    [InlineData("""{"op":"inject","target":"x","opcode":1}""", "bad-args", false)]
    [InlineData("""{"op":"inject","id":1,"opcode":1}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x","guid":"01","opcode":1}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"guid":"0","opcode":1}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"guid":"12345678901234567","opcode":1}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x"}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x","opcode":70000}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x","opcode":"NOPE"}""", "unknown-opcode", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x","opcode":1,"payload":"abc"}""", "bad-args", true)]
    [InlineData("""{"op":"inject","id":1,"target":"x","opcode":1,"payload":7}""", "bad-args", true)]
    [InlineData("""{"op":"console","id":1,"line":"   "}""", "bad-args", true)]
    [InlineData("""{"op":"console","id":1}""", "bad-args", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","dir":"up"}""", "bad-args", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","opcodes":[]}""", "bad-args", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","opcodes":["NOPE"]}""", "unknown-opcode", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","maxPackets":0}""", "bad-args", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","seconds":3601}""", "bad-args", true)]
    [InlineData("""{"op":"tap","id":1,"target":"x","bytes":4097}""", "bad-args", true)]
    public void TheParser_RefusesMalformedToolLines(string line, string code, bool hasId)
    {
        (bool ok, FeedRequest request, FeedError error) = Parse(line);
        Assert.False(ok);
        Assert.Equal(code, error.Code);
        Assert.Equal(hasId, request.HasId);
    }

    [Fact]
    public void ATap_FiltersByDirectionAndOpcode_CutsPayloads_AndEndsOnItsCount()
    {
        var tap = new FeedPacketTap(3, new ToolTarget(null!, "x", 1, false), TapDirections.In, [(ushort)WorldOpcode.CmsgQueryTime], maxPackets: 2, maxBytes: 2);

        tap.Offer(inbound: false, WorldOpcode.CmsgQueryTime, [1, 2, 3], injected: false); // wrong direction
        tap.Offer(inbound: true, WorldOpcode.CmsgPing, [1, 2, 3], injected: false); // filtered out
        tap.Offer(inbound: true, WorldOpcode.CmsgQueryTime, [1, 2, 3], injected: true);
        Assert.Null(tap.Reason);
        tap.Offer(inbound: true, WorldOpcode.CmsgQueryTime, [], injected: false);
        Assert.Equal("count", tap.Reason);
        tap.Offer(inbound: true, WorldOpcode.CmsgQueryTime, [9], injected: false); // after the end

        Assert.True(tap.Reader.TryRead(out TapPacket first));
        Assert.Equal((1L, true, 3), (first.Seq, first.Injected, first.Length));
        Assert.Equal(new byte[] { 1, 2 }, first.Bytes);
        Assert.True(tap.Reader.TryRead(out TapPacket second));
        Assert.Equal((2L, false, 0), (second.Seq, second.Injected, second.Length));
        Assert.False(tap.Reader.TryRead(out _));
        Assert.True(tap.Reader.Completion.IsCompleted);
        Assert.Equal(0, tap.Dropped);

        tap.End("stopped");
        Assert.Equal("count", tap.Reason); // the first reason wins
    }

    [Fact]
    public void ATapWithAFullQueue_DropsAndCounts_WithoutWaiting()
    {
        var tap = new FeedPacketTap(1, new ToolTarget(null!, "x", 1, false), TapDirections.Both, null, maxPackets: 1000, maxBytes: 16, capacity: 3);
        for (int i = 0; i < 10; i++)
        {
            tap.Offer(inbound: i % 2 == 0, WorldOpcode.SmsgNotification, [0], injected: false);
        }

        Assert.Equal(7, tap.Dropped);
        tap.SessionClosed();
        Assert.Equal("closed", tap.Reason);
    }
}
