using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// The acceptance path through the real world host: an Alliance player kills a flagged Horde player with one blow, is paid the honor,
/// told by SMSG_PVP_CREDIT, has it queued and persisted and keeps it across a relog; then the week ends and the weekly job ranks him
/// and publishes the new rank on his honor tab. Nothing here waits on wall-clock time.
/// </summary>
public sealed class HonorEndToEndTests
{
    [Fact]
    public async Task A_one_blow_pvp_kill_pays_credits_persists_and_survives_a_relog_then_the_week_ranks_the_killer()
    {
        var store = new MemoryHonorStore();
        uint today = HonorTestServices.Today;
        uint thisWeek = today - 3; // the honor week the players log in under; the week ends later in the test (a consistent state: the next maintenance day is thisWeek + 7)
        await store.SaveMaintenanceAsync(new HonorMaintenanceState(thisWeek, thisWeek + 7, false)); // not due: attach leaves it alone
        await using WorldTestHost host = HonorTestServices.Start(store);

        byte[] key = await host.AddAccountAsync("HEKILLER");
        WorldTestClient killer = await host.ConnectAsync();
        await killer.AuthenticateAsync("HEKILLER", key);
        await killer.CreateCharacterAsync("Hekiller");
        await killer.LoginAsync(1);
        await using WorldTestClient victim = await host.EnterWorldAsync("HEVICTIM", "Hevictim", race: 2);
        store.AddCharacter(1, 1);
        await host.OnWorldAsync(() =>
        {
            foreach (ArcaneCore.Game.Maps.Map map in host.World.Maps)
            {
                map.Combat.Hooks = new CombatHooks(); // no faction templates in the test world: use the team and PvP-flag rules
            }

            MapCombat.UpdatePvp(host.World.FindOnlinePlayer("Hevictim")!, true);
        });

        // The kill.
        await host.OnWorldAsync(() =>
        {
            Player a = host.World.FindOnlinePlayer("Hekiller")!;
            Player b = host.World.FindOnlinePlayer("Hevictim")!;
            a.Map!.Combat.DealDamage(a, b, 5000);
            Assert.False(b.IsAlive);
        });

        // Paid, with the honor tab and the client told.
        byte[] credit = await killer.ReadUntilAsync(WorldOpcode.SmsgPvpCredit);
        int honor = BinaryPrimitives.ReadInt32LittleEndian(credit);
        Assert.InRange(honor, 1, 400);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(credit.AsSpan(4)));   // the victim's guid
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(credit.AsSpan(12)));  // unranked victim: shown as the first rank
        Assert.Equal(1u, await host.PlayerStateAsync("Hekiller", p => p.GetUInt16(UpdateFields.PlayerFieldSessionKills, 0)));
        Assert.Equal((uint)honor, await host.PlayerStateAsync("Hekiller", p => p.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution)));

        // Persisted through the write queue.
        HonorFeature feature = await host.PlayerStateAsync("Hekiller", p => ((WorldSession)p.Session).Services.GetRequiredService<HonorFeature>());
        await feature.FlushAsync();
        HonorCpRecord row = Assert.Single(store.Rows(1));
        Assert.Equal((4, 2u, (byte)HonorKind.Honorable), ((int)row.VictimType, row.VictimId, row.Type));
        Assert.Equal((float)honor, row.Cp);

        // A relog keeps it.
        await killer.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Hekiller") is null, "the killer to leave");
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("HEKILLER", key);
        await again.LoginAsync(1);
        Assert.Equal(1u, await host.PlayerStateAsync("Hekiller", p => p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills)));

        // The week ends: only 1 kill is below the 15 needed, so he stays unranked (inactive); a character with 15 would not.
        await store.SaveMaintenanceAsync(new HonorMaintenanceState(thisWeek, today, false));
        HonorMaintenanceFeature maintenance = await host.PlayerStateAsync("Hekiller",
            p => ((WorldSession)p.Session).Services.GetRequiredService<HonorMaintenanceFeature>());
        Assert.Equal(1, await maintenance.RunAsync(live: true));
        Assert.Equal((byte)0, await host.PlayerStateAsync("Hekiller", p => p.GetByte(UpdateFields.PlayerBytes3, 3)));
        Assert.Equal(1u, await host.PlayerStateAsync("Hekiller", p => p.GetUInt32(UpdateFields.PlayerFieldLastWeekKills)));
        Assert.Equal(0u, store.State(1).Standing); // below the kill minimum: no standing
    }
}
