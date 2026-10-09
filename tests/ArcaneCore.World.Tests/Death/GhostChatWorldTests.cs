using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// A dead character still talks to the server: vmangos WorldSession::HandleMessagechatOpcode parses '.' commands
/// (ChatHandler::ParseCommands) for the living and the dead alike, and only Player::Say / Yell / TextEmote are gated on
/// being alive. Found in the wave-10 rehearsal: a released GM's <c>.server shutdown</c> was silently dropped.
/// </summary>
public sealed class GhostChatWorldTests
{
    private static async Task KillAndReleaseAsync(WorldTestHost host, string name)
    {
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
        });
        await host.WaitForWorldAsync(() => (host.World.FindOnlinePlayer(name)!.Flags & PlayerFlags.Ghost) != 0, "the spirit to be a ghost");
    }

    [Fact]
    public async Task AReleasedGhostGm_RunsCommands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GHOSTGM", "Ghostgm", AccountSecurity.Administrator);
        await KillAndReleaseAsync(host, "Ghostgm");
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ADeadPlayerNotYetReleased_RunsCommands()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("CORPSEGM", "Corpsegm");
        await host.OnWorldAsync(() =>
        {
            Player p = host.World.FindOnlinePlayer("Corpsegm")!;
            p.Health = 0;
            p.Map!.Combat.KillPlayer(p);
        });
        await player.CollectAsync();

        await player.SendChatAsync(ChatType.Say, Language.Common, ".server info");

        Assert.StartsWith("Core revision: ", (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AGhostsSay_IsNotHeardByTheLiving()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient ghost = await host.EnterWorldAsync("GHOSTSAY", "Ghostsay");
        await using WorldTestClient living = await host.EnterWorldAsync("LIVESAY", "Livesay");
        await KillAndReleaseAsync(host, "Ghostsay");
        await ghost.CollectAsync();
        await living.CollectAsync();

        await ghost.SendChatAsync(ChatType.Say, Language.Common, "boo");

        Assert.DoesNotContain((await living.CollectAsync()), p => p.Opcode == WorldOpcode.SmsgMessagechat);
    }

    [Fact]
    public async Task AGhost_CanWhisperTheLiving()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient ghost = await host.EnterWorldAsync("GHOSTWSP", "Ghostwsp");
        await using WorldTestClient living = await host.EnterWorldAsync("LIVEWSP", "Livewsp");
        await KillAndReleaseAsync(host, "Ghostwsp");
        await ghost.CollectAsync();
        await living.CollectAsync();

        await ghost.SendChatAsync(ChatType.Whisper, Language.Common, "hello from beyond", "Livewsp");

        ChatMessage heard = await living.ReadChatAsync();
        Assert.Equal(ChatType.Whisper, heard.Type);
        Assert.Equal("hello from beyond", heard.Text);
    }
}
