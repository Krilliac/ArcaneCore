using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Teleport;
using ArcaneCore.World.Spells;
using ArcaneCore.Kernel.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.unstuck (vmangos HandleUnstuckCommand, TeleportCommands.cpp:1068) and the Undying Soul expiry (SpellAuras.cpp:1919).</summary>
public sealed class UnstuckCommandTests
{
    [Fact]
    public void Unstuck_IsAPlayerCommand()
        => Assert.NotNull(ChatCommands.CreateTable().Resolve("unstuck", AccountSecurity.Player));

    [Fact]
    public async Task BelowLevelTen_ItIsUnavailable()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("UNSTUCK1", "Unstuckone");
        await host.OnWorldAsync(() => AddUndyingSoul(host, 60_000));

        await player.SendChatAsync(ChatType.Say, Language.Common, ".unstuck");

        Assert.Equal(UnstuckCommands.Unavailable, (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task Alive_GetsUndyingSoul_AndDiesWhenItExpires()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("UNSTUCK2", "Unstucktwo");
        await host.OnWorldAsync(() =>
        {
            AddUndyingSoul(host, 300);
            host.World.FindOnlinePlayer("Unstucktwo")!.SetUInt32(UpdateFields.UnitFieldLevel, 10);
        });

        await player.SendChatAsync(ChatType.Say, Language.Common, ".unstuck");

        foreach (string line in UnstuckCommands.Alive.Split('\n'))
        {
            Assert.Equal(line, (await player.ReadChatAsync()).Text); // one system message per line
        }

        await host.WaitForWorldAsync(() => !host.World.FindOnlinePlayer("Unstucktwo")!.IsAlive, "the unstuck death");
    }

    private static void AddUndyingSoul(WorldTestHost host, int durationMs)
    {
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        spells.Store = new SpellStore(
        [
            .. spells.Store.All.Where(s => s.Id != UnstuckCommands.UndyingSoul),
            new SpellInfo
            {
                Id = UnstuckCommands.UndyingSoul, Name = "Undying Soul", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Duration = new SpellDuration(durationMs, 0, durationMs),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }],
            },
        ], [], []);
    }
}