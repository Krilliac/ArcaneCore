using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Cold-provider persistence contract for the current hunter pet row. The full loopback
/// WorldRuntime restart remains covered by the coordinator's host fixture because its
/// PersistentHost is intentionally private to the lifecycle test fixture.
/// </summary>
public sealed partial class WorldLifecyclePersistenceTests
{
    private const int CharacterId = 77;

    [Fact]
    public async Task CurrentPetRow_SurvivesFreshSqliteProvider_WithStableIdentityVitalsAndActionBar()
    {
        string path = Path.Combine(_directory.Path, "pets.sqlite");
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1,
        }.ToString()).Options;
        var expected = new PersistentPetSnapshot(CharacterId, 9001, 416, 12, 3456, 321, 87, 654,
            2, [0x01020304, 0x05060708, 0, 0], [new PersistentPetSpell(1234, true, false)]);

        await using (var first = new CharacterDbContext(options))
        {
            await SchemaBootstrapper.EnsureAsync(first, CharacterDbContext.Schema);
            await new EfPersistentPetStore(first).SaveCurrentAsync(expected);
        }

        // A fresh provider/context proves the durable current row, rather than an in-memory cache.
        await using (var cold = new CharacterDbContext(options))
        {
            PersistentPetSnapshot? actual = await new EfPersistentPetStore(cold).LoadCurrentAsync(CharacterId);
            Assert.NotNull(actual);
            Assert.Equal(expected.PetNumber, actual.PetNumber);
            Assert.Equal(expected.Entry, actual.Entry);
            Assert.Equal((expected.Level, expected.Experience, expected.ReactState), (actual.Level, actual.Experience, actual.ReactState));
            Assert.Equal((expected.Health, expected.Mana, expected.Happiness), (actual.Health, actual.Mana, actual.Happiness));
            Assert.Equal(expected.ActionBar, actual.ActionBar.ToArray());
            Assert.Equal(expected.Spells, actual.Spells.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutAndColdWorldRestart_RestoresCurrentHunterPetFromTheSameSqlite(bool dead)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        ulong character;
        const uint petNumber = 9002;
        await using (PersistentHost first = await PersistentHost.StartAsync(fixture, new SaveControl(), token))
        {
            await using WorldClient client = await AuthenticateAsync(first, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync(CharacterName, token);
            character = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await using (AsyncServiceScope scope = first.Services.CreateAsyncScope())
            {
                CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
                CharacterRecord row = await db.Characters.SingleAsync(r => r.Id == checked((int)character), token);
                row.Class = (byte)Class.Hunter;
                await db.SaveChangesAsync(token);
            }
            await connection.LoginAsync(character, token);
            await first.World.InvokeAsync(() =>
            {
                Player owner = first.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                var snapshot = new PersistentPetSnapshot((int)character, petNumber, 9909001, 12, 3456, 321, 87, 654, 2,
                    [0x01020304, 0x05060708, 0, 0], [new PersistentPetSpell(1234, true, false)]);
                PetsFeature pets = first.Services.GetRequiredService<PetsFeature>();
                Creature? pet = pets.Service.RestoreCurrentPet(owner, snapshot);
                Assert.NotNull(pet);
                pet.Health = 321;
                return true;
            }).WaitAsync(token);
            await first.World.InvokeAsync(() =>
            {
                SpellFeature feature = first.Services.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
                {
                    Id = 9909002, Name = "Synthetic Revive Pet", Attributes = SpellAttributes.AllowCastWhileDead,
                    RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.SummonDeadPet, BasePoints = 49, BaseDice = 1, DieSides = 1,
                        TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                if (dead)
                {
                    Player owner = first.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                    owner.Map!.FindUpdater<CreatureMapSystem>()!.KillCreature((Creature)owner.Map.FindObject(owner.PetGuid)!);
                }
                return true;
            }).WaitAsync(token);
            await first.World.InvokeAsync(() => first.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!).WaitAsync(token);
            await connection.SendAsync(WorldOpcode.CmsgLogoutRequest, [], token);
            await connection.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete, token);
            await first.StopWorldAsync(token);
        }

        await using PersistentHost restarted = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await using WorldClient freshClient = await AuthenticateAsync(restarted, token);
        var fresh = new ScenarioConnection(freshClient);
        await fresh.LoginAsync(character, token);
        await restarted.World.InvokeAsync(() =>
        {
            SpellFeature feature = restarted.Services.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = 9909002, Name = "Synthetic Revive Pet", Attributes = SpellAttributes.AllowCastWhileDead,
                RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SummonDeadPet, BasePoints = 49, BaseDice = 1, DieSides = 1,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            return true;
        }).WaitAsync(token);
        Creature? restored = await restarted.World.InvokeAsync(() =>
                restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character))) is { } owner
                    && owner.Map?.FindObject(owner.PetGuid) is Creature pet ? pet : null).WaitAsync(token);
        byte[]? petPacket = null;
        if (dead)
        {
            Assert.Null(restored);
            await restarted.World.InvokeAsync(() =>
            {
                Player owner = restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
                SpellFeature feature = restarted.Services.GetRequiredService<SpellFeature>();
                Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(owner, 9909002,
                    SpellCastTargets.ForSelf(), triggered: true));
                return true;
            }).WaitAsync(token);
            petPacket = await fresh.ReadUntilAsync(WorldOpcode.SmsgPetSpells, token);
            restored = await restarted.World.InvokeAsync(() =>
                restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character))) is { } owner
                    && owner.Map?.FindObject(owner.PetGuid) is Creature pet ? pet : null).WaitAsync(token);
        }
        Assert.NotNull(restored);
        Assert.Equal(petNumber, await restarted.World.InvokeAsync(() =>
            restarted.Services.GetRequiredService<PetsFeature>().Service.CaptureCurrentPet(
                restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!)!.PetNumber).WaitAsync(token));
        Assert.Equal(dead ? restored.MaxHealth / 2 : 321u, restored.Health);
        if (petPacket is not null) Assert.Equal(restored.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(petPacket));
        Assert.Equal((byte)Class.Hunter, await restarted.World.InvokeAsync(() =>
            (byte)restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!.Class).WaitAsync(token));

        (uint Health, uint MaxHealth, ulong Guid) revived = await restarted.World.InvokeAsync(() =>
        {
            Player owner = restarted.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            Creature pet = (Creature)owner.Map!.FindObject(owner.PetGuid)!;
            ulong guid = pet.Guid.Value;
            owner.Map.FindUpdater<CreatureMapSystem>()!.KillCreature(pet);
            restarted.Services.GetRequiredService<PetsFeature>().Service.SummonDeadPet(owner, 50,
                restarted.Services.GetRequiredService<SpellFeature>().System);
            return (pet.Health, pet.MaxHealth, guid);
        }).WaitAsync(token);
        Assert.Equal(revived.MaxHealth / 2, revived.Health);
        Assert.Equal(restored.Guid.Value, revived.Guid);
    }

    private static async Task SeedSyntheticPetTemplateAsync(string path, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1,
        }.ToString()).Options;
        await using var db = new WorldDbContext(options);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: token);
        db.Set<CreatureTemplateRow>().Add(new CreatureTemplateRow
        {
            Entry = 9909001, Name = "Synthetic Persistent Pet", MinLevel = 12, MaxLevel = 12,
            Faction = 1, CreatureType = 1, Family = 1, MinLevelHealth = 500, MaxLevelHealth = 500,
            MaxLevelMana = 100, InhabitType = 3, MeleeBaseAttackTime = 2000, RangedBaseAttackTime = 2000,
        });
        await db.SaveChangesAsync(token);
    }

}
