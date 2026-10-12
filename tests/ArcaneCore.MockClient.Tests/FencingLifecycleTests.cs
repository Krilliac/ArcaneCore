using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The whole character lifecycle with durable ownership fencing on (<c>Cluster:Fencing:Enabled</c>), on a real SQLite
/// characters database, real SRP and world sessions: create (including the start-action bar write, which must carry the
/// world's token), login, move, logout, relog, then stop releasing the claim. Fencing stays off unless a test asks for it.
/// </summary>
public sealed class FencingLifecycleTests
{
    private const string Account = "FENCING";
    private const string Password = "PASSWORD";
    private const byte Race = 1;
    private const byte Class = 1;
    private const uint StartAction = 78;

    [Fact]
    public async Task FencedWorld_CreateWithStartActions_Login_Move_Logout_Relog_RestoresPosition_And_StopReleasesTheClaim()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        var logged = new LoggedExceptions();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true },
            services => services.AddLogging(logging => logging.AddProvider(logged)), token);
        await server.AddAccountAsync(Account, Password, token);
        await SeedStartActionAsync(server, token);
        Assert.True(await IsClaimedAsync(server, token));

        MockLocation destination;
        ulong guid;
        await using (WorldClient client = await AuthenticateAsync(server, token))
        {
            var connection = new ScenarioConnection(client);
            try
            {
                await connection.CreateCharacterAsync("Fenced", token);
            }
            catch (MockProtocolException)
            {
                // The world refuses the creation and logs why; a fenced write without the world's token is the known cause.
                Assert.Empty(logged.Of<CharacterOwnershipRejectedException>());
                throw;
            }

            MockCharacter created = Assert.Single(await connection.EnumerateAsync(token));
            guid = created.Guid;
            int id = checked((int)guid);

            // The start-action write happened under the fence and reached the database.
            Assert.Contains(await ReadActionButtonsAsync(server, id, token), b => b.Button == 0 && b.Action == StartAction);

            await connection.LoginAsync(guid, token);
            destination = new MockLocation(created.Map, created.X + 32f, created.Y - 17f, created.Z + 1f, 1.25f);
            await MoveThroughWireAsync(server, connection, guid, destination, token);
            await connection.LogoutAsync(token);
            await server.FlushCharacterAsync(id, token);

            // Relog on the same connection: the persisted position, written with the world's token, is restored.
            MockLogin relog = await connection.LoginAsync(guid, token);
            Assert.Equal(destination, relog.Location);
            Assert.Equal(guid, relog.Self.Guid);
        }

        // Set-based writes of the stores that used to bypass the fence (ExecuteDelete/ExecuteUpdate), made while the world owns the database.
        await using (AsyncServiceScope writes = server.Services.CreateAsyncScope())
        {
            int id = checked((int)guid);
            await writes.ServiceProvider.GetRequiredService<IHonorStore>().ResetAsync(id, token);
            await writes.ServiceProvider.GetRequiredService<ICharacterSpellStore>().RemoveAsync(id, 1, token);
            await writes.ServiceProvider.GetRequiredService<IExploredZonesStore>().DeleteAsync(id, token);
            await writes.ServiceProvider.GetRequiredService<IInstanceStore>().DeleteBindAsync(id, 1, token);
        }

        await server.StopWorldAsync(token);
        Assert.False(await IsClaimedAsync(server, token));
        Assert.Empty(logged.Of<CharacterWriteFenceException>());
        Assert.Empty(logged.Of<CharacterOwnershipRejectedException>());

        // The same file can be owned again once the claim is released.
        await using SyntheticArcaneServer successor = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true, CharactersDatabasePath = server.CharactersDatabasePath }, null, token);
        Assert.True(await IsClaimedAsync(successor, token));
        await using AsyncServiceScope scope = successor.Services.CreateAsyncScope();
        CharacterRecord row = Assert.IsType<CharacterRecord>(await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
            .GetByIdAsync(checked((int)guid), token));
        Assert.Equal((destination.Map, destination.X, destination.Y, destination.Z),
            (row.MapId, row.X, row.Y, row.Z));
    }

    [Fact]
    public async Task SecondWorldOnTheSameCharactersFile_FailsStartup_WhileTheFirstOwnsIt()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer first = await SyntheticArcaneServer.StartAsync(
            new SyntheticServerOptions { EnableFencing = true }, configureServices: null, token);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { EnableFencing = true, CharactersDatabasePath = first.CharactersDatabasePath }, null, token));
        Assert.Contains("already owned by another world authority", failure.Message);

        // The refused start did not disturb the owner.
        Assert.True(await IsClaimedAsync(first, token));
    }

    [Fact]
    public async Task FencingIsOffByDefault_NoClaimIsTaken_AndCreationWithStartActionsStillWorks()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken token = deadline.Token;
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(token);
        await server.AddAccountAsync(Account, Password, token);
        await SeedStartActionAsync(server, token);
        Assert.False(await IsClaimedAsync(server, token));

        await using WorldClient client = await AuthenticateAsync(server, token);
        var connection = new ScenarioConnection(client);
        await connection.CreateCharacterAsync("Plain", token);
        MockCharacter created = Assert.Single(await connection.EnumerateAsync(token));
        Assert.Contains(await ReadActionButtonsAsync(server, checked((int)created.Guid), token),
            b => b.Button == 0 && b.Action == StartAction);
        Assert.False(await IsClaimedAsync(server, token));
    }

    private sealed class LoggedExceptions : ILoggerProvider, ILogger
    {
        private readonly List<Exception> _exceptions = [];

        public IReadOnlyList<T> Of<T>() where T : Exception
        {
            lock (_exceptions) return [.. _exceptions.OfType<T>()];
        }

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose() { }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null) return;
            lock (_exceptions) _exceptions.Add(exception);
        }
    }

    private static async Task SeedStartActionAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        WorldDbContext db = scope.ServiceProvider.GetRequiredService<WorldDbContext>();
        db.Set<PlayerCreateActionRow>().Add(new PlayerCreateActionRow { Race = Race, Class = Class, Button = 0, Action = StartAction, Type = 0 });
        await db.SaveChangesAsync(token);
    }

    private static async Task<bool> IsClaimedAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().IsOwnedAsync("world", token);
    }

    private static async Task<IReadOnlyList<ActionButton>> ReadActionButtonsAsync(SyntheticArcaneServer server, int id, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        // Bypass any decorator: a fresh provider read of the stored bar.
        return await new EfCharacterStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
            .GetActionButtonsAsync(id, token);
    }

    private static async Task MoveThroughWireAsync(SyntheticArcaneServer server, ScenarioConnection connection,
        ulong guid, MockLocation location, CancellationToken token)
    {
        // Unflagged vanilla MovementInfo: flags, time, four floats, fall time.
        byte[] movement = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(movement.AsSpan(4), 100);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(8), location.X);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(12), location.Y);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(16), location.Z);
        BinaryPrimitives.WriteSingleLittleEndian(movement.AsSpan(20), location.Orientation);
        await connection.SendAsync(WorldOpcode.MsgMoveStop, movement, token);
        while (!await server.World.InvokeAsync(() =>
        {
            var player = server.World.FindOnlinePlayer(ArcaneCore.Game.ObjectGuid.Player(checked((uint)guid)));
            return player is not null && player.X == location.X && player.Y == location.Y
                && player.Z == location.Z && player.Orientation == location.Orientation;
        }).WaitAsync(token))
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, Account, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync(Account, logon.SessionKey, token));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}
