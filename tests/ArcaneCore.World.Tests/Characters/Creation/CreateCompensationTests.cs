using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// A creation that fails after the row was inserted leaves nothing behind (vmangos saves a new
/// character in one transaction, Player.cpp:16193-16315): the character goes back out through the
/// whole delete contract, so the list, the name and the directory are clean again.
/// </summary>
public sealed class CreateCompensationTests
{
    /// <summary>Fails the creation of "Compfail" once, "Compalways" every time; registered in every test host.</summary>
    private sealed class CompensationProbeServices : IWorldTestServices
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<CompensationProbe>();
            services.AddSingleton<ICharacterHooks>(sp => sp.GetRequiredService<CompensationProbe>());
        }
    }

    private sealed class CompensationProbe : ICharacterHooks
    {
        private static readonly ConcurrentDictionary<string, int> Seen = new();

        public Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
        {
            if (character.Name is "Compfail" or "Compalways" && Seen.AddOrUpdate(character.Name, 1, (_, n) => n + 1) is var n && (character.Name == "Compalways" || n == 1))
            {
                throw new InvalidOperationException("feature bug");
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<byte[]> ListAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        return await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum);
    }

    [Fact]
    public async Task AFailingFeatureHook_LeavesNoCharacterNoNameAndNoDirectoryEntry()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("COMP1");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("COMP1", key);

        Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Compfail"));

        Assert.Equal(0, (await ListAsync(client))[0]);
        Assert.False(await host.Characters.IsNameTakenAsync("Compfail"));
        Assert.Null(host.Directory.FindByName("Compfail"));

        // The name is free again; the probe fails only the first creation.
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("Compfail"));
        Assert.Equal(1, (await ListAsync(client))[0]);
    }

    [Fact]
    public async Task TheRollbackFreesTheRealmSlot()
    {
        await using var host = WorldTestHost.Start(configure: o => o.CharactersPerRealm = 1);
        byte[] key = await host.AddAccountAsync("COMP2");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("COMP2", key);

        Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Compalways"));
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("Fine"));
    }

    [Fact]
    public async Task LegacyMode_KeepsTheHalfCreatedCharacter()
    {
        await using var host = WorldTestHost.Start(configureServices: CreateHandlerOrderTests.Config(("Mode", "Legacy")));
        byte[] key = await host.AddAccountAsync("COMP3");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("COMP3", key);

        Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Compalways"));
        Assert.True(await host.Characters.IsNameTakenAsync("Compalways")); // the pre-lane behaviour, kept behind Mode=Legacy
    }
}
