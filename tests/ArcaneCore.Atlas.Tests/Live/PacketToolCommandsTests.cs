using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>
/// The packet-tools commands (docs/areas/atlas.md, "Driving the server headlessly"): the payload field builder, opcode arguments, the
/// headless refusal, what each command puts on the pipe, how the daemon's answers and refusals come back, and the tap stream.
/// </summary>
public sealed class PacketToolCommandsTests
{
    private const string Cmd = "live.inject";

    private static readonly string[] ToolCommands = ["server.console", "live.inject", "live.send", "live.tap.start", "live.tap.read", "live.tap.stop"];

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject ToolsHello()
    {
        JsonObject hello = Wire.Hello();
        hello["tools"] = true;
        return hello;
    }

    /// <summary>Invoke as a running instance does (not headless), the data passed through its JSON text.</summary>
    private static async Task<CommandResult> AttachedAsync(LiveRig rig, string name, string args = "{}")
    {
        CommandResult result = await rig.Registry().InvokeAsync(name, Obj(args), new NoServices(), CommandOrigin.Cli, headless: false, CancellationToken.None);
        return result.Data is null ? result : result with { Data = JsonNode.Parse(result.Data.ToJsonString()) };
    }

    [Fact]
    public void Fields_AreWrittenLittleEndian_OneAfterAnother()
    {
        byte[] bytes = PacketFields.BuildFields(Cmd, ["u8:0xAB", "u16:0x1234", "u32:1", "u64:0x0102030405060708", "f32:1.5", "guid:0x1A2B", "cstring:hi"]);

        byte[] expected =
        [
            0xAB,
            0x34, 0x12,
            0x01, 0x00, 0x00, 0x00,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x00, 0x00, 0xC0, 0x3F, // 1.5f = 0x3FC00000
            0x2B, 0x1A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            (byte)'h', (byte)'i', 0x00,
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Fields_TakeNegativesAsTwosComplement_ACstringKeepsItsColonsAndSpaces_AndUtf8()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xFF }, PacketFields.BuildFields(Cmd, ["u8:-1", "u16:-2"]));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, PacketFields.BuildFields(Cmd, ["u32:2147483647"]));
        Assert.Equal([.. Encoding.UTF8.GetBytes(" a:b é"), 0], PacketFields.BuildFields(Cmd, ["cstring: a:b é"]));
        Assert.Equal(new byte[] { 0 }, PacketFields.BuildFields(Cmd, ["cstring:"]));
        Assert.Empty(PacketFields.BuildFields(Cmd, []));
    }

    [Theory]
    [InlineData("u8:256", "does not fit u8")]
    [InlineData("u8:-129", "does not fit u8")]
    [InlineData("u16:65536", "does not fit u16")]
    [InlineData("u32:0x100000000", "does not fit u32")]
    [InlineData("u64:18446744073709551616", "does not fit u64")]
    [InlineData("u32:1.5", "does not fit u32")]
    [InlineData("f32:NaN", "finite number")]
    [InlineData("f32:abc", "finite number")]
    [InlineData("guid:xyz", "guid in hex")]
    [InlineData("guid:0x11112222333344445", "guid in hex")]
    [InlineData("i32:1", "unknown type 'i32'")]
    [InlineData("u32", "is not type:value")]
    [InlineData(":1", "is not type:value")]
    public void Fields_RefuseWhatDoesNotFit(string field, string message)
    {
        CommandException refused = Assert.Throws<CommandException>(() => PacketFields.BuildFields(Cmd, [field]));
        Assert.Equal(CommandException.BadArguments, refused.Code);
        Assert.Contains(message, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_TakesHexOrFields_NotBoth_AndBoundsTheSize()
    {
        Assert.Equal(new byte[] { 0x0A, 0xFF }, PacketFields.Build(Cmd, "0x0a ff", []));
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, PacketFields.Build(Cmd, null, ["u32:1"]));
        Assert.Empty(PacketFields.Build(Cmd, null, []));
        Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(() => PacketFields.Build(Cmd, "00", ["u8:1"])).Code);
        Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(() => PacketFields.Build(Cmd, "abc", [])).Code);
        Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(() => PacketFields.Build(Cmd, new string('0', (PacketFields.MaxPayload + 1) * 2), [])).Code);
    }

    [Theory]
    [InlineData("CMSG_QUERY_TIME", 462)]
    [InlineData("cmsg_query_time", 462)]
    [InlineData("462", 462)]
    [InlineData("0x1CE", 462)]
    [InlineData("SMSG_NOTIFICATION", 459)]
    public void Opcodes_AreTakenByNameOrNumber(string text, int value)
        => Assert.Equal(value, PacketFields.ParseOpcode(Cmd, text));

    [Theory]
    [InlineData("CMSG_NOPE")]
    [InlineData("0x7FFF")]
    [InlineData("70000")]
    public void UnknownOpcodes_AreRefused(string text)
        => Assert.Equal(CommandException.BadArguments, Assert.Throws<CommandException>(() => PacketFields.ParseOpcode(Cmd, text)).Code);

    [Fact]
    public async Task Headless_EveryToolCommandIsUnavailable_ButOpcodesFindWorks()
    {
        await using var rig = new LiveRig();
        foreach (string name in ToolCommands)
        {
            CommandResult result = await rig.RunAsync(name, Obj("""{"line":"x","target":"x","opcode":"CMSG_QUERY_TIME"}"""));
            Assert.False(result.Ok);
            Assert.Equal(CommandException.Unavailable, result.ErrorCode);
            Assert.Contains("needs a running 'arcane-atlas serve' instance", result.ErrorMessage, StringComparison.Ordinal);
        }

        CommandResult find = await rig.RunAsync("opcodes.find", Obj("""{"query":"query_time","side":"client"}"""));
        Assert.True(find.Ok, find.ErrorMessage);
        JsonArray found = find.Data!["opcodes"]!.AsArray();
        Assert.Contains(found, o => o!["name"]!.GetValue<string>() == "CMSG_QUERY_TIME" && o["opcode"]!.GetValue<int>() == 462 && o["side"]!.GetValue<string>() == "client");
        Assert.DoesNotContain(found, o => o!["name"]!.GetValue<string>().StartsWith("SMSG_", StringComparison.Ordinal));

        CommandResult byNumber = await rig.RunAsync("opcodes.find", Obj("""{"query":"0x1CF"}"""));
        Assert.Equal("SMSG_QUERY_TIME_RESPONSE", Assert.Single(byNumber.Data!["opcodes"]!.AsArray())!["name"]!.GetValue<string>());
    }

    [Fact]
    public void TheCatalog_HoldsTheSevenCommands_SixOfThemInstanceOnly()
    {
        var registry = new CommandRegistry();
        using var feed = new LiveFeed(LiveOptions.Default, new ArcaneCore.Atlas.Core.EventHub(), new ArcaneCore.Atlas.Core.ViewStore(ArcaneCore.Atlas.Core.ViewState.Default), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        LiveCommands.Register(registry, feed);

        foreach (string name in ToolCommands)
        {
            Assert.True(registry.TryGet(name, out CommandDefinition? definition), name);
            Assert.Equal(CommandSurfaces.Instance, definition.Surfaces);
        }

        Assert.True(registry.TryGet("opcodes.find", out CommandDefinition? find));
        Assert.True(find.Surfaces.HasFlag(CommandSurfaces.Headless));
    }

    [Fact]
    public async Task Inject_SendsTheTypedRequest_AndReturnsTheDaemonsAnswer()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync(hello: ToolsHello());

        Task<CommandResult> call = AttachedAsync(rig, "live.inject", """{"target":"Feedalice","opcode":"cmsg_standstatechange","fields":["u32:1"]}""");
        JsonObject sent = await server.ReadAsync();
        Assert.Equal("inject", sent["op"]!.GetValue<string>());
        Assert.Equal("Feedalice", sent["target"]!.GetValue<string>());
        Assert.Equal(0x101, sent["opcode"]!.GetValue<int>());
        Assert.Equal("01000000", sent["payload"]!.GetValue<string>());
        long id = sent["id"]!.GetValue<long>();
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{id}},"ok":true,"kind":"inject","target":{"name":"Feedalice","guid":"0000000000000002","bot":false},"opcode":257,"name":"CMSG_STANDSTATECHANGE","length":4,"handler":"world","verdict":"queued"}"""));

        CommandResult result = await call.WaitAsync(FakeFeedServer.Patience);
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("queued", result.Data!["verdict"]!.GetValue<string>());
        Assert.False(result.Data.AsObject().ContainsKey("op"));
    }

    [Fact]
    public async Task Send_ByGuid_WithHex_AndADaemonRefusal_FailsWithItsCode()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync(hello: ToolsHello());

        Task<CommandResult> call = AttachedAsync(rig, "live.send", """{"guid":"0x2","opcode":459,"payload":"68 69 00"}""");
        JsonObject sent = await server.ReadAsync();
        Assert.Equal("send", sent["op"]!.GetValue<string>());
        Assert.Equal("0000000000000002", sent["guid"]!.GetValue<string>());
        Assert.False(sent.ContainsKey("target"));
        Assert.Equal(459, sent["opcode"]!.GetValue<int>());
        Assert.Equal("686900", sent["payload"]!.GetValue<string>());
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{sent["id"]}},"ok":false,"kind":"send","code":"offline","message":"no online character is guid 0000000000000002"}"""));

        CommandResult result = await call.WaitAsync(FakeFeedServer.Patience);
        Assert.False(result.Ok);
        Assert.Equal(CommandException.Failed, result.ErrorCode);
        Assert.Contains("offline: no online character", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"opcode":"CMSG_QUERY_TIME"}""", "exactly one of 'target'")]
    [InlineData("""{"target":"a","guid":"2","opcode":"CMSG_QUERY_TIME"}""", "exactly one of 'target'")]
    [InlineData("""{"target":"a","opcode":"CMSG_NOPE"}""", "not an opcode name")]
    [InlineData("""{"target":"a","opcode":"CMSG_QUERY_TIME","payload":"00","fields":["u8:1"]}""", "not both")]
    [InlineData("""{"target":"a"}""", "'opcode' is required")]
    public async Task Inject_RefusesBadArguments_BeforeAnythingIsSent(string args, string message)
    {
        await using var rig = new LiveRig();
        await rig.ConnectAsync(hello: ToolsHello());

        CommandResult result = await AttachedAsync(rig, "live.inject", args);
        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
        Assert.Contains(message, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADaemonWithoutTheTools_IsUnavailable_AndSoIsNoConnection()
    {
        await using var rig = new LiveRig();
        CommandResult unconnected = await AttachedAsync(rig, "server.console", """{"line":"reload status"}""");
        Assert.Equal(CommandException.Unavailable, unconnected.ErrorCode);

        await rig.ConnectAsync();
        CommandResult off = await AttachedAsync(rig, "server.console", """{"line":"reload status"}""");
        Assert.False(off.Ok);
        Assert.Equal(CommandException.Unavailable, off.ErrorCode);
        Assert.Contains("World:Control:PacketTools:Enabled", off.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_SendsTheLine_AndReturnsItsReplyLines()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync(hello: ToolsHello());

        Task<CommandResult> call = AttachedAsync(rig, "server.console", """{"line":".reload status"}""");
        JsonObject sent = await server.ReadAsync();
        Assert.Equal("console", sent["op"]!.GetValue<string>());
        Assert.Equal(".reload status", sent["line"]!.GetValue<string>());
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{sent["id"]}},"ok":true,"kind":"console","lines":["config: not reloaded since start"],"settled":true}"""));

        CommandResult result = await call.WaitAsync(FakeFeedServer.Patience);
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("config: not reloaded since start", result.Data!["lines"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Tap_StartsWithTheFilter_StreamsPackets_ThenItsEnd_ThroughTapRead()
    {
        await using var rig = new LiveRig();
        FakeFeedServer server = await rig.ConnectAsync(hello: ToolsHello());

        Task<CommandResult> start = AttachedAsync(rig, "live.tap.start", """{"target":"Feedalice","dir":"in","opcodes":["CMSG_QUERY_TIME","463"],"maxPackets":2,"seconds":5,"bytes":16}""");
        JsonObject sent = await server.ReadAsync();
        Assert.Equal("tap", sent["op"]!.GetValue<string>());
        Assert.Equal("in", sent["dir"]!.GetValue<string>());
        Assert.Equal("[462,463]", sent["opcodes"]!.ToJsonString());
        Assert.Equal((2, 5, 16), (sent["maxPackets"]!.GetValue<int>(), sent["seconds"]!.GetValue<int>(), sent["bytes"]!.GetValue<int>()));
        long id = sent["id"]!.GetValue<long>();
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{id}},"ok":true,"kind":"tap","tap":{{id}}}"""));
        Assert.True((await start.WaitAsync(FakeFeedServer.Patience)).Ok);

        await server.WriteAsync(Obj($$"""{"op":"packet","tap":{{id}},"seq":1,"t":1,"dir":"in","opcode":462,"name":"CMSG_QUERY_TIME","len":0,"hex":""}"""));
        CommandResult first = await AttachedAsync(rig, "live.tap.read", """{"wait":10}""");
        Assert.True(first.Ok, first.ErrorMessage);
        Assert.Equal("CMSG_QUERY_TIME", Assert.Single(first.Data!["packets"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Null(first.Data["ended"]);

        await server.WriteAsync(Obj($$"""{"op":"packet","tap":{{id}},"seq":2,"t":2,"dir":"in","opcode":462,"name":"CMSG_QUERY_TIME","len":0,"hex":"","inj":true}"""));
        await server.WriteAsync(Obj($$"""{"op":"tapEnd","tap":{{id}},"reason":"count","packets":2,"dropped":0}"""));
        var later = new List<JsonNode?>();
        JsonNode? ended = null;
        await Wait.UntilAsync(
            () =>
            {
                JsonObject read = rig.Feed.ReadToolTraffic(10);
                later.AddRange(read["packets"]!.AsArray().Select(p => p?.DeepClone()));
                ended ??= read["ended"]?.DeepClone();
                return ended is not null;
            },
            "the tap's end arrives");
        Assert.True(Assert.Single(later)!["inj"]!.GetValue<bool>());
        Assert.Equal("count", ended!["reason"]!.GetValue<string>());

        Task<CommandResult> stop = AttachedAsync(rig, "live.tap.stop");
        JsonObject untap = await server.ReadAsync();
        Assert.Equal("untap", untap["op"]!.GetValue<string>());
        await server.WriteAsync(Obj($$"""{"op":"result","id":{{untap["id"]}},"ok":false,"kind":"untap","code":"no-tap","message":"this connection has no running tap"}"""));
        CommandResult stopped = await stop.WaitAsync(FakeFeedServer.Patience);
        Assert.Equal(CommandException.Failed, stopped.ErrorCode);
        Assert.Contains("no-tap", stopped.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TapRead_ChecksItsBounds()
    {
        await using var rig = new LiveRig();
        await rig.ConnectAsync(hello: ToolsHello());

        Assert.Equal(CommandException.BadArguments, (await AttachedAsync(rig, "live.tap.read", """{"max":0}""")).ErrorCode);
        Assert.Equal(CommandException.BadArguments, (await AttachedAsync(rig, "live.tap.read", """{"wait":31}""")).ErrorCode);
        CommandResult empty = await AttachedAsync(rig, "live.tap.read");
        Assert.True(empty.Ok, empty.ErrorMessage);
        Assert.Empty(empty.Data!["packets"]!.AsArray());
    }

    [Fact]
    public void TheHello_CarriesTheToolsFlag_WhenTheDaemonOffersThem()
    {
        Assert.True(FrameParser.TryParseHello(ToolsHello(), out HelloInfo? on, out _));
        Assert.True(on.Tools);
        Assert.True(FrameParser.TryParseHello(Wire.Hello(), out HelloInfo? off, out _));
        Assert.False(off.Tools);
        JsonObject bad = Wire.Hello();
        bad["tools"] = "yes";
        Assert.False(FrameParser.TryParseHello(bad, out _, out string? problem));
        Assert.Contains("tools", problem, StringComparison.Ordinal);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
