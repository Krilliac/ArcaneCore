using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.PacketHeuristics;

/// <summary>The <c>.packetcheck</c> command: the runtime switch, over chat (the real route) and over the console route.</summary>
public sealed class PacketHeuristicsCommandTests
{
    private const int StatusLineCount = 2 + PacketChecks.Count + 1;

    private static async Task<List<string>> RunAsync(WorldTestClient client, string line, int lines)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        var replies = new List<string>();
        for (int i = 0; i < lines; i++)
        {
            ChatMessage reply = await client.ReadChatAsync();
            Assert.Equal(ChatType.System, reply.Type);
            replies.Add(reply.Text);
        }

        return replies;
    }

    [Fact]
    public async Task TheCommand_SwitchesTheChecksAtRuntime_AndStatusReportsEveryCheck()
    {
        await using var host = WorldTestHost.Start();
        PacketHeuristicsFeature feature = PacketHeuristicsTestKit.FeatureOf(host);
        (WorldTestClient admin, _) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PHCMD", "Phcmdone", AccountSecurity.Administrator);
        await using (admin)
        {
            Assert.False(feature.Settings.Enabled);

            List<string> status = await RunAsync(admin, ".packetcheck status", StatusLineCount);
            Assert.StartsWith("Packet heuristics: off (report only)", status[0], StringComparison.Ordinal);
            for (int i = 0; i < PacketChecks.Count; i++)
            {
                Assert.Contains(PacketChecks.Code((PacketCheck)i), status[2 + i], StringComparison.Ordinal);
            }

            List<string> on = await RunAsync(admin, ".packetcheck on", StatusLineCount);
            Assert.StartsWith("Packet heuristics: ON", on[0], StringComparison.Ordinal);
            Assert.True(feature.Settings.Enabled);

            List<string> bots = await RunAsync(admin, ".packetcheck bots on", StatusLineCount);
            Assert.Contains("Bots INCLUDED", bots[0], StringComparison.Ordinal);
            Assert.False(feature.Settings.ExemptBots);

            List<string> staff = await RunAsync(admin, ".packetcheck staff on", StatusLineCount);
            Assert.Contains("staff INCLUDED", staff[0], StringComparison.Ordinal);
            Assert.False(feature.Settings.ExemptStaff);

            await RunAsync(admin, ".packetcheck bots off", StatusLineCount);
            await RunAsync(admin, ".packetcheck staff off", StatusLineCount);
            Assert.True(feature.Settings.ExemptBots);
            Assert.True(feature.Settings.ExemptStaff);

            List<string> reset = await RunAsync(admin, ".packetcheck reset", 1);
            Assert.StartsWith("Packet heuristics: counters zeroed", reset[0], StringComparison.Ordinal);

            List<string> off = await RunAsync(admin, ".packetcheck off", StatusLineCount);
            Assert.StartsWith("Packet heuristics: off", off[0], StringComparison.Ordinal);
            Assert.False(feature.Settings.Enabled);
        }
    }

    [Fact]
    public async Task APlayerWithoutTheRank_CannotSwitchIt_AndABadArgumentGetsTheSyntax()
    {
        await using var host = WorldTestHost.Start();
        PacketHeuristicsFeature feature = PacketHeuristicsTestKit.FeatureOf(host);
        (WorldTestClient player, _) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PHCMDP", "Phcmdplay");
        await using (player)
        {
            await player.SendChatAsync(ChatType.Say, Language.Common, ".packetcheck on");
            await Task.Delay(200);
            Assert.False(feature.Settings.Enabled);
        }

        (WorldTestClient admin, _) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PHCMDA", "Phcmdadmin", AccountSecurity.Administrator);
        await using (admin)
        {
            List<string> reply = await RunAsync(admin, ".packetcheck bots maybe", 1);
            Assert.Contains("Syntax: .packetcheck bots on|off", reply[0], StringComparison.Ordinal);
            Assert.False(feature.Settings.Enabled);
        }
    }

    [Fact]
    public async Task TheConsoleRoute_RunsTheSameCommand_ThroughThePostedSet()
    {
        await using var host = WorldTestHost.Start();
        PacketHeuristicsFeature feature = PacketHeuristicsTestKit.FeatureOf(host);
        (WorldTestClient client, WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "PHCON", "Phconone", AccountSecurity.Administrator);
        await using (client)
        {
            var replies = new List<string>();
            bool enabledBefore = feature.Settings.Enabled;
            (CommandContext context, bool appliedInline) = await host.OnWorldAsync(() =>
            {
                CommandTable table = ChatCommands.CreateTable();
                CommandContext ctx = CommandContext.ForConsole(session, table, replies.Add);
                ChatCommand command = table.Resolve("packetcheck on", AccountSecurity.Administrator)!;
                Assert.True(command.Handler!(ctx, string.Empty));
                return (ctx, feature.Settings.Enabled != enabledBefore); // the set is queued with World.Post: it cannot have run yet
            });
            context.Complete();
            await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30)); // the command is open until the posted set has replied

            Assert.False(appliedInline);
            Assert.True(feature.Settings.Enabled);
            Assert.StartsWith("Packet heuristics: ON", replies[0], StringComparison.Ordinal);
            Assert.Equal(StatusLineCount, replies.Count);
        }
    }
}
