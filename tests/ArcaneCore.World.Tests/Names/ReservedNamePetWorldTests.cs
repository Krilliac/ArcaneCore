using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Schema;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Names;
using ArcaneCore.Protocol;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Tests.Pets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Names;

public sealed class ReservedNamePetWorldTests
{
    [Fact]
    public async Task PetRename_UsesSqliteReservedNameStore_ThenAllowsOtherName()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcane-reserved-pet-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (WorldDbContext seed = new(options))
            {
                await SchemaBootstrapper.EnsureAsync(seed, WorldDbContext.Schema);
                seed.Set<ReservedNameRow>().Add(new ReservedNameRow { Name = "reserved" });
                await seed.SaveChangesAsync();
            }

            var factory = new PooledDbContextFactory<WorldDbContext>(options);
            var template = new CreatureTemplate { Entry = PetTestServices.Entry, Name = "Reserved Pet", MinLevel = 1, MaxLevel = 1,
                DisplayIds = [903], MinLevelHealth = PetTestServices.Health, MaxLevelHealth = PetTestServices.Health, Faction = 35 };
            CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddSingleton<IDbContextFactory<WorldDbContext>>(factory);
                    services.AddSingleton<IReservedNameStore, EfReservedNameStore>();
                });
            }
            finally { CreatureTestStore.Current.Value = null; }

            await using (host)
            await using (WorldTestClient client = await host.EnterWorldAsync("RESERVPET", "Reservpet", AccountSecurity.Administrator))
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer("Reservpet")!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                    PetsFeature pets = host.WorldServices.GetRequiredService<PetsFeature>();
                    var snapshot = new PersistentPetSnapshot((int)player.Guid.Low, 801, PetTestServices.Entry, 1, 0, 50, 0, 0, 1, [], [], Name: "OldName", NameTimestamp: 1, RenameAllowed: true);
                    Assert.NotNull(pets.Service.RestoreCurrentPet(player, snapshot));
                });
                await client.CollectAsync();
                ulong petGuid = await host.PlayerStateAsync("Reservpet", p => p.GetPet()!.Guid.Value);

                await client.SendAsync(WorldOpcode.CmsgPetRename, Rename(petGuid, "reserved"));
                await client.ReadUntilAsync(WorldOpcode.SmsgPetNameInvalid);
                Assert.Equal("OldName", await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<PetsFeature>()
                    .Service.CaptureCurrentPet(host.World.FindOnlinePlayer("Reservpet")!)!.Name));

                await using (WorldDbContext update = new(options))
                {
                    update.Set<ReservedNameRow>().Remove(new ReservedNameRow { Name = "reserved" });
                    update.Set<ReservedNameRow>().Add(new ReservedNameRow { Name = "newname" });
                    await update.SaveChangesAsync();
                }

                await client.SendChatAsync(ChatType.Say, Language.Common, ".reload reserved_name");
                Assert.Equal("Re-loading reserved_name...", (await client.ReadChatAsync()).Text);
                Assert.StartsWith("reserved_name reloaded:", (await client.ReadChatAsync()).Text);

                // The same live pet consumer sees the new SQL snapshot after the world-thread publish.
                await client.SendAsync(WorldOpcode.CmsgPetRename, Rename(petGuid, "newname"));
                await client.ReadUntilAsync(WorldOpcode.SmsgPetNameInvalid);
                await client.SendAsync(WorldOpcode.CmsgPetRename, Rename(petGuid, "reserved"));
                await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<PetsFeature>().Service
                    .CaptureCurrentPet(host.World.FindOnlinePlayer("Reservpet")!)!.Name == "reserved", "reloaded pet rename");
            }
        }
        finally
        {
            using (var own = new SqliteConnection($"Data Source={path}")) SqliteConnection.ClearPool(own); // only this test's pool
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] Rename(ulong petGuid, string name)
    {
        var writer = new PacketWriter(32);
        writer.WriteUInt64(petGuid);
        writer.WriteCString(name);
        return writer.ToArray();
    }
}
