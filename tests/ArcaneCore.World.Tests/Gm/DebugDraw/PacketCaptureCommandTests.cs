using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.Sniff;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.DebugDraw;

/// <summary>
/// <c>.debug capture on|off &lt;player&gt;</c> end to end over loopback: Administrator only, refused while
/// <c>Diagnostics:PacketCapture:Enabled</c> is off (the default), and when on, a PKT 3.1 trace of the target's plaintext
/// packets in both directions that <c>arcane-sniff decode</c> reads back.
/// </summary>
public sealed class PacketCaptureCommandTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void Level_DebugCaptureNeedsAnAdministrator()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("debug capture", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("debug capture", AccountSecurity.Administrator));
    }

    /// <summary>The next system line the client receives (its own say echoes and others' chat are skipped).</summary>
    private static async Task<string> CommandAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync(Wait);
            if (message.Type == ChatType.System) return message.Text;
        }
    }

    [Fact]
    public async Task Capture_IsRefusedWhileDiagnosticsLeavesItOff()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Capturer", AccountSecurity.Administrator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Captured");

        Assert.Equal("Packet capture is disabled by Diagnostics:PacketCapture:Enabled.", await CommandAsync(admin, ".debug capture on Captured"));
    }

    [Fact]
    public async Task Capture_WritesTheTargetsPacketsBothWays_AndSniffDecodesThem()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcane-capture-" + Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?>
        {
            ["Diagnostics:PacketCapture:Enabled"] = "true",
            ["Diagnostics:PacketCapture:Directory"] = directory,
        };
        try
        {
            await using (WorldTestHost host = WorldTestHost.Start(configureServices: services =>
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())))
            {
                await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Capturer", AccountSecurity.Administrator);
                await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Captured");

                string on = await CommandAsync(admin, ".debug capture on Captured");
                Assert.StartsWith("Packet capture on: ", on, StringComparison.Ordinal);
                Assert.Equal("Packet capture is already on for this session.", await CommandAsync(admin, ".debug capture on Captured"));

                // The target's own say: CMSG_MESSAGECHAT in, its SMSG_MESSAGECHAT echo out.
                await target.SendChatAsync(ChatType.Say, Language.Common, "capture me");
                ChatMessage echo;
                do echo = await target.ReadChatAsync(Wait);
                while (echo.Text != "capture me");

                // A ping the oracle decodes in both directions (CMSG_PING sequence 0x1234, round time 0; SMSG_PONG echoes the sequence).
                await target.SendAsync(WorldOpcode.CmsgPing, [0x34, 0x12, 0, 0, 0, 0, 0, 0]);
                await target.ReadUntilAsync(WorldOpcode.SmsgPong, Wait);

                string off = await CommandAsync(admin, ".debug capture off Captured");
                Assert.Equal("Packet capture off: " + on["Packet capture on: ".Length..], off);
                Assert.Equal("Packet capture was not on for this session.", await CommandAsync(admin, ".debug capture off Captured"));
            }

            string trace = Assert.Single(Directory.GetFiles(directory, "*.pkt"));
            using var text = new StringWriter();
            _ = await SniffDecoder.DecodeAsync(trace, text);
            string decoded = text.ToString();
            string[] lines = decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Any(l => l.Contains(" CMSG CMSG_MESSAGECHAT ", StringComparison.Ordinal) && l.Contains("\"Message\":\"capture me\"", StringComparison.Ordinal)), decoded);
            // The vendored oracle has no SMSG_MESSAGECHAT reader (allowlisted in docs/integration/wire-oracle-histogram-20261008.tsv): the record is listed with its failure.
            Assert.True(lines.Any(l => l.Contains(" SMSG SMSG_MESSAGECHAT ", StringComparison.Ordinal) && l.Contains("decode failed at byte", StringComparison.Ordinal)), decoded);
            Assert.True(lines.Any(l => l.Contains(" CMSG CMSG_PING ", StringComparison.Ordinal) && l.Contains("\"SequenceId\":4660", StringComparison.Ordinal)), decoded);
            Assert.True(lines.Any(l => l.Contains(" SMSG SMSG_PONG ", StringComparison.Ordinal) && l.Contains("\"SequenceId\":4660", StringComparison.Ordinal)), decoded);
            // Plaintext payloads, both directions: the say text is in the CMSG and in the SMSG record.
            byte[] raw = await File.ReadAllBytesAsync(trace);
            byte[] said = System.Text.Encoding.UTF8.GetBytes("capture me");
            Assert.Equal(2, Occurrences(raw, said));
            // Only the target's session was traced: the administrator's command lines never reach the target.
            Assert.DoesNotContain(".debug capture", decoded, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static int Occurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int at = haystack.AsSpan().IndexOf(needle); at >= 0; count++)
        {
            int next = haystack.AsSpan(at + 1).IndexOf(needle);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return count;
    }
}
