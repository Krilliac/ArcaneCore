using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Registers an in-memory <see cref="IExploredZonesStore"/> for the host started inside <see cref="Use"/>.</summary>
internal sealed class ExploredZonesTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<MemoryExploredZonesStore?> Current = new();

    public static WorldTestHost Start(MemoryExploredZonesStore store)
    {
        Current.Value = store;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            Current.Value = null;
        }
    }

    public void Register(IServiceCollection services)
    {
        if (Current.Value is { } store)
        {
            services.AddSingleton<IExploredZonesStore>(store);
        }
    }
}

/// <summary>Thread-safe in-memory store with injectable failures.</summary>
internal sealed class MemoryExploredZonesStore : IExploredZonesStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, uint[]> _rows = [];

    public volatile bool FailWrites;
    public volatile bool CorruptLoads;

    public uint[]? Row(int id)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(id)?.ToArray();
        }
    }

    public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        if (CorruptLoads)
        {
            throw new InvalidDataException("explored_zones is malformed");
        }

        return Task.FromResult(Row(characterId));
    }

    public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("controlled explored-zones storage failure");
        }

        lock (_lock)
        {
            _rows[characterId] = [.. words];
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _rows.Remove(characterId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Explored zones survive a relog (vmangos Player.cpp:14648 load, :16481 save) and fail closed.</summary>
public sealed class ExploredZonesRelogTests
{
    private static ExploredZonesPersistence Persistence(WorldTestHost host) => host.WorldServices.GetRequiredService<ExploredZonesPersistence>();

    private static async Task DiscoverAsync(WorldTestHost host, string name, uint areaFlag)
        => await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            Assert.Equal(ExploreOutcome.Discovered, ExploredZonesFields.Mark(player, areaFlag));
            WorldStateHooks.For(host.World).ExploredZonesSink!.Changed(player);
        });

    private static async Task<(WorldTestClient Client, byte[] Key)> FirstLoginAsync(WorldTestHost host, string account, string character)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        await client.LoginAsync(1);
        return (client, key);
    }

    [Fact]
    public async Task Discovery_IsWrittenAtOnce_AndRestoredAfterRelog()
    {
        var store = new MemoryExploredZonesStore();
        await using WorldTestHost host = ExploredZonesTestServices.Start(store);
        (WorldTestClient first, byte[] key) = await FirstLoginAsync(host, "EXPLORER", "Explorer");

        await DiscoverAsync(host, "Explorer", 35); // word 1, bit 3
        await Persistence(host).FlushAsync();

        // written on discovery: a crash now (no logout yet) would keep it
        uint[] stored = store.Row(1)!;
        Assert.Equal(0x8u, stored[1]);
        Assert.Equal(1, stored.Count(w => w != 0));

        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("EXPLORER", key);
        await again.LoginAsync(1);
        Assert.Equal(0x8u, await host.PlayerStateAsync("Explorer", p => p.GetUInt32(UpdateFields.PlayerExploredZones1 + 1)));
        Assert.Equal(0u, await host.PlayerStateAsync("Explorer", p => p.GetUInt32(UpdateFields.PlayerExploredZones1)));
    }

    [Fact]
    public async Task Logout_WritesTheWordsEvenWithoutADiscoveryHook()
    {
        var store = new MemoryExploredZonesStore();
        await using WorldTestHost host = ExploredZonesTestServices.Start(store);
        (WorldTestClient first, _) = await FirstLoginAsync(host, "LOGOUTER", "Logouter");
        await host.OnWorldAsync(() => ExploredZonesFields.Mark(host.World.FindOnlinePlayer("Logouter")!, 64)); // no sink call

        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");
        await Persistence(host).FlushAsync();

        Assert.Equal(1u, store.Row(1)![2]);
    }

    [Fact]
    public async Task MalformedStoredRow_FailsTheLogin_InsteadOfZeroing()
    {
        var store = new MemoryExploredZonesStore();
        await using WorldTestHost host = ExploredZonesTestServices.Start(store);
        byte[] key = await host.AddAccountAsync("BROKEN");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("BROKEN", key);
        await client.CreateCharacterAsync("Broken");
        store.CorruptLoads = true;

        var login = new PacketWriter(8);
        login.WriteUInt64(1);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.Equal(0, host.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task AWriteThatIsStillNotDurable_BlocksTheNextLogin_UntilItRecovers()
    {
        var store = new MemoryExploredZonesStore();
        await using WorldTestHost host = ExploredZonesTestServices.Start(store);
        (WorldTestClient first, byte[] key) = await FirstLoginAsync(host, "RETAIN", "Retain");

        store.FailWrites = true;
        await DiscoverAsync(host, "Retain", 100);
        await Persistence(host).FlushAsync();
        Assert.True(Persistence(host).Writes.HasRetainedFailure(1));
        Assert.Null(store.Row(1));

        // logout is never blocked by the failing store
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

        await using (WorldTestClient refused = await host.ConnectAsync())
        {
            await refused.AuthenticateAsync("RETAIN", key);
            var login = new PacketWriter(8);
            login.WriteUInt64(1);
            await refused.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            await refused.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        }

        store.FailWrites = false;
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("RETAIN", key);
        await again.LoginAsync(1);
        Assert.Equal(1u << (100 % 32), await host.PlayerStateAsync("Retain", p => p.GetUInt32(UpdateFields.PlayerExploredZones1 + (100 / 32))));
        Assert.False(Persistence(host).Writes.HasRetainedFailure(1));
    }

    [Fact]
    public async Task DeleteHook_IsDiscovered_AndForgetsRetainedWrites()
    {
        Assert.Contains(typeof(ExploredZonesDeleteHook), WorldFeatures.FeatureTypes.Where(typeof(ICharacterDeleteHook).IsAssignableFrom));

        var store = new MemoryExploredZonesStore();
        await using WorldTestHost host = ExploredZonesTestServices.Start(store);
        (WorldTestClient first, _) = await FirstLoginAsync(host, "DELETED", "Deleted");
        store.FailWrites = true;
        await DiscoverAsync(host, "Deleted", 7);
        await Persistence(host).FlushAsync();
        Assert.True(Persistence(host).Writes.HasRetainedFailure(1));

        var hook = host.WorldServices.GetRequiredService<ExploredZonesDeleteHook>();
        await hook.OnCharacterDeletedAsync(null!, new CharacterRecord { Id = 1, Name = "Deleted" });

        Assert.False(Persistence(host).Writes.HasRetainedFailure(1));
        store.FailWrites = false; // the logout save below must succeed, or shutdown would (rightly) report it
        await first.DisposeAsync();
    }
}
