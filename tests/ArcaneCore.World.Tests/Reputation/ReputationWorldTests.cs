using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// Reputation over the real world socket: the login SMSG_INITIALIZE_FACTIONS contents, client
/// toggles persisted through the write queue and restored after a relog, and rejected toggles.
/// </summary>
public sealed class ReputationWorldTests
{
    private const uint Stormwind = 72;   // list 7: forced peace for alliance
    private const uint BootyBay = 21;    // list 0: invisible until contact

    [Fact]
    public async Task Login_SendsSixtyFourSlotsWithRaceDefaults_AndNoWatchedFaction()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        await using WorldTestClient client = await host.EnterWorldAsync("REPLOGIN", "Replogin");

        byte[] factions = client.LoginPacket(WorldOpcode.SmsgInitializeFactions);
        Assert.Equal(4 + (64 * 5), factions.Length);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(factions));
        Assert.Equal(0x11, factions[4 + (7 * 5)]);
        Assert.Equal(0, factions[4]);
        Assert.Equal(0x04, factions[4 + (10 * 5)]);
        Assert.Equal(-1, await host.PlayerStateAsync("Replogin", p => p.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex)));
    }

    [Fact]
    public async Task Toggles_ArePersisted_AndRestoredAfterRelog_WhileForbiddenTogglesChangeNothing()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        byte[] key = await host.AddAccountAsync("REPTOGGLE");
        WorldTestClient first = await host.ConnectAsync();
        await first.AuthenticateAsync("REPTOGGLE", key);
        await first.CreateCharacterAsync("Reptoggle");
        await first.LoginAsync(1);

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Reptoggle")!;
            Assert.True(Feature(player).Service.ModifyReputation(player, BootyBay, 3100));
        });
        Assert.Equal([0, 0, 0, 0], await first.ReadUntilAsync(WorldOpcode.SmsgSetFactionVisible));
        Assert.Equal(ReputationPackets.SetFactionStanding([(0, 3100)]), await first.ReadUntilAsync(WorldOpcode.SmsgSetFactionStanding));

        await first.SendAsync(WorldOpcode.CmsgSetFactionAtwar, [0, 0, 0, 0, 1]);
        await first.SendAsync(WorldOpcode.CmsgSetFactionAtwar, [7, 0, 0, 0, 1]);   // forced peace
        await first.SendAsync(WorldOpcode.CmsgSetFactionAtwar, [10, 0, 0, 0, 1]);  // hidden
        await first.SendAsync(WorldOpcode.CmsgSetFactionInactive, [7, 0]);         // malformed
        await first.SendAsync(WorldOpcode.CmsgSetWatchedFaction, [0, 0, 0, 0]);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Reptoggle")!.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex) == 0,
            "the watched faction to change");
        await (await host.PlayerStateAsync("Reptoggle", Feature)).FlushAsync();

        Assert.Equal(new CharacterReputationRow(1, BootyBay, 3100, 0x03), store.Row(1, BootyBay));
        Assert.Null(store.Row(1, Stormwind));
        Assert.Equal(0, store.Watched(1));
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("REPTOGGLE", key);
        await again.LoginAsync(1);
        byte[] factions = again.LoginPacket(WorldOpcode.SmsgInitializeFactions);
        Assert.Equal(0x03, factions[4]);
        Assert.Equal(3100, BinaryPrimitives.ReadInt32LittleEndian(factions.AsSpan(5)));
        Assert.Equal(0x11, factions[4 + (7 * 5)]);
        Assert.Equal(0, await host.PlayerStateAsync("Reptoggle", p => p.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex)));
        Assert.True(await host.PlayerStateAsync("Reptoggle", p => Feature(p).Service.IsAtWar(p, BootyBay)));
    }

    [Fact]
    public async Task FailedGainAndWatched_RefuseRelog_UntilRecovery_ThenRestoreBoth()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        byte[] key = await host.AddAccountAsync("REPFAIL");
        try
        {
            WorldTestClient first = await host.ConnectAsync();
            await first.AuthenticateAsync("REPFAIL", key);
            await first.CreateCharacterAsync("Repfail");
            await first.LoginAsync(1);

            store.FailWrites = true;
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repfail")!;
                Assert.True(Feature(player).Service.ModifyReputation(player, BootyBay, 3100));
            });
            await first.SendAsync(WorldOpcode.CmsgSetWatchedFaction, [0, 0, 0, 0]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Repfail")!.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex) == 0,
                "the watched faction to change");
            await (await host.PlayerStateAsync("Repfail", Feature)).FlushAsync();
            Assert.True(store.WriteAttempts > 0, "the failing store was never reached");
            Assert.Null(store.Row(1, BootyBay));

            await first.DisposeAsync();
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

            await using WorldTestClient again = await host.ConnectAsync();
            await again.AuthenticateAsync("REPFAIL", key);
            var login = new PacketWriter(8);
            login.WriteUInt64(1);
            await again.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
            Assert.Equal((byte)CharResult.CharLoginFailed, (await again.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed))[0]);
            Assert.Equal(0, host.World.OnlinePlayerCount);

            store.FailWrites = false;
            await again.LoginAsync(1);
            byte[] factions = again.LoginPacket(WorldOpcode.SmsgInitializeFactions);
            Assert.Equal(0x01, factions[4]);
            Assert.Equal(3100, BinaryPrimitives.ReadInt32LittleEndian(factions.AsSpan(5)));
            Assert.Equal(0, await host.PlayerStateAsync("Repfail", p => p.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex)));
            Assert.Equal(new CharacterReputationRow(1, BootyBay, 3100, 0x01), store.Row(1, BootyBay));
            Assert.Equal(0, store.Watched(1));
        }
        finally
        {
            store.FailWrites = false;
        }
    }

    [Fact]
    public async Task RepeatedFailuresWhileOnline_NextChangePersistsAll()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        try
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REPREPEAT", "Represent");
            store.FailWrites = true;
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Represent")!;
                Assert.True(Feature(player).Service.ModifyReputation(player, BootyBay, 3100));
            });
            await (await host.PlayerStateAsync("Represent", Feature)).FlushAsync();
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Represent")!;
                Assert.True(Feature(player).Service.ModifyReputation(player, Stormwind, 900));
            });
            await (await host.PlayerStateAsync("Represent", Feature)).FlushAsync();
            Assert.Null(store.Row(1, BootyBay));
            Assert.Null(store.Row(1, Stormwind));

            store.FailWrites = false;
            await client.SendAsync(WorldOpcode.CmsgSetWatchedFaction, [0, 0, 0, 0]);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Represent")!.GetInt32(UpdateFields.PlayerFieldWatchedFactionIndex) == 0,
                "the watched faction to change");
            await (await host.PlayerStateAsync("Represent", Feature)).FlushAsync();

            Assert.Equal(3100, store.Row(1, BootyBay)?.Standing);
            Assert.Equal(900, store.Row(1, Stormwind)?.Standing);
            Assert.Equal(0, store.Watched(1));
        }
        finally
        {
            store.FailWrites = false;
        }
    }

    [Fact]
    public async Task DeletingCharacterWithRetainedFailure_SucceedsAndWritesNothingBack()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        byte[] key = await host.AddAccountAsync("REPDELETE");
        try
        {
            WorldTestClient first = await host.ConnectAsync();
            await first.AuthenticateAsync("REPDELETE", key);
            await first.CreateCharacterAsync("Repdelete");
            await first.LoginAsync(1);
            ReputationFeature feature = await host.PlayerStateAsync("Repdelete", Feature);
            store.FailWrites = true;
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repdelete")!;
                Assert.True(Feature(player).Service.ModifyReputation(player, BootyBay, 3100));
            });
            await feature.FlushAsync();
            await first.DisposeAsync();
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the session to leave");

            store.FailWrites = false;
            await using WorldTestClient again = await host.ConnectAsync();
            await again.AuthenticateAsync("REPDELETE", key);
            var guid = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(guid, 1);
            await again.SendAsync(WorldOpcode.CmsgCharDelete, guid);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, (await again.ReadUntilAsync(WorldOpcode.SmsgCharDelete))[0]);
            await feature.FlushAsync();

            Assert.Null(store.Row(1, BootyBay));
            Assert.Equal(-1, store.Watched(1));
        }
        finally
        {
            store.FailWrites = false;
        }
    }

    [Fact]
    public async Task LogoutRetry_PersistsRetainedGain_WithoutRelogOrFurtherChanges()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = Start(store);
        try
        {
            WorldTestClient client = await host.EnterWorldAsync("REPLOGOUT", "Replogout");
            store.FailWrites = true;
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Replogout")!;
                Assert.True(Feature(player).Service.ModifyReputation(player, BootyBay, 3100));
            });
            ReputationFeature feature = await host.PlayerStateAsync("Replogout", Feature);
            await feature.FlushAsync();
            Assert.True(feature.HasRetainedFailure(1));
            int before = store.WriteAttempts;

            await client.DisposeAsync();
            await WorldTestHost.WaitForAsync(() => store.WriteAttempts > before, "the logout retry to reach the store");
            store.FailWrites = false; // storage recovers while the retry is backing off
            await WorldTestHost.WaitForAsync(() => store.Row(1, BootyBay) is not null, "the retained gain to persist");
            await feature.FlushAsync();
            Assert.False(feature.HasRetainedFailure(1));
            Assert.Equal(3100, store.Row(1, BootyBay)!.Standing);
        }
        finally
        {
            store.FailWrites = false;
        }
    }

    [Fact]
    public async Task WithoutFactionData_LoginStillSendsAnEmptyList()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("REPEMPTY", "Repempty");
        byte[] factions = client.LoginPacket(WorldOpcode.SmsgInitializeFactions);
        Assert.Equal(324, factions.Length);
        Assert.All(factions.Skip(4), b => Assert.Equal(0, b));
        Assert.Equal(0, await host.PlayerStateAsync("Repempty", p => Feature(p).Service.Factions.Count));
    }

    private static ReputationFeature Feature(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();

    private static WorldTestHost Start(MemoryReputationStore store)
    {
        ReputationTestServices.Current.Value = store;
        try { return WorldTestHost.Start(); }
        finally { ReputationTestServices.Current.Value = null; }
    }
}

