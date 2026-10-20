using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Pets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// .pet delete and .pet rename while the owner's login is between the pet loading hook and the world entry: the owner is not online yet, so
/// the command runs, and the login must not restore (and on logout write back) the pet it read before the change.
/// </summary>
public sealed class PetGmLoginRaceTests
{
    private const uint PetNumber = 811;

    /// <summary>One table behind both stores, as character_pet is behind the EF pet stores.</summary>
    private sealed class MemoryPetTable : IPersistentPetStore, IPetAdminStore
    {
        private readonly Lock _gate = new();
        private PersistentPetSnapshot? _current;

        public List<PersistentPetSnapshot> Saves { get; } = [];

        public void Seed(PersistentPetSnapshot snapshot)
        {
            lock (_gate) _current = snapshot;
        }

        public Task<PersistentPetSnapshot?> LoadCurrentAsync(int characterId, CancellationToken cancellationToken = default)
        {
            lock (_gate) return Task.FromResult(_current is { } pet && pet.CharacterId == characterId ? pet : null);
        }

        public Task SaveCurrentAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Saves.Add(snapshot);
                _current = snapshot;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        {
            lock (_gate) if (_current?.CharacterId == characterId) _current = null;
            return Task.CompletedTask;
        }

        public Task<StoredPetLocation?> FindAsync(uint petNumber, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_current is { } pet && pet.PetNumber == petNumber
                    ? new StoredPetLocation(pet.PetNumber, pet.CharacterId, pet.Name ?? string.Empty, false)
                    : null);
            }
        }

        public Task<bool> RenameAsync(uint petNumber, string name, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_current?.PetNumber != petNumber) return Task.FromResult(false);
                _current = _current with { Name = name };
                return Task.FromResult(true);
            }
        }

        public Task<bool> DeleteAsync(uint petNumber, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_current?.PetNumber != petNumber) return Task.FromResult(false);
                _current = null;
                return Task.FromResult(true);
            }
        }
    }

    /// <summary>Holds one character's login after every loading hook has run, before the player enters the world.</summary>
    private sealed class LoginGate(string character) : ICharacterHooks
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnPlayerLoadedAsync(WorldSession session, CharacterRecord record, Player player)
        {
            if (!string.Equals(record.Name, character, StringComparison.Ordinal)) return;
            Reached.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Delete_DuringOwnerLogin_IsNotUndoneByTheLoginOrItsLogout()
    {
        (MemoryPetTable table, Creature? pet) = await RunDuringLoginAsync("Racedel", ".pet delete 811", "Pet #811 (\"Fang\", owner #{0}) deleted.");

        Assert.Null(pet);
        Assert.Null(await table.FindAsync(PetNumber));
        Assert.Empty(table.Saves);
    }

    [Fact]
    public async Task Rename_DuringOwnerLogin_RestoresTheRenamedPet()
    {
        (MemoryPetTable table, Creature? pet) = await RunDuringLoginAsync("Raceren", ".pet rename 811 Bitey", "Pet #811 (\"Fang\", owner #{0}) renamed to \"Bitey\"");

        Assert.NotNull(pet);
        Assert.Equal("Bitey", pet.Summon!.Charm!.Name);
        Assert.Equal("Bitey", (await table.FindAsync(PetNumber))!.Name);
        Assert.All(table.Saves, save => Assert.Equal("Bitey", save.Name));
    }

    /// <summary>
    /// Hold a hunter's login after the pet loading hook, run <paramref name="command"/> as a GM, let the login finish, and return the pet the
    /// owner has in the world. The owner then disconnects so any write-back of the old pet reaches <see cref="MemoryPetTable.Saves"/>.
    /// </summary>
    private static async Task<(MemoryPetTable Table, Creature? Pet)> RunDuringLoginAsync(string hunter, string command, string expectedReply)
    {
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Login Race Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        var table = new MemoryPetTable();
        var gate = new LoginGate(hunter);
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost prepared;
        try
        {
            prepared = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IPersistentPetStore>(table);
                services.AddSingleton<IPetAdminStore>(table);
                services.AddSingleton<ICharacterHooks>(gate);
            });
        }
        finally { CreatureTestStore.Current.Value = null; }

        await using WorldTestHost host = prepared;
        await using WorldTestClient gm = await host.EnterWorldAsync("PETRACEGM", hunter + "gm", AccountSecurity.Administrator);

        byte[] key = await host.AddAccountAsync("PETRACE" + hunter.ToUpperInvariant());
        await using WorldTestClient owner = await host.ConnectAsync();
        await owner.AuthenticateAsync("PETRACE" + hunter.ToUpperInvariant(), key);
        await owner.CreateCharacterAsync(hunter);
        Account account = (await host.Accounts.FindByUsernameAsync("PETRACE" + hunter.ToUpperInvariant()))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        record.Class = (byte)ArcaneCore.Game.Class.Hunter;
        table.Seed(new PersistentPetSnapshot(record.Id, PetNumber, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], [], Name: "Fang"));

        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)record.Id);
        await owner.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, expectedReply, record.Id), (await gm.ReadChatAsync()).Text);

        gate.Release.SetResult();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(hunter) is { IsInWorld: true }, "the hunter entered the world");
        // The restore may finish off the world thread and post back, so give it a second to show up.
        Creature? pet = null;
        for (int attempt = 0; attempt < 50 && pet is null; attempt++)
        {
            pet = await host.OnWorldAsync(() =>
                host.World.FindOnlinePlayer(hunter) is { } player && player.Map?.FindObject(player.PetGuid) is Creature live ? live : null);
            if (pet is null) await Task.Delay(20);
        }

        // Dropping the connection logs the owner out at once (a logout request would wait out the 20 s timer).
        await owner.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(hunter) is null, "the hunter logged out");
        await host.WorldServices.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(record.Id);
        return (table, pet);
    }
}
