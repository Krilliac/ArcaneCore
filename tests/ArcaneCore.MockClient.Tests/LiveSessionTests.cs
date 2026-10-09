using ArcaneCore.Game;
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
}
