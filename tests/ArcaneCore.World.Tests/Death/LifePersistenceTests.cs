using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// Health, power and experience survive a relog and are applied after every loading hook
/// (vmangos Player::LoadFromDB, Player.cpp:15057-15070).
/// </summary>
public sealed class LifePersistenceTests
{
    private static async Task<(WorldTestClient Client, byte[] Key)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        return (client, key);
    }

    private static async Task<WorldTestClient> RelogAsync(WorldTestHost host, WorldTestClient old, string account, byte[] key, string name)
    {
        await old.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name) is null, "the session to leave the world");
        WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync(account, key);
        await again.LoginAsync(1);
        return again;
    }

    [Fact]
    public async Task HealthPowerAndExperience_SurviveARelog()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient first, byte[] key) = await CreateAsync(host, "LIFE1", "Liferelog");
        await first.LoginAsync(1);
        (uint health, uint rage) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Liferelog")!;
            player.Health = Math.Max(1u, player.MaxHealth / 2);
            player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage, 900); // large enough that out-of-combat decay cannot reach 0 however long a loaded machine takes to relog
            player.SetUInt32(UpdateFields.PlayerXp, 123);
            return (player.Health, 900u);
        });

        await using WorldTestClient again = await RelogAsync(host, first, "LIFE1", key, "Liferelog");

        Assert.Equal(health, await host.PlayerStateAsync("Liferelog", p => p.Health));
        // Out of combat, rage decays (vmangos Player::RegenerateAll), so it is at most what was stored; a lost restore reads 0.
        uint restoredRage = await host.PlayerStateAsync("Liferelog", p => p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.InRange(restoredRage, 1u, rage);
        Assert.Equal(123u, await host.PlayerStateAsync("Liferelog", p => p.GetUInt32(UpdateFields.PlayerXp)));
    }

    [Fact]
    public async Task StoredValuesAboveTheMaximums_AreClamped()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await CreateAsync(host, "LIFE2", "Lifeclamp");
        await using (client)
        {
            host.Characters.SetLife(1, new CharacterLife(999_999, [999_999, 999_999, 999_999, 999_999, 999_999], 4_000_000, 0, false, null));
            await client.LoginAsync(1);

            uint maxHealth = await host.PlayerStateAsync("Lifeclamp", p => p.MaxHealth);
            Assert.Equal(maxHealth, await host.PlayerStateAsync("Lifeclamp", p => p.Health));
            uint maxRage = await host.PlayerStateAsync("Lifeclamp", p => p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage));
            uint rage = await host.PlayerStateAsync("Lifeclamp", p => p.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
            Assert.InRange(rage, maxRage - 100, maxRage); // clamped to the maximum, less any out-of-combat decay already applied
            uint next = await host.PlayerStateAsync("Lifeclamp", p => p.GetUInt32(UpdateFields.PlayerNextLevelXp));
            Assert.Equal(next - 1, await host.PlayerStateAsync("Lifeclamp", p => p.GetUInt32(UpdateFields.PlayerXp))); // never a pending level-up
        }
    }

    [Fact]
    public async Task ACharacterNeverSaved_KeepsItsFreshValues()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await CreateAsync(host, "LIFE3", "Lifefresh");
        await using (client)
        {
            await client.LoginAsync(1);
            Assert.Equal(await host.PlayerStateAsync("Lifefresh", p => p.MaxHealth), await host.PlayerStateAsync("Lifefresh", p => p.Health));
            Assert.Equal(0u, await host.PlayerStateAsync("Lifefresh", p => p.GetUInt32(UpdateFields.PlayerXp)));
        }
    }

    [Fact]
    public async Task StoredHealth_IsAppliedAfterEveryLoadingHookRaisedTheMaximum()
    {
        // A loading hook (items, auras …) that raises the maximum must not leave the stored
        // health clamped to the smaller pre-hook maximum.
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _) = await CreateAsync(host, "LIFE4", "Lifemax");
        await using (client)
        {
            host.Characters.SetLife(1, new CharacterLife(4000, [0, 0, 0, 0, 0], 0, 0, false, null));
            await client.LoginAsync(1);
            Assert.Equal(5000u, await host.PlayerStateAsync("Lifemax", p => p.MaxHealth));
            Assert.Equal(4000u, await host.PlayerStateAsync("Lifemax", p => p.Health));
        }
    }

    [Fact]
    public async Task AMaximumRaisedByTheLoginAuras_GetsTheStoredHealthOnTheNextTick()
    {
        // Auras are restored in PlayerLoggedIn handlers, after the loaded phase.
        await using WorldTestHost host = WorldTestHost.Start();
        host.World.PlayerLoggedIn += p =>
        {
            if (p.Name == "Lifeaura")
            {
                p.MaxHealth = 5000;
            }
        };
        (WorldTestClient client, _) = await CreateAsync(host, "LIFE5", "Lifeaura");
        await using (client)
        {
            host.Characters.SetLife(1, new CharacterLife(4000, [0, 0, 0, 0, 0], 0, 0, false, null));
            await client.LoginAsync(1);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lifeaura")!.Health == 4000, "the stored health to be re-applied");
            Assert.Equal(5000u, await host.PlayerStateAsync("Lifeaura", p => p.MaxHealth));
            Assert.Null(await host.PlayerStateAsync("Lifeaura", p => p.LoadedLife));
        }
    }

    /// <summary>A loading hook that raises the maximum health of a character named Lifemax.</summary>
    private sealed class MaxRaiserServices : IWorldTestServices
    {
        public void Register(IServiceCollection services) => services.AddSingleton<ICharacterHooks>(new MaxRaiser());
    }

    private sealed class MaxRaiser : ICharacterHooks
    {
        public Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
        {
            if (character.Name == "Lifemax")
            {
                player.MaxHealth = 5000;
                player.Health = 5000;
            }

            return Task.CompletedTask;
        }
    }
}
