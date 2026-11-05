using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Observer;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The mock client's observer channel: the client's own view of the world served in the daemon's map-feed protocol 1, so Atlas can connect to it.
/// The wire messages are checked against the field list of docs/areas/map-feed.md, and a real pipe round trip speaks the protocol as Atlas does.
/// </summary>
public sealed class ObserverFeedTests
{
    private static readonly ulong Self = 0x0A01;
    private static readonly ulong Wolf = ObjectGuid.WithEntry(HighGuid.Unit, 299, 7).Value;
    private static readonly ulong Chest = ObjectGuid.WithEntry(HighGuid.GameObject, 55, 3).Value;
    private static readonly ulong Item = ObjectGuid.Item(9).Value;

    private static ObserverView View() => ObserverFrames.ViewOf(
        0, Self, new MockPosition(1.5f, 2.5f, 3.5f),
        [
            (Chest, new MockPosition(10f, 11f, 12f)),
            (Wolf, new MockPosition(20f, 21f, 22f)),
            (Item, new MockPosition(1f, 1f, 1f)),
            (ObjectGuid.WithEntry(HighGuid.Unit, 300, 1).Value, null),
            (Self, new MockPosition(99f, 99f, 99f)),
        ]);

    [Fact]
    public void TheView_HasTheClientFirst_ThenDrawableObjectsWithAPosition_InGuidOrder()
    {
        ObserverView view = View();

        Assert.Equal(0u, view.Map);
        Assert.Equal([Self, .. new[] { Chest, Wolf }.Order()], view.Entities.Select(e => e.Guid).ToArray());
        Assert.Equal("player", view.Entities[0].Kind);
        Assert.Equal(1.5f, view.Entities[0].X); // the client's own position, not the duplicate carrying 99
        ObserverEntity wolf = view.Entities.Single(e => e.Guid == Wolf);
        Assert.Equal(("creature", 299u, 20f), (wolf.Kind, wolf.Entry, wolf.X));
        Assert.Equal("gameobject", view.Entities.Single(e => e.Guid == Chest).Kind);
        Assert.DoesNotContain(view.Entities, e => e.Guid == Item); // an item is not drawn
    }

    [Fact]
    public void Hello_AnswersProtocolOneWithThePidAndTheMap()
    {
        JsonObject hello = ObserverFrames.Hello(4242, View());

        Assert.Equal("hello", (string?)hello["op"]);
        Assert.Equal(1, (int?)hello["protocol"]);
        Assert.Equal(4242, (int?)hello["pid"]);
        JsonObject map = Assert.IsType<JsonObject>(Assert.Single(hello["maps"]!.AsArray()));
        Assert.Equal(0, (int?)map["map"]);
        Assert.Equal(1, (int?)map["players"]);
        Assert.Equal(1, (int?)map["creatures"]);
    }

    [Fact]
    public void AFrame_CarriesSixteenDigitGuids_KindsPositionsAndEntries_AndHonoursTheKindFilter()
    {
        JsonObject frame = ObserverFrames.Frame(3, 1500, View(), null);

        Assert.Equal("frame", (string?)frame["op"]);
        Assert.Equal(3, (long?)frame["seq"]);
        Assert.Equal(1500, (long?)frame["clockMs"]);
        JsonArray entities = frame["entities"]!.AsArray();
        Assert.Equal(3, entities.Count);
        JsonObject first = entities[0]!.AsObject();
        Assert.Equal("0000000000000A01", (string?)first["g"]);
        Assert.Equal("player", (string?)first["k"]);
        Assert.Null(first["e"]); // a player has no entry
        JsonObject wolf = entities.Single(e => (string?)e!["k"] == "creature")!.AsObject();
        Assert.Equal(299, (int?)wolf["e"]);
        Assert.Equal(20.0, (double?)wolf["x"]);
        Assert.Equal(3, (int?)frame["counters"]!["emitted"]);

        JsonObject onlyCreatures = ObserverFrames.Frame(4, 1600, View(), new HashSet<string> { "creature" });
        Assert.Equal("creature", (string?)Assert.Single(onlyCreatures["entities"]!.AsArray())!["k"]);
        Assert.Equal(1, (int?)onlyCreatures["counters"]!["emitted"]);
    }

    [Fact]
    public void KindOf_NamesOnlyWhatAtlasDraws()
    {
        Assert.Equal("player", ObserverFrames.KindOf(Self));
        Assert.Equal("creature", ObserverFrames.KindOf(Wolf));
        Assert.Equal("creature", ObserverFrames.KindOf(ObjectGuid.WithEntry(HighGuid.Pet, 5, 1).Value));
        Assert.Equal("gameobject", ObserverFrames.KindOf(Chest));
        Assert.Null(ObserverFrames.KindOf(Item));
        Assert.Null(ObserverFrames.KindOf(ObjectGuid.WithEntry(HighGuid.Corpse, 0, 1).Value));
    }

    [Fact]
    public void ThePipeName_IsTheDaemonsPrefixPlusThePid()
        => Assert.Equal("arcanecore-mapfeed-123", ObserverFrames.PipeName(123));

