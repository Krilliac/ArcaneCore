using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Tests.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>How <see cref="LiveActions.CapsAsync"/> treats a failed lookup: a refusal is remembered briefly, a missing connection is not.</summary>
public sealed class LiveActionsCapsCacheTests
{
    private sealed class CountingRequester : IFeedRequester
    {
        public int CapsAsked { get; private set; }

        public bool Refuse { get; set; } = true;

        public Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken)
        {
            if (message["op"]!.GetValue<string>() == "caps")
            {
                CapsAsked++;
            }

            return Task.FromResult(Refuse
                ? new JsonObject { ["ok"] = false, ["code"] = "denied", ["message"] = "not for you" }
                : new JsonObject { ["ok"] = true, ["actions"] = true, ["templates"] = new JsonArray("kill") });
        }
    }

    [Fact]
    public async Task ARefusedCapsAnswer_IsRemembered_ForTheRefusalWindow_AndForceAsksAgain()
    {
        var requester = new CountingRequester();
        var clock = new ManualTime();
        var actions = new LiveActions(() => requester, new FakeDirectory(), () => 0, null, clock);

        CommandException first = await Assert.ThrowsAsync<CommandException>(() => actions.CapsAsync(false, CancellationToken.None));
        CommandException second = await Assert.ThrowsAsync<CommandException>(() => actions.CapsAsync(false, CancellationToken.None));

        Assert.Equal(1, requester.CapsAsked);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(CommandException.Failed, second.Code);

        await Assert.ThrowsAsync<CommandException>(() => actions.CapsAsync(true, CancellationToken.None));
        Assert.Equal(2, requester.CapsAsked);

        clock.Advance(TimeSpan.FromSeconds(LiveActions.CapsRefusalSeconds + 1));
        requester.Refuse = false;
        ActionCaps caps = await actions.CapsAsync(false, CancellationToken.None);
        Assert.Equal(3, requester.CapsAsked);
        Assert.True(caps.Actions);
    }

    [Fact]
    public async Task ANotConnectedFailure_IsNeverRemembered()
    {
        var requester = new CountingRequester { Refuse = false };
        IFeedRequester? current = null;
        var actions = new LiveActions(() => current, new FakeDirectory(), () => 0, null, new ManualTime());

        CommandException down = await Assert.ThrowsAsync<CommandException>(() => actions.CapsAsync(false, CancellationToken.None));
        Assert.Equal(CommandException.Unavailable, down.Code);

        current = requester;
        ActionCaps caps = await actions.CapsAsync(false, CancellationToken.None);

        Assert.True(caps.Actions);
        Assert.Equal(1, requester.CapsAsked);
    }
}
