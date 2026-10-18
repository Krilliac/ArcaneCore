using System.IO.Pipes;
using System.Security.Cryptography;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

public sealed class FeedDiscoveryTests
{
    [Theory]
    [InlineData("arcanecore-mapfeed-1234", true, 1234)]
    [InlineData("ARCANECORE-MAPFEED-77", true, 77)]
    [InlineData("arcanecore-mapfeed-2147483647", true, int.MaxValue)]
    [InlineData("arcanecore-mapfeed-", false, 0)]
    [InlineData("arcanecore-mapfeed-12a", false, 0)]
    [InlineData("arcanecore-mapfeed--5", false, 0)]
    [InlineData("arcanecore-mapfeed-+5", false, 0)]
    [InlineData("arcanecore-mapfeed- 5", false, 0)]
    [InlineData("arcanecore-mapfeed-0", false, 0)]
    [InlineData("arcanecore-mapfeed-99999999999", false, 0)]
    [InlineData("arcanecore-world-1234", false, 0)]
    [InlineData("xarcanecore-mapfeed-1234", false, 0)]
    [InlineData("", false, 0)]
    public void ThePidIsTheDigitsAfterThePrefix_AndNothingElseCounts(string name, bool ok, int expected)
    {
        Assert.Equal(ok, PipeFeedDiscovery.TryParsePid(name, out int pid));
        Assert.Equal(expected, pid);
    }

    [Fact]
    public void FindFeeds_ListsEachFeedOnceAscending_AndIgnoresEveryOtherPipe()
    {
        string[] pipes =
        [
            "arcanecore-mapfeed-300",
            "arcanecore-world-5",
            "arcanecore-mapfeed-20",
            "ARCANECORE-MAPFEED-300",
            "arcanecore-mapfeed-bogus",
            "mojo.1234.5678",
            "arcanecore-mapfeed-4000",
        ];

        IReadOnlyList<int> found = new PipeFeedDiscovery(() => pipes).FindFeeds();

        Assert.Equal(new[] { 20, 300, 4000 }, found);
    }

    [Fact]
    public void PipeExists_MatchesTheNameIgnoringCase()
    {
        var discovery = new PipeFeedDiscovery(() => ["Arcanecore-Mapfeed-9"]);

        Assert.True(discovery.PipeExists("arcanecore-mapfeed-9"));
        Assert.False(discovery.PipeExists("arcanecore-mapfeed-10"));
    }

    [Fact]
    public void ThePipeNameIsTheDocumentedOne()
    {
        Assert.Equal("arcanecore-mapfeed-1234", LiveOptions.Default.PipeNameFor(1234));
    }

    [NonWindowsFact]
    public void WithoutWindowsPipes_DiscoveryIsRefusedAsUnavailable_AndPipeExistsCannotTell()
    {
        var discovery = new PipeFeedDiscovery();

        CommandException e = Assert.Throws<CommandException>(discovery.FindFeeds);

        Assert.Equal(CommandException.Unavailable, e.Code);
        Assert.Contains("pass the world daemon's pid", e.Message, StringComparison.Ordinal);
        Assert.Null(discovery.PipeExists("arcanecore-mapfeed-1"));
    }

    [Fact]
    public async Task AnExplicitPid_ConnectsThroughTheRealDiscovery_OnEveryPlatform_WithoutListingPipes()
    {
        // The real discovery refuses to list pipes off Windows, so a connect that listed them would fail there; with a pid it must not.
        await using var rig = new LiveRig(discovery: new PipeFeedDiscovery());

        await rig.ConnectAsync(pid: 4242);

        Assert.True(rig.Feed.IsConnected);
    }

    [NonWindowsFact]
    public async Task WithoutAPid_ConnectingOffWindows_IsUnavailable_AndSaysToPassThePid()
    {
        await using var rig = new LiveRig(discovery: new PipeFeedDiscovery());

        CommandResult result = await rig.RunAsync("live.connect");

        Assert.False(result.Ok);
        Assert.Equal(CommandException.Unavailable, result.ErrorCode);
        Assert.Contains("pass the world daemon's pid", result.ErrorMessage, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task OnWindows_TheRealPipeNamespaceIsListed_WithoutTakingTheSingleInstanceOfTheFeed()
    {
        // A pid no process has: the listing is by name only.
        int pid = RandomNumberGenerator.GetInt32(1_000_000_000, int.MaxValue);
        string name = LiveProtocol.PipeName(pid);
        await using var server = new FakeFeedServer(name);
        var discovery = new PipeFeedDiscovery();

        IReadOnlyList<int> found = discovery.FindFeeds();
        bool? exists = discovery.PipeExists(name);
        bool? missing = discovery.PipeExists(LiveProtocol.PipeName(pid - 1));

        Assert.Contains(pid, found);
        Assert.True(exists);
        Assert.False(missing);

        // The single instance is still free: a real client connects after the listing.
        Task accept = server.AcceptAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accept;
        Assert.True(client.IsConnected);
    }
}