    private static async Task<JsonObject> ReadAsync(StreamReader reader, CancellationToken ct)
        => JsonNode.Parse((await reader.ReadLineAsync(ct).ConfigureAwait(false))!)!.AsObject();

    private static async Task SendAsync(Stream pipe, string line, CancellationToken ct)
        => await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct).ConfigureAwait(false);

    private static async Task IgnoreBrokenPipeAsync(Task write)
    {
        try
        {
            await write;
        }
        catch (IOException)
        {
            // The server closed while the client was still writing: that is the point of the test.
        }
    }

    [Fact]
    public async Task OverARealPipe_HelloSubscribeFramesQueryAndRefusals_FollowTheMapFeedProtocol()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = timeout.Token;
        string name = "arcane-observer-test-" + Guid.NewGuid().ToString("N");
        await using ObserverFeedServer server = ObserverFeedServer.Start(name, 777, View);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        using var reader = new StreamReader(pipe, Encoding.UTF8);

        await SendAsync(pipe, """{"op":"subscribe","map":0}""", ct);
        JsonObject early = await ReadAsync(reader, ct);
        Assert.Equal("hello-required", (string?)early["code"]);

        await SendAsync(pipe, """{"op":"hello","protocol":1}""", ct);
        JsonObject hello = await ReadAsync(reader, ct);
        Assert.Equal("hello", (string?)hello["op"]);
        Assert.Equal(777, (int?)hello["pid"]);

        await SendAsync(pipe, """{"op":"query","id":5,"kind":"los","map":0,"a":[0,0,0],"b":[1,1,1]}""", ct);
        JsonObject result = await ReadAsync(reader, ct);
        Assert.Equal(5, (long?)result["id"]);
        Assert.False((bool?)result["ok"]);
        Assert.Equal("not-loaded", (string?)result["code"]);

        await SendAsync(pipe, """{"op":"subscribe","map":9}""", ct);
        Assert.Equal("map-not-loaded", (string?)(await ReadAsync(reader, ct))["code"]);

        await SendAsync(pipe, """{"op":"subscribe","map":0,"hz":10}""", ct);
        JsonObject frame = await ReadAsync(reader, ct);
        Assert.Equal("frame", (string?)frame["op"]);
        Assert.Equal(1, (long?)frame["seq"]);
        Assert.Equal(3, frame["entities"]!.AsArray().Count);
        JsonObject next = await ReadAsync(reader, ct);
        Assert.Equal(2, (long?)next["seq"]);

        await SendAsync(pipe, """{"op":"unsubscribe"}""", ct);
    }

    [Fact]
    public async Task AWrongProtocolAndAnUnknownOp_AreRefusedWithTheDaemonsCodes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = timeout.Token;
        string name = "arcane-observer-test-" + Guid.NewGuid().ToString("N");
        await using ObserverFeedServer server = ObserverFeedServer.Start(name, 777, View);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        using var reader = new StreamReader(pipe, Encoding.UTF8);

        await SendAsync(pipe, """{"op":"hello","protocol":2}""", ct);
        Assert.Equal("protocol", (string?)(await ReadAsync(reader, ct))["code"]);
        await SendAsync(pipe, "not json", ct);
        Assert.Equal("bad-json", (string?)(await ReadAsync(reader, ct))["code"]);
        await SendAsync(pipe, """{"op":"hello","protocol":1}""", ct);
        Assert.Equal("hello", (string?)(await ReadAsync(reader, ct))["op"]);
        await SendAsync(pipe, """{"op":"dance"}""", ct);
        Assert.Equal("unknown-op", (string?)(await ReadAsync(reader, ct))["code"]);
    }

    [Fact]
    public async Task ALineOverTheLimit_IsRefusedAndTheConnectionClosed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken ct = timeout.Token;
        string name = "arcane-observer-test-" + Guid.NewGuid().ToString("N");
        await using ObserverFeedServer server = ObserverFeedServer.Start(name, 777, View);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        using var reader = new StreamReader(pipe, Encoding.UTF8);

        // The pipe has no buffer, so the write stays pending until the server reads; the server stops reading at the limit, answers and closes.
        // Start the send, read the answer, then let the rest of the write find a closed pipe (the same pattern as the daemon's pipe tests).
        Task send = SendAsync(pipe, new string('x', ObserverFeedServer.MaxLineBytes + 1), ct);

        Assert.Equal("line-too-long", (string?)(await ReadAsync(reader, ct))["code"]);
        await IgnoreBrokenPipeAsync(send);
        Assert.Null(await reader.ReadLineAsync(ct)); // closed
    }

    [Fact]
    public void ASnapshot_ServesTheLatestPublishedView()
    {
        var snapshot = new ObserverSnapshot(View());
        ObserverView moved = ObserverFrames.ViewOf(1, Self, new MockPosition(5f, 6f, 7f), []);

        snapshot.Publish(moved);

        Assert.Same(moved, snapshot.Current);
    }
}
