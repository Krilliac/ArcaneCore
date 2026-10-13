using System.Text;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>LFG poll, bug report and the no-effect notices (vmangos MiscHandler.cpp:277-282, 342-344, 433-435, 566-571).</summary>
public sealed class MiscNoticeHandlersTests
{
    [Fact]
    public async Task LookingForGroup_AnswersWithNoListings_AndTheNoticesKeepTheSessionAlive()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LFGPOLL", "Lfgpoll");

        await client.SendAsync(WorldOpcode.CmsgPlayerLogout, []);
        await client.SendAsync(WorldOpcode.CmsgSetTargetObsolete, new byte[8]);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverQuestAutolaunch, []);
        await client.SendAsync(WorldOpcode.CmsgBug, Bug(suggestion: true, "More murlocs", "Creatures"));
        await client.SendAsync(WorldOpcode.MsgLookingForGroup, []);
        byte[] reply = await client.ReadUntilAsync(WorldOpcode.MsgLookingForGroup);
        Assert.Equal(0u, BitConverter.ToUInt32(reply, 0));
    }

    [Fact]
    public void Bug_ParsesTheReport_AndRefusesAShortPacket()
    {
        Assert.True(MiscNoticeHandlers.TryParseBug(Bug(false, "falls through floor", "Map"), out bool suggestion, out string content, out string type));
        Assert.False(suggestion);
        Assert.Equal("falls through floor", content);
        Assert.Equal("Map", type);
        Assert.False(MiscNoticeHandlers.TryParseBug([1, 0], out _, out _, out _));
    }

    private static byte[] Bug(bool suggestion, string content, string type)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(suggestion ? 1u : 0u));
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length + 1));
        bytes.AddRange(Encoding.UTF8.GetBytes(content));
        bytes.Add(0);
        bytes.AddRange(BitConverter.GetBytes((uint)type.Length + 1));
        bytes.AddRange(Encoding.UTF8.GetBytes(type));
        bytes.Add(0);
        return [.. bytes];
    }
}
