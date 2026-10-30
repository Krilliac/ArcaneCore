using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.cooldown list, clear and clearclientside (vmangos UnitCommands.cpp:2597-2665).</summary>
public sealed class CooldownCommandTests
{
    private const uint Spell = 90001;

    [Fact]
    public void Children_AreGameMasterCommands()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "cooldown list", "cooldown clear", "cooldown clearclientside" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Theory]
    [InlineData(0u, "0ms")]
    [InlineData(1500u, "1s 500ms")]
    [InlineData(3_723_000u, "1h 2m 3s")]
    public void Duration_LeavesOutZeroParts(uint ms, string expected) => Assert.Equal(expected, SpellCommands.Duration(ms));

    [Fact]
    public async Task List_ShowsARunningCooldown_AndClearRemovesIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CDGM", "Cdgm", AccountSecurity.GameMaster);
        long restoredAtMs = 0;
        await host.OnWorldAsync(() =>
        {
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            spells.Store = new SpellStore([.. spells.Store.All, new SpellInfo { Id = Spell, Name = "Test Cooldown" }], [], []);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            spells.RestoreCooldowns(host.World.FindOnlinePlayer("Cdgm")!, [new PersistedCooldown(SpellCooldownKind.Spell, Spell, now + 600_000)], now);
            restoredAtMs = host.World.Clock.ElapsedMs;
        });
        // Cooldown ends run on the tick-start game clock, so the list shows exactly 10m until a tick has begun after the restore.
        await host.WaitForWorldAsync(() => host.World.Clock.ElapsedMs > restoredAtMs, "game clock to move past the restore");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cooldown list");
        Assert.StartsWith($"Spell {Spell} (Test Cooldown) has 9m", (await gm.ReadChatAsync()).Text);
        Assert.Equal("Cdgm has 1 cooldowns.", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".cooldown clear {Spell}");
        Assert.Equal($"Cleared the cooldown of spell {Spell} for Cdgm.", (await gm.ReadChatAsync()).Text);
        Assert.Empty(await host.PlayerStateAsync("Cdgm", p => host.WorldServices.GetRequiredService<SpellFeature>().System.GetActiveCooldowns(p).ToArray()));
    }
}