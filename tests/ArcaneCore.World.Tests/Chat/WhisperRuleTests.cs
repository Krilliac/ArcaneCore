using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

/// <summary>
/// Whispering staff after vmangos ChatHandler.cpp:405-428 and MasterPlayer::AcceptsWhispersFrom
/// (MasterPlayer.h:99): a plain player only reaches a game master who accepts whispers (or who
/// whispered them first); <c>.whispers on|off</c> (CharacterCommands.cpp:1283, SEC_MODERATOR).
/// </summary>
public sealed class WhisperRuleTests
{
    private static async Task<(WorldTestHost Host, WorldTestClient Player, WorldTestClient Gm)> StartAsync(Action<ChatOptions>? options = null)
    {
        WorldTestHost host = WorldTestHost.Start();
        options?.Invoke(host.WorldServices.GetRequiredService<ChatFeature>().Options);
        WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");
        WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.Moderator);
        await player.CollectAsync();
        await gm.CollectAsync();
        return (host, player, gm);
    }

    private static async Task<string> SystemReplyAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    [Fact]
    public async Task AGameMaster_IsHiddenFromPlainPlayers_UntilItAcceptsWhispers()
    {
        (WorldTestHost host, WorldTestClient player, WorldTestClient gm) = await StartAsync();
        await using (host) await using (player) await using (gm)
        {
            // The default state of a staff account is "not accepting": the target reads as offline.
            await player.SendChatAsync(ChatType.Whisper, Language.Common, "hello?", target: "Staff");
            Assert.Equal("Staff", new PacketReader(await player.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound)).ReadCString());
            Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

            Assert.Equal("Accepting Whisper: OFF", await SystemReplyAsync(gm, ".whispers"));
            Assert.Equal("Accepting Whisper: ON", await SystemReplyAsync(gm, ".whispers on"));
            Assert.Equal("Accepting Whisper: ON", await SystemReplyAsync(gm, ".whispers"));

            await player.SendChatAsync(ChatType.Whisper, Language.Common, "hello again", target: "Staff");
            Assert.Equal("hello again", (await gm.ReadChatAsync()).Text);

            Assert.Equal("Accepting Whisper: OFF", await SystemReplyAsync(gm, ".whispers off"));
            await player.CollectAsync();
            await player.SendChatAsync(ChatType.Whisper, Language.Common, "gone", target: "Staff");
            await player.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound);
        }
    }

    [Fact]
    public async Task WhisperingAPlayerFirst_LetsThemReply_UntilWhispersAreSwitchedOff()
    {
        (WorldTestHost host, WorldTestClient player, WorldTestClient gm) = await StartAsync();
        await using (host) await using (player) await using (gm)
        {
            // vmangos MasterPlayer::Whisper: a staff member who is not accepting whispers adds the
            // receiver to its allowed whisperers.
            await gm.SendChatAsync(ChatType.Whisper, Language.Common, "how can I help?", target: "Player");
            Assert.Equal("how can I help?", (await player.ReadChatAsync()).Text);
            await gm.CollectAsync(); // the whisper inform

            await player.SendChatAsync(ChatType.Whisper, Language.Common, "my quest is stuck", target: "Staff");
            Assert.Equal("my quest is stuck", (await gm.ReadChatAsync()).Text);

            // .whispers off clears the allowed list (HandleWhispersCommand: ClearAllowedWhisperers).
            await SystemReplyAsync(gm, ".whispers off");
            await player.CollectAsync();
            await player.SendChatAsync(ChatType.Whisper, Language.Common, "hello?", target: "Staff");
            await player.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound);
        }
    }

    [Fact]
    public async Task StaffToStaff_WhispersAreNotHidden()
    {
        (WorldTestHost host, WorldTestClient player, WorldTestClient gm) = await StartAsync();
        await using (host) await using (player) await using (gm)
        await using (WorldTestClient other = await host.EnterWorldAsync("OTHERSTAFF", "Otherstaff", AccountSecurity.GameMaster))
        {
            await other.CollectAsync();
            await other.SendChatAsync(ChatType.Whisper, Language.Common, "colleague", target: "Staff");
            Assert.Equal("colleague", (await gm.ReadChatAsync()).Text);
        }
    }

    [Fact]
    public async Task TheWhispersCommand_RejectsOtherValues_WithTheReferenceText()
    {
        (WorldTestHost host, WorldTestClient player, WorldTestClient gm) = await StartAsync();
        await using (host) await using (player) await using (gm)
        {
            // ExtractOnOff takes "on"/"ON"/"off"/"OFF" only; LANG_USE_BOL (259) otherwise.
            Assert.Equal("Incorrect value, use on or off", await SystemReplyAsync(gm, ".whispers maybe"));
            Assert.Equal("Incorrect value, use on or off", await SystemReplyAsync(gm, ".whispers 1"));
            Assert.Equal("Accepting Whisper: ON", await SystemReplyAsync(gm, ".whispers ON"));
        }
    }

    [Fact]
    public async Task GmWhisperingToOption_MakesStaffAcceptWhispersAtLogin()
    {
        (WorldTestHost host, WorldTestClient player, WorldTestClient gm) = await StartAsync(o => o.GmWhisperingTo = 1);
        await using (host) await using (player) await using (gm)
        {
            await player.SendChatAsync(ChatType.Whisper, Language.Common, "open door", target: "Staff");
            Assert.Equal("open door", (await gm.ReadChatAsync()).Text);
            Assert.Equal("Accepting Whisper: ON", await SystemReplyAsync(gm, ".whispers"));
        }
    }
}
