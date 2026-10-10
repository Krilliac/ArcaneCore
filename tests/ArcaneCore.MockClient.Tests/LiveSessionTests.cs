using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>The offline parts of <c>arcane-mock live</c> (the dev runner's wire client): what it sends and how it reads replies.</summary>
public sealed class LiveSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-live-" + Guid.NewGuid().ToString("N"));

    public LiveSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void TheSayPacket_IsReadBackByTheServersOwnReader_AsASayWithTheText()
    {
        byte[] payload = LiveSession.SayPacket(".server info");

        var reader = new PacketReader(payload);
        Assert.Equal((uint)ChatType.Say, reader.ReadUInt32());
        Assert.Equal((uint)Language.Common, reader.ReadUInt32());
        Assert.Equal(".server info", reader.ReadCString());
    }

    [Fact]
    public void ASystemLineBuiltByTheServer_IsDecoded_AndOtherChatTypesAreNot()
    {
        byte[] system = ChatPackets.BuildSystemMessage("ArcaneCore test line");
        byte[] notice = ChatPackets.BuildMessage(ChatType.Whisper, Language.Universal, ObjectGuid.Empty, "psst", ChatTag.None);

        Assert.Equal("ArcaneCore test line", LiveSession.DecodeSystemLine(system));
        Assert.Null(LiveSession.DecodeSystemLine(notice));
        Assert.Null(LiveSession.DecodeSystemLine([0x0A, 0, 0]));
    }

    [Fact]
    public void ANotificationBuiltByTheServer_IsDecoded()
    {
        Assert.Equal("You don't know that language", LiveSession.DecodeNotification(ChatPackets.BuildNotification("You don't know that language")));
        Assert.Equal(string.Empty, LiveSession.DecodeNotification([]));
    }

    [Fact]
    public void AWhisperScriptLine_IsReadBackByTheServersOwnReader_AsAWhisperToTheTarget()
    {
        var reader = new PacketReader(LiveSession.ScriptPacket("/w Ironwander what level are you?"));
        Assert.Equal((uint)ChatType.Whisper, reader.ReadUInt32());
        Assert.Equal((uint)Language.Common, reader.ReadUInt32());
        Assert.Equal("Ironwander", reader.ReadCString());
        Assert.Equal("what level are you?", reader.ReadCString());
        Assert.Equal(0, reader.Remaining);

        // Anything else (and a whisper without text) is a say, as before.
        Assert.Equal(LiveSession.SayPacket(".gps"), LiveSession.ScriptPacket(".gps"));
        Assert.Equal(LiveSession.SayPacket("/w Ironwander"), LiveSession.ScriptPacket("/w Ironwander"));
    }

    [Fact]
    public void PlayerLinesBuiltByTheServer_AreDecodedWithTypeAndSender_AndSystemLinesAreNot()
    {
        var bot = new ObjectGuid(0x2A);
        byte[] whisper = ChatPackets.BuildMessage(ChatType.Whisper, Language.Common, bot, "I'm level 7.", ChatTag.None);
        byte[] say = ChatPackets.BuildMessage(ChatType.Say, Language.Common, bot, "Hello there", ChatTag.None);

        Assert.Equal("whisper from 0x2A: I'm level 7.", LiveSession.DecodePlayerLine(whisper));
        Assert.Equal("say from 0x2A: Hello there", LiveSession.DecodePlayerLine(say));
        Assert.Null(LiveSession.DecodePlayerLine(ChatPackets.BuildSystemMessage("ArcaneCore test line")));
        Assert.Null(LiveSession.DecodePlayerLine([0x07, 0, 0]));
    }

    [Fact]
    public void Credentials_ComeFromAFile_NeverAnArgument()
    {
        string file = Path.Combine(_dir, "dev-account.txt");
        File.WriteAllLines(file, ["# generated", "account=DEVGM", "password=Abc123Xyz"]);

        LiveSession.Options options = LiveSession.Parse(["--credentials-file", file, "--say", ".gps", "--say", ".server info", "--interval-ms", "250"]);

        Assert.Equal("DEVGM", options.Account);
        Assert.Equal("Abc123Xyz", options.Password);
        Assert.Equal([".gps", ".server info"], options.Say);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.Interval);
        Assert.Equal(3724, options.Realm.Port);
    }

    [Fact]
    public void ARealmThatIsNotLoopback_IsRefused_AndNoPasswordIsAnError()
    {
        string file = Path.Combine(_dir, "c.txt");
        File.WriteAllLines(file, ["account=A", "password=B"]);

        Assert.ThrowsAny<Exception>(() => LiveSession.Parse(["--credentials-file", file, "--realm", "8.8.8.8:3724"]));
        Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", "ARCANE_LIVE_TEST_UNSET_" + Guid.NewGuid().ToString("N")]));
        Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--credentials-file", file, "--bogus"]));
    }

    [Fact]
    public void ExplicitCharacterSelection_IsMarkedAndDoesNotUseImplicitFallback()
    {
        string file = Path.Combine(_dir, "c-explicit.txt");
        File.WriteAllLines(file, ["account=A", "password=B"]);

        LiveSession.Options explicitOptions = LiveSession.Parse(["--credentials-file", file, "--character", "Fresh"]);
        LiveSession.Options implicitOptions = LiveSession.Parse(["--credentials-file", file]);

        Assert.True(explicitOptions.CharacterExplicit);
        Assert.False(implicitOptions.CharacterExplicit);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void LogoutResponseAcceptsImmediateAndDelayedSuccess(byte instant)
        => Assert.True(LiveSession.IsSuccessfulLogoutResponse([0, 0, 0, 0, instant]));

    [Fact]
    public void LogoutResponseRejectsFailureAndMalformedBodies()
    {
        Assert.False(LiveSession.IsSuccessfulLogoutResponse([1, 0, 0, 0, 1]));
        Assert.False(LiveSession.IsSuccessfulLogoutResponse([0, 0, 0, 0]));
        Assert.False(LiveSession.IsSuccessfulLogoutResponse([0, 0, 0, 0, 2]));
    }

    [Fact]
    public void SelectionHelperRequiresExactExplicitName_ButKeepsImplicitFallback()
    {
        MockCharacter[] characters = [new(7, "Existing", 1, 1, 0, [], 1, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])];

        MockCharacter? exact = LiveSession.SelectCharacter(characters, "existing", true, out bool exactCreate);
        MockCharacter? missing = LiveSession.SelectCharacter(characters, "Fresh", true, out bool missingCreate);
        MockCharacter? implicitChoice = LiveSession.SelectCharacter(characters, "Fresh", false, out bool implicitCreate);

        Assert.Equal((ulong)7, exact!.Guid);
        Assert.False(exactCreate);
        Assert.Null(missing);
        Assert.True(missingCreate);
        Assert.Equal((ulong)7, implicitChoice!.Guid);
        Assert.False(implicitCreate);

        Assert.Null(LiveSession.SelectCharacter([], "Fresh", false, out bool emptyCreate));
        Assert.True(emptyCreate);
    }

    /// <summary>
    /// In a crowd the server never falls silent (movement and update packets every few milliseconds). The session still sends its
    /// script lines and its say lines on the interval, and the stream stays framed: the next read after the session is a whole
    /// packet. Before, every send waited for 400 ms of silence first, so a mock client among the stress bots sent its first line
    /// and nothing after it (no GM command, no observer probe) for the whole run.
    /// </summary>
    [Fact]
    public async Task UnderAContinuousPacketStream_ScriptAndSayLinesAreStillSentOnTheInterval()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync(deadline.Token).AsTask();
        await using WorldClient client = await WorldClient.ConnectAsync((IPEndPoint)listener.LocalEndpoint, deadline.Token);
        using TcpClient server = await accepted;
        NetworkStream stream = server.GetStream();

        // The server streams an SMSG_PONG every 5 ms (about 15 on the Windows timer), never 400 ms apart, until the test ends.
        using var streaming = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        int streamed = 0;
        Task producer = Task.Run(async () =>
        {
            byte[] pong = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(pong, 6);
            BinaryPrimitives.WriteUInt16LittleEndian(pong.AsSpan(2), (ushort)WorldOpcode.SmsgPong);
            try
            {
                while (true)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(pong.AsSpan(4), (uint)Interlocked.Increment(ref streamed));
                    await stream.WriteAsync(pong, streaming.Token);
                    await Task.Delay(5, streaming.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        // What the client sends: its chat lines, read whole frame by frame.
        var chat = new ConcurrentQueue<string>();
        Task consumer = Task.Run(async () =>
        {
            byte[] header = new byte[6];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, streaming.Token);
                    byte[] payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header) - 4];
                    await stream.ReadExactlyAsync(payload, streaming.Token);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2)) == (uint)WorldOpcode.CmsgMessagechat)
                        chat.Enqueue(Encoding.UTF8.GetString(payload.AsSpan(8, payload.Length - 9)));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException) { }
        });

        string script = Path.Combine(_dir, "script.txt");
        File.WriteAllLines(script, [".gm on"]);
        var options = new LiveSession.Options((IPEndPoint)listener.LocalEndpoint, "A", "B", "C", [".gps"], TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(2), StopFile: null, script, CharacterExplicit: false);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(8)); // the session itself lasts 2 seconds
        int sent = -1;
        try
        {
            sent = await LiveSession.ExchangeAsync(client, options, TextWriter.Null, "test", bounded.Token);
        }
        catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }

        // The stream is still framed: the next read is a whole SMSG_PONG with its counter.
        WorldFrame next = await client.ReadAsync(deadline.Token);
        Assert.Equal((ushort)WorldOpcode.SmsgPong, next.Opcode);
        Assert.Equal(4, next.Payload.Length);

        await Task.Delay(200, deadline.Token); // the last lines reach the server
        await streaming.CancelAsync();
        await Task.WhenAll(producer, consumer);
        string[] lines = [.. chat];
        Assert.True(Volatile.Read(ref streamed) > 50, $"the server streamed only {streamed} packets");
        Assert.Equal(".gm on", lines.FirstOrDefault());
        Assert.True(lines.Count(line => line == ".gps") >= 5, $"sent {lines.Length} chat lines under the stream: {string.Join(" | ", lines)}");
        Assert.Equal(lines.Length, sent);
    }
}