using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using System.Buffers.Binary;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed partial class WorldLifecyclePersistenceTests
{
    [Fact]
    public async Task CurrentPetCooldowns_SurviveFreshSqliteContext_AndExpiredRowsRemainData()
    {
        string path = Path.Combine(_directory.Path, "pet-cooldowns.sqlite");
        var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1,
        }.ToString()).Options;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expected = new PersistentPetSnapshot(CharacterId, 9010, 416, 12, 0, 100, 10, 700, 2, [], [],
            Cooldowns: [new PersistentPetCooldown(0, 1234, 12, now + 60_000), new PersistentPetCooldown(1, 0, 12, now + 30_000)]);
        await using (CharacterDbContext first = new(options))
        {
            await SchemaBootstrapper.EnsureAsync(first, CharacterDbContext.Schema);
            await new EfPersistentPetStore(first).SaveCurrentAsync(expected);
        }
        await using CharacterDbContext cold = new(options);
        PersistentPetSnapshot? loaded = await new EfPersistentPetStore(cold).LoadCurrentAsync(CharacterId);
        Assert.NotNull(loaded);
        PersistentPetSnapshot actual = loaded!;
        Assert.Equal(expected.Cooldowns, actual.Cooldowns);
        await new EfPersistentPetStore(cold).DeleteAsync(CharacterId);
        Assert.Empty(await cold.Set<PersistentPetCooldownRow>().Where(r => r.CharacterId == CharacterId).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdWorldLogin_PublishesRestoredPetCooldownTrailer(bool expired)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        await SeedSyntheticPetTemplateAsync(fixture.WorldDatabasePath, token);
        await SeedCooldownSpellAsync(fixture.WorldDatabasePath, token);
        const uint petNumber = 9011;
        const uint spell = 49010;
        ulong character;
        await using (PersistentHost host = await PersistentHost.StartAsync(fixture, new SaveControl(), token))
        {
            await using WorldClient client = await AuthenticateAsync(host, token);
            var connection = new ScenarioConnection(client);
            await connection.CreateCharacterAsync("PETCD", token);
            character = Assert.Single(await connection.EnumerateAsync(token)).Guid;
            await using (CharacterDbContext db = new(Options<CharacterDbContext>(fixture.CharacterDatabasePath)))
            {
                await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema, cancellationToken: token);
                CharacterRecord row = await db.Characters.SingleAsync(r => r.Id == checked((int)character), token);
                row.Class = (byte)Class.Hunter;
                await new EfPersistentPetStore(db).SaveCurrentAsync(new PersistentPetSnapshot(
                    (int)character, petNumber, 9909001, 12, 0, 300, 10, 500, 2, [], [new PersistentPetSpell(spell, false, false)],
                    Cooldowns: [new PersistentPetCooldown(0, spell, 17, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expired ? -1_000 : 60_000))]), token);
                await db.SaveChangesAsync(token);
            }
            await host.StopWorldAsync(token);
        }

        await using PersistentHost coldHost = await PersistentHost.StartAsync(fixture, new SaveControl(), token);
        await using WorldClient freshClient = await AuthenticateAsync(coldHost, token);
        var fresh = new ScenarioConnection(freshClient);
        await fresh.LoginAsync(character, token);
        byte[] packet = await fresh.ReadUntilAsync(WorldOpcode.SmsgPetSpells, token);
        const int spellCountOffset = 56;
        int cooldownCountOffset = spellCountOffset + 1 + packet[spellCountOffset] * 4;
        Assert.Equal(expired ? (byte)0 : (byte)1, packet[cooldownCountOffset]);
        if (!expired)
        {
            Assert.Equal((ushort)spell, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(cooldownCountOffset + 1)));
            Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(cooldownCountOffset + 5)) > 0);
        }
        SpellCastResult cast = await coldHost.World.InvokeAsync(() =>
        {
            var owner = coldHost.World.FindOnlinePlayer(ObjectGuid.Player(checked((uint)character)))!;
            Creature pet = (Creature)owner.Map!.FindObject(owner.PetGuid)!;
            return coldHost.Services.GetRequiredService<SpellFeature>().System.CastSpell(pet, spell, SpellCastTargets.ForSelf(), triggered: false);
        }).WaitAsync(token);
        Assert.Equal(expired ? SpellCastResult.CastOk : SpellCastResult.NotReady, cast);
    }

    private static async Task SeedCooldownSpellAsync(string path, CancellationToken token)
    {
        await using WorldDbContext db = WorldContext(path);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: token);
        db.Set<SpellTemplateRow>().Add(new SpellTemplateRow { Id = 49010, Category = 17, RecoveryTime = 60_000 });
        await db.SaveChangesAsync(token);
    }

    private static DbContextOptions<T> Options<T>(string path) where T : DbContext
        => new DbContextOptionsBuilder<T>().UseSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1,
        }.ToString()).Options;

    private static WorldDbContext WorldContext(string path) => new(Options<WorldDbContext>(path));
}
