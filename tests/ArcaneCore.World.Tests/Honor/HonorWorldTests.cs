using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// Honor over the real world socket: login loads the stored state before the player is visible, awards are persisted through the
/// write queue and survive a relog, a failing store refuses the login until it recovers, the PvP flags come back, and the master
/// switch turns the whole thing off. Time-dependent expectations use rows dated at the week begin so no assertion depends on
/// the wall-clock minute.
/// </summary>
public sealed class HonorWorldTests
{
    private static uint Today => HonorTestServices.Today; // the clock every honor test host runs at

    private static uint WeekBegin => HonorMaintenancePlanner.LastMaintenanceDay(Today, new HonorOptions().MaintenanceDay);

    private static HonorFeature Feature(Player player) => ((WorldSession)player.Session).Services.GetRequiredService<HonorFeature>();

    private static async Task<(WorldTestClient Client, byte[] Key)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        return (client, key);
    }

    [Fact]
    public void The_feature_and_its_delete_hook_are_discovered()
    {
        Assert.Contains(typeof(HonorFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(HonorCharacterDeleteHook), WorldFeatures.FeatureTypes);
    }

    [Fact]
    public async Task Login_loads_the_stored_state_and_publishes_the_honor_tab_before_the_player_is_visible()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient client, _) = await CreateAsync(host, "HONLOGIN", "Honlogin");
        store.Seed(1, new CharacterHonorState(5500f, 9, 7, 4, 44.5f, 7, 3, 0, false), new HonorCpRecord(4, 99, 188f, WeekBegin, (byte)HonorKind.Honorable));
        await client.LoginAsync(1);

        (uint thisWeek, uint lifetime, int rank, int highest, int bar, uint standing) = await host.PlayerStateAsync("Honlogin", p => (
            p.GetUInt32(UpdateFields.PlayerFieldThisWeekKills),
            p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills),
            (int)p.GetByte(UpdateFields.PlayerBytes3, 3),
            (int)p.GetByte(UpdateFields.PlayerFieldBytes, 3),
            (int)p.GetByte(UpdateFields.PlayerFieldBytes2, 0),
            p.GetUInt32(UpdateFields.PlayerFieldLastWeekRank)));
        Assert.Equal((1u, 8u, 7, 9, 25, 7u), (thisWeek, lifetime, rank, highest, bar, standing));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task The_equip_gate_uses_the_stored_highest_rank_after_login()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient client, _) = await CreateAsync(host, "HONGATE", "Hongate");
        store.Seed(1, CharacterHonorState.Empty with { RankPoints = 5500f, HighestRank = 12 });
        await client.LoginAsync(1);
        byte requirement = await host.PlayerStateAsync("Hongate", p => p.Inventory.Requirements.HonorRank(p.Inventory));
        Assert.Equal((byte)12, requirement);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task An_award_is_persisted_and_survives_a_relog()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "HONKEEP", "Honkeep");
        await first.LoginAsync(1);

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Honkeep")!;
            Assert.True(Feature(player).Service.Add(player, 188.3f, HonorKind.Honorable, null));
        });
        await (await host.PlayerStateAsync("Honkeep", Feature)).FlushAsync();

        HonorCpRecord stored = Assert.Single(store.Rows(1));
        Assert.Equal((0, 1u, 188.3f, (byte)HonorKind.Honorable), ((int)stored.VictimType, stored.VictimId, stored.Cp, stored.Type));
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("HONKEEP", key);
        await again.LoginAsync(1);
        Assert.Equal(1u, await host.PlayerStateAsync("Honkeep", p => p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills)));
        Assert.Single(store.Rows(1)); // loading does not write anything back
    }

    [Fact]
    public async Task A_failing_store_refuses_the_relog_until_it_recovers_then_nothing_is_lost()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "HONFAIL", "Honfail");
        await first.LoginAsync(1);
        try
        {
            store.FailWrites = true;
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Honfail")!;
                Assert.True(Feature(player).Service.Add(player, 100f, HonorKind.Honorable, null));
            });
            HonorFeature feature = await host.PlayerStateAsync("Honfail", Feature);
            await feature.FlushAsync();
            Assert.True(store.WriteAttempts > 0, "the failing store was never reached");
            Assert.Empty(store.Rows(1));
            Assert.True(feature.HasRetainedFailure(1));

            await first.DisposeAsync();
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

            await using WorldTestClient again = await host.ConnectAsync();
            await again.AuthenticateAsync("HONFAIL", key);
            var login = new PacketWriter(8);
            login.WriteUInt64(1);
            await again.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            Assert.Equal((byte)CharResult.CharLoginFailed, (await again.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
            Assert.Equal(0, host.World.OnlinePlayerCount);

            store.FailWrites = false;
            await again.LoginAsync(1);
            Assert.Equal(1u, await host.PlayerStateAsync("Honfail", p => p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills)));
            Assert.Single(store.Rows(1));
        }
        finally
        {
            store.FailWrites = false;
        }
    }

    [Fact]
    public async Task The_pvp_flag_and_desire_are_saved_at_logout_and_restored_at_login()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "HONPVP", "Honpvp");
        await first.LoginAsync(1);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Honpvp")!;
            player.Flags |= PlayerFlags.PvpDesired;
            MapCombat.UpdatePvp(player, true);
        });
        HonorFeature feature = await host.PlayerStateAsync("Honpvp", Feature);
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");
        await WorldTestHost.WaitForAsync(() => store.State(1).PvpFlags != 0, "the PvP flags to be saved");
        await feature.FlushAsync();
        Assert.Equal(HonorService.PvpFlaggedBit | HonorService.PvpDesiredBit, store.State(1).PvpFlags);

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("HONPVP", key);
        await again.LoginAsync(1);
        (bool flagged, bool desired) = await host.PlayerStateAsync("Honpvp", p => ((p.UnitFlags & UnitFlags.Pvp) != 0, (p.Flags & PlayerFlags.PvpDesired) != 0));
        Assert.True(flagged);
        Assert.True(desired);
    }

    [Fact]
    public async Task With_honor_disabled_nothing_is_loaded_or_ranked()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store, services =>
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["World:Honor:Enabled"] = "false" }).Build()));
        (WorldTestClient client, _) = await CreateAsync(host, "HONOFF", "Honoff");
        store.Seed(1, CharacterHonorState.Empty with { RankPoints = 5500f, HighestRank = 9 });
        await client.LoginAsync(1);

        (int rank, byte requirement, bool active) = await host.PlayerStateAsync("Honoff", p => (
            (int)p.GetByte(UpdateFields.PlayerBytes3, 3), p.Inventory.Requirements.HonorRank(p.Inventory), Feature(p).ActiveService is not null));
        Assert.Equal((0, (byte)0, false), (rank, requirement, active));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task A_fresh_start_records_the_maintenance_days_before_any_player_computes_honor()
    {
        var store = new MemoryHonorStore();
        await using WorldTestHost host = HonorTestServices.Start(store);
        HonorMaintenanceState state = Assert.IsType<HonorMaintenanceState>(store.Maintenance);
        Assert.Equal(state.LastDay + 7, state.NextDay);
        Assert.False(state.Marker);
        Assert.Equal((int)new HonorOptions().MaintenanceDay, HonorMaintenancePlanner.Weekday(state.LastDay));
    }

    [Fact]
    public async Task An_existing_maintenance_row_is_kept_as_the_week_begin()
    {
        var store = new MemoryHonorStore();
        // A stored week that is not due yet (so the maintenance feature leaves it alone) and differs from what the clock would compute.
        uint last = WeekBegin - 3;
        var kept = new HonorMaintenanceState(last, WeekBegin + 100, false);
        await store.SaveMaintenanceAsync(kept);
        await using WorldTestHost host = HonorTestServices.Start(store);
        (WorldTestClient client, _) = await CreateAsync(host, "HONWEEK", "Honweek");
        await client.LoginAsync(1);
        Assert.Equal(last, await host.PlayerStateAsync("Honweek", p => Feature(p).WeekBeginDay));
        Assert.Equal(kept, store.Maintenance);
        await client.DisposeAsync();
    }
}
