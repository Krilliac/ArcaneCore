using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// <see cref="ICharacterHooks.OnPlayerLoadedAsync"/>: runs for every hook after every
/// <see cref="ICharacterHooks.OnPlayerLoadingAsync"/> has finished and before the player is
/// handed to the world thread (vmangos Player::LoadFromDB applies stored health and power only
/// after the inventory, spells and auras have been loaded and UpdateAllStats ran:
/// Player.cpp:15057-15075).
/// </summary>
public sealed class LoadedPhaseTests
{
    [Fact]
    public async Task Loaded_RunsForEveryHookAfterEveryLoadingHook_AndBeforeTheWorldSeesThePlayer()
    {
        LoadedProbe.Reset();
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LOADED1");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LOADED1", key);
        await client.CreateCharacterAsync("Loadedone");
        Account account = (await host.Accounts.FindByUsernameAsync("LOADED1"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        await client.LoginAsync((ulong)record.Id);

        string[] events = LoadedProbe.Events.ToArray();
        Assert.Equal(["A.Loading", "B.Loading", "A.Loaded", "B.Loaded"], events);
        Assert.Equal([false, false], LoadedProbe.OnlineDuringLoaded.ToArray());
        Assert.Equal([2, 2], LoadedProbe.LoadingDoneAtLoaded.ToArray());
    }

    [Fact]
    public async Task AFailingLoadedHook_FailsTheLogin_AndTheCharacterNeverEntersTheWorld()
    {
        LoadedProbe.Reset();
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LOADED2");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LOADED2", key);
        await client.CreateCharacterAsync("Loadedfail");
        Account account = (await host.Accounts.FindByUsernameAsync("LOADED2"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)record.Id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        byte[] failed = await client.ReadUntilAsync(WorldOpcode.SmsgCharacterLoginFailed);
        Assert.Equal((byte)CharResult.CharLoginFailed, failed[0]);
        Assert.Equal(0, host.World.OnlinePlayerCount);
    }

    /// <summary>Registers two probes; the registration order is the call order.</summary>
    private sealed class LoadedProbeServices : IWorldTestServices
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<ICharacterHooks>(new LoadedProbe("A"));
            services.AddSingleton<ICharacterHooks>(new LoadedProbe("B"));
        }
    }

    private sealed class LoadedProbe(string name) : ICharacterHooks
    {
        private static readonly object s_gate = new();
        private static int s_loadingDone;

        public static ConcurrentQueue<string> Events { get; private set; } = new();

        public static ConcurrentQueue<bool> OnlineDuringLoaded { get; private set; } = new();

        public static ConcurrentQueue<int> LoadingDoneAtLoaded { get; private set; } = new();

        public static void Reset()
        {
            lock (s_gate)
            {
                Events = new ConcurrentQueue<string>();
                OnlineDuringLoaded = new ConcurrentQueue<bool>();
                LoadingDoneAtLoaded = new ConcurrentQueue<int>();
                s_loadingDone = 0;
            }
        }

        public Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
        {
            if (character.Name.StartsWith("Loaded", StringComparison.Ordinal))
            {
                Events.Enqueue(name + ".Loading");
                Interlocked.Increment(ref s_loadingDone);
            }

            return Task.CompletedTask;
        }

        public Task OnPlayerLoadedAsync(WorldSession session, CharacterRecord character, Player player)
        {
            if (!character.Name.StartsWith("Loaded", StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (character.Name == "Loadedfail")
            {
                throw new InvalidOperationException("feature bug");
            }

            Events.Enqueue(name + ".Loaded");
            OnlineDuringLoaded.Enqueue(session.World.IsOnline(player.Guid));
            LoadingDoneAtLoaded.Enqueue(Volatile.Read(ref s_loadingDone));

            return Task.CompletedTask;
        }
    }
}
