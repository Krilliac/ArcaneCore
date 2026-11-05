using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Honor;
using ArcaneCore.World.Tests.Pets;
using ArcaneCore.World.Tests.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

/// <summary>
/// The real persisting features of a full world host (skills, rest, spell state, honor, the persistent pet and the guild) around a cluster
/// transfer: the capture's <c>PlayerTransferPreparing</c> queues the dirty state that logout would queue (so a flush makes it durable before the
/// target loads it), and the retire after the commit starts no write of any of them (the target owns the rows).
/// </summary>
public sealed class TransferFeatureWritesHostTests
{
    private const uint PetNumber = 4711;
    private const string Name = "Xferwrites";

    private sealed class CountingRestStore : ICharacterRestStore
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingSpellStateStore : ICharacterSpellStateStore
    {
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterSpellState([], []));

        public Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return Task.CompletedTask;
        }

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingPetStore : IPersistentPetStore
    {
        private readonly Lock _gate = new();
        private PersistentPetSnapshot? _current;
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public void Seed(PersistentPetSnapshot snapshot)
        {
            lock (_gate)
            {
                _current = snapshot;
            }
        }

        public Task<PersistentPetSnapshot?> LoadCurrentAsync(int characterId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_current is { } pet && pet.CharacterId == characterId ? pet : null);
            }
        }

        public Task SaveCurrentAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            lock (_gate)
            {
                _current = snapshot;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingSocialStore : ISocialStore
    {
        private int _guildSaves;

        public int GuildSaves => Volatile.Read(ref _guildSaves);

        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SocialEntry>>([]);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GuildData>>([]);

        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _guildSaves);
            return Task.CompletedTask;
        }

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required WorldTestHost Host { get; init; }
        public required WorldTestClient Client { get; init; }
        public required Player Player { get; init; }
        public required int CharacterId { get; init; }
        public required CountingRestStore Rest { get; init; }
        public required CountingSpellStateStore Spells { get; init; }
        public required CountingPetStore Pets { get; init; }
        public required CountingSocialStore Social { get; init; }
        public required MemoryHonorStore Honor { get; init; }

        public SkillsFeature Skills => Host.WorldServices.GetRequiredService<SkillsFeature>();

        public ICharacterSkillStore SkillStore => Host.WorldServices.GetRequiredService<ICharacterSkillStore>();

        /// <summary>Waits until every per-character queue has written what it holds.</summary>
        public async Task FlushAllAsync()
        {
            IServiceProvider services = Host.WorldServices;
            await Skills.Saves.FlushAllAsync(TimeSpan.FromSeconds(10));
            await services.GetRequiredService<SpellFeature>().State.FlushAsync();
            await services.GetRequiredService<RestFeature>().Writes.FlushAsync();
            await services.GetRequiredService<HonorFeature>().FlushAsync();
            await services.GetRequiredService<PetsFeature>().Service.FlushCharacterAsync(CharacterId);
            await services.GetRequiredService<SocialFeature>().Context.Persistence.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Host.DisposeAsync();
        }
    }

    /// <summary>A hunter with a restored pet, skills, honor, rest and spell state stores, and a guild it leads; every queue is idle.</summary>
    private static async Task<Rig> StartAsync()
    {
        var rest = new CountingRestStore();
        var spells = new CountingSpellStateStore();
        var pets = new CountingPetStore();
        var social = new CountingSocialStore();
        var honor = new MemoryHonorStore();
        var template = new CreatureTemplate
        {
            Entry = PetTestServices.Entry, Name = "Transfer Pet", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try
        {
            host = HonorTestServices.Start(honor, services =>
            {
                SkillAuraWorldTests.Configure(services);
                services.AddSingleton<ICharacterRestStore>(rest);
                services.AddSingleton<ICharacterSpellStateStore>(spells);
                services.AddSingleton<IPersistentPetStore>(pets);
                services.AddSingleton<ISocialStore>(social);
            });
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        byte[] key = await host.AddAccountAsync("XFERWRITES");
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("XFERWRITES", key);
        await client.CreateCharacterAsync(Name);
        Account account = (await host.Accounts.FindByUsernameAsync("XFERWRITES"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        record.Class = (byte)ArcaneCore.Game.Class.Hunter;
        pets.Seed(new PersistentPetSnapshot(record.Id, PetNumber, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], [], Name: "Fang"));
        await client.LoginAsync((ulong)record.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name) is { IsInWorld: true }, "the hunter entered the world");

        // The restored pet may finish off the world thread and post back.
        Player? player = null;
        for (int attempt = 0; attempt < 100 && player is null; attempt++)
        {
            player = await host.OnWorldAsync(() =>
                host.World.FindOnlinePlayer(Name) is { } p && p.Map?.FindObject(p.PetGuid) is Creature ? p : null);
            if (player is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(player);

        // Social is ready (its rows loaded) and the guild list is loaded: found a guild so the player is a member.
        SocialFeature socialFeature = host.WorldServices.GetRequiredService<SocialFeature>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await host.OnWorldAsync(() => socialFeature.ExecuteWhenReady(player, _ => ready.TrySetResult()));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        GuildAdminResult created = GuildAdminResult.NotLoaded;
        for (int attempt = 0; attempt < 100 && created == GuildAdminResult.NotLoaded; attempt++)
        {
            created = await host.OnWorldAsync(() => socialFeature.Context.Guilds.Create(player.Guid.Low, "Transfers", out _));
            if (created == GuildAdminResult.NotLoaded)
            {
                await Task.Delay(20);
            }
        }

        Assert.Equal(GuildAdminResult.Ok, created);

        var rig = new Rig
        {
            Host = host, Client = client, Player = player, CharacterId = record.Id,
            Rest = rest, Spells = spells, Pets = pets, Social = social, Honor = honor,
        };
        await rig.FlushAllAsync();
        return rig;
    }

    [Fact]
    public async Task Capture_QueuesSkillsRestSpellStateHonorAndPet_AndTheyAreDurableAfterTheFlush()
    {
        await using Rig rig = await StartAsync();
        int rest = rig.Rest.Saves, spells = rig.Spells.Saves, pets = rig.Pets.Saves, honor = rig.Honor.WriteAttempts;

        // A skill change made after the last debounce, and a pvp flag change: state only logout (or a later tick) would have queued.
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Player.Skills!.Set(SkillIds.Defense, 5, 5);
            rig.Player.Flags |= PlayerFlags.PvpDesired;
            Assert.True(rig.Player.Skills.IsDirty);
            rig.Host.World.RaisePlayerTransferPreparing(rig.Player);
            Assert.False(rig.Player.Skills.IsDirty); // the snapshot was taken and queued in the same command
        });
        await rig.FlushAllAsync();

        CharacterSkillSnapshot saved = await rig.SkillStore.LoadAsync(rig.CharacterId);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.Defense, 5, 5), saved.Skills);
        Assert.True(rig.Rest.Saves > rest, "rest state not queued by the capture");
        Assert.True(rig.Spells.Saves > spells, "spell state not queued by the capture");
        Assert.True(rig.Pets.Saves > pets, "the current pet not queued by the capture");
        Assert.True(rig.Honor.WriteAttempts > honor, "the pvp flags not queued by the capture");
    }

    [Fact]
    public async Task Retire_StartsNoSkillRestSpellHonorPetOrGuildWrite()
    {
        await using Rig rig = await StartAsync();
        int rest = rig.Rest.Saves, spells = rig.Spells.Saves, pets = rig.Pets.Saves, honor = rig.Honor.WriteAttempts, guild = rig.Social.GuildSaves;
        Assert.True(guild > 0); // the guild was created and written: a retire that saved it would raise the count

        await rig.Host.OnWorldAsync(() =>
        {
            rig.Player.Skills!.Set(SkillIds.Defense, 5, 5);
            rig.Player.Flags |= PlayerFlags.PvpDesired;
            Assert.True(rig.Host.World.DetachPlayerForTransfer(rig.Player));
            Assert.True(rig.Host.World.RetireDetachedPlayer(rig.Player));
            Assert.True(rig.Player.Skills.IsDirty); // never snapshotted by the retire
        });
        await rig.FlushAllAsync();

        CharacterSkillSnapshot saved = await rig.SkillStore.LoadAsync(rig.CharacterId);
        Assert.DoesNotContain(new CharacterSkillRow((ushort)SkillIds.Defense, 5, 5), saved.Skills);
        Assert.Equal(rest, rig.Rest.Saves);
        Assert.Equal(spells, rig.Spells.Saves);
        Assert.Equal(pets, rig.Pets.Saves);
        Assert.Equal(honor, rig.Honor.WriteAttempts);
        Assert.Equal(guild, rig.Social.GuildSaves);
    }
}