internal sealed class ReputationTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<MemoryReputationStore?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } store)
        {
            return;
        }

        services.AddSingleton<ICharacterReputationStore>(store);
        services.AddSingleton(new FactionCatalog(
        [
            new FactionRecord(Stormwind, 7, [1 | 4 | 8 | 64, 2 | 16 | 32 | 128, 0, 0], [0, 0, 0, 0], [0, -42000, 0, 0], [0x11, 0x0E, 0, 0], 469, "Stormwind"),
            new FactionRecord(469, 10, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0x04, 0, 0, 0], 0, "Alliance"),
            new FactionRecord(BootyBay, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "Booty Bay"),
        ]));
    }

    private const uint Stormwind = 72;
    private const uint BootyBay = 21;
}

/// <summary>Thread-safe in-memory reputation storage (writes come from the queue's consumer).</summary>
internal sealed class MemoryReputationStore : ICharacterReputationStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(int, uint), CharacterReputationRow> _rows = [];
    private readonly Dictionary<int, int> _watched = [];
    private int _writeAttempts;

    /// <summary>While set, every write throws <see cref="IOException"/> (the attempt is still counted).</summary>
    public volatile bool FailWrites;

    /// <summary>Write calls made so far, failed or not.</summary>
    public int WriteAttempts => Volatile.Read(ref _writeAttempts);

    private void BeforeWrite()
    {
        Interlocked.Increment(ref _writeAttempts);
        if (FailWrites)
        {
            throw new IOException("controlled reputation storage failure");
        }
    }

    public CharacterReputationRow? Row(int characterId, uint faction)
    {
        lock (_lock)
        {
            return _rows.TryGetValue((characterId, faction), out CharacterReputationRow? row) ? row : null;
        }
    }

    public int Watched(int characterId)
    {
        lock (_lock)
        {
            return _watched.GetValueOrDefault(characterId, -1);
        }
    }

    public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(new CharacterReputationData(
                [.. _rows.Values.Where(r => r.CharacterId == characterId).OrderBy(r => r.Faction)],
                _watched.GetValueOrDefault(characterId, -1)));
        }
    }

    public Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default)
    {
        BeforeWrite();
        lock (_lock)
        {
            foreach (CharacterReputationRow row in upserts)
            {
                _rows[(characterId, row.Faction)] = row;
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default)
    {
        BeforeWrite();
        lock (_lock)
        {
            _watched[characterId] = watchedFaction;
        }

        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        BeforeWrite();
        lock (_lock)
        {
            foreach ((int, uint) key in _rows.Keys.Where(k => k.Item1 == characterId).ToList())
            {
                _rows.Remove(key);
            }

            _watched.Remove(characterId);
        }

        return Task.CompletedTask;
    }

    /// <summary>This in-memory store has no characters table, so a deleted character's id never has a live row.</summary>
    public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        => DeleteCharacterAsync(characterId, cancellationToken);
}
